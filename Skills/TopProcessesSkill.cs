using System.Diagnostics;

namespace DesktopCopilot.Skills;

public sealed class TopProcessesSkill : ISkill
{
    public string Name => "Top Processes";
    public string[] Keywords =>
    [
        "what's eating my ram", "what is eating my ram", "eating my ram", "eating my cpu",
        "top processes", "what's using my memory", "what is using my memory",
        "memory hog", "cpu hog", "hungry process", "process list",
        "check processes", "check top processes", "what's running",
    ];

    public Task<string> RunAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var all = Process.GetProcesses();

            static long SafeWorkingSet(Process p) { try { return p.WorkingSet64; } catch { return 0; } }
            static double SafeCpuMs(Process p) { try { return p.TotalProcessorTime.TotalMilliseconds; } catch { return 0; } }

            var byMemory = all
                .Select(p => (p.ProcessName, Mb: SafeWorkingSet(p) / 1_048_576.0))
                .OrderByDescending(x => x.Mb)
                .Take(5)
                .Select(x => $"{x.ProcessName} ({x.Mb:F0} MB)");

            var byCpu = all
                .Select(p => (p.ProcessName, Ms: SafeCpuMs(p)))
                .Where(x => x.Ms > 0)
                .OrderByDescending(x => x.Ms)
                .Take(5)
                .Select(x => $"{x.ProcessName} ({x.Ms / 1000.0:F1}s CPU)");

            return $"Top 5 by memory: {string.Join(", ", byMemory)}. " +
                   $"Top 5 by CPU time: {string.Join(", ", byCpu)}.";
        }, cancellationToken);
}
