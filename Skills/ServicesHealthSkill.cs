using System.Management;

namespace DesktopCopilot.Skills;

public sealed class ServicesHealthSkill : ISkill
{
    public string Name => "Services Health";
    public string[] Keywords =>
    [
        "services", "running services", "service health", "windows services",
        "check services", "stopped services", "failed services",
    ];

    public Task<string> RunAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            try
            {
                // Find automatic-start services that are currently stopped
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DisplayName, Name, State, StartMode FROM Win32_Service " +
                    "WHERE StartMode = 'Auto' AND State != 'Running'");

                var stopped = new List<string>();
                foreach (ManagementObject obj in searcher.Get())
                {
                    var display = obj["DisplayName"]?.ToString() ?? obj["Name"]?.ToString() ?? "Unknown";
                    var state = obj["State"]?.ToString() ?? "Unknown";
                    stopped.Add($"{display} ({state})");
                }

                // Also get a total count of running services
                using var runningSearcher = new ManagementObjectSearcher(
                    "SELECT COUNT(*) FROM Win32_Service WHERE State = 'Running'");
                var runningCount = 0;
                foreach (ManagementObject obj in runningSearcher.Get())
                {
                    runningCount = Convert.ToInt32(obj.Properties["__COUNT"]?.Value ?? 0);
                }

                if (stopped.Count == 0)
                    return $"All automatic services are running. Total running services: {runningCount}.";

                return $"{runningCount} services running. " +
                       $"{stopped.Count} automatic service(s) not running: {string.Join(", ", stopped.Take(10))}.";
            }
            catch (Exception ex)
            {
                AppLog.Info($"ServicesHealthSkill error: {ex.Message}");
                return $"Unable to query service health: {ex.Message}";
            }
        }, cancellationToken);
}
