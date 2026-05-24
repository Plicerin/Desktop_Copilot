using System.IO;
using System.Threading;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;

namespace DesktopCopilot;

public sealed class SpeechCaptureService : IDisposable
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int ChannelCount = 1;
    private const int RecordingBufferMilliseconds = 100;
    private const string BundledModelFolderName = "Models";
    private const string ModelFileName = "ggml-tiny.en.bin";
    private static readonly GgmlType ModelType = GgmlType.TinyEn;
    private static readonly TimeSpan TranscriptionTimeout = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim FactoryLock = new(1, 1);

    private static WhisperFactory? _whisperFactory;
    private static string? _modelPath;

    private readonly object _syncRoot = new();
    private CaptureMode? _activeMode;
    private CancellationTokenSource? _pendingLookupCts;
    private RecordingSession? _recordingSession;
    private bool _suppressResult;
    private bool _disposed;

    static SpeechCaptureService()
    {
        RuntimeOptions.RuntimeLibraryOrder =
        [
            RuntimeLibrary.Cpu,
            RuntimeLibrary.CpuNoAvx
        ];
    }

    public event EventHandler<ListeningStateChangedEventArgs>? ListeningStateChanged;
    public event EventHandler<TranscriptCapturedEventArgs>? TranscriptCaptured;
    public event EventHandler<ConfirmationCapturedEventArgs>? ConfirmationCaptured;
    public event EventHandler<SpeechCaptureFailedEventArgs>? CaptureFailed;

    public bool IsListening { get; private set; }

    public void StartDictation()
    {
        AppLog.Info($"SpeechCaptureService.StartDictation backend=Whisper model={ModelFileName}");
        StartListening(CaptureMode.Dictation);
    }

    public void StartConfirmation()
    {
        AppLog.Info($"SpeechCaptureService.StartConfirmation backend=Whisper model={ModelFileName}");
        StartListening(CaptureMode.Confirmation);
    }

    public async Task WarmUpAsync()
    {
        AppLog.Info("SpeechCaptureService.WarmUpAsync: ensuring Whisper model/runtime.");

        try
        {
            await EnsureFactoryAsync(CancellationToken.None);
            AppLog.Info($"SpeechCaptureService.WarmUpAsync: ready modelPath={_modelPath} runtime={RuntimeOptions.LoadedLibrary}");
        }
        catch (Exception ex)
        {
            AppLog.Error("SpeechCaptureService.WarmUpAsync failed.", ex);
        }
    }

    public void StopListening()
    {
        if (!IsListening)
        {
            return;
        }

        RecordingSession? session;
        CancellationTokenSource pendingLookup;
        CaptureMode completedMode;

        lock (_syncRoot)
        {
            if (_activeMode is null || _recordingSession is null)
            {
                return;
            }

            AppLog.Info("SpeechCaptureService.StopListening backend=Whisper");
            _suppressResult = false;
            completedMode = _activeMode.Value;
            session = _recordingSession;
            _recordingSession = null;
            SetListeningState(isListening: false);
            CancelPendingLookup();
            pendingLookup = new CancellationTokenSource();
            _pendingLookupCts = pendingLookup;
        }

        session.Stop();
        _ = Task.Run(() => AwaitTranscriptionAsync(completedMode, session, pendingLookup.Token));
    }

    public void CancelListening()
    {
        if (!IsListening)
        {
            return;
        }

        RecordingSession? session = null;

        lock (_syncRoot)
        {
            AppLog.Info("SpeechCaptureService.CancelListening backend=Whisper");
            _suppressResult = true;
            CancelPendingLookup();
            session = _recordingSession;
            _recordingSession = null;
            _activeMode = null;
            SetListeningState(isListening: false);
        }

        session?.Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPendingLookup();
        _recordingSession?.Dispose();
        _recordingSession = null;
    }

    private void StartListening(CaptureMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsListening)
        {
            return;
        }

        var session = new RecordingSession();

        lock (_syncRoot)
        {
            CancelPendingLookup();
            _recordingSession = session;
            _activeMode = mode;
            _suppressResult = false;
            AppLog.Info($"SpeechCaptureService.StartListening mode={mode} backend=Whisper");
            session.Start();
            SetListeningState(isListening: true);
        }
    }

    private async Task AwaitTranscriptionAsync(CaptureMode mode, RecordingSession session, CancellationToken cancellationToken)
    {
        AppLog.Info($"Awaiting Whisper transcription mode={mode}");

        try
        {
            var audioBytes = await session.AudioTask.WaitAsync(cancellationToken);
            if (audioBytes.Length <= 44)
            {
                CompleteWithFailure(new SpeechCaptureFailedEventArgs("No speech was recorded.", mode));
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TranscriptionTimeout);
            var transcript = await TranscribeAsync(audioBytes, timeout.Token);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                CompleteWithFailure(new SpeechCaptureFailedEventArgs("No speech was recognized.", mode));
                return;
            }

            CompleteFromTranscript(mode, transcript);
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("AwaitTranscriptionAsync canceled.");
        }
        catch (Exception exception)
        {
            AppLog.Error("AwaitTranscriptionAsync failed.", exception);
            CompleteWithFailure(new SpeechCaptureFailedEventArgs("Whisper transcription failed.", mode));
        }
        finally
        {
            session.Dispose();
        }
    }

    private async Task<string> TranscribeAsync(byte[] audioBytes, CancellationToken cancellationToken)
    {
        var factory = await EnsureFactoryAsync(cancellationToken);
        using var audioStream = new MemoryStream(audioBytes, writable: false);
        using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .Build();

        var segments = new List<string>();
        await foreach (var result in processor.ProcessAsync(audioStream, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                segments.Add(result.Text.Trim());
            }
        }

        var transcript = string.Join(" ", segments).Trim();
        AppLog.Info($"Whisper transcription complete text=\"{transcript}\"");
        return transcript;
    }

    private static async Task<WhisperFactory> EnsureFactoryAsync(CancellationToken cancellationToken)
    {
        if (_whisperFactory is not null)
        {
            return _whisperFactory;
        }

        await FactoryLock.WaitAsync(cancellationToken);
        try
        {
            if (_whisperFactory is not null)
            {
                return _whisperFactory;
            }

            _modelPath = await EnsureModelAvailableAsync(cancellationToken);
            _whisperFactory = WhisperFactory.FromPath(_modelPath);
            AppLog.Info($"Whisper factory created modelPath={_modelPath} runtime={RuntimeOptions.LoadedLibrary}");
            return _whisperFactory;
        }
        finally
        {
            FactoryLock.Release();
        }
    }

    private static async Task<string> EnsureModelAvailableAsync(CancellationToken cancellationToken)
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, BundledModelFolderName, ModelFileName);
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        var modelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopCopilot",
            "Models");

        Directory.CreateDirectory(modelDirectory);
        var modelPath = Path.Combine(modelDirectory, ModelFileName);
        if (File.Exists(modelPath))
        {
            return modelPath;
        }

        AppLog.Info($"Whisper model missing. Downloading {ModelFileName} to {modelPath}");
        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ModelType, cancellationToken: cancellationToken);
        await using var fileWriter = File.Create(modelPath);
        await modelStream.CopyToAsync(fileWriter, cancellationToken);
        await fileWriter.FlushAsync(cancellationToken);
        return modelPath;
    }

    private void CompleteFromTranscript(CaptureMode mode, string transcript)
    {
        AppLog.Info($"Whisper transcription received text=\"{transcript}\"");

        bool suppressResult;
        lock (_syncRoot)
        {
            suppressResult = _suppressResult;
            _activeMode = null;
            _suppressResult = false;
            DisposePendingLookup();
        }

        if (suppressResult)
        {
            return;
        }

        if (mode == CaptureMode.Dictation)
        {
            TranscriptCaptured?.Invoke(this, new TranscriptCapturedEventArgs(transcript));
            return;
        }

        var normalized = transcript.Trim().ToLowerInvariant();
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

        CompleteWithFailure(new SpeechCaptureFailedEventArgs("No confirmation was recognized.", mode, transcript));
    }

    private void CompleteWithFailure(SpeechCaptureFailedEventArgs args)
    {
        bool suppressResult;
        lock (_syncRoot)
        {
            suppressResult = _suppressResult;
            _activeMode = null;
            _suppressResult = false;
            DisposePendingLookup();
        }

        if (!suppressResult)
        {
            CaptureFailed?.Invoke(this, args);
        }
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

    private void SetListeningState(bool isListening)
    {
        if (IsListening == isListening)
        {
            return;
        }

        IsListening = isListening;
        ListeningStateChanged?.Invoke(this, new ListeningStateChangedEventArgs(IsListening, _activeMode));
    }

    private sealed class RecordingSession : IDisposable
    {
        private readonly WaveInEvent _waveIn;
        private readonly MemoryStream _audioBuffer = new();
        private readonly WaveFileWriter _waveWriter;
        private readonly TaskCompletionSource<byte[]> _audioCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public RecordingSession()
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(SampleRate, BitsPerSample, ChannelCount),
                BufferMilliseconds = RecordingBufferMilliseconds
            };

            _waveWriter = new WaveFileWriter(_audioBuffer, _waveIn.WaveFormat);
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
        }

        public Task<byte[]> AudioTask => _audioCompletionSource.Task;

        public void Start() => _waveIn.StartRecording();

        public void Stop() => _waveIn.StopRecording();

        public void Dispose()
        {
            DisposeCore(null, cancelled: true);
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
            _waveWriter.Flush();
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            DisposeCore(e.Exception, cancelled: false);
        }

        private void DisposeCore(Exception? exception, bool cancelled)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.RecordingStopped -= OnRecordingStopped;
            _waveIn.Dispose();
            _waveWriter.Dispose();
            var audioBytes = _audioBuffer.ToArray();
            _audioBuffer.Dispose();

            if (exception is not null)
            {
                _audioCompletionSource.TrySetException(exception);
                return;
            }

            if (cancelled)
            {
                _audioCompletionSource.TrySetCanceled();
                return;
            }

            _audioCompletionSource.TrySetResult(audioBytes);
        }
    }
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
