using System.Timers;
using DesktopCopilot.Skills;

namespace DesktopCopilot;

/// <summary>
/// Fires a full PC health report every morning at 9:00 AM.
/// </summary>
public sealed class DailyHealthReportService : IDisposable
{
    private const int ReportHour = 9;

    private readonly SkillRouter _skillRouter = new();
    private readonly System.Timers.Timer _timer = new() { AutoReset = false };
    private bool _disposed;

    public event EventHandler<string>? ReportReady;

    public DailyHealthReportService()
    {
        _timer.Elapsed += OnTimerElapsed;
        ScheduleNextRun();
    }

    public void RunNow()
    {
        AppLog.Info("DailyHealthReportService.RunNow: triggered manually.");
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var sections = new List<string>();

            foreach (var skill in _skillRouter.AllSkills)
            {
                try
                {
                    var data = await skill.RunAsync(cts.Token);
                    sections.Add($"[{skill.Name}] {data}");
                }
                catch (Exception ex)
                {
                    sections.Add($"[{skill.Name}] Error: {ex.Message}");
                }
            }

            ReportReady?.Invoke(this, string.Join("\n", sections));
        });
    }

    private void ScheduleNextRun()
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(ReportHour);
        if (now >= next)
            next = next.AddDays(1);

        var delay = (next - now).TotalMilliseconds;
        _timer.Interval = delay;
        _timer.Start();
        AppLog.Info($"DailyHealthReportService: next report at {next:yyyy-MM-dd HH:mm:ss} (in {TimeSpan.FromMilliseconds(delay):hh\\:mm\\:ss})");
    }

    private async void OnTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        AppLog.Info("DailyHealthReportService: collecting morning health report.");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var sections = new List<string>();

            foreach (var skill in _skillRouter.AllSkills)
            {
                try
                {
                    var data = await skill.RunAsync(cts.Token);
                    sections.Add($"[{skill.Name}] {data}");
                    AppLog.Info($"DailyHealthReportService: {skill.Name} = {data}");
                }
                catch (Exception ex)
                {
                    sections.Add($"[{skill.Name}] Error: {ex.Message}");
                    AppLog.Info($"DailyHealthReportService: {skill.Name} error: {ex.Message}");
                }
            }

            var aggregated = string.Join("\n", sections);
            ReportReady?.Invoke(this, aggregated);
        }
        catch (Exception ex)
        {
            AppLog.Error("DailyHealthReportService: failed to collect report.", ex);
        }
        finally
        {
            ScheduleNextRun();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }
}
