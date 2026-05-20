using System.IO;

namespace DesktopCopilot.Skills;

public sealed class DiskSpaceSkill : ISkill
{
    public string Name => "Disk Space";
    public string[] Keywords => ["disk", "drive", "hard drive", "storage", "space", "disk space"];

    public Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d =>
            {
                var totalGb = d.TotalSize / 1_073_741_824.0;
                var freeGb = d.AvailableFreeSpace / 1_073_741_824.0;
                var usedPct = (1 - freeGb / totalGb) * 100;
                return $"Drive {d.Name.TrimEnd('\\')} — {freeGb:F1} GB free of {totalGb:F1} GB ({usedPct:F0}% used)";
            });

        var result = string.Join("; ", drives);
        return Task.FromResult(string.IsNullOrWhiteSpace(result) ? "No fixed drives found." : result);
    }
}
