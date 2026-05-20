using System.Diagnostics;

namespace DesktopCopilot.Skills;

public sealed class EventLogSkill : ISkill
{
    public string Name => "Event Log";
    public string[] Keywords =>
    [
        "event log", "errors", "system errors", "recent errors", "warnings", "recent warnings",
        "check event log", "check errors", "check warnings", "what went wrong", "system warnings",
        "application errors",
    ];

    private static readonly string[] LogNames = ["System", "Application"];

    public Task<string> RunAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var since = DateTime.Now.AddHours(-24);
            var errorCount = 0;
            var warningCount = 0;
            var topErrors = new List<string>();

            foreach (var logName in LogNames)
            {
                try
                {
                    using var log = new EventLog(logName);
                    // Scan newest entries first; stop after 500 to cap scan time
                    var entries = log.Entries.Cast<EventLogEntry>()
                        .Reverse()
                        .TakeWhile(e => e.TimeGenerated >= since)
                        .Take(500)
                        .ToList();

                    foreach (var e in entries)
                    {
                        if (e.EntryType == EventLogEntryType.Error)
                        {
                            errorCount++;
                            if (topErrors.Count < 3)
                            {
                                var msg = e.Message.Split('\n')[0].Trim();
                                if (msg.Length > 80) msg = msg[..80] + "…";
                                topErrors.Add($"[{logName}] {e.Source}: {msg}");
                            }
                        }
                        else if (e.EntryType == EventLogEntryType.Warning)
                        {
                            warningCount++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Info($"EventLogSkill: error reading {logName}: {ex.Message}");
                }
            }

            var summary = $"Last 24 hours — Errors: {errorCount}, Warnings: {warningCount}.";
            if (topErrors.Count > 0)
                summary += " Top errors: " + string.Join(" | ", topErrors);
            return summary;
        }, cancellationToken);
}
