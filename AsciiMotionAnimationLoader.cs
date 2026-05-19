using System.IO;
using System.Text.Json;

namespace DesktopCopilot;

public static class AsciiMotionAnimationLoader
{
    private const string PreferredJsonName = "widget-animation.json";
    private const string PreferredTextName = "widget-animation.txt";
    private const string PreferredDisplayNameName = "widget-animation.name.txt";

    public static string GetAnimationDirectory()
    {
        return GetAnimationDirectories().First();
    }

    public static IReadOnlyList<string> GetAnimationDirectories()
    {
        var directories = new List<string>();

        AddAnimationDirectory(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "DesktopCopilot",
                "Animations"));

        AddAnimationDirectory(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DesktopCopilot",
                "Animations"));

        return directories;

        void AddAnimationDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)
                || directories.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(path);
            directories.Add(path);
        }
    }

    public static bool TryLoadDefault(out AnimationSequence sequence, out string loadedPath)
    {
        var candidatePaths = new List<string>();

        foreach (var animationDirectory in GetAnimationDirectories())
        {
            var preferredJson = Path.Combine(animationDirectory, PreferredJsonName);
            if (File.Exists(preferredJson))
            {
                candidatePaths.Add(preferredJson);
            }

            var preferredText = Path.Combine(animationDirectory, PreferredTextName);
            if (File.Exists(preferredText))
            {
                candidatePaths.Add(preferredText);
            }

            candidatePaths.AddRange(
                Directory.EnumerateFiles(animationDirectory, "*.json")
                    .Where(path => !path.Equals(preferredJson, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc));

            candidatePaths.AddRange(
                Directory.EnumerateFiles(animationDirectory, "*.txt")
                    .Where(path => !path.Equals(preferredText, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc));
        }

        foreach (var candidatePath in candidatePaths)
        {
            try
            {
                sequence = LoadFromFile(candidatePath);
                loadedPath = candidatePath;
                return true;
            }
            catch
            {
            }
        }

        sequence = null!;
        loadedPath = string.Empty;
        return false;
    }

    public static AnimationSequence LoadFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("The animation file was not found.", filePath);
        }

        var extension = Path.GetExtension(filePath);
        return extension.ToLowerInvariant() switch
        {
            ".json" => LoadJson(filePath),
            ".txt" => LoadText(filePath),
            _ => throw new InvalidOperationException("Only Ascii-Motion JSON and text exports are supported.")
        };
    }

    public static string GetAnimationDisplayName(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Unknown";
        }

        var fileName = Path.GetFileName(filePath);
        if (!fileName.Equals(PreferredJsonName, StringComparison.OrdinalIgnoreCase)
            && !fileName.Equals(PreferredTextName, StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return fileName;
        }

        var displayNamePath = Path.Combine(directory, PreferredDisplayNameName);
        if (!File.Exists(displayNamePath))
        {
            return fileName;
        }

        var displayName = File.ReadAllText(displayNamePath).Trim();
        return string.IsNullOrWhiteSpace(displayName)
            ? fileName
            : displayName;
    }

    public static void SavePreferredAnimationDisplayName(string displayName)
    {
        var displayNamePath = Path.Combine(GetAnimationDirectory(), PreferredDisplayNameName);
        File.WriteAllText(displayNamePath, displayName.Trim());
    }

    private static AnimationSequence LoadJson(string filePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(filePath));
        var root = document.RootElement;

        if (!root.TryGetProperty("frames", out var framesElement) || framesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("The JSON export does not contain a frames array.");
        }

        var loopAnimation = true;
        if (root.TryGetProperty("animation", out var animationElement)
            && animationElement.ValueKind == JsonValueKind.Object
            && animationElement.TryGetProperty("looping", out var loopingElement)
            && loopingElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            loopAnimation = loopingElement.GetBoolean();
        }

        var canvasWidth = TryReadCanvasDimension(root, "width");
        var canvasHeight = TryReadCanvasDimension(root, "height");

        var frames = new List<AnimationFrame>();
        foreach (var frameElement in framesElement.EnumerateArray())
        {
            frames.Add(ParseJsonFrame(frameElement, canvasWidth, canvasHeight));
        }

        if (frames.Count == 0)
        {
            throw new InvalidOperationException("The JSON export does not contain any animation frames.");
        }

        return new AnimationSequence(frames, loopAnimation, canvasWidth, canvasHeight);
    }

    private static AnimationFrame ParseJsonFrame(JsonElement frameElement, int canvasWidth, int canvasHeight)
    {
        if (frameElement.ValueKind is JsonValueKind.String or JsonValueKind.Array)
        {
            var rawContent = ReadFrameContent(frameElement);
            var normalizedContent = NormalizeContentToCanvas(
                string.IsNullOrEmpty(rawContent) ? " " : rawContent,
                canvasWidth,
                canvasHeight);

            return new AnimationFrame(
                NormalizeLineEndings(normalizedContent),
                TimeSpan.FromMilliseconds(160));
        }

        if (frameElement.ValueKind != JsonValueKind.Object
            || !frameElement.TryGetProperty("content", out var contentElement))
        {
            throw new InvalidOperationException("A JSON frame is missing its content.");
        }

        var content = ReadFrameContent(contentElement);
        if (string.IsNullOrWhiteSpace(content)
            && frameElement.TryGetProperty("contentString", out var contentStringElement)
            && contentStringElement.ValueKind == JsonValueKind.String)
        {
            content = contentStringElement.GetString() ?? string.Empty;
        }

        if (string.IsNullOrEmpty(content))
        {
            content = " ";
        }

        content = NormalizeContentToCanvas(content, canvasWidth, canvasHeight);

        var duration = TimeSpan.FromMilliseconds(160);
        if (frameElement.TryGetProperty("duration", out var durationElement)
            && durationElement.ValueKind == JsonValueKind.Number
            && durationElement.TryGetDouble(out var durationMilliseconds))
        {
            duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        }

        var foregroundColors = TryReadForegroundColors(frameElement);
        return new AnimationFrame(NormalizeLineEndings(content), duration, foregroundColors);
    }

    private static string ReadFrameContent(JsonElement contentElement)
    {
        return contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                Environment.NewLine,
                contentElement.EnumerateArray().Select(line => line.GetString() ?? string.Empty)),
            _ => string.Empty
        };
    }

    private static AnimationSequence LoadText(string filePath)
    {
        var lines = File.ReadAllLines(filePath).ToList();
        var startIndex = GetTextExportStartIndex(lines);
        var frames = new List<AnimationFrame>();
        var currentFrameLines = new List<string>();
        var skipSeparatorBlankLine = false;

        for (var index = startIndex; index < lines.Count; index++)
        {
            var line = lines[index];

            if (line.Trim() == ",")
            {
                RemoveSingleTrailingSeparatorLine(currentFrameLines);
                AddTextFrame(frames, currentFrameLines);
                currentFrameLines.Clear();
                skipSeparatorBlankLine = true;
                continue;
            }

            if (skipSeparatorBlankLine && string.IsNullOrWhiteSpace(line))
            {
                skipSeparatorBlankLine = false;
                continue;
            }

            skipSeparatorBlankLine = false;
            currentFrameLines.Add(line);
        }

        AddTextFrame(frames, currentFrameLines);

        if (frames.Count == 0)
        {
            throw new InvalidOperationException("The text export does not contain any animation frames.");
        }

        return new AnimationSequence(frames, looping: true);
    }

    private static int GetTextExportStartIndex(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0 || !lines[0].StartsWith("ASCII Motion Text Export", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Trim() != "---")
            {
                continue;
            }

            var firstFrameLineIndex = index + 1;
            if (firstFrameLineIndex < lines.Count && string.IsNullOrWhiteSpace(lines[firstFrameLineIndex]))
            {
                firstFrameLineIndex++;
            }

            return firstFrameLineIndex;
        }

        return 0;
    }

    private static void RemoveSingleTrailingSeparatorLine(List<string> frameLines)
    {
        if (frameLines.Count > 0 && string.IsNullOrWhiteSpace(frameLines[^1]))
        {
            frameLines.RemoveAt(frameLines.Count - 1);
        }
    }

    private static void AddTextFrame(List<AnimationFrame> frames, List<string> frameLines)
    {
        if (frameLines.Count == 0)
        {
            return;
        }

        var content = string.Join(Environment.NewLine, frameLines);
        frames.Add(new AnimationFrame(NormalizeLineEndings(content), TimeSpan.FromMilliseconds(160)));
    }

    private static string NormalizeLineEndings(string content)
    {
        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private static string NormalizeContentToCanvas(string content, int canvasWidth, int canvasHeight)
    {
        var lines = NormalizeLineEndings(content)
            .Replace(Environment.NewLine, "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();

        if (canvasHeight > 0)
        {
            while (lines.Count < canvasHeight)
            {
                lines.Add(string.Empty);
            }
        }

        if (canvasWidth > 0)
        {
            for (var index = 0; index < lines.Count; index++)
            {
                lines[index] = lines[index].PadRight(canvasWidth);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyDictionary<string, string> TryReadForegroundColors(JsonElement frameElement)
    {
        if (!frameElement.TryGetProperty("colors", out var colorsElement)
            || colorsElement.ValueKind != JsonValueKind.Object
            || !colorsElement.TryGetProperty("foreground", out var foregroundElement))
        {
            return new Dictionary<string, string>();
        }

        return ReadColorMap(foregroundElement);
    }

    private static IReadOnlyDictionary<string, string> ReadColorMap(JsonElement colorElement)
    {
        if (colorElement.ValueKind == JsonValueKind.Object)
        {
            return colorElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "#FFFFFF");
        }

        if (colorElement.ValueKind == JsonValueKind.String)
        {
            var rawJson = colorElement.GetString();
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return new Dictionary<string, string>();
            }

            using var colorDocument = JsonDocument.Parse(rawJson);
            if (colorDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>();
            }

            return colorDocument.RootElement.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "#FFFFFF");
        }

        return new Dictionary<string, string>();
    }

    private static int TryReadCanvasDimension(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty("canvas", out var canvasElement)
            && canvasElement.ValueKind == JsonValueKind.Object
            && canvasElement.TryGetProperty(propertyName, out var directValue)
            && directValue.ValueKind == JsonValueKind.Number
            && directValue.TryGetInt32(out var canvasDimension))
        {
            return canvasDimension;
        }

        if (root.TryGetProperty("metadata", out var metadataElement)
            && metadataElement.ValueKind == JsonValueKind.Object
            && metadataElement.TryGetProperty("canvasSize", out var canvasSizeElement)
            && canvasSizeElement.ValueKind == JsonValueKind.Object
            && canvasSizeElement.TryGetProperty(propertyName, out var metadataValue)
            && metadataValue.ValueKind == JsonValueKind.Number
            && metadataValue.TryGetInt32(out var metadataDimension))
        {
            return metadataDimension;
        }

        return 0;
    }
}
