using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DesktopCopilot;

public sealed class SpeechCaptureService : IDisposable
{
    private const byte VirtualKeyControl = 0x11;
    private const byte VirtualKeySpace = 0x20;
    private const byte VirtualKeyEscape = 0x1B;
    private const uint KeyEventKeyUp = 0x0002;
    private static readonly TimeSpan HandyStartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _syncRoot = new();
    private CaptureMode? _activeMode;
    private CancellationTokenSource? _pendingLookupCts;
    private long _historyBaselineId;
    private bool _hotkeyHeld;
    private bool _suppressResult;
    private bool _disposed;

    public event EventHandler<ListeningStateChangedEventArgs>? ListeningStateChanged;
    public event EventHandler<TranscriptCapturedEventArgs>? TranscriptCaptured;
    public event EventHandler<ConfirmationCapturedEventArgs>? ConfirmationCaptured;
    public event EventHandler<SpeechCaptureFailedEventArgs>? CaptureFailed;

    public bool IsListening { get; private set; }

    public void StartDictation()
    {
        AppLog.Info($"SpeechCaptureService.StartDictation backend=Handy model={TryGetSelectedModel() ?? "unknown"}");
        StartListening(CaptureMode.Dictation);
    }

    public void StartConfirmation()
    {
        AppLog.Info($"SpeechCaptureService.StartConfirmation backend=Handy model={TryGetSelectedModel() ?? "unknown"}");
        StartListening(CaptureMode.Confirmation);
    }

    public void StopListening()
    {
        if (!IsListening)
        {
            return;
        }

        CancellationTokenSource pendingLookup;
        CaptureMode completedMode;
        long baselineId;

        lock (_syncRoot)
        {
            if (_activeMode is null)
            {
                return;
            }

            AppLog.Info("SpeechCaptureService.StopListening backend=Handy");
            _suppressResult = false;
            completedMode = _activeMode.Value;
            baselineId = _historyBaselineId;
            ReleaseTranscribeHotkey();
            SetListeningState(isListening: false);
            CancelPendingLookup();
            pendingLookup = new CancellationTokenSource();
            _pendingLookupCts = pendingLookup;
        }

        _ = Task.Run(() => AwaitTranscriptionAsync(completedMode, baselineId, pendingLookup.Token));
    }

    public void CancelListening()
    {
        if (!IsListening)
        {
            return;
        }

        lock (_syncRoot)
        {
            AppLog.Info("SpeechCaptureService.CancelListening backend=Handy");
            _suppressResult = true;
            CancelPendingLookup();
            SendCancelCommand();
            ReleaseTranscribeHotkey();
            _activeMode = null;
            _historyBaselineId = 0;
            SetListeningState(isListening: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPendingLookup();
        ReleaseTranscribeHotkey();
    }

    private void StartListening(CaptureMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsListening)
        {
            return;
        }

        EnsureHandyRunning();

        lock (_syncRoot)
        {
            CancelPendingLookup();
            _activeMode = mode;
            _historyBaselineId = GetLatestHistoryId();
            _suppressResult = false;
            AppLog.Info($"SpeechCaptureService.StartListening mode={mode} backend=Handy baselineId={_historyBaselineId}");
            PressTranscribeHotkey();
            SetListeningState(isListening: true);
        }
    }

    private async Task AwaitTranscriptionAsync(CaptureMode mode, long baselineId, CancellationToken cancellationToken)
    {
        AppLog.Info($"Awaiting Handy transcription mode={mode} baselineId={baselineId}");

        try
        {
            var startedAt = DateTime.UtcNow;

            while (DateTime.UtcNow - startedAt < TranscriptionTimeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = TryGetLatestTranscription(baselineId);
                if (entry is not null)
                {
                    CompleteFromTranscription(mode, entry);
                    return;
                }

                await Task.Delay(PollInterval, cancellationToken);
            }

            AppLog.Info($"Handy transcription timed out after {TranscriptionTimeout.TotalSeconds:F1}s baselineId={baselineId}");
            CompleteWithFailure(new SpeechCaptureFailedEventArgs("Handy did not return a transcription in time.", mode));
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("AwaitTranscriptionAsync canceled.");
        }
        catch (Exception exception)
        {
            AppLog.Error("AwaitTranscriptionAsync failed.", exception);
            CompleteWithFailure(new SpeechCaptureFailedEventArgs("Handy transcription failed.", mode));
        }
    }

    private void CompleteFromTranscription(CaptureMode mode, HandyTranscriptionEntry entry)
    {
        AppLog.Info($"Handy transcription received id={entry.Id} timestamp={entry.Timestamp} text=\"{entry.Text}\"");

        bool suppressResult;
        lock (_syncRoot)
        {
            suppressResult = _suppressResult;
            _activeMode = null;
            _historyBaselineId = 0;
            _suppressResult = false;
            DisposePendingLookup();
        }

        if (suppressResult)
        {
            return;
        }

        if (mode == CaptureMode.Dictation)
        {
            TranscriptCaptured?.Invoke(this, new TranscriptCapturedEventArgs(entry.Text));
            return;
        }

        var normalized = entry.Text.Trim().ToLowerInvariant();
        if (normalized.Contains("yes", StringComparison.Ordinal))
        {
            ConfirmationCaptured?.Invoke(this, new ConfirmationCapturedEventArgs(true));
            return;
        }

        if (normalized.Contains("no", StringComparison.Ordinal) || normalized.Contains("cancel", StringComparison.Ordinal))
        {
            ConfirmationCaptured?.Invoke(this, new ConfirmationCapturedEventArgs(false));
            return;
        }

        CompleteWithFailure(new SpeechCaptureFailedEventArgs("No confirmation was recognized.", mode, entry.Text));
    }

    private void CompleteWithFailure(SpeechCaptureFailedEventArgs args)
    {
        bool suppressResult;
        lock (_syncRoot)
        {
            suppressResult = _suppressResult;
            _activeMode = null;
            _historyBaselineId = 0;
            _suppressResult = false;
            DisposePendingLookup();
        }

        if (!suppressResult)
        {
            CaptureFailed?.Invoke(this, args);
        }
    }

    private static HandyTranscriptionEntry? TryGetLatestTranscription(long baselineId)
    {
        var historyPath = GetHandyHistoryPath();
        if (!File.Exists(historyPath))
        {
            throw new FileNotFoundException("Handy history database was not found.", historyPath);
        }

        using var connection = new SqliteConnection($"Data Source={historyPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, timestamp, transcription_text
            FROM transcription_history
            WHERE id > $baselineId
            ORDER BY id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$baselineId", baselineId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new HandyTranscriptionEntry(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2));
    }

    private static long GetLatestHistoryId()
    {
        var historyPath = GetHandyHistoryPath();
        if (!File.Exists(historyPath))
        {
            throw new InvalidOperationException("Handy history database was not found.");
        }

        using var connection = new SqliteConnection($"Data Source={historyPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM transcription_history";
        var result = command.ExecuteScalar();
        return result is long longValue ? longValue : Convert.ToInt64(result);
    }

    private static void EnsureHandyRunning()
    {
        if (Process.GetProcessesByName("handy").Length > 0)
        {
            return;
        }

        var executablePath = GetHandyExecutablePath();
        if (!File.Exists(executablePath))
        {
            throw new InvalidOperationException("Handy is not installed.");
        }

        AppLog.Info($"Starting Handy from {executablePath}");
        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)
        });

        var startedAt = DateTime.UtcNow;
        while (Process.GetProcessesByName("handy").Length == 0)
        {
            if (DateTime.UtcNow - startedAt > HandyStartTimeout)
            {
                throw new InvalidOperationException("Handy did not start in time.");
            }

            Thread.Sleep(100);
        }
    }

    private void PressTranscribeHotkey()
    {
        if (_hotkeyHeld)
        {
            return;
        }

        keybd_event(VirtualKeyControl, 0, 0, 0);
        keybd_event(VirtualKeySpace, 0, 0, 0);
        _hotkeyHeld = true;
    }

    private void ReleaseTranscribeHotkey()
    {
        if (!_hotkeyHeld)
        {
            return;
        }

        keybd_event(VirtualKeySpace, 0, KeyEventKeyUp, 0);
        keybd_event(VirtualKeyControl, 0, KeyEventKeyUp, 0);
        _hotkeyHeld = false;
    }

    private static void SendCancelCommand()
    {
        keybd_event(VirtualKeyEscape, 0, 0, 0);
        keybd_event(VirtualKeyEscape, 0, KeyEventKeyUp, 0);
    }

    private void CancelPendingLookup()
    {
        _pendingLookupCts?.Cancel();
        DisposePendingLookup();
    }

    private void DisposePendingLookup()
    {
        _pendingLookupCts?.Dispose();
        _pendingLookupCts = null;
    }

    private static string GetHandyExecutablePath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Handy",
            "handy.exe");
    }

    private static string GetHandyHistoryPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.pais.handy",
            "history.db");
    }

    private static string GetHandySettingsPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "com.pais.handy",
            "settings_store.json");
    }

    private static string? TryGetSelectedModel()
    {
        try
        {
            var settingsPath = GetHandySettingsPath();
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (document.RootElement.TryGetProperty("settings", out var settings)
                && settings.TryGetProperty("selected_model", out var selectedModel))
            {
                return selectedModel.GetString();
            }

            return null;
        }
        catch (Exception exception)
        {
            AppLog.Error("Unable to read Handy selected model.", exception);
            return null;
        }
    }

    private void SetListeningState(bool isListening)
    {
        if (IsListening == isListening)
        {
            return;
        }

        IsListening = isListening;
        ListeningStateChanged?.Invoke(this, new ListeningStateChangedEventArgs(IsListening, _activeMode));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private sealed record HandyTranscriptionEntry(long Id, long Timestamp, string Text);
}

public enum CaptureMode
{
    Dictation,
    Confirmation
}

public sealed class ListeningStateChangedEventArgs(bool isListening, CaptureMode? mode) : EventArgs
{
    public bool IsListening { get; } = isListening;

    public CaptureMode? Mode { get; } = mode;
}

public sealed class TranscriptCapturedEventArgs(string transcript, float? confidence = null) : EventArgs
{
    public string Transcript { get; } = transcript;

    public float? Confidence { get; } = confidence;
}

public sealed class ConfirmationCapturedEventArgs(bool isConfirmed) : EventArgs
{
    public bool IsConfirmed { get; } = isConfirmed;
}

public sealed class SpeechCaptureFailedEventArgs(
    string reason,
    CaptureMode mode,
    string? heardTranscript = null,
    float? heardConfidence = null) : EventArgs
{
    public string Reason { get; } = reason;

    public CaptureMode Mode { get; } = mode;

    public string? HeardTranscript { get; } = heardTranscript;

    public float? HeardConfidence { get; } = heardConfidence;
}
