using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using NAudio.Wave;

namespace DesktopCopilot;

public sealed class EdgeTtsService : IDisposable
{
    private static readonly string[] PythonRuntimes = ["python", "py"];
    private static readonly SemaphoreSlim BootstrapLock = new(1, 1);
    private const int SpeechLevelWindowMs = 45;
    private readonly MediaPlayer _player = new();
    private bool _disposed;
    private static bool _bootstrapAttempted;
    private static bool _bootstrapSucceeded;
    private static string _bootstrapFailure = string.Empty;

    public event EventHandler<SpeechLevelChangedEventArgs>? SpeechLevelChanged;

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var outputFile = Path.Combine(Path.GetTempPath(), $"desktop-copilot-{Guid.NewGuid():N}.mp3");

        try
        {
            await GenerateSpeechAsync(text, outputFile, cancellationToken);
            await PlayAudioAsync(outputFile, cancellationToken);
        }
        finally
        {
            TryDelete(outputFile);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.Close();
    }

    private static async Task GenerateSpeechAsync(string text, string outputFile, CancellationToken cancellationToken)
    {
        var lastFailure = string.Empty;

        foreach (var runtime in PythonRuntimes)
        {
            try
            {
                using var process = StartGeneratorProcess(runtime, text, outputFile);

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

                await process.WaitForExitAsync(cancellationToken);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode == 0)
                {
                    return;
                }

                lastFailure = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();

                if (IsMissingEdgeTts(lastFailure))
                {
                    var bootstrapResult = await EnsureEdgeTtsInstalledAsync(cancellationToken);
                    if (bootstrapResult.Success)
                    {
                        using var retryProcess = StartGeneratorProcess(runtime, text, outputFile);

                        var retryStdoutTask = retryProcess.StandardOutput.ReadToEndAsync(cancellationToken);
                        var retryStderrTask = retryProcess.StandardError.ReadToEndAsync(cancellationToken);

                        await retryProcess.WaitForExitAsync(cancellationToken);

                        var retryStdout = await retryStdoutTask;
                        var retryStderr = await retryStderrTask;

                        if (retryProcess.ExitCode == 0)
                        {
                            return;
                        }

                        lastFailure = string.IsNullOrWhiteSpace(retryStderr) ? retryStdout.Trim() : retryStderr.Trim();
                    }
                    else if (!string.IsNullOrWhiteSpace(bootstrapResult.Failure))
                    {
                        lastFailure = bootstrapResult.Failure;
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                lastFailure = $"Unable to start {runtime}.";
            }
        }

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(lastFailure)
            ? "Unable to generate speech with edge-tts."
            : lastFailure);
    }

    private static Process StartGeneratorProcess(string runtime, string text, string outputFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("edge_tts");
        startInfo.ArgumentList.Add("--voice");
        startInfo.ArgumentList.Add("en-US-AvaMultilingualNeural");
        startInfo.ArgumentList.Add("--text");
        startInfo.ArgumentList.Add(text);
        startInfo.ArgumentList.Add("--write-media");
        startInfo.ArgumentList.Add(outputFile);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {runtime}.");
    }

    private static bool IsMissingEdgeTts(string message) =>
        message.Contains("No module named edge_tts", StringComparison.OrdinalIgnoreCase);

    private static async Task<EdgeTtsBootstrapResult> EnsureEdgeTtsInstalledAsync(CancellationToken cancellationToken)
    {
        await BootstrapLock.WaitAsync(cancellationToken);
        try
        {
            if (_bootstrapSucceeded)
            {
                return new EdgeTtsBootstrapResult(true, string.Empty);
            }

            if (_bootstrapAttempted)
            {
                return new EdgeTtsBootstrapResult(false, _bootstrapFailure);
            }

            _bootstrapAttempted = true;
            AppLog.Info("edge_tts module missing. Attempting automatic install.");

            foreach (var runtime in PythonRuntimes)
            {
                try
                {
                    using var process = StartBootstrapProcess(runtime);

                    var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

                    await process.WaitForExitAsync(cancellationToken);

                    var stdout = await stdoutTask;
                    var stderr = await stderrTask;

                    if (process.ExitCode == 0)
                    {
                        _bootstrapSucceeded = true;
                        _bootstrapFailure = string.Empty;
                        AppLog.Info($"edge_tts installed successfully via {runtime}.");
                        return new EdgeTtsBootstrapResult(true, string.Empty);
                    }

                    _bootstrapFailure = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    _bootstrapFailure = $"Unable to start {runtime} for edge_tts installation.";
                }
            }

            if (string.IsNullOrWhiteSpace(_bootstrapFailure))
            {
                _bootstrapFailure = "Unable to install edge_tts automatically.";
            }

            AppLog.Error($"edge_tts automatic install failed: {_bootstrapFailure}");
            return new EdgeTtsBootstrapResult(false, _bootstrapFailure);
        }
        finally
        {
            BootstrapLock.Release();
        }
    }

    private static Process StartBootstrapProcess(string runtime)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runtime,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("pip");
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--user");
        startInfo.ArgumentList.Add("edge-tts");

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {runtime}.");
    }

    private Task PlayAudioAsync(string outputFile, CancellationToken cancellationToken)
    {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        var speechLevels = AnalyzeSpeechLevels(outputFile);
        using var levelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var levelTask = EmitSpeechLevelsAsync(speechLevels, levelCts.Token);

        EventHandler? endedHandler = null;
        EventHandler<ExceptionEventArgs>? failedHandler = null;

        void Cleanup()
        {
            if (completed)
            {
                return;
            }

            completed = true;
            levelCts.Cancel();
            _player.MediaEnded -= endedHandler;
            _player.MediaFailed -= failedHandler;
            _player.Stop();
            _player.Close();
            PublishSpeechLevel(0f);
        }

        endedHandler = (_, _) =>
        {
            Cleanup();
            completionSource.TrySetResult();
        };

        failedHandler = (_, args) =>
        {
            Cleanup();
            completionSource.TrySetException(args.ErrorException);
        };

        using var registration = cancellationToken.Register(() =>
        {
            Cleanup();
            completionSource.TrySetCanceled(cancellationToken);
        });

        _player.MediaEnded += endedHandler;
        _player.MediaFailed += failedHandler;
        _player.Open(new Uri(outputFile));
        _player.Play();

        return completionSource.Task;
    }

    private async Task EmitSpeechLevelsAsync(IReadOnlyList<float> speechLevels, CancellationToken cancellationToken)
    {
        if (speechLevels.Count == 0)
        {
            PublishSpeechLevel(0f);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var publishedIndex = -1;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var targetIndex = (int)(stopwatch.ElapsedMilliseconds / SpeechLevelWindowMs);
                if (targetIndex >= speechLevels.Count)
                {
                    break;
                }

                if (targetIndex != publishedIndex)
                {
                    publishedIndex = targetIndex;
                    PublishSpeechLevel(speechLevels[targetIndex]);
                }

                await Task.Delay(15, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private IReadOnlyList<float> AnalyzeSpeechLevels(string outputFile)
    {
        var levels = new List<float>();

        using var reader = new AudioFileReader(outputFile);
        var sampleRate = reader.WaveFormat.SampleRate;
        var channels = reader.WaveFormat.Channels;
        var samplesPerWindow = Math.Max(1, sampleRate * channels * SpeechLevelWindowMs / 1000);
        var buffer = new float[Math.Max(samplesPerWindow, 4096)];
        var maxAmplitude = 0f;
        var sampleCount = 0;

        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            for (var index = 0; index < read; index++)
            {
                maxAmplitude = Math.Max(maxAmplitude, Math.Abs(buffer[index]));
                sampleCount++;

                if (sampleCount < samplesPerWindow)
                {
                    continue;
                }

                levels.Add(Math.Clamp((float)Math.Sqrt(maxAmplitude), 0f, 1f));
                maxAmplitude = 0f;
                sampleCount = 0;
            }
        }

        if (sampleCount > 0)
        {
            levels.Add(Math.Clamp((float)Math.Sqrt(maxAmplitude), 0f, 1f));
        }

        return levels;
    }

    private void PublishSpeechLevel(float level) =>
        SpeechLevelChanged?.Invoke(this, new SpeechLevelChangedEventArgs(level));

    private static void TryDelete(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public sealed record EdgeTtsBootstrapResult(bool Success, string Failure);
}

public sealed class SpeechLevelChangedEventArgs(float level) : EventArgs
{
    public float Level { get; } = level;
}
