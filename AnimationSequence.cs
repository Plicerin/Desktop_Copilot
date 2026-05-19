namespace DesktopCopilot;

public sealed class AnimationSequence
{
    public AnimationSequence(
        IReadOnlyList<AnimationFrame> frames,
        bool looping,
        int? canvasWidth = null,
        int? canvasHeight = null)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("Animation sequences must contain at least one frame.", nameof(frames));
        }

        Frames = frames;
        Looping = looping;
        CanvasWidth = canvasWidth is > 0
            ? canvasWidth.Value
            : frames.Max(frame => frame.Content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Max(line => line.Length));
        CanvasHeight = canvasHeight is > 0
            ? canvasHeight.Value
            : frames.Max(frame => frame.Content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Length);
    }

    public IReadOnlyList<AnimationFrame> Frames { get; }

    public bool Looping { get; }

    public int CanvasWidth { get; }

    public int CanvasHeight { get; }
}

public sealed class AnimationFrame
{
    public AnimationFrame(
        string content,
        TimeSpan duration,
        IReadOnlyDictionary<string, string>? foregroundColors = null)
    {
        Content = content ?? string.Empty;
        Duration = duration <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(160)
            : duration;
        ForegroundColors = foregroundColors ?? new Dictionary<string, string>();
    }

    public string Content { get; }

    public TimeSpan Duration { get; }

    public IReadOnlyDictionary<string, string> ForegroundColors { get; }
}
