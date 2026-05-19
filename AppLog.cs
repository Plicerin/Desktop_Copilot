using System.IO;
using System.Text;

namespace DesktopCopilot;

public static class AppLog
{
    private static readonly object SyncRoot = new();
    private static string? _currentLogPath;

    public static string Initialize()
    {
        lock (SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(_currentLogPath))
            {
                return _currentLogPath;
            }

            var logsDirectory = GetLogsDirectory();
            Directory.CreateDirectory(logsDirectory);

            _currentLogPath = Path.Combine(
                logsDirectory,
                $"desktop-copilot-{DateTime.Now:yyyyMMdd-HHmmss}.log");

            WriteCore("INFO", "Logging initialized.");
            return _currentLogPath;
        }
    }

    public static string GetLogsDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopCopilot",
            "Logs");
    }

    public static void Info(string message)
    {
        WriteCore("INFO", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            WriteCore("ERROR", message);
            return;
        }

        WriteCore("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    private static void WriteCore(string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                var logPath = _currentLogPath ?? Initialize();
                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [")
                    .Append(level)
                    .Append("] ")
                    .Append(message)
                    .AppendLine()
                    .ToString();

                File.AppendAllText(logPath, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
