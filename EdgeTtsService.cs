using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace DesktopCopilot;

public sealed class EdgeTtsService : IDisposable
{
    private readonly MediaPlayer _player = new();
    private bool _disposed;

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

        foreach (var runtime in new[] { "python", "py" })
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

    private Task PlayAudioAsync(string outputFile, CancellationToken cancellationToken)
    {
        var completionSource = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;

        EventHandler? endedHandler = null;
        EventHandler<ExceptionEventArgs>? failedHandler = null;

        void Cleanup()
        {
            if (completed)
            {
                return;
            }

            completed = true;
            _player.MediaEnded -= endedHandler;
            _player.MediaFailed -= failedHandler;
            _player.Stop();
            _player.Close();
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
}
