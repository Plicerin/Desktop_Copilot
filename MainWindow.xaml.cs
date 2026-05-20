using System.IO;
using System.Media;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfPoint = System.Windows.Point;

namespace DesktopCopilot;

public partial class MainWindow : Window
{
    private static readonly AnimationSequence DefaultAnimation = AnimationPresets.Get(BuiltInAnimationPreset.SignalBloom);
    private const string ReleaseCueSoundPath = @"C:\Windows\Media\Speech On.wav";
    private const bool AnimationPaused = false;
    private const double DefaultWidgetWidth = 240;
    private const double DefaultWidgetHeight = 240;
    private const double DragThreshold = 12;
    private const double FrameContentPadding = 6;

    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _speechResultTimer;
    private readonly CopilotCliService _copilotCliService;
    private readonly EdgeTtsService _ttsService;
    private readonly SpeechCaptureService _speechCaptureService;
    private readonly Skills.SkillRouter _skillRouter = new();
    private readonly Skills.ActionSkillRouter _actionSkillRouter;
    private readonly DailyHealthReportService _dailyHealthReport;
    private AnimationSequence _animationSequence;
    private TimeSpan _currentFrameDuration;
    private int _frameIndex;
    private bool _awaitingSpeechResult;
    private bool _isListeningGestureActive;
    private double _animationSpeedFactor;
    private WidgetState _widgetState;
    private WpfPoint _pressStart;
    private AnimationFrame? _lastRenderedFrame;
    private string? _loadedAnimationPath;
    private string? _htmlAnimationPath;
    private HtmlAnimationWindow? _htmlWindow;
    private BuiltInAnimationPreset _builtInAnimationPreset;
    private FrameStylePreset _frameStylePreset;
    private ColorPalettePreset _colorPalettePreset;
    private double? _backgroundFrameContentInset;
    private bool _isFrameDragActive;

    // Kitty zoom mode
    private List<string>? _kittyZoomSourceFrames;
    private int _kittyZoomSourceW;
    private int _kittyZoomSourceH;
    private int _kittyCropW;
    private int _kittyCropH;
    private int _kittyPanX;
    private bool _kittyZoomMode;
    private string? _kittyZoomSourcePath;

    public MainWindow()
    {
        InitializeComponent();
        AppLog.Info("MainWindow constructed.");
        OrbContentHost.SizeChanged += (_, _) => { UpdateOrbContentClip(); SyncHtmlWindowPosition(); };
        FrameOverlayCanvas.SizeChanged += (_, _) => RedrawFrameOverlay();
        LocationChanged += (_, _) => SyncHtmlWindowPosition();
        IsVisibleChanged += (_, _) =>
        {
            if (_htmlWindow is null) return;
            if (IsVisible) _htmlWindow.Show();
            else _htmlWindow.Hide();
        };

        Loaded += OnLoaded;
        _copilotCliService = new CopilotCliService();
        _ttsService = new EdgeTtsService();
        _speechCaptureService = new SpeechCaptureService();
        _speechCaptureService.ListeningStateChanged += OnListeningStateChanged;
        _speechCaptureService.TranscriptCaptured += OnTranscriptCaptured;
        _speechCaptureService.CaptureFailed += OnCaptureFailed;
        _dailyHealthReport = new DailyHealthReportService();
        _dailyHealthReport.ReportReady += OnDailyReportReady;
        _actionSkillRouter = new Skills.ActionSkillRouter(SpeakAsync);
        _builtInAnimationPreset = BuiltInAnimationPreset.SignalBloom;
        _frameStylePreset = FrameStylePreset.MinimalGlass;
        _colorPalettePreset = ColorPalettePreset.Copilot;
        _animationSequence = DefaultAnimation;
        _animationSpeedFactor = 1.0;
        _currentFrameDuration = _animationSequence.Frames[0].Duration;

        _animationTimer = new DispatcherTimer
        {
            Interval = _currentFrameDuration
        };
        _animationTimer.Tick += (_, _) => AdvanceFrame();

        _speechResultTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _speechResultTimer.Tick += OnSpeechResultTimeout;

        ApplyVisualState(WidgetState.Idle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info("MainWindow loaded.");
        LoadFrameImage();
        ApplyVisualState(_widgetState);
        PositionNearDesktopEdge();
        LoadStartupAnimation();
        AdvanceFrame();
        if (!AnimationPaused)
        {
            _animationTimer.Start();
        }
    }

    private void AdvanceFrame()
    {
        if (_animationSequence.Frames.Count == 0)
        {
            AsciiFaceText.Text = string.Empty;
            return;
        }

        if (_frameIndex >= _animationSequence.Frames.Count)
        {
            _frameIndex = _animationSequence.Looping
                ? 0
                : _animationSequence.Frames.Count - 1;
        }

        var frame = _animationSequence.Frames[_frameIndex];
        RenderFrame(frame);
        _currentFrameDuration = frame.Duration;

        if (_animationSequence.Looping)
        {
            _frameIndex = (_frameIndex + 1) % _animationSequence.Frames.Count;
        }
        else if (_frameIndex < _animationSequence.Frames.Count - 1)
        {
            _frameIndex++;
        }

        RefreshAnimationInterval();
    }

    private void RenderFrame(AnimationFrame frame)
    {
        _lastRenderedFrame = frame;
        AsciiFaceText.Text = string.Empty;
        AsciiFaceText.Inlines.Clear();
        var palette = WidgetAppearanceCatalog.GetPalette(_colorPalettePreset);

        var lines = frame.Content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        for (var y = 0; y < lines.Length; y++)
        {
            if (y > 0)
            {
                AsciiFaceText.Inlines.Add(new LineBreak());
            }

            AppendColoredLine(lines[y], y, frame.ForegroundColors, palette);
        }
    }

    private void AppendColoredLine(string line, int y, IReadOnlyDictionary<string, string> foregroundColors, WidgetColorPalette palette)
    {
        if (line.Length == 0)
        {
            return;
        }

        var segmentStart = 0;
        string? currentColor = TryGetColor(0);

        for (var x = 1; x <= line.Length; x++)
        {
            var nextColor = x < line.Length ? TryGetColor(x) : null;
            if (x < line.Length && string.Equals(currentColor, nextColor, StringComparison.Ordinal))
            {
                continue;
            }

            var segment = line[segmentStart..x].Replace(' ', '\u00A0');
            if (segment.Length > 0)
            {
                var run = new Run(segment);
                if (!string.IsNullOrWhiteSpace(currentColor))
                {
                    run.Foreground = BrushFromHex(MapAnimationColor(currentColor, palette));
                }

                AsciiFaceText.Inlines.Add(run);
            }

            segmentStart = x;
            currentColor = nextColor;
        }

        string? TryGetColor(int x)
        {
            return foregroundColors.TryGetValue($"{x},{y}", out var hex)
                ? hex
                : null;
        }
    }

    private void PositionNearDesktopEdge()
    {
        const double margin = 24;
        var workArea = SystemParameters.WorkArea;

        Left = workArea.Right - Width - margin;
        Top = workArea.Bottom - Height - margin;
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (System.Windows.Application.Current is App app)
            app.ShowContextMenu();
        e.Handled = true;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AppLog.Info($"MouseLeftButtonDown state={_widgetState} buttonState={e.ButtonState}");
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        _pressStart = e.GetPosition(this);

        // Click on the frame border (outside the animation circle) → drag only, no speech
        if (!OrbContentHost.IsMouseOver)
        {
            _isFrameDragActive = true;
            Activate();
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_widgetState is WidgetState.Thinking or WidgetState.Speaking or WidgetState.Executing)
        {
            return;
        }

        _isListeningGestureActive = true;
        _awaitingSpeechResult = false;
        _speechResultTimer.Stop();
        Activate();
        CaptureMouse();

        try
        {
            _speechCaptureService.StartDictation();
            ApplyVisualState(WidgetState.Listening);
        }
        catch (InvalidOperationException)
        {
            HandleListeningUnavailable();
        }
        catch (PlatformNotSupportedException)
        {
            HandleListeningUnavailable();
        }

        e.Handled = true;
    }

    private void Window_MouseMove(object sender, WpfMouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        var delta = currentPosition - _pressStart;

        if (_isFrameDragActive)
        {
            if (Math.Abs(delta.X) >= DragThreshold || Math.Abs(delta.Y) >= DragThreshold)
            {
                _isFrameDragActive = false;
                ReleaseMouseCapture();
                DragMove();
            }
            return;
        }

        if (!_isListeningGestureActive)
        {
            return;
        }

        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
        {
            return;
        }

        AppLog.Info("Mouse move exceeded drag threshold; cancelling listening for drag.");
        _isListeningGestureActive = false;
        _awaitingSpeechResult = false;
        _speechResultTimer.Stop();
        ReleaseMouseCapture();
        _speechCaptureService.CancelListening();
        ApplyVisualState(WidgetState.Idle);
        DragMove();
    }

    private void Window_LostMouseCapture(object sender, WpfMouseEventArgs e)
    {
        AppLog.Info($"Window_LostMouseCapture listeningGesture={_isListeningGestureActive} frameDrag={_isFrameDragActive}");

        if (_isFrameDragActive)
        {
            _isFrameDragActive = false;
            return;
        }

        if (!_isListeningGestureActive)
        {
            return;
        }

        _isListeningGestureActive = false;
        _awaitingSpeechResult = true;
        _speechResultTimer.Start();
        _speechCaptureService.StopListening();
        ApplyVisualState(WidgetState.Thinking);
        PlayReleaseCue();
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        AppLog.Info($"MouseLeftButtonUp listeningGesture={_isListeningGestureActive} frameDrag={_isFrameDragActive}");

        if (_isFrameDragActive)
        {
            _isFrameDragActive = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (!_isListeningGestureActive)
        {
            return;
        }

        _isListeningGestureActive = false;
        ReleaseMouseCapture();
        _awaitingSpeechResult = true;
        _speechResultTimer.Start();
        _speechCaptureService.StopListening();
        ApplyVisualState(WidgetState.Thinking);
        PlayReleaseCue();
        e.Handled = true;
    }

    private void OnListeningStateChanged(object? sender, ListeningStateChangedEventArgs e)
    {
        AppLog.Info($"Listening state changed isListening={e.IsListening} mode={e.Mode}");
        Dispatcher.Invoke(() =>
        {
            if (e.IsListening)
            {
                ApplyVisualState(WidgetState.Listening);
                return;
            }

            if (!_awaitingSpeechResult && !_isListeningGestureActive && _widgetState == WidgetState.Listening)
            {
                ApplyVisualState(WidgetState.Idle);
            }
        });
    }

    private void OnTranscriptCaptured(object? sender, TranscriptCapturedEventArgs e)
    {
        AppLog.Info($"Transcript captured awaitingSpeechResult={_awaitingSpeechResult} transcript=\"{e.Transcript}\" confidence={e.Confidence?.ToString("F2") ?? "n/a"}");
        if (!_awaitingSpeechResult)
        {
            return;
        }

        _awaitingSpeechResult = false;
        _speechResultTimer.Stop();
        _ = Dispatcher.InvokeAsync(() => _ = ProcessTranscriptAsync(e.Transcript, e.Confidence));
    }

    private void OnCaptureFailed(object? sender, SpeechCaptureFailedEventArgs e)
    {
        AppLog.Info($"Capture failed awaitingSpeechResult={_awaitingSpeechResult} mode={e.Mode} reason=\"{e.Reason}\" heard=\"{e.HeardTranscript}\" confidence={e.HeardConfidence?.ToString("F2") ?? "n/a"}");
        if (!_awaitingSpeechResult)
        {
            return;
        }

        _awaitingSpeechResult = false;
        _speechResultTimer.Stop();
        _ = Dispatcher.InvokeAsync(() => _ = HandleCaptureFailureAsync(e));
    }

    private void Window_DragOver(object sender, WpfDragEventArgs e)
    {
        if (e.Data.GetDataPresent(WpfDataFormats.FileDrop))
        {
            e.Effects = WpfDragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.Effects = WpfDragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, WpfDragEventArgs e)
    {
        if (!e.Data.GetDataPresent(WpfDataFormats.FileDrop))
        {
            return;
        }

        var paths = e.Data.GetData(WpfDataFormats.FileDrop) as string[];
        if (paths is null || paths.Length == 0)
        {
            return;
        }

        e.Handled = true;

        var animationPath = paths.FirstOrDefault(p =>
            p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

        if (animationPath is not null)
        {
            _ = Dispatcher.InvokeAsync(() => LoadDroppedAnimation(animationPath));
            return;
        }

        _ = Dispatcher.InvokeAsync(() => _ = ProcessFileDropAsync(paths));
    }

    private async Task ProcessFileDropAsync(string[] filePaths)
    {
        AppLog.Info($"Processing file drop count={filePaths.Length} paths=[{string.Join(", ", filePaths)}]");
        ApplyVisualState(WidgetState.Executing);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var response = await _copilotCliService.ExecuteFileDropAsync(filePaths, timeout.Token);

            if (!string.IsNullOrWhiteSpace(response))
            {
                await SpeakAsync(response);
            }

            ApplyVisualState(WidgetState.Idle);
        }
        catch (Exception exception)
        {
            AppLog.Error("ProcessFileDropAsync failed.", exception);
            await HandlePipelineFailureAsync("I couldn't process the dropped files.");
        }
    }

    private async Task ProcessTranscriptAsync(string transcript, float? confidence)
    {
        AppLog.Info($"Processing transcript=\"{transcript}\" confidence={confidence?.ToString("F2") ?? "n/a"}");
        ApplyVisualState(WidgetState.Thinking);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));

            var actionSkill = _actionSkillRouter.Match(transcript);
            if (actionSkill is not null)
            {
                AppLog.Info($"Action skill matched: {actionSkill.GetType().Name}");
                var actionResponse = await actionSkill.ExecuteAsync(transcript, timeout.Token);
                if (!string.IsNullOrWhiteSpace(actionResponse))
                    await SpeakAsync(actionResponse);
                ApplyVisualState(WidgetState.Idle);
                return;
            }

            var skill = _skillRouter.Match(transcript);
            string response;

            if (skill is not null)
            {
                AppLog.Info($"Skill matched: \"{skill.Name}\"");
                var rawData = await skill.RunAsync(timeout.Token);
                AppLog.Info($"Skill raw data: \"{rawData}\"");
                response = await _copilotCliService.ExecuteSkillAsync(skill.Name, rawData, timeout.Token);
            }
            else
            {
                response = await _copilotCliService.ExecuteRequestAsync(transcript, confidence, timeout.Token);
            }

            if (!string.IsNullOrWhiteSpace(response))
            {
                await SpeakAsync(response);
            }

            ApplyVisualState(WidgetState.Idle);
        }
        catch (Exception exception)
        {
            AppLog.Error("ProcessTranscriptAsync failed.", exception);
            await HandlePipelineFailureAsync("I couldn't get a response from Copilot.");
        }
    }

    public void RunMorningReport() => _dailyHealthReport.RunNow();

    private async void OnDailyReportReady(object? sender, string aggregatedData)
    {
        AppLog.Info("OnDailyReportReady: delivering morning health report.");
        await Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                ApplyVisualState(WidgetState.Thinking);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var response = await _copilotCliService.ExecuteDailyReportAsync(aggregatedData, cts.Token);
                if (!string.IsNullOrWhiteSpace(response))
                    await SpeakAsync(response);
                ApplyVisualState(WidgetState.Idle);
            }
            catch (Exception ex)
            {
                AppLog.Error("OnDailyReportReady failed.", ex);
            }
        });
    }

    private async Task HandleCaptureFailureAsync(SpeechCaptureFailedEventArgs e)
    {
        AppLog.Info($"HandleCaptureFailureAsync reason=\"{e.Reason}\" mode={e.Mode} heard=\"{e.HeardTranscript}\" confidence={e.HeardConfidence?.ToString("F2") ?? "n/a"}");

        if (!string.IsNullOrWhiteSpace(e.HeardTranscript))
        {
            await SpeakAsync($"I heard {e.HeardTranscript}. Please try again.");
            ApplyVisualState(WidgetState.Idle);
            return;
        }

        await SpeakAsync("I didn't catch that. Try again.");
        ApplyVisualState(WidgetState.Idle);
    }

    private void ApplyVisualState(WidgetState state)
    {
        AppLog.Info($"ApplyVisualState {state}");
        _widgetState = state;
        var palette = WidgetAppearanceCatalog.GetPalette(_colorPalettePreset);

        var (shellFill, shellStroke, glowFill, faceColor, animationSpeedFactor) = state switch
        {
            WidgetState.Listening => ("#FF16324A", "#FF65D8FF", "#CC00A4FF", "#FFFFFFFF", 0.5625),
            WidgetState.Thinking => ("#FF2E1F4A", "#FFC89EFF", "#B55A2DFF", "#FFFFFFFF", 0.4375),
            WidgetState.Speaking => ("#FF143A33", "#FF57FFD4", "#CC00C9A7", "#FFFFFFFF", 0.625),
            WidgetState.Executing => ("#FF3B1616", "#FFFF8E8E", "#CCE74848", "#FFFFFFFF", 0.375),
            WidgetState.Error => ("#FF4A1616", "#FFFF7676", "#CCEF4444", "#FFFFFFFF", 0.34375),
            _ => (palette.IdleShellFill, palette.IdleShellStroke, palette.IdleGlowFill, palette.IdleFaceColor, 1.0)
        };

        if (state == WidgetState.Idle && _loadedAnimationPath is null)
        {
            if (_builtInAnimationPreset == BuiltInAnimationPreset.BalletSilhouette)
            {
                faceColor = "#FF63FF87";
                glowFill = "#1826B84D";
            }
            else if (_builtInAnimationPreset == BuiltInAnimationPreset.Donut)
            {
                shellFill = "#FF22170F";
                shellStroke = "#FFB88458";
                glowFill = "#336A3E1E";
                faceColor = "#FFD29A63";
            }
        }

        OuterShell.Fill = BrushFromHex(shellFill);
        OuterShell.Stroke = BrushFromHex(shellStroke);
        InnerGlow.Fill = BrushFromHex(glowFill);
        AsciiFaceText.Foreground = BrushFromHex(faceColor);
        ApplyFrameStyle(palette);
        RefreshRenderedFrame();
        _animationSpeedFactor = animationSpeedFactor;
        RefreshAnimationInterval();
    }

    private void HandleListeningUnavailable()
    {
        AppLog.Info("HandleListeningUnavailable");
        ApplyVisualState(WidgetState.Error);
        SystemSounds.Exclamation.Play();
    }

    private async Task HandlePipelineFailureAsync(string message)
    {
        AppLog.Info($"HandlePipelineFailureAsync message=\"{message}\"");
        ApplyVisualState(WidgetState.Error);

        try
        {
            await SpeakAsync(message);
        }
        catch
        {
            SystemSounds.Exclamation.Play();
        }

        ApplyVisualState(WidgetState.Idle);
    }

    private async Task SpeakAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        AppLog.Info($"SpeakAsync message=\"{message}\"");
        ApplyVisualState(WidgetState.Speaking);
        await _ttsService.SpeakAsync(message);
    }

    public string? LoadAnimationFile(string path)
    {
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadHtmlAnimationAsync(path);
            return path;
        }

        LoadAnimationFromPath(path);
        return _loadedAnimationPath;
    }

    public string? PromptForAnimationAndLoad()
    {
        var openFileDialog = new Win32OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Animation files (*.json;*.txt;*.html)|*.json;*.txt;*.html|Ascii-Motion exports (*.json;*.txt)|*.json;*.txt|HTML animations (*.html)|*.html",
            InitialDirectory = AsciiMotionAnimationLoader.GetAnimationDirectory(),
            Multiselect = false,
            Title = "Load Animation"
        };

        if (openFileDialog.ShowDialog(this) != true)
        {
            return null;
        }

        var path = openFileDialog.FileName;
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            _ = LoadHtmlAnimationAsync(path);
            return path;
        }

        LoadAnimationFromPath(path);
        return _loadedAnimationPath;
    }

    public string? ReloadAnimation()
    {
        if (!string.IsNullOrWhiteSpace(_htmlAnimationPath) && File.Exists(_htmlAnimationPath))
        {
            _ = LoadHtmlAnimationAsync(_htmlAnimationPath);
            return _htmlAnimationPath;
        }

        if (!string.IsNullOrWhiteSpace(_loadedAnimationPath) && File.Exists(_loadedAnimationPath))
        {
            LoadAnimationFromPath(_loadedAnimationPath);
            return _loadedAnimationPath;
        }

        if (AsciiMotionAnimationLoader.TryLoadDefault(out var sequence, out var loadedPath))
        {
            ApplyAnimationSequence(sequence, loadedPath);
            return loadedPath;
        }

        UseBuiltInAnimation(_builtInAnimationPreset);
        return null;
    }

    private async Task LoadHtmlAnimationAsync(string path)
    {
        AppLog.Info($"LoadHtmlAnimationAsync path=\"{path}\"");
        UnloadHtmlAnimation();
        _htmlAnimationPath = path;

        _animationTimer.Stop();
        AnimationViewbox.Visibility = Visibility.Collapsed;

        _htmlWindow = new HtmlAnimationWindow { Owner = this };
        SyncHtmlWindowPosition();
        _htmlWindow.Show();

        try
        {
            await _htmlWindow.NavigateToAsync(path);
        }
        catch (Exception ex)
        {
            AppLog.Error("LoadHtmlAnimationAsync navigation failed.", ex);
            UnloadHtmlAnimation();
        }
    }

    private void UnloadHtmlAnimation()
    {
        if (_htmlAnimationPath is null && _htmlWindow is null) return;

        _htmlAnimationPath = null;
        _htmlWindow?.Close();
        _htmlWindow = null;

        AnimationViewbox.Visibility = Visibility.Visible;
        if (!AnimationPaused)
        {
            _animationTimer.Start();
        }
    }

    private void SyncHtmlWindowPosition()
    {
        if (_htmlWindow is null || OrbContentHost.ActualWidth <= 0) return;

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var origin = OrbContentHost.PointToScreen(new WpfPoint(0, 0));
            _htmlWindow.Left = origin.X / dpi.DpiScaleX;
            _htmlWindow.Top = origin.Y / dpi.DpiScaleY;
            _htmlWindow.Width = OrbContentHost.ActualWidth;
            _htmlWindow.Height = OrbContentHost.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            // Visual not yet in tree
        }
    }

    private void LoadStartupAnimation()
    {
        if (AsciiMotionAnimationLoader.TryLoadDefault(out var sequence, out var loadedPath))
        {
            ApplyAnimationSequence(sequence, loadedPath);
            return;
        }

        UseBuiltInAnimation(_builtInAnimationPreset);
    }

    private void LoadAnimationFromPath(string filePath)
    {
        var sequence = AsciiMotionAnimationLoader.LoadFromFile(filePath);
        ApplyAnimationSequence(sequence, filePath);
    }

    private void LoadDroppedAnimation(string sourcePath)
    {
        try
        {
            var animDir = AsciiMotionAnimationLoader.GetAnimationDirectory();
            var destPath = Path.Combine(animDir, "widget-animation.json");

            if (!string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourcePath, destPath, overwrite: true);
            }

            AsciiMotionAnimationLoader.SavePreferredAnimationDisplayName(
                Path.GetFileNameWithoutExtension(sourcePath));

            LoadAnimationFromPath(destPath);

            if (System.Windows.Application.Current is App app)
            {
                app.UpdateCurrentAnimationMenuItem();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("LoadDroppedAnimation failed.", ex);
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_kittyZoomMode) return;

        const int stepW = 5;
        const int stepH = 3;
        const int stepPan = 2;

        switch (e.Key)
        {
            case Key.Up:
                _kittyCropW = Math.Min(_kittyCropW + stepW, _kittyZoomSourceW);
                _kittyCropH = Math.Min(_kittyCropH + stepH, _kittyZoomSourceH);
                ApplyKittyCrop();
                e.Handled = true;
                break;
            case Key.Down:
                _kittyCropW = Math.Max(_kittyCropW - stepW, 10);
                _kittyCropH = Math.Max(_kittyCropH - stepH, 5);
                ApplyKittyCrop();
                e.Handled = true;
                break;
            case Key.Left:
                _kittyPanX = Math.Max(_kittyPanX - stepPan, -(_kittyZoomSourceW - _kittyCropW) / 2);
                ApplyKittyCrop();
                e.Handled = true;
                break;
            case Key.Right:
                _kittyPanX = Math.Min(_kittyPanX + stepPan, (_kittyZoomSourceW - _kittyCropW) / 2);
                ApplyKittyCrop();
                e.Handled = true;
                break;
            case Key.S:
                SaveKittyCrop();
                e.Handled = true;
                break;
            case Key.Escape:
                ExitKittyZoomMode();
                e.Handled = true;
                break;
        }
    }

    public void EnterKittyZoomMode(string sourcePath, int initialCropW, int initialCropH)
    {
        var source = AsciiMotionAnimationLoader.LoadFromFile(sourcePath);
        _kittyZoomSourceFrames = source.Frames.Select(f => f.Content).ToList();
        _kittyZoomSourceW = source.CanvasWidth;
        _kittyZoomSourceH = source.CanvasHeight;
        _kittyZoomSourcePath = sourcePath;
        _kittyCropW = Math.Min(initialCropW, _kittyZoomSourceW);
        _kittyCropH = Math.Min(initialCropH, _kittyZoomSourceH);
        _kittyPanX = 0;
        _kittyZoomMode = true;
        ApplyKittyCrop();
        Activate();
        Focus();
    }

    private void ApplyKittyCrop()
    {
        if (_kittyZoomSourceFrames is null) return;

        int x0 = Math.Max(0, Math.Min((_kittyZoomSourceW - _kittyCropW) / 2 + _kittyPanX, _kittyZoomSourceW - _kittyCropW));
        int y0 = Math.Max(0, (_kittyZoomSourceH - _kittyCropH) / 2);

        var animFrames = new List<AnimationFrame>();
        for (int i = 0; i < _kittyZoomSourceFrames.Count; i += 2)
        {
            var lines = _kittyZoomSourceFrames[i]
                .Split('\n')
                .Select(l => l.PadRight(_kittyZoomSourceW))
                .Skip(y0).Take(_kittyCropH)
                .Select(l => l.Substring(x0, Math.Min(_kittyCropW, l.Length - x0)))
                .ToArray();
            animFrames.Add(new AnimationFrame(string.Join("\n", lines), TimeSpan.FromMilliseconds(160)));
        }

        ApplyAnimationSequence(new AnimationSequence(animFrames, looping: true), _loadedAnimationPath);
    }

    private void SaveKittyCrop()
    {
        if (_kittyZoomSourceFrames is null || _kittyZoomSourcePath is null) return;

        int x0 = Math.Max(0, Math.Min((_kittyZoomSourceW - _kittyCropW) / 2 + _kittyPanX, _kittyZoomSourceW - _kittyCropW));
        int y0 = Math.Max(0, (_kittyZoomSourceH - _kittyCropH) / 2);

        var frames = new List<string>();
        for (int i = 0; i < _kittyZoomSourceFrames.Count; i += 2)
        {
            var lines = _kittyZoomSourceFrames[i]
                .Split('\n')
                .Select(l => l.PadRight(_kittyZoomSourceW))
                .Skip(y0).Take(_kittyCropH)
                .Select(l => l.Substring(x0, Math.Min(_kittyCropW, l.Length - x0)))
                .ToArray();
            frames.Add(string.Join("\n", lines));
        }

        var outPath = Path.Combine(Path.GetDirectoryName(_kittyZoomSourcePath)!, "widget-animation.json");
        var json = System.Text.Json.JsonSerializer.Serialize(new { frames = frames.ToArray() });
        File.WriteAllText(outPath, json);
        LoadAnimationFromPath(outPath);
        ExitKittyZoomMode();
    }

    private void ExitKittyZoomMode()
    {
        _kittyZoomMode = false;
        _kittyZoomSourceFrames = null;
    }

    public string UseBuiltInAnimation(BuiltInAnimationPreset preset)
    {
        _builtInAnimationPreset = preset;
        ApplyAnimationSequence(AnimationPresets.Get(preset), loadedPath: null);
        ApplyVisualState(_widgetState);
        return AnimationPresets.GetDisplayName(preset);
    }

    public string UseFrameStyle(FrameStylePreset preset)
    {
        _frameStylePreset = preset;
        ApplyVisualState(_widgetState);
        return WidgetAppearanceCatalog.GetDisplayName(preset);
    }

    public string UseColorPalette(ColorPalettePreset preset)
    {
        _colorPalettePreset = preset;
        ApplyVisualState(_widgetState);
        return WidgetAppearanceCatalog.GetDisplayName(preset);
    }

    public string GetCurrentAnimationDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(_htmlAnimationPath))
        {
            return Path.GetFileNameWithoutExtension(_htmlAnimationPath);
        }

        return !string.IsNullOrWhiteSpace(_loadedAnimationPath)
            ? AsciiMotionAnimationLoader.GetAnimationDisplayName(_loadedAnimationPath)
            : AnimationPresets.GetDisplayName(_builtInAnimationPreset);
    }

    private void ApplyAnimationSequence(AnimationSequence sequence, string? loadedPath)
    {
        UnloadHtmlAnimation();
        _animationSequence = sequence;
        _loadedAnimationPath = loadedPath;
        _frameIndex = 0;
        UpdateWidgetLayoutForAnimation(sequence);
        AdvanceFrame();
    }

    private void RefreshAnimationInterval()
    {
        var interval = Math.Max(80, _currentFrameDuration.TotalMilliseconds * _animationSpeedFactor);
        _animationTimer.Interval = TimeSpan.FromMilliseconds(interval);
    }

    private void UpdateWidgetLayoutForAnimation(AnimationSequence sequence)
    {
        Width = DefaultWidgetWidth;
        Height = DefaultWidgetHeight;
        AnimationCanvas.Width = double.NaN;
        AnimationCanvas.Height = double.NaN;
        UpdateOrbContentLayout();

        PositionNearDesktopEdge();
    }

    private void LoadFrameImage()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/frame.png", UriKind.Absolute);
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(uri);
            BackgroundFrameImage.Source = bitmap;
            BackgroundFrameImage.Visibility = Visibility.Visible;
            AnimationBackdrop.Visibility = Visibility.Collapsed;
            _backgroundFrameContentInset = 49;
        }
        catch (Exception ex)
        {
            AppLog.Error("Failed to load frame image.", ex);
            BackgroundFrameImage.Source = null;
            BackgroundFrameImage.Visibility = Visibility.Collapsed;
            AnimationBackdrop.Visibility = Visibility.Visible;
            _backgroundFrameContentInset = null;
        }

        ApplyFrameStyle(WidgetAppearanceCatalog.GetPalette(_colorPalettePreset));
    }

    private void ApplyFrameStyle(WidgetColorPalette palette)
    {
        var style = WidgetAppearanceCatalog.GetFrameStyle(_frameStylePreset);
        var hasBackgroundImage = BackgroundFrameImage.Visibility == Visibility.Visible;
        var usePngFrame = style.UsePngFrame && PngFrameImage.Source is not null;

        PngFrameImage.Visibility = usePngFrame ? Visibility.Visible : Visibility.Collapsed;

        if (PngFrameImage.RenderTransform is ScaleTransform pngFrameScale)
        {
            pngFrameScale.ScaleX = style.PngScale;
            pngFrameScale.ScaleY = style.PngScale;
        }

        OuterShell.Visibility = (!hasBackgroundImage && style.ShowOuterShell) ? Visibility.Visible : Visibility.Collapsed;
        OuterShell.Margin = new Thickness(style.OuterMargin);
        OuterShell.StrokeThickness = style.OuterStrokeThickness;
        OuterShell.Opacity = style.ShellOpacity;

        ConfigureRing(
            FrameAccentRing,
            !hasBackgroundImage && style.ShowAccentRing,
            style.AccentMargin,
            style.AccentStrokeThickness,
            style.AccentOpacity,
            palette.FrameAccentStroke);

        ConfigureRing(
            FrameInnerRing,
            !hasBackgroundImage && style.ShowInnerRing,
            style.InnerMargin,
            style.InnerStrokeThickness,
            style.InnerOpacity,
            palette.FrameInnerStroke);

        UpdateOrbContentLayout(style);
        DrawFrameOverlay(style.OverlayType, palette);
    }

    private void ConfigureRing(
        System.Windows.Shapes.Ellipse ring,
        bool isVisible,
        double margin,
        double strokeThickness,
        double opacity,
        string stroke)
    {
        ring.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        ring.Margin = new Thickness(margin);
        ring.StrokeThickness = strokeThickness;
        ring.Opacity = opacity;
        ring.Stroke = BrushFromHex(stroke);
    }

    private static SolidColorBrush BrushFromHex(string hex)
    {
        return new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(hex));
    }

    private void RedrawFrameOverlay()
    {
        var style = WidgetAppearanceCatalog.GetFrameStyle(_frameStylePreset);
        var palette = WidgetAppearanceCatalog.GetPalette(_colorPalettePreset);
        DrawFrameOverlay(style.OverlayType, palette);
    }

    private void DrawFrameOverlay(FrameOverlayType type, WidgetColorPalette palette)
    {
        FrameOverlayCanvas.Children.Clear();
        if (type == FrameOverlayType.None) return;
        if (FrameOverlayCanvas.ActualWidth <= 0) return;

        var cx = FrameOverlayCanvas.ActualWidth / 2;
        var cy = FrameOverlayCanvas.ActualHeight / 2;
        var stroke = BrushFromHex(palette.FrameAccentStroke);

        switch (type)
        {
            case FrameOverlayType.Crosshair:      DrawCrosshair(cx, cy, stroke);      break;
            case FrameOverlayType.CornerBrackets: DrawCornerBrackets(cx, cy, stroke); break;
            case FrameOverlayType.SegmentedArc:   DrawSegmentedArc(cx, cy, stroke);   break;
            case FrameOverlayType.TickRing:       DrawTickRing(cx, cy, stroke);       break;
        }
    }

    private void DrawCrosshair(double cx, double cy, SolidColorBrush stroke)
    {
        const double Gap = 32;
        const double Reach = 112;
        const double Sw = 1.5;

        (double x1, double y1, double x2, double y2)[] lines =
        [
            (cx, cy - Gap, cx, cy - Reach),
            (cx, cy + Gap, cx, cy + Reach),
            (cx - Gap, cy, cx - Reach, cy),
            (cx + Gap, cy, cx + Reach, cy),
        ];

        foreach (var (x1, y1, x2, y2) in lines)
            FrameOverlayCanvas.Children.Add(MakeLine(x1, y1, x2, y2, stroke, Sw));
    }

    private void DrawCornerBrackets(double cx, double cy, SolidColorBrush stroke)
    {
        const double R = 90;
        const double Arm = 22;
        const double Sw = 1.5;

        (double sx, double sy)[] corners = [(-1, -1), (1, -1), (-1, 1), (1, 1)];

        foreach (var (sx, sy) in corners)
        {
            double bx = cx + sx * R;
            double by = cy + sy * R;
            FrameOverlayCanvas.Children.Add(MakeLine(bx, by, bx - sx * Arm, by, stroke, Sw));
            FrameOverlayCanvas.Children.Add(MakeLine(bx, by, bx, by - sy * Arm, stroke, Sw));
        }
    }

    private void DrawSegmentedArc(double cx, double cy, SolidColorBrush stroke)
    {
        const double R = 108;
        const double GapDeg = 22.0;
        const double Sw = 2.0;

        for (int i = 0; i < 4; i++)
        {
            double startDeg = i * 90 + GapDeg / 2;
            double endDeg = startDeg + (90 - GapDeg);
            FrameOverlayCanvas.Children.Add(MakeArc(cx, cy, R, startDeg, endDeg, stroke, Sw));
        }
    }

    private void DrawTickRing(double cx, double cy, SolidColorBrush stroke)
    {
        const double R = 106;
        const double Sw = 1.0;

        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = R * 2,
            Height = R * 2,
            Stroke = stroke,
            StrokeThickness = Sw * 0.75,
            Fill = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false,
            Opacity = 0.6,
        };
        System.Windows.Controls.Canvas.SetLeft(ring, cx - R);
        System.Windows.Controls.Canvas.SetTop(ring, cy - R);
        FrameOverlayCanvas.Children.Add(ring);

        for (int i = 0; i < 36; i++)
        {
            bool isMajor = i % 3 == 0;
            double angleRad = i * 10.0 * Math.PI / 180.0;
            double outerR = R;
            double innerR = R - (isMajor ? 9 : 4);
            FrameOverlayCanvas.Children.Add(MakeLine(
                cx + outerR * Math.Cos(angleRad), cy + outerR * Math.Sin(angleRad),
                cx + innerR * Math.Cos(angleRad), cy + innerR * Math.Sin(angleRad),
                stroke, isMajor ? Sw * 1.5 : Sw * 0.75));
        }
    }

    private static System.Windows.Shapes.Line MakeLine(
        double x1, double y1, double x2, double y2,
        System.Windows.Media.Brush stroke, double sw) =>
        new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = sw, IsHitTestVisible = false };

    private static System.Windows.Shapes.Path MakeArc(
        double cx, double cy, double r,
        double startDeg, double endDeg,
        System.Windows.Media.Brush stroke, double sw)
    {
        double startRad = startDeg * Math.PI / 180.0;
        double endRad = endDeg * Math.PI / 180.0;
        var startPt = new System.Windows.Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad));
        var endPt = new System.Windows.Point(cx + r * Math.Cos(endRad), cy + r * Math.Sin(endRad));
        var figure = new PathFigure { StartPoint = startPt };
        figure.Segments.Add(new ArcSegment(endPt, new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(figure);
        return new System.Windows.Shapes.Path
        {
            Data = geo, Stroke = stroke, StrokeThickness = sw,
            Fill = System.Windows.Media.Brushes.Transparent, IsHitTestVisible = false,
        };
    }

    private void PlayReleaseCue()
    {
        if (!File.Exists(ReleaseCueSoundPath))
        {
            AppLog.Info($"Release cue sound not found at {ReleaseCueSoundPath}.");
            return;
        }

        try
        {
            var player = new SoundPlayer(ReleaseCueSoundPath);
            player.Play();
        }
        catch (FileNotFoundException exception)
        {
            AppLog.Error("Release cue sound file could not be opened.", exception);
        }
        catch (InvalidOperationException exception)
        {
            AppLog.Error("Release cue sound failed to play.", exception);
        }
    }

    private void UpdateOrbContentLayout()
    {
        UpdateOrbContentLayout(WidgetAppearanceCatalog.GetFrameStyle(_frameStylePreset));
    }

    private void UpdateOrbContentLayout(FrameStyleSpec style)
    {
        var contentInset = _backgroundFrameContentInset
            ?? Math.Ceiling(GetInnermostFrameInset(style) + FrameContentPadding);
        OrbContentHost.Margin = new Thickness(contentInset);
        UpdateOrbContentClip();
    }

    private void UpdateOrbContentClip()
    {
        if (OrbContentHost.ActualWidth <= 0 || OrbContentHost.ActualHeight <= 0)
        {
            OrbContentHost.Clip = null;
            return;
        }

        OrbContentHost.Clip = new EllipseGeometry(new Rect(0, 0, OrbContentHost.ActualWidth, OrbContentHost.ActualHeight));
    }

    private static double GetInnermostFrameInset(FrameStyleSpec style)
    {
        var inset = 0d;

        if (style.ShowOuterShell)
        {
            inset = Math.Max(inset, style.OuterMargin + (style.OuterStrokeThickness / 2));
        }

        if (style.ShowAccentRing)
        {
            inset = Math.Max(inset, style.AccentMargin + (style.AccentStrokeThickness / 2));
        }

        if (style.ShowInnerRing)
        {
            inset = Math.Max(inset, style.InnerMargin + (style.InnerStrokeThickness / 2));
        }

        return inset;
    }

    private void RefreshRenderedFrame()
    {
        if (_lastRenderedFrame is not null)
        {
            RenderFrame(_lastRenderedFrame);
        }
    }

    private static string MapAnimationColor(string sourceHex, WidgetColorPalette palette)
    {
        var sourceColor = (WpfColor)WpfColorConverter.ConvertFromString(sourceHex);
        var (_, saturation, value) = ToHsv(sourceColor);

        if (value >= 0.98 && saturation <= 0.05)
        {
            return palette.AnimationHighlight;
        }

        if (value <= 0.3)
        {
            return palette.AnimationShadow;
        }

        var hue = GetHue(sourceColor);
        if (hue >= 150 && hue < 195)
        {
            return palette.AnimationAccent;
        }

        if (hue >= 195 && hue < 255)
        {
            return value >= 0.75
                ? palette.AnimationPrimaryLight
                : value >= 0.5
                    ? palette.AnimationPrimary
                    : palette.AnimationPrimaryDark;
        }

        if (hue >= 255 && hue < 330)
        {
            return value >= 0.75
                ? palette.AnimationSecondaryLight
                : value >= 0.5
                    ? palette.AnimationSecondary
                    : palette.AnimationSecondaryDark;
        }

        return value >= 0.7
            ? palette.AnimationPrimaryLight
            : value >= 0.45
                ? palette.AnimationPrimary
                : palette.AnimationPrimaryDark;
    }

    private static (double Hue, double Saturation, double Value) ToHsv(WpfColor color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;

        var hue = GetHue(color);
        var saturation = max <= 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    private static double GetHue(WpfColor color)
    {
        var red = color.R / 255d;
        var green = color.G / 255d;
        var blue = color.B / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var delta = max - min;

        if (delta == 0)
        {
            return 0;
        }

        double hue;
        if (max == red)
        {
            hue = ((green - blue) / delta) % 6;
        }
        else if (max == green)
        {
            hue = ((blue - red) / delta) + 2;
        }
        else
        {
            hue = ((red - green) / delta) + 4;
        }

        hue *= 60;
        return hue < 0 ? hue + 360 : hue;
    }

    private void OnSpeechResultTimeout(object? sender, EventArgs e)
    {
        AppLog.Info($"Speech result timeout awaitingSpeechResult={_awaitingSpeechResult}");
        _speechResultTimer.Stop();

        if (!_awaitingSpeechResult)
        {
            return;
        }

        _awaitingSpeechResult = false;
        ApplyVisualState(WidgetState.Error);
        SystemSounds.Exclamation.Play();
        ApplyVisualState(WidgetState.Idle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _animationTimer.Stop();
        _speechResultTimer.Stop();
        _speechCaptureService.Dispose();
        _ttsService.Dispose();
        _dailyHealthReport.Dispose();
        base.OnClosed(e);
    }

    private enum WidgetState
    {
        Idle,
        Listening,
        Thinking,
        Speaking,
        Executing,
        Error
    }
}
