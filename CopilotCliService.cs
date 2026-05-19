using System.Diagnostics;
using System.Text;

namespace DesktopCopilot;

public sealed class CopilotCliService
{
    private const string SessionName = "DesktopCopilot Background Actor";
    private const string MissingSessionMarker = "No session, task, or name matched";
    private readonly CrashTriageService _crashTriageService = new();

    static CopilotCliService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<string> ExecuteFileDropAsync(string[] filePaths, CancellationToken cancellationToken)
    {
        AppLog.Info($"CopilotCliService.ExecuteFileDropAsync count={filePaths.Length}");
        var prompt = BuildFileDropPrompt(filePaths);
        var response = await RunCopilotAsync(prompt, useResume: true, cancellationToken);
        if (response.ExitCode == 0)
        {
            var normalized = NormalizeResponse(response.Stdout);
            AppLog.Info($"Copilot CLI file drop completed via resume. Response=\"{normalized}\"");
            return normalized;
        }

        if (!string.IsNullOrWhiteSpace(response.Stderr)
            && response.Stderr.Contains(MissingSessionMarker, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("Copilot CLI named session not found. Creating a new named session.");
            var firstRunResponse = await RunCopilotAsync(prompt, useResume: false, cancellationToken);
            if (firstRunResponse.ExitCode == 0)
            {
                var normalized = NormalizeResponse(firstRunResponse.Stdout);
                AppLog.Info($"Copilot CLI file drop completed via new named session. Response=\"{normalized}\"");
                return normalized;
            }

            ThrowFromResponse(firstRunResponse);
        }

        ThrowFromResponse(response);
        return string.Empty;
    }

    public async Task<string> ExecuteRequestAsync(string transcript, float? confidence, CancellationToken cancellationToken)
    {
        AppLog.Info($"CopilotCliService.ExecuteRequestAsync transcript=\"{transcript}\" confidence={confidence?.ToString("F2") ?? "n/a"}");
        var crashTriageContext = await BuildCrashTriageContextAsync(transcript, cancellationToken);
        var prompt = BuildPrompt(transcript, confidence, crashTriageContext);
        var response = await RunCopilotAsync(prompt, useResume: true, cancellationToken);
        if (response.ExitCode == 0)
        {
            var normalized = NormalizeResponse(response.Stdout);
            AppLog.Info($"Copilot CLI completed via resume. Response=\"{normalized}\"");
            return normalized;
        }

        if (!string.IsNullOrWhiteSpace(response.Stderr)
            && response.Stderr.Contains(MissingSessionMarker, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info("Copilot CLI named session not found. Creating a new named session.");
            var firstRunResponse = await RunCopilotAsync(prompt, useResume: false, cancellationToken);
            if (firstRunResponse.ExitCode == 0)
            {
                var normalized = NormalizeResponse(firstRunResponse.Stdout);
                AppLog.Info($"Copilot CLI completed via new named session. Response=\"{normalized}\"");
                return normalized;
            }

            ThrowFromResponse(firstRunResponse);
        }

        ThrowFromResponse(response);
        return string.Empty;
    }

    private async Task<string?> BuildCrashTriageContextAsync(string transcript, CancellationToken cancellationToken)
    {
        if (!_crashTriageService.IsCrashTriageRelevant(transcript))
        {
            return null;
        }

        AppLog.Info("Crash-related request detected. Collecting local crash triage context.");
        var report = await _crashTriageService.CaptureAsync(cancellationToken);
        return _crashTriageService.BuildPromptContext(report);
    }

    private static async Task<CopilotInvocationResult> RunCopilotAsync(
        string prompt,
        bool useResume,
        CancellationToken cancellationToken)
    {
        AppLog.Info($"Launching Copilot CLI useResume={useResume}");
        var startInfo = new ProcessStartInfo
        {
            FileName = "copilot",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(AppContext.BaseDirectory);
        startInfo.ArgumentList.Add("--allow-all");
        startInfo.ArgumentList.Add("-s");

        if (useResume)
        {
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(SessionName);
        }
        else
        {
            startInfo.ArgumentList.Add("--name");
            startInfo.ArgumentList.Add(SessionName);
        }

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(prompt);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start Copilot CLI.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new CopilotInvocationResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static string BuildFileDropPrompt(string[] filePaths)
    {
        var fileList = string.Join("\n", filePaths.Select(p => $"- {p}"));
        return $$"""
            You are the background Copilot actor behind a Windows 11 desktop widget.
            The user dropped the following file(s) onto the widget:

            {{fileList}}

            Read and inspect the file(s). If there is something actionable, do it.
            Otherwise summarize what you see in one or two short sentences suitable for speech.
            Do not include markdown fences, JSON, or bullet lists in your reply unless absolutely necessary.
            """;
    }

    private static string BuildPrompt(string transcript, float? confidence, string? crashTriageContext)
    {
        return $$"""
            You are the background Copilot actor behind a Windows 11 desktop maintenance widget.
            The widget is only a voice front end. You should directly do the work yourself using your available tools whenever the request calls for actions on the computer, files, shell, web, or GitHub.

            Recognized speech confidence:
            "{{confidence?.ToString("F2") ?? "unknown"}}"

            The recognized speech transcript was:
            "{{transcript}}"

            {{(string.IsNullOrWhiteSpace(crashTriageContext)
                ? "No extra local diagnostic context was collected for this request."
                : crashTriageContext)}}

            Operating rules:
            - Act directly instead of returning a plan.
            - Use tools as needed, including shell commands and file edits, to complete the request.
            - Because this input came from speech recognition, first judge whether the transcript is probably accurate enough to act on.
            - If the transcript seems garbled, ambiguous, or unsafe to execute, do not take actions; reply with one short sentence asking the user to repeat or clarify what they want.
            - If the request is ambiguous or unsafe to perform without more detail, do not guess; ask for the missing detail in one short sentence.
            - Keep your final user-facing response concise and suitable for speech, ideally one or two short sentences.
            - Do not include markdown fences, JSON, or bullet lists unless they are absolutely necessary.
            """;
    }

    private static string NormalizeResponse(string rawOutput)
    {
        var trimmed = FixCommonMojibake(rawOutput).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "Done.";
        }

        return trimmed;
    }

    private static string FixCommonMojibake(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || (!text.Contains('â') && !text.Contains('Ã')))
        {
            return text;
        }

        try
        {
            var windows1252 = Encoding.GetEncoding(1252);
            var utf8Bytes = windows1252.GetBytes(text);
            var repaired = Encoding.UTF8.GetString(utf8Bytes);
            return string.IsNullOrWhiteSpace(repaired) ? text : repaired;
        }
        catch
        {
            return text;
        }
    }

    private static void ThrowFromResponse(CopilotInvocationResult response)
    {
        AppLog.Error($"Copilot CLI failed exitCode={response.ExitCode} stderr=\"{response.Stderr}\"");
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(response.Stderr)
            ? "Copilot CLI exited with an error."
            : response.Stderr.Trim());
    }
}

public sealed record CopilotInvocationResult(int ExitCode, string Stdout, string Stderr);
