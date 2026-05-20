using System.Management;

namespace DesktopCopilot.Skills;

public sealed class SmartDiskSkill : ISkill
{
    public string Name => "SMART Disk Health";
    public string[] Keywords =>
    [
        "smart", "disk health", "drive health", "hard drive health", "ssd health",
        "disk failure", "drive failure", "disk status", "drive status",
        "check smart", "check disk health", "check drive health",
        "is my drive failing", "is my disk failing",
    ];

    public Task<string> RunAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var results = new List<string>();

            // Basic WMI drive status (always available)
            try
            {
                using var driveSearcher = new ManagementObjectSearcher(
                    "SELECT Model, Status, Size, MediaType FROM Win32_DiskDrive");
                foreach (ManagementObject obj in driveSearcher.Get())
                {
                    var model = obj["Model"]?.ToString() ?? "Unknown drive";
                    var status = obj["Status"]?.ToString() ?? "Unknown";
                    var sizeBytes = obj["Size"] is null ? 0L : Convert.ToInt64(obj["Size"]);
                    var sizeGb = sizeBytes / 1_073_741_824.0;
                    results.Add($"{model} ({sizeGb:F0} GB) — Status: {status}");
                }
            }
            catch (Exception ex)
            {
                results.Add($"Drive query error: {ex.Message}");
            }

            // SMART failure prediction via MSStorageDriver_FailurePredictStatus (root\wmi)
            var smartLines = new List<string>();
            try
            {
                using var smartSearcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT InstanceName, PredictFailure FROM MSStorageDriver_FailurePredictStatus");
                foreach (ManagementObject obj in smartSearcher.Get())
                {
                    var instance = obj["InstanceName"]?.ToString() ?? "Unknown";
                    var predict = obj["PredictFailure"] is bool b && b;
                    // InstanceName is verbose (SCSI\DISK&...) — shorten it
                    var label = instance.Length > 40 ? instance[..40] + "…" : instance;
                    smartLines.Add(predict
                        ? $"⚠ SMART PREDICTS FAILURE: {label}"
                        : $"SMART OK: {label}");
                }
            }
            catch
            {
                // SMART WMI requires admin or specific driver support; not always available
                smartLines.Add("SMART data not available (may require admin privileges).");
            }

            if (smartLines.Count > 0)
                results.AddRange(smartLines);

            return results.Count > 0
                ? string.Join("; ", results)
                : "No disk drives found.";
        }, cancellationToken);
}
