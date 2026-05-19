using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopCopilot;

public partial class HtmlAnimationWindow : Window
{
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    private bool _webViewReady;

    public HtmlAnimationWindow()
    {
        InitializeComponent();
        ContentRendered += (_, _) => ApplyEllipticalClip();
        SizeChanged += (_, _) => ApplyEllipticalClip();
    }

    public async Task NavigateToAsync(string htmlFilePath)
    {
        if (!_webViewReady)
        {
            await WebView.EnsureCoreWebView2Async();
            _webViewReady = true;
        }

        var fileUri = new Uri(htmlFilePath).AbsoluteUri;
        WebView.CoreWebView2.Navigate(fileUri);
    }

    private void ApplyEllipticalClip()
    {
        var helper = new WindowInteropHelper(this);
        var hwnd = helper.EnsureHandle();
        if (hwnd == IntPtr.Zero) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        var h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        if (w <= 0 || h <= 0) return;

        var region = CreateEllipticRgn(0, 0, w, h);
        SetWindowRgn(hwnd, region, true);
    }
}
