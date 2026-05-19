using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Security;
using System.Text;

namespace DesktopCopilot;

public sealed class CrashTriageService
{
    private static readonly string[] CrashKeywords =
    {
        "crash",
        "crashed",
        "crashing",
        "freeze",
        "frozen",
        "hang",
        "hung",
        "not responding",
        "stopped working",
        "blue screen",
        "bsod",
        "bugcheck",
        "restarted unexpectedly"
    };

    private static readonly HashSet<string> PreferredApplicationProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Application Error",
        "Application Hang",
        ".NET Runtime",
        "Windows Error Reporting"
    };

    private static readonly HashSet<string> PreferredSystemProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "BugCheck",
        "volmgr",
        "WHEA-Logger",
        "Display",
        "Kernel-Power",
        "EventLog"
    };

    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(72);
    private const int MaxEventCount = 12;
    private const int MaxDumpCount = 6;
    private const int MaxWerCount = 6;

    public bool IsCrashTriageRelevant(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return false;
        }

        return CrashKeywords.Any(keyword => transcript.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    public Task<CrashTriageReport> CaptureAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => Capture(cancellationToken), cancellationToken);
    }

    public string BuildPromptContext(CrashTriageReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Local crash triage context from this PC:");
        builder.AppendLine($"- Capture time: {report.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- Inspection window: last {(int)report.InspectionWindow.TotalHours} hours");
        builder.AppendLine($"- Recent crash/error events found: {report.Events.Count}");
        builder.AppendLine($"- Recent dump files found: {report.DumpFiles.Count}");
        builder.AppendLine($"- Recent Windows error reports found: {report.WerReports.Count}");

        if (report.Events.Count > 0)
        {
            builder.AppendLine("Recent event log signals:");
            foreach (var entry in report.Events)
            {
                builder.AppendLine($"- [{entry.TimeCreated.LocalDateTime:MM-dd HH:mm}] {entry.LogName}/{entry.ProviderName} id {entry.EventId}: {entry.Message}");
            }
        }

        if (report.DumpFiles.Count > 0)
        {
            builder.AppendLine("Recent dump files:");
            foreach (var dump in report.DumpFiles)
            {
                builder.AppendLine($"- [{dump.LastWriteTime.LocalDateTime:MM-dd HH:mm}] {dump.Path} ({FormatBytes(dump.LengthBytes)})");
            }
        }

        if (report.WerReports.Count > 0)
        {
            builder.AppendLine("Recent Windows error reports:");
            foreach (var wer in report.WerReports)
            {
                builder.AppendLine($"- [{wer.LastWriteTime.LocalDateTime:MM-dd HH:mm}] {wer.ReportName}: {wer.EventType} / {wer.AppName}");
            }
        }

        if (report.CollectionWarnings.Count > 0)
        {
            builder.AppendLine("Collection warnings:");
            foreach (var warning in report.CollectionWarnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        builder.AppendLine("Use this context only if it matches the user's crash or instability issue.");
        return builder.ToString().TrimEnd();
    }

    public string BuildSnapshotSummary(CrashTriageReport report)
    {
        return $"Found {report.Events.Count} recent crash events, {report.DumpFiles.Count} dump files, and {report.WerReports.Count} Windows error reports.";
    }

    public string SaveReport(CrashTriageReport report)
    {
        var logsDirectory = AppLog.GetLogsDirectory();
        Directory.CreateDirectory(logsDirectory);

        var reportPath = Path.Combine(
            logsDirectory,
            $"crash-triage-{report.CapturedAt:yyyyMMdd-HHmmss}.txt");

        File.WriteAllText(reportPath, BuildFullReport(report));
        return reportPath;
    }

    private CrashTriageReport Capture(CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var events = new List<CrashTriageEvent>();
        events.AddRange(ReadRecentEvents("Application", PreferredApplicationProviders, warnings, cancellationToken));
        events.AddRange(ReadRecentEvents("System", PreferredSystemProviders, warnings, cancellationToken));

        var dumps = new List<CrashDumpFile>();
        dumps.AddRange(ReadDumpFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump"), warnings, cancellationToken));
        dumps.AddRange(ReadDumpFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "LiveKernelReports"), warnings, cancellationToken));

        var werReports = new List<CrashWerReport>();
        werReports.AddRange(ReadWerReports(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportArchive"), warnings, cancellationToken));
        werReports.AddRange(ReadWerReports(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "WER", "ReportQueue"), warnings, cancellationToken));

        return new CrashTriageReport(
            CapturedAt: DateTimeOffset.Now,
            InspectionWindow: RecentWindow,
            Events: events
                .OrderByDescending(entry => entry.TimeCreated)
                .Take(MaxEventCount)
                .ToArray(),
            DumpFiles: dumps
                .OrderByDescending(dump => dump.LastWriteTime)
                .Take(MaxDumpCount)
                .ToArray(),
            WerReports: werReports
                .OrderByDescending(report => report.LastWriteTime)
                .Take(MaxWerCount)
                .ToArray(),
            CollectionWarnings: warnings);
    }

    private static IEnumerable<CrashTriageEvent> ReadRecentEvents(
        string logName,
        IReadOnlySet<string> preferredProviders,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var results = new List<CrashTriageEvent>();
        var windowMilliseconds = (long)RecentWindow.TotalMilliseconds;
        var queryText = $"*[System[(Level=1 or Level=2) and TimeCreated[timediff(@SystemTime) <= {windowMilliseconds}]]]";

        try
        {
            var query = new EventLogQuery(logName, PathType.LogName, queryText)
            {
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);
            for (EventRecord? record = reader.ReadEvent(); record is not null && results.Count < MaxEventCount; record = reader.ReadEvent())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (record)
                {
                    var message = CollapseWhitespace(SafeFormatDescription(record));
                    if (!ShouldIncludeEvent(record, message, preferredProviders))
                    {
                        continue;
                    }

                    results.Add(new CrashTriageEvent(
                        LogName: logName,
                        ProviderName: record.ProviderName ?? "Unknown",
                        EventId: record.Id,
                        Level: record.LevelDisplayName ?? record.Level?.ToString() ?? "Error",
                        TimeCreated: record.TimeCreated is { } timeCreated
                            ? new DateTimeOffset(timeCreated)
                            : DateTimeOffset.MinValue,
                        Message: Truncate(message, 220)));
                }
            }
        }
        catch (EventLogNotFoundException)
        {
            warnings.Add($"{logName} event log was not found.");
        }
        catch (EventLogException exception)
        {
            warnings.Add($"Couldn't read {logName} event log: {exception.Message}");
        }
        catch (SecurityException exception)
        {
            warnings.Add($"Permission denied reading {logName} event log: {exception.Message}");
        }

        return results;
    }

    private static IEnumerable<CrashDumpFile> ReadDumpFiles(
        string directoryPath,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var results = new List<CrashDumpFile>();

        if (!Directory.Exists(directoryPath))
        {
            return results;
        }

        try
        {
            foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*.dmp", SearchOption.AllDirectories)
                         .Select(path => new FileInfo(path))
                         .OrderByDescending(file => file.LastWriteTimeUtc)
                         .Take(MaxDumpCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(new CrashDumpFile(
                    filePath.FullName,
                    new DateTimeOffset(filePath.LastWriteTimeUtc),
                    filePath.Length));
            }
        }
        catch (IOException exception)
        {
            warnings.Add($"Couldn't read dump files in {directoryPath}: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            warnings.Add($"Permission denied reading dump files in {directoryPath}: {exception.Message}");
        }
        catch (SecurityException exception)
        {
            warnings.Add($"Permission denied reading dump files in {directoryPath}: {exception.Message}");
        }

        return results;
    }

    private static IEnumerable<CrashWerReport> ReadWerReports(
        string directoryPath,
        ICollection<string> warnings,
        CancellationToken cancellationToken)
    {
        var results = new List<CrashWerReport>();

        if (!Directory.Exists(directoryPath))
        {
            return results;
        }

        try
        {
            foreach (var reportDirectory in new DirectoryInfo(directoryPath)
                         .EnumerateDirectories()
                         .OrderByDescending(directory => directory.LastWriteTimeUtc)
                         .Take(MaxWerCount))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reportFilePath = Path.Combine(reportDirectory.FullName, "Report.wer");
                if (!File.Exists(reportFilePath))
                {
                    continue;
                }

                var metadata = ParseWerMetadata(reportFilePath);
                results.Add(new CrashWerReport(
                    ReportName: reportDirectory.Name,
                    LastWriteTime: new DateTimeOffset(reportDirectory.LastWriteTimeUtc),
                    EventType: metadata.TryGetValue("FriendlyEventName", out var eventType)
                        ? eventType
                        : GetMetadataValue(metadata, "EventType", "Unknown"),
                    AppName: GetMetadataValue(metadata, "AppName", GetMetadataValue(metadata, "AppPath", "Unknown app")),
                    AppPath: GetMetadataValue(metadata, "AppPath", "Unknown path"),
                    ReportPath: reportFilePath));
            }
        }
        catch (IOException exception)
        {
            warnings.Add($"Couldn't read WER reports in {directoryPath}: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            warnings.Add($"Permission denied reading WER reports in {directoryPath}: {exception.Message}");
        }
        catch (SecurityException exception)
        {
            warnings.Add($"Permission denied reading WER reports in {directoryPath}: {exception.Message}");
        }

        return results;
    }

    private static Dictionary<string, string> ParseWerMetadata(string reportFilePath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(reportFilePath))
        {
            if (string.IsNullOrWhiteSpace(line) || line[0] == '[')
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static bool ShouldIncludeEvent(EventRecord record, string message, IReadOnlySet<string> preferredProviders)
    {
        if (!string.IsNullOrWhiteSpace(record.ProviderName) && preferredProviders.Contains(record.ProviderName))
        {
            return true;
        }

        if (record.Level == 1)
        {
            return true;
        }

        return CrashKeywords.Any(keyword => message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeFormatDescription(EventRecord record)
    {
        try
        {
            return record.FormatDescription() ?? record.ToXml() ?? $"Event {record.Id}";
        }
        catch (EventLogException)
        {
            return record.ToXml() ?? $"Event {record.Id}";
        }
    }

    private static string GetMetadataValue(IReadOnlyDictionary<string, string> metadata, string key, string fallback)
    {
        return metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static string BuildFullReport(CrashTriageReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Desktop Copilot Crash Triage Report");
        builder.AppendLine($"Captured: {report.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Window: last {(int)report.InspectionWindow.TotalHours} hours");
        builder.AppendLine();

        builder.AppendLine("Recent crash/error events");
        if (report.Events.Count == 0)
        {
            builder.AppendLine("- None found");
        }
        else
        {
            foreach (var entry in report.Events)
            {
                builder.AppendLine($"- [{entry.TimeCreated.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {entry.LogName}/{entry.ProviderName} id {entry.EventId} ({entry.Level})");
                builder.AppendLine($"  {entry.Message}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent dump files");
        if (report.DumpFiles.Count == 0)
        {
            builder.AppendLine("- None found");
        }
        else
        {
            foreach (var dump in report.DumpFiles)
            {
                builder.AppendLine($"- [{dump.LastWriteTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {dump.Path} ({FormatBytes(dump.LengthBytes)})");
            }
        }

        builder.AppendLine();
        builder.AppendLine("Recent Windows error reports");
        if (report.WerReports.Count == 0)
        {
            builder.AppendLine("- None found");
        }
        else
        {
            foreach (var reportEntry in report.WerReports)
            {
                builder.AppendLine($"- [{reportEntry.LastWriteTime.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {reportEntry.EventType} - {reportEntry.AppName}");
                builder.AppendLine($"  App path: {reportEntry.AppPath}");
                builder.AppendLine($"  Report path: {reportEntry.ReportPath}");
            }
        }

        if (report.CollectionWarnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Collection warnings");
            foreach (var warning in report.CollectionWarnings)
            {
                builder.AppendLine($"- {warning}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string CollapseWhitespace(string value)
    {
        return string.Join(" ", value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength
            ? value
            : $"{value[..(maxLength - 3)]}...";
    }

    private static string FormatBytes(long length)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = length;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.#} {units[unitIndex]}";
    }
}

public sealed record CrashTriageReport(
    DateTimeOffset CapturedAt,
    TimeSpan InspectionWindow,
    IReadOnlyList<CrashTriageEvent> Events,
    IReadOnlyList<CrashDumpFile> DumpFiles,
    IReadOnlyList<CrashWerReport> WerReports,
    IReadOnlyList<string> CollectionWarnings);

public sealed record CrashTriageEvent(
    string LogName,
    string ProviderName,
    int EventId,
    string Level,
    DateTimeOffset TimeCreated,
    string Message);

public sealed record CrashDumpFile(
    string Path,
    DateTimeOffset LastWriteTime,
    long LengthBytes);

public sealed record CrashWerReport(
    string ReportName,
    DateTimeOffset LastWriteTime,
    string EventType,
    string AppName,
    string AppPath,
    string ReportPath);
