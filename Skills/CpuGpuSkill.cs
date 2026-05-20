using System.Diagnostics;
using System.Management;

namespace DesktopCopilot.Skills;

public sealed class CpuGpuSkill : ISkill
{
    public string Name => "CPU and GPU";
    public string[] Keywords =>
    [
        "cpu", "gpu", "processor", "graphics card", "temperature", "cpu usage", "gpu usage",
        "cpu temperature", "gpu temperature", "check cpu", "check gpu", "check temperature",
        "check processor", "how hot", "overheating",
    ];

    public Task<string> RunAsync(CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            var parts = new List<string>();

            // CPU load + clock speed
            try
            {
                using var cpuSearcher = new ManagementObjectSearcher(
                    "SELECT Name, LoadPercentage, CurrentClockSpeed FROM Win32_Processor");
                foreach (ManagementObject obj in cpuSearcher.Get())
                {
                    var name = obj["Name"]?.ToString()?.Trim() ?? "Unknown CPU";
                    var load = obj["LoadPercentage"];
                    var speed = obj["CurrentClockSpeed"];
                    parts.Add($"CPU: {name} — Load: {load}%, Clock: {speed} MHz");
                }
            }
            catch (Exception ex) { parts.Add($"CPU query error: {ex.Message}"); }

            // Thermal zones
            try
            {
                using var thermalSearcher = new ManagementObjectSearcher(
                    "root\\wmi", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
                var temps = new List<string>();
                foreach (ManagementObject obj in thermalSearcher.Get())
                {
                    var raw = Convert.ToDouble(obj["CurrentTemperature"]);
                    var c = raw / 10.0 - 273.15;
                    temps.Add($"{c:F0}°C");
                }
                if (temps.Count > 0)
                    parts.Add($"Thermal zones: {string.Join(", ", temps)}");
            }
            catch { /* thermal WMI not available on all systems */ }

            // GPU — try nvidia-smi first, fall back to WMI adapter info
            var nvidiaLine = TryNvidiaSmi();
            if (!string.IsNullOrWhiteSpace(nvidiaLine))
            {
                parts.Add($"GPU (NVIDIA): {nvidiaLine}");
            }
            else
            {
                try
                {
                    using var gpuSearcher = new ManagementObjectSearcher(
                        "SELECT Name, AdapterRAM, CurrentRefreshRate FROM Win32_VideoController WHERE AdapterRAM > 0");
                    foreach (ManagementObject obj in gpuSearcher.Get())
                    {
                        var name = obj["Name"]?.ToString() ?? "Unknown GPU";
                        var ramBytes = obj["AdapterRAM"] is null ? 0L : Convert.ToInt64(obj["AdapterRAM"]);
                        var ramMb = ramBytes > 0 ? $"{ramBytes / 1_048_576} MB VRAM" : "VRAM unknown";
                        parts.Add($"GPU: {name} ({ramMb})");
                    }
                }
                catch (Exception ex) { parts.Add($"GPU query error: {ex.Message}"); }
            }

            return parts.Count > 0 ? string.Join("; ", parts) : "Could not read CPU/GPU data.";
        }, cancellationToken);

    private static string TryNvidiaSmi()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,utilization.gpu,temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return string.Empty;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3000);
            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output)) return string.Empty;
            var cols = output.Split(',');
            return cols.Length >= 3
                ? $"{cols[0].Trim()}, Usage: {cols[1].Trim()}%, Temp: {cols[2].Trim()}°C"
                : output;
        }
        catch { return string.Empty; }
    }
}
