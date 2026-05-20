using System.Windows;
using WpfClipboard = System.Windows.Clipboard;
using WinFormsClipboard = System.Windows.Forms.Clipboard;

namespace DesktopCopilot.Skills;

public sealed class ClipboardSkill : IActionSkill
{
    private static readonly string[] Keywords =
        ["read my clipboard", "read clipboard", "what's in my clipboard", "what is in my clipboard",
         "clipboard", "read the clipboard", "paste"];

    public bool TryMatch(string transcript)
    {
        var lower = transcript.ToLowerInvariant();
        return Keywords.Any(k => lower.Contains(k));
    }

    public Task<string> ExecuteAsync(string transcript, CancellationToken cancellationToken)
    {
        // Clipboard requires the UI (STA) thread.
        var result = System.Windows.Application.Current.Dispatcher.Invoke<string>(() =>
        {
            try
            {
                if (WpfClipboard.ContainsText())
                {
                    var text = WpfClipboard.GetText().Trim();
                    if (string.IsNullOrWhiteSpace(text))
                        return "The clipboard is empty.";

                    if (text.Length > 300)
                        return text[..300] + "… and more.";

                    AppLog.Info($"ClipboardSkill: reading {text.Length} chars.");
                    return text;
                }

                if (WpfClipboard.ContainsImage())
                    return "The clipboard contains an image.";

                if (WpfClipboard.ContainsFileDropList())
                {
                    var files = WpfClipboard.GetFileDropList();
                    var count = files.Count;
                    return $"The clipboard contains {count} file{(count == 1 ? "" : "s")}: {string.Join(", ", files.Cast<string>().Select(System.IO.Path.GetFileName))}";
                }

                return "The clipboard is empty.";
            }
            catch (Exception ex)
            {
                AppLog.Info($"ClipboardSkill: error: {ex.Message}");
                return "I couldn't read the clipboard.";
            }
        });

        return Task.FromResult(result);
    }
}
