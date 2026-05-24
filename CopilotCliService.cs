using System.Diagnostics;
using System.Text;

namespace DesktopCopilot;

public sealed class CopilotCliService
{
    private const string SessionName = "DesktopCopilot Background Actor";
    private readonly CrashTriageService _crashTriageService = new();
    private bool _sessionExists;

    static CopilotCliService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// Pre-warms the named Copilot CLI session at startup so the first real request
    /// hits --resume instead of paying for session creation latency.
    /// </summary>
    public async Task WarmUpAsync()
    {
        AppLog.Info("CopilotCliService.WarmUpAsync: pre-warming named session.");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var warmup = await RunCopilotAsync("Reply with only the word OK.", useResume: false, cts.Token);
            _sessionExists = warmup.ExitCode == 0;
            AppLog.Info($"CopilotCliService.WarmUpAsync: done sessionExists={_sessionExists}");
        }
        catch (Exception ex)
        {
            AppLog.Info($"CopilotCliService.WarmUpAsync: failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a prompt through the named session. Uses --resume when the session is known
    /// to exist, and falls back to session creation on a miss — tracking state to avoid
    /// paying the double round-trip cost on subsequent calls.
    /// </summary>
    private async Task<string> InvokeWithSessionAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_sessionExists)
        {
            var resumeResponse = await RunCopilotAsync(prompt, useResume: true, cancellationToken);
            if (resumeResponse.ExitCode == 0)
                return NormalizeResponse(resumeResponse.Stdout);

            // In non-interactive mode the CLI can fail resume with exit code 1 and no stderr
            // even when the original named session was created successfully. Recreate the
            // session instead of surfacing the transient resume failure to the user.
            AppLog.Info($"Copilot CLI resume failed. Recreating named session. exitCode={resumeResponse.ExitCode} stderr=\"{resumeResponse.Stderr}\"");
            _sessionExists = false;
        }

        AppLog.Info($"Copilot CLI: creating new named session. sessionWasKnown={_sessionExists}");
        var newResponse = await RunCopilotAsync(prompt, useResume: false, cancellationToken);
        if (newResponse.ExitCode == 0)
        {
            _sessionExists = true;
            return NormalizeResponse(newResponse.Stdout);
        }

        ThrowFromResponse(newResponse);
        return string.Empty;
    }

    public async Task<string> ExecuteFileDropAsync(string[] filePaths, CancellationToken cancellationToken)
    {
        AppLog.Info($"CopilotCliService.ExecuteFileDropAsync count={filePaths.Length}");
        var prompt = BuildFileDropPrompt(filePaths);
        return await InvokeWithSessionAsync(prompt, cancellationToken);
    }

    public async Task<string> ExecuteDailyReportAsync(string aggregatedData, CancellationToken cancellationToken)
    {
        AppLog.Info("CopilotCliService.ExecuteDailyReportAsync");
        return await InvokeWithSessionAsync(BuildDailyReportPrompt(aggregatedData), cancellationToken);
    }

    public async Task<string> ExecuteSkillAsync(string skillName, string rawData, CancellationToken cancellationToken)
    {
        AppLog.Info($"CopilotCliService.ExecuteSkillAsync skill=\"{skillName}\"");
        return await InvokeWithSessionAsync(BuildSkillPrompt(skillName, rawData), cancellationToken);
    }

    public async Task<string> ExecuteRequestAsync(string transcript, float? confidence, CancellationToken cancellationToken)
    {
        AppLog.Info($"CopilotCliService.ExecuteRequestAsync transcript=\"{transcript}\" confidence={confidence?.ToString("F2") ?? "n/a"}");
        var crashTriageContext = await BuildCrashTriageContextAsync(transcript, cancellationToken);
        var prompt = BuildPrompt(transcript, confidence, crashTriageContext);
        return await InvokeWithSessionAsync(prompt, cancellationToken);
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

    private static string BuildDailyReportPrompt(string aggregatedData)
    {
        return $$"""
            You are the background Copilot actor behind a Windows 11 desktop maintenance widget.
            It is 9:00 AM and you are delivering the daily PC health report. Here is the raw data collected from all health checks:

            {{aggregatedData}}

            Give a concise spoken morning briefing suitable for text-to-speech. Cover each area in one short sentence.
            Lead with "Good morning" and end with an overall verdict: healthy, needs attention, or has issues.
            Do not include markdown fences, JSON, or bullet lists.
            """;
    }

    private static string BuildSkillPrompt(string skillName, string rawData)
    {
        return $$"""
            You are the background Copilot actor behind a Windows 11 desktop maintenance widget.
            A PC health skill named "{{skillName}}" just ran and collected the following raw data:

            {{rawData}}

            Summarize this data in one or two short sentences suitable for speech. Be direct and specific.
            Mention anything that looks unhealthy or worth the user's attention.
            Do not include markdown fences, JSON, or bullet lists.
            """;
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
