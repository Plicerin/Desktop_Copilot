using System.Diagnostics;
using System.IO;
using System.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Wpf = System.Windows;

namespace DesktopCopilot;

public partial class App : Wpf.Application
{
    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _currentAnimationMenuItem;
    private Forms.ToolStripMenuItem? _fileAnimationsMenu;
    private readonly CrashTriageService _crashTriageService = new();

    protected override void OnStartup(Wpf.StartupEventArgs e)
    {
        base.OnStartup(e);
        var logPath = AppLog.Initialize();
        AppLog.Info($"Application startup. Log path: {logPath}");

        MainWindow = new MainWindow();
        MainWindow.Show();

        _notifyIcon = BuildNotifyIcon();
    }

    protected override void OnExit(Wpf.ExitEventArgs e)
    {
        AppLog.Info("Application exiting.");

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.OnExit(e);
    }

    private Forms.NotifyIcon BuildNotifyIcon()
    {
        var contextMenu = new Forms.ContextMenuStrip();
        _currentAnimationMenuItem = new Forms.ToolStripMenuItem("Current Animation: (loading)")
        {
            Enabled = true
        };
        var frameStylesMenu = new Forms.ToolStripMenuItem("Frame Styles");
        foreach (var preset in WidgetAppearanceCatalog.FrameStyles)
        {
            var capturedPreset = preset;
            frameStylesMenu.DropDownItems.Add(
                WidgetAppearanceCatalog.GetDisplayName(capturedPreset),
                null,
                (_, _) => UseFrameStyle(capturedPreset));
        }

        var colorPalettesMenu = new Forms.ToolStripMenuItem("Color Palettes");
        foreach (var preset in WidgetAppearanceCatalog.ColorPalettes)
        {
            var capturedPreset = preset;
            colorPalettesMenu.DropDownItems.Add(
                WidgetAppearanceCatalog.GetDisplayName(capturedPreset),
                null,
                (_, _) => UseColorPalette(capturedPreset));
        }

        var builtInAnimationsMenu = new Forms.ToolStripMenuItem("Built-in Animations");

        foreach (var preset in AnimationPresets.All)
        {
            var capturedPreset = preset;
            builtInAnimationsMenu.DropDownItems.Add(
                AnimationPresets.GetDisplayName(capturedPreset),
                null,
                (_, _) => UseBuiltInAnimation(capturedPreset));
        }

        _fileAnimationsMenu = new Forms.ToolStripMenuItem("File Animations") { Enabled = false };

        contextMenu.Items.Add(_currentAnimationMenuItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(frameStylesMenu);
        contextMenu.Items.Add(colorPalettesMenu);
        contextMenu.Items.Add(builtInAnimationsMenu);
        contextMenu.Items.Add(_fileAnimationsMenu);
        contextMenu.Items.Add("Load Animation...", null, (_, _) => LoadAnimationFromFile());
        contextMenu.Items.Add("Reload Animation", null, (_, _) => ReloadAnimation());
        contextMenu.Items.Add("Zoom Kitty Animation", null, (_, _) => ZoomKittyAnimation());
        contextMenu.Items.Add("Open Animation Folder", null, (_, _) => OpenAnimationFolder());
        contextMenu.Items.Add("Run Crash Triage Snapshot", null, async (_, _) => await RunCrashTriageSnapshotAsync());
        contextMenu.Items.Add("Open Logs Folder", null, (_, _) => OpenLogsFolder());
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add("Show / Hide", null, (_, _) => ToggleWidgetVisibility());
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => Shutdown());

        var iconUri = new Uri("pack://application:,,,/tray.ico", UriKind.Absolute);
        var iconStream = Wpf.Application.GetResourceStream(iconUri)?.Stream;
        var trayIcon = iconStream is not null
            ? new Drawing.Icon(iconStream)
            : Drawing.SystemIcons.Application;

        var notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = contextMenu,
            Icon = trayIcon,
            Text = "Desktop Copilot",
            Visible = true
        };

        notifyIcon.DoubleClick += (_, _) => ToggleWidgetVisibility();
        contextMenu.Opening += (_, _) => RefreshFileAnimationsMenu();
        UpdateCurrentAnimationMenuItem();
        return notifyIcon;
    }

    public void ShowContextMenu()
    {
        _notifyIcon?.ContextMenuStrip?.Show(Forms.Cursor.Position);
    }

    private void ToggleWidgetVisibility()
    {
        if (MainWindow is null)
        {
            return;
        }

        if (MainWindow.IsVisible)
        {
            MainWindow.Hide();
            return;
        }

        MainWindow.Show();
        MainWindow.Activate();
    }

    private void RefreshFileAnimationsMenu()
    {
        if (_fileAnimationsMenu is null) return;

        _fileAnimationsMenu.DropDownItems.Clear();
        var dir = AsciiMotionAnimationLoader.GetAnimationDirectory();

        if (Directory.Exists(dir))
        {
            var files = Directory.GetFiles(dir)
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f));

            foreach (var file in files)
            {
                var capturedFile = file;
                _fileAnimationsMenu.DropDownItems.Add(
                    Path.GetFileNameWithoutExtension(file),
                    null,
                    (_, _) => LoadFileAnimation(capturedFile));
            }
        }

        _fileAnimationsMenu.Enabled = _fileAnimationsMenu.DropDownItems.Count > 0;
    }

    private void LoadFileAnimation(string path)
    {
        if (MainWindow is not MainWindow window) return;

        try
        {
            window.LoadAnimationFile(path);
            UpdateCurrentAnimationMenuItem();
        }
        catch (Exception exception)
        {
            ShowTrayMessage("Animation load failed", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void LoadAnimationFromFile()
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        try
        {
            var loadedPath = window.PromptForAnimationAndLoad();
            if (!string.IsNullOrWhiteSpace(loadedPath))
            {
                UpdateCurrentAnimationMenuItem();
            }
        }
        catch (Exception exception)
        {
            ShowTrayMessage("Animation load failed", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void ReloadAnimation()
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        try
        {
            var reloadedPath = window.ReloadAnimation();
            UpdateCurrentAnimationMenuItem();
        }
        catch (Exception exception)
        {
            ShowTrayMessage("Animation reload failed", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void OpenAnimationFolder()
    {
        var animationDirectory = AsciiMotionAnimationLoader.GetAnimationDirectory();
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{animationDirectory}\"",
            UseShellExecute = true
        });
    }

    private void ZoomKittyAnimation()
    {
        if (MainWindow is not MainWindow window) return;

        var kittyPath = Path.Combine(AsciiMotionAnimationLoader.GetAnimationDirectory(), "kitty.json");
        if (!File.Exists(kittyPath))
        {
            ShowTrayMessage("File not found", "kitty.json not found in the Animations folder.", Forms.ToolTipIcon.Error);
            return;
        }

        window.EnterKittyZoomMode(kittyPath, initialCropW: 98, initialCropH: 50);
    }

    private void OpenLogsFolder()
    {
        var logsDirectory = AppLog.GetLogsDirectory();
        Directory.CreateDirectory(logsDirectory);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{logsDirectory}\"",
            UseShellExecute = true
        });
    }

    private async Task RunCrashTriageSnapshotAsync()
    {
        try
        {
            var report = await _crashTriageService.CaptureAsync(CancellationToken.None);
            var reportPath = _crashTriageService.SaveReport(report);
            ShowTrayMessage(
                "Crash triage saved",
                $"{_crashTriageService.BuildSnapshotSummary(report)} Saved to {Path.GetFileName(reportPath)}.");
        }
        catch (Exception exception)
        {
            AppLog.Error("Crash triage snapshot failed.", exception);
            ShowTrayMessage("Crash triage failed", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void ShowTrayMessage(string title, string text, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        _notifyIcon?.ShowBalloonTip(3000, title, text, icon);
    }

    private void UseBuiltInAnimation(BuiltInAnimationPreset preset)
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        try
        {
            var animationName = window.UseBuiltInAnimation(preset);
            UpdateCurrentAnimationMenuItem();
        }
        catch (Exception exception)
        {
            ShowTrayMessage("Animation change failed", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void UseFrameStyle(FrameStylePreset preset)
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        try
        {
            var styleName = window.UseFrameStyle(preset);
            ShowTrayMessage("Frame style selected", styleName);
        }
        catch (Exception exception)
        {
            ShowTrayMessage("Frame style change failed", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void UseColorPalette(ColorPalettePreset preset)
    {
        if (MainWindow is not MainWindow window)
        {
            return;
        }

        try
        {
            var paletteName = window.UseColorPalette(preset);
            ShowTrayMessage("Color palette selected", paletteName);
        }
        catch (Exception exception)
        {
            ShowTrayMessage("Color palette change failed", exception.Message, Forms.ToolTipIcon.Error);
        }
    }

    private void UpdateCurrentAnimationMenuItem()
    {
        if (_currentAnimationMenuItem is null
            || MainWindow is not MainWindow window)
        {
            return;
        }

        _currentAnimationMenuItem.Text = $"Current Animation: {window.GetCurrentAnimationDisplayName()}";
    }
}

