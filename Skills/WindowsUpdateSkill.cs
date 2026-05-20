using System.Management;

namespace DesktopCopilot.Skills;

public sealed class WindowsUpdateSkill : ISkill
{
    public string Name => "Windows Update";
    public string[] Keywords => ["updates", "windows update", "update", "pending updates", "available updates"];

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                // Query installed patches via WMI (works without elevation)
                using var searcher = new ManagementObjectSearcher(
                    @"root\cimv2",
                    "SELECT * FROM Win32_QuickFixEngineering");

                var patches = searcher.Get().Cast<ManagementObject>().ToList();
                var recent = patches
                    .Where(p =>
                    {
                        var installed = p["InstalledOn"]?.ToString();
                        if (DateTime.TryParse(installed, out var d))
                            return d >= DateTime.Now.AddDays(-30);
                        return false;
                    })
                    .Count();

                return $"Total hotfixes/patches installed: {patches.Count}. " +
                       $"Installed in last 30 days: {recent}. " +
                       "Note: for pending updates, Windows Update service must be checked separately.";
            }
            catch (Exception ex)
            {
                AppLog.Info($"WindowsUpdateSkill error: {ex.Message}");
                return $"Unable to query Windows Update information: {ex.Message}";
            }
        }, cancellationToken);
    }
}
