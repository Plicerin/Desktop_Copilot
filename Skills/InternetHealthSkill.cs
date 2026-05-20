using System.Net.NetworkInformation;

namespace DesktopCopilot.Skills;

public sealed class InternetHealthSkill : ISkill
{
    public string Name => "Internet Health";
    public string[] Keywords => ["internet", "connection", "network", "ping", "online", "connectivity", "internet connection"];

    private static readonly (string label, string host)[] Targets =
    [
        ("Google DNS", "8.8.8.8"),
        ("Cloudflare DNS", "1.1.1.1"),
        ("Google", "google.com"),
    ];

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<string>();
        using var ping = new Ping();

        foreach (var (label, host) in Targets)
        {
            try
            {
                var reply = await ping.SendPingAsync(host, timeout: 3000);
                if (reply.Status == IPStatus.Success)
                    results.Add($"{label}: {reply.RoundtripTime}ms");
                else
                    results.Add($"{label}: unreachable ({reply.Status})");
            }
            catch (Exception ex)
            {
                results.Add($"{label}: error ({ex.Message})");
            }
        }

        return string.Join("; ", results);
    }
}
