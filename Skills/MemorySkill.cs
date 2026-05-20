using System.Runtime.InteropServices;

namespace DesktopCopilot.Skills;

public sealed class MemorySkill : ISkill
{
    public string Name => "Memory";
    public string[] Keywords => ["memory", "ram", "available ram", "available memory", "free memory", "free ram"];

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    public Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
            return Task.FromResult("Unable to read memory status.");

        var totalGb = status.ullTotalPhys / 1_073_741_824.0;
        var availGb = status.ullAvailPhys / 1_073_741_824.0;
        var usedPct = status.dwMemoryLoad;

        return Task.FromResult(
            $"Total RAM: {totalGb:F1} GB; Available: {availGb:F1} GB; In use: {usedPct}%");
    }
}
