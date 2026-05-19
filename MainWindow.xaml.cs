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
using WpfPoint = System.Windows.Point;

namespace DesktopCopilot;

public partial class MainWindow : Window
{
    private static readonly AnimationSequence DefaultAnimation = AnimationPresets.Get(BuiltInAnimationPreset.SignalBloom);
    private const string ReleaseCueSoundPath = @"C:\Windows\Media\Speech On.wav";
    private const bool AnimationPaused = false;
    private const double DefaultWidgetSize = 248;
    private const double DragThreshold = 12;
    private const double FrameContentPadding = 6;

    private readonly DispatcherTimer _animationTimer;
    private readonly DispatcherTimer _speechResultTimer;
    private readonly CopilotCliService _copilotCliService;
    private readonly EdgeTtsService _ttsService;
    private readonly SpeechCaptureService _speechCaptureService;
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
    private BuiltInAnimationPreset _builtInAnimationPreset;
    private FrameStylePreset _frameStylePreset;
    private ColorPalettePreset _colorPalettePreset;

    public MainWindow()
    {
        InitializeComponent();
        AppLog.Info("MainWindow constructed.");
        OrbContentHost.SizeChanged += (_, _) => UpdateOrbContentClip();

        Loaded += OnLoaded;
        _copilotCliService = new CopilotCliService();
        _ttsService = new EdgeTtsService();
        _speechCaptureService = new SpeechCaptureService();
        _speechCaptureService.ListeningStateChanged += OnListeningStateChanged;
        _speechCaptureService.TranscriptCaptured += OnTranscriptCaptured;
        _speechCaptureService.CaptureFailed += OnCaptureFailed;
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

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        AppLog.Info($"MouseLeftButtonDown state={_widgetState} buttonState={e.ButtonState}");
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (_widgetState is WidgetState.Thinking or WidgetState.Speaking or WidgetState.Executing)
        {
            return;
        }

        _pressStart = e.GetPosition(this);
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
        if (!_isListeningGestureActive || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var currentPosition = e.GetPosition(this);
        var delta = currentPosition - _pressStart;

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
        AppLog.Info($"Window_LostMouseCapture listeningGesture={_isListeningGestureActive}");
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
        AppLog.Info($"MouseLeftButtonUp listeningGesture={_isListeningGestureActive}");
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

    private async Task ProcessTranscriptAsync(string transcript, float? confidence)
    {
        AppLog.Info($"Processing transcript=\"{transcript}\" confidence={confidence?.ToString("F2") ?? "n/a"}");
        ApplyVisualState(WidgetState.Thinking);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var response = await _copilotCliService.ExecuteRequestAsync(transcript, confidence, timeout.Token);

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

    public string? PromptForAnimationAndLoad()
    {
        var openFileDialog = new Win32OpenFileDialog
        {
            CheckFileExists = true,
            Filter = "Ascii-Motion exports (*.json;*.txt)|*.json;*.txt|JSON exports (*.json)|*.json|Text exports (*.txt)|*.txt",
            InitialDirectory = AsciiMotionAnimationLoader.GetAnimationDirectory(),
            Multiselect = false,
            Title = "Load Ascii-Motion Export"
        };

        if (openFileDialog.ShowDialog(this) != true)
        {
            return null;
        }

        LoadAnimationFromPath(openFileDialog.FileName);
        return _loadedAnimationPath;
    }

    public string? ReloadAnimation()
    {
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
        return !string.IsNullOrWhiteSpace(_loadedAnimationPath)
            ? AsciiMotionAnimationLoader.GetAnimationDisplayName(_loadedAnimationPath)
            : AnimationPresets.GetDisplayName(_builtInAnimationPreset);
    }

    private void ApplyAnimationSequence(AnimationSequence sequence, string? loadedPath)
    {
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
        Width = DefaultWidgetSize;
        Height = DefaultWidgetSize;
        AnimationCanvas.Width = double.NaN;
        AnimationCanvas.Height = double.NaN;
        UpdateOrbContentLayout();

        PositionNearDesktopEdge();
    }

    private void LoadFrameImage()
    {
        AppLog.Info("PNG frame disabled. Using vector frame styles.");
        PngFrameImage.Source = null;
        PngFrameImage.Visibility = Visibility.Collapsed;
        ApplyFrameStyle(WidgetAppearanceCatalog.GetPalette(_colorPalettePreset));
    }

    private void ApplyFrameStyle(WidgetColorPalette palette)
    {
        var style = WidgetAppearanceCatalog.GetFrameStyle(_frameStylePreset);
        var usePngFrame = style.UsePngFrame && PngFrameImage.Source is not null;

        PngFrameImage.Visibility = usePngFrame ? Visibility.Visible : Visibility.Collapsed;

        if (PngFrameImage.RenderTransform is ScaleTransform pngFrameScale)
        {
            pngFrameScale.ScaleX = style.PngScale;
            pngFrameScale.ScaleY = style.PngScale;
        }

        OuterShell.Visibility = style.ShowOuterShell ? Visibility.Visible : Visibility.Collapsed;
        OuterShell.Margin = new Thickness(style.OuterMargin);
        OuterShell.StrokeThickness = style.OuterStrokeThickness;
        OuterShell.Opacity = style.ShellOpacity;

        ConfigureRing(
            FrameAccentRing,
            style.ShowAccentRing,
            style.AccentMargin,
            style.AccentStrokeThickness,
            style.AccentOpacity,
            palette.FrameAccentStroke);

        ConfigureRing(
            FrameInnerRing,
            style.ShowInnerRing,
            style.InnerMargin,
            style.InnerStrokeThickness,
            style.InnerOpacity,
            palette.FrameInnerStroke);

        UpdateOrbContentLayout(style);
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
        var contentInset = Math.Ceiling(GetInnermostFrameInset(style) + FrameContentPadding);
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
