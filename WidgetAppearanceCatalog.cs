namespace DesktopCopilot;

public enum FrameStylePreset
{
    ShaderRing,
    ClassicChrome,
    NeonHalo,
    MinimalGlass
}

public enum ColorPalettePreset
{
    Copilot,
    Amber,
    Emerald,
    Mono
}

public sealed record FrameStyleSpec(
    bool UsePngFrame,
    bool ShowOuterShell,
    double PngScale,
    double ShellOpacity,
    double OuterMargin,
    double OuterStrokeThickness,
    bool ShowAccentRing,
    double AccentMargin,
    double AccentStrokeThickness,
    double AccentOpacity,
    bool ShowInnerRing,
    double InnerMargin,
    double InnerStrokeThickness,
    double InnerOpacity);

public sealed record WidgetColorPalette(
    string IdleShellFill,
    string IdleShellStroke,
    string IdleGlowFill,
    string IdleFaceColor,
    string FrameAccentStroke,
    string FrameInnerStroke,
    string AnimationPrimaryLight,
    string AnimationPrimary,
    string AnimationPrimaryDark,
    string AnimationSecondaryLight,
    string AnimationSecondary,
    string AnimationSecondaryDark,
    string AnimationAccent,
    string AnimationShadow,
    string AnimationHighlight);

public static class WidgetAppearanceCatalog
{
    public static IReadOnlyList<FrameStylePreset> FrameStyles { get; } =
        new[]
        {
            FrameStylePreset.ShaderRing,
            FrameStylePreset.ClassicChrome,
            FrameStylePreset.NeonHalo,
            FrameStylePreset.MinimalGlass
        };

    public static IReadOnlyList<ColorPalettePreset> ColorPalettes { get; } =
        new[]
        {
            ColorPalettePreset.Copilot,
            ColorPalettePreset.Amber,
            ColorPalettePreset.Emerald,
            ColorPalettePreset.Mono
        };

    public static string GetDisplayName(FrameStylePreset preset)
    {
        return preset switch
        {
            FrameStylePreset.ShaderRing => "Soft Ring",
            FrameStylePreset.ClassicChrome => "Classic Chrome",
            FrameStylePreset.NeonHalo => "Neon Halo",
            FrameStylePreset.MinimalGlass => "Minimal Glass",
            _ => preset.ToString()
        };
    }

    public static string GetDisplayName(ColorPalettePreset preset)
    {
        return preset switch
        {
            ColorPalettePreset.Copilot => "Copilot",
            ColorPalettePreset.Amber => "Amber",
            ColorPalettePreset.Emerald => "Emerald",
            ColorPalettePreset.Mono => "Mono",
            _ => preset.ToString()
        };
    }

    public static FrameStyleSpec GetFrameStyle(FrameStylePreset preset)
    {
        return preset switch
        {
            FrameStylePreset.ShaderRing => new FrameStyleSpec(
                UsePngFrame: false,
                ShowOuterShell: true,
                PngScale: 1.0,
                ShellOpacity: 0.9,
                OuterMargin: 6,
                OuterStrokeThickness: 2,
                ShowAccentRing: true,
                AccentMargin: 11,
                AccentStrokeThickness: 2,
                AccentOpacity: 0.7,
                ShowInnerRing: true,
                InnerMargin: 18,
                InnerStrokeThickness: 1,
                InnerOpacity: 0.45),
            FrameStylePreset.ClassicChrome => new FrameStyleSpec(
                UsePngFrame: false,
                ShowOuterShell: true,
                PngScale: 1.0,
                ShellOpacity: 1.0,
                OuterMargin: 8,
                OuterStrokeThickness: 2.5,
                ShowAccentRing: true,
                AccentMargin: 12,
                AccentStrokeThickness: 3,
                AccentOpacity: 0.9,
                ShowInnerRing: true,
                InnerMargin: 18,
                InnerStrokeThickness: 1.5,
                InnerOpacity: 0.65),
            FrameStylePreset.NeonHalo => new FrameStyleSpec(
                UsePngFrame: false,
                ShowOuterShell: true,
                PngScale: 1.0,
                ShellOpacity: 0.95,
                OuterMargin: 6,
                OuterStrokeThickness: 3,
                ShowAccentRing: true,
                AccentMargin: 10,
                AccentStrokeThickness: 4,
                AccentOpacity: 1.0,
                ShowInnerRing: true,
                InnerMargin: 18,
                InnerStrokeThickness: 2,
                InnerOpacity: 0.85),
            FrameStylePreset.MinimalGlass => new FrameStyleSpec(
                UsePngFrame: false,
                ShowOuterShell: true,
                PngScale: 1.0,
                ShellOpacity: 0.72,
                OuterMargin: 10,
                OuterStrokeThickness: 1.25,
                ShowAccentRing: true,
                AccentMargin: 16,
                AccentStrokeThickness: 1.25,
                AccentOpacity: 0.55,
                ShowInnerRing: false,
                InnerMargin: 20,
                InnerStrokeThickness: 1,
                InnerOpacity: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
        };
    }

    public static WidgetColorPalette GetPalette(ColorPalettePreset preset)
    {
        return preset switch
        {
            ColorPalettePreset.Copilot => new WidgetColorPalette(
                IdleShellFill: "#FF1A1D24",
                IdleShellStroke: "#40FFFFFF",
                IdleGlowFill: "#2800A4FF",
                IdleFaceColor: "#FFF7FAFF",
                FrameAccentStroke: "#A0C778FF",
                FrameInnerStroke: "#55FFFFFF",
                AnimationPrimaryLight: "#7DCBFF",
                AnimationPrimary: "#4A80FF",
                AnimationPrimaryDark: "#0D3DFF",
                AnimationSecondaryLight: "#C778FF",
                AnimationSecondary: "#9654FF",
                AnimationSecondaryDark: "#6C40FF",
                AnimationAccent: "#1AFABC",
                AnimationShadow: "#060449",
                AnimationHighlight: "#FFFFFF"),
            ColorPalettePreset.Amber => new WidgetColorPalette(
                IdleShellFill: "#FF241A12",
                IdleShellStroke: "#FFB17A4D",
                IdleGlowFill: "#33FF9B47",
                IdleFaceColor: "#FFFFF2E7",
                FrameAccentStroke: "#D6FFBE7A",
                FrameInnerStroke: "#80FFD7AE",
                AnimationPrimaryLight: "#FFD9AB",
                AnimationPrimary: "#FFB865",
                AnimationPrimaryDark: "#B56A1D",
                AnimationSecondaryLight: "#FFD0B5",
                AnimationSecondary: "#FF9452",
                AnimationSecondaryDark: "#A94D22",
                AnimationAccent: "#FFE46B",
                AnimationShadow: "#4A2410",
                AnimationHighlight: "#FFF8EE"),
            ColorPalettePreset.Emerald => new WidgetColorPalette(
                IdleShellFill: "#FF14211D",
                IdleShellStroke: "#FF3DAA89",
                IdleGlowFill: "#3300D48C",
                IdleFaceColor: "#FFEFFFF7",
                FrameAccentStroke: "#CC7EFFD8",
                FrameInnerStroke: "#7096FFE1",
                AnimationPrimaryLight: "#B7FFF0",
                AnimationPrimary: "#58D6B3",
                AnimationPrimaryDark: "#127A62",
                AnimationSecondaryLight: "#98FFD5",
                AnimationSecondary: "#31C48D",
                AnimationSecondaryDark: "#0E7750",
                AnimationAccent: "#52FFD2",
                AnimationShadow: "#062E27",
                AnimationHighlight: "#F3FFF9"),
            ColorPalettePreset.Mono => new WidgetColorPalette(
                IdleShellFill: "#FF191919",
                IdleShellStroke: "#FFB8B8B8",
                IdleGlowFill: "#30D0D0D0",
                IdleFaceColor: "#FFF2F2F2",
                FrameAccentStroke: "#CCFFFFFF",
                FrameInnerStroke: "#709B9B9B",
                AnimationPrimaryLight: "#F2F2F2",
                AnimationPrimary: "#CFCFCF",
                AnimationPrimaryDark: "#8A8A8A",
                AnimationSecondaryLight: "#E6E6E6",
                AnimationSecondary: "#B8B8B8",
                AnimationSecondaryDark: "#6F6F6F",
                AnimationAccent: "#FFFFFF",
                AnimationShadow: "#303030",
                AnimationHighlight: "#FFFFFF"),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
        };
    }
}
