using System.Text.RegularExpressions;
using System.Timers;

namespace DesktopCopilot.Skills;

public sealed class ReminderSkill : IActionSkill
{
    private static readonly string[] Keywords =
        ["remind me", "set a reminder", "set reminder", "reminder in", "remind me in", "alert me in"];

    private readonly Func<string, Task> _speak;

    public ReminderSkill(Func<string, Task> speak)
    {
        _speak = speak;
    }

    public bool TryMatch(string transcript)
    {
        var lower = transcript.ToLowerInvariant();
        return Keywords.Any(k => lower.Contains(k));
    }

    public Task<string> ExecuteAsync(string transcript, CancellationToken cancellationToken)
    {
        var minutes = ParseMinutes(transcript);
        if (minutes <= 0)
            return Task.FromResult("I didn't catch how long to wait. Try saying 'remind me in 10 minutes'.");

        var label = minutes == 1 ? "1 minute" : $"{minutes} minutes";
        AppLog.Info($"ReminderSkill: setting reminder for {minutes} min.");

        var timer = new System.Timers.Timer(minutes * 60_000.0) { AutoReset = false };
        timer.Elapsed += async (_, _) =>
        {
            AppLog.Info("ReminderSkill: reminder fired.");
            await _speak($"Reminder! {minutes} {(minutes == 1 ? "minute has" : "minutes have")} passed.");
            timer.Dispose();
        };
        timer.Start();

        return Task.FromResult($"Got it. I'll remind you in {label}.");
    }

    private static int ParseMinutes(string transcript)
    {
        var lower = transcript.ToLowerInvariant();

        // "in an hour" / "in 1 hour" / "in 2 hours"
        var hourMatch = Regex.Match(lower, @"in\s+(\d+|an?)\s+hours?");
        if (hourMatch.Success)
        {
            var raw = hourMatch.Groups[1].Value;
            if (raw is "a" or "an") return 60;
            if (int.TryParse(raw, out var h)) return h * 60;
        }

        // "in 30 minutes" / "in a minute"
        var minMatch = Regex.Match(lower, @"in\s+(\d+|an?|a\s+few)\s+minutes?");
        if (minMatch.Success)
        {
            var raw = minMatch.Groups[1].Value.Trim();
            if (raw is "a" or "an") return 1;
            if (raw == "a few") return 3;
            if (int.TryParse(raw, out var m)) return m;
        }

        // "in 10" (bare number)
        var bareMatch = Regex.Match(lower, @"in\s+(\d+)");
        if (bareMatch.Success && int.TryParse(bareMatch.Groups[1].Value, out var bare))
            return bare;

        return 0;
    }
}
