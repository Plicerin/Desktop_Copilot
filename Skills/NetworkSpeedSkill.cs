using System.Diagnostics;
using System.Net.Http;

namespace DesktopCopilot.Skills;

public sealed class NetworkSpeedSkill : ISkill
{
    public string Name => "Network Speed";
    public string[] Keywords =>
    [
        "network speed", "internet speed", "download speed", "speed test", "bandwidth",
        "how fast is my internet", "how fast is my connection", "check network speed",
        "check internet speed", "check download speed", "check bandwidth",
    ];

    // 10 MB test payload from Cloudflare's public speed endpoint
    private const string TestUrl = "https://speed.cloudflare.com/__down?bytes=10485760";

    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };

            // Warm the connection
            using var warmRequest = new HttpRequestMessage(HttpMethod.Head, "https://speed.cloudflare.com");
            await client.SendAsync(warmRequest, cancellationToken);

            var sw = Stopwatch.StartNew();
            var bytes = await client.GetByteArrayAsync(TestUrl, cancellationToken);
            sw.Stop();

            var totalMb = bytes.Length / 1_048_576.0;
            var seconds = sw.Elapsed.TotalSeconds;
            var mbps = bytes.Length * 8.0 / (seconds * 1_000_000.0);

            AppLog.Info($"NetworkSpeedSkill: {totalMb:F1} MB in {seconds:F2}s = {mbps:F1} Mbps");
            return $"Download speed: {mbps:F1} Mbps (downloaded {totalMb:F1} MB in {seconds:F1} seconds via Cloudflare).";
        }
        catch (Exception ex)
        {
            AppLog.Info($"NetworkSpeedSkill error: {ex.Message}");
            return $"Speed test failed: {ex.Message}";
        }
    }
}
