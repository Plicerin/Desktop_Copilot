using System.Runtime.InteropServices;

namespace DesktopCopilot.Skills;

public sealed class LockComputerSkill : IActionSkill
{
    private static readonly string[] Keywords =
        ["lock the computer", "lock my computer", "lock screen", "lock the screen", "lock computer", "lock"];

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    public bool TryMatch(string transcript)
    {
        var lower = transcript.ToLowerInvariant();
        return Keywords.Any(k => lower.Contains(k));
    }

    public Task<string> ExecuteAsync(string transcript, CancellationToken cancellationToken)
    {
        AppLog.Info("LockComputerSkill: locking workstation.");
        LockWorkStation();
        return Task.FromResult("Locking the computer.");
    }
}
