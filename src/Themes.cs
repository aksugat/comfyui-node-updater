using System.Windows;
using System.Windows.Media;

namespace ComfyNodeUpdater;

/// <summary>A colour palette. Every brush the XAML asks for is derived from these, in Apply().</summary>
public sealed record Theme(
    string Id,
    string Name,
    string Bg,
    string Card,
    string CardAlt,
    string Line,
    string Text,
    string Dim,
    string Accent,
    string AccentHover,
    string Green,
    string Amber,
    string Red,
    bool Dark);

public static class Themes
{
    public static readonly Theme[] All =
    {
        new("midnight", "Midnight", "#0B0E13", "#121821", "#0E131B", "#202B3A", "#E6EDF3", "#94A3B8",
            "#4C8DFF", "#6AA2FF", "#56D364", "#E3B341", "#FF7B84", true),
        new("oled", "OLED", "#000000", "#0A0A0C", "#050507", "#1A1A1F", "#F5F7FA", "#8B93A1",
            "#3B82F6", "#60A5FA", "#4ADE80", "#FBBF24", "#F87171", true),
        new("nord", "Nord", "#2E3440", "#3B4252", "#333B4A", "#4C566A", "#ECEFF4", "#A8B3C4",
            "#88C0D0", "#9DD3E0", "#A3BE8C", "#EBCB8B", "#BF616A", true),
        new("dracula", "Dracula", "#282A36", "#343746", "#2F3241", "#44475A", "#F8F8F2", "#A6A9B8",
            "#BD93F9", "#CBA9FA", "#50FA7B", "#F1FA8C", "#FF5555", true),
        new("latte", "Latte", "#F3EFE6", "#FFFFFF", "#EAE4D8", "#D9D0C0", "#2A2520", "#6B6257",
            "#2563EB", "#1D4ED8", "#15803D", "#B45309", "#B91C1C", false),
    };

    public static Theme Current { get; private set; } = All[0];

    public static event Action? Changed;

    public static Theme ById(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];

    /// <summary>Replaces every palette entry the XAML asks for with this theme's colours.</summary>
    public static void Apply(Theme theme)
    {
        Current = theme;

        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var card = Color(theme.Card);
        var line = Color(theme.Line);
        var text = Color(theme.Text);
        var accent = Color(theme.Accent);

        Set(resources, "Bg", Color(theme.Bg));
        Set(resources, "Card", card);
        Set(resources, "CardAlt", Color(theme.CardAlt));
        Set(resources, "Line", line);
        Set(resources, "Text", text);
        Set(resources, "Dim", Color(theme.Dim));
        Set(resources, "Accent", accent);
        Set(resources, "AccentHover", Color(theme.AccentHover));
        Set(resources, "Green", Color(theme.Green));
        Set(resources, "Amber", Color(theme.Amber));
        Set(resources, "Red", Color(theme.Red));

        // derived so a palette only lists its real colours
        Set(resources, "ButtonBg", Mix(card, line, 0.45));
        Set(resources, "ButtonBorder", line);
        Set(resources, "ButtonHover", Mix(card, line, 0.85));
        Set(resources, "ButtonHoverBorder", Mix(line, text, 0.25));
        Set(resources, "InputBg", Color(theme.CardAlt));
        Set(resources, "InputBorder", Mix(line, text, 0.18));
        Set(resources, "ScrollThumb", Mix(line, text, 0.2));
        Set(resources, "Sha", theme.Dark ? Mix(Color(theme.Dim), Color(theme.Bg), 0.25) : Mix(Color(theme.Dim), text, 0.25));
        Set(resources, "AccentSoft", WithAlpha(accent, 0x24));
        Set(resources, "AccentSoftHover", WithAlpha(accent, 0x3D));
        Set(resources, "AccentLine", WithAlpha(accent, 0x66));
        Set(resources, "OnAccent", Luminance(accent) > 0.6 ? Color("#07101E") : Colors.White);

        Changed?.Invoke();
    }

    public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }

    private static Color WithAlpha(Color color, byte alpha) => System.Windows.Media.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Mix(Color a, Color b, double t)
    {
        byte Channel(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return System.Windows.Media.Color.FromRgb(Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    private static double Luminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
}
