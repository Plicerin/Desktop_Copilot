using System.Diagnostics;

namespace DesktopCopilot.Skills;

public sealed class OpenAppSkill : IActionSkill
{
    private static readonly string[] Triggers = ["open ", "launch ", "start "];

    // Common spoken names → executable/URI
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calculator"] = "calc",
        ["calc"] = "calc",
        ["notepad"] = "notepad",
        ["paint"] = "mspaint",
        ["file explorer"] = "explorer",
        ["explorer"] = "explorer",
        ["task manager"] = "taskmgr",
        ["control panel"] = "control",
        ["settings"] = "ms-settings:",
        ["camera"] = "microsoft.windows.camera:",
        ["store"] = "ms-windows-store:",
        ["teams"] = "msteams:",
        ["outlook"] = "outlook",
        ["word"] = "winword",
        ["excel"] = "excel",
        ["powerpoint"] = "powerpnt",
        ["chrome"] = "chrome",
        ["edge"] = "msedge",
        ["firefox"] = "firefox",
        ["spotify"] = "spotify",
        ["discord"] = "discord",
        ["slack"] = "slack",
        ["terminal"] = "wt",
        ["command prompt"] = "cmd",
        ["powershell"] = "powershell",
        ["snipping tool"] = "snippingtool",
        ["clock"] = "ms-clock:",
        ["weather"] = "bingweather:",
        ["maps"] = "bingmaps:",
    };

    public bool TryMatch(string transcript)
    {
        var lower = transcript.ToLowerInvariant();
        return Triggers.Any(t => lower.Contains(t));
    }

    public Task<string> ExecuteAsync(string transcript, CancellationToken cancellationToken)
    {
        var lower = transcript.ToLowerInvariant();
        string? appToken = null;

        foreach (var trigger in Triggers)
        {
            var idx = lower.IndexOf(trigger, StringComparison.Ordinal);
            if (idx >= 0)
            {
                appToken = transcript[(idx + trigger.Length)..].Trim().TrimEnd('.', '!', '?');
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(appToken))
            return Task.FromResult("I didn't catch which app to open.");

        var target = Aliases.TryGetValue(appToken, out var alias) ? alias : appToken;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
            AppLog.Info($"OpenAppSkill: launched \"{target}\" (from \"{appToken}\")");
            return Task.FromResult($"Opening {appToken}.");
        }
        catch (Exception ex)
        {
            AppLog.Info($"OpenAppSkill: failed to open \"{target}\": {ex.Message}");
            return Task.FromResult($"I couldn't open {appToken}.");
        }
    }
}
