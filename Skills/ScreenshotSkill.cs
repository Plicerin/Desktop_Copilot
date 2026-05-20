using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Forms = System.Windows.Forms;

namespace DesktopCopilot.Skills;

public sealed class ScreenshotSkill : IActionSkill
{
    private static readonly string[] Keywords =
        ["screenshot", "take a screenshot", "capture the screen", "capture screen", "take screenshot", "screen capture"];

    public bool TryMatch(string transcript)
    {
        var lower = transcript.ToLowerInvariant();
        return Keywords.Any(k => lower.Contains(k));
    }

    public Task<string> ExecuteAsync(string transcript, CancellationToken cancellationToken)
    {
        try
        {
            var bounds = Forms.Screen.PrimaryScreen?.Bounds
                ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

            using var bitmap = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Screenshots");
            Directory.CreateDirectory(folder);

            var fileName = $"screenshot-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            var filePath = Path.Combine(folder, fileName);
            bitmap.Save(filePath, ImageFormat.Png);

            AppLog.Info($"ScreenshotSkill: saved to {filePath}");
            return Task.FromResult($"Screenshot saved to Pictures\\Screenshots\\{fileName}.");
        }
        catch (Exception ex)
        {
            AppLog.Info($"ScreenshotSkill: failed: {ex.Message}");
            return Task.FromResult("I couldn't take the screenshot.");
        }
    }
}
