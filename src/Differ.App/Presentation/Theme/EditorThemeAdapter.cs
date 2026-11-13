using System.Windows;
using System.Windows.Media;
using Differ.App.Services;

namespace Differ.App.Presentation.Theme;

internal static class EditorThemeAdapter
{
    internal static bool IsDark(FrameworkElement element) =>
        string.Equals(element.TryFindResource("Theme.Name") as string, "Dark", StringComparison.OrdinalIgnoreCase);

    internal static object CreatePalette(FrameworkElement element, double readingScale)
    {
        var target = element.TryFindResource(ThemeService.TargetThemeKey) as ResourceDictionary;
        string Colour(string key) => ToCssHex((target?[key] ?? element.TryFindResource(key)) as SolidColorBrush
            ?? throw new InvalidOperationException($"Missing theme brush: {key}"));

        return new
        {
            background = Colour("Dui.Surface.Content"),
            foreground = Colour("Dui.Text.Primary"),
            border = Colour("Dui.Border.Subtle"),
            focus = Colour("Dui.Focus"),
            chrome = Colour("Dui.Surface.PaneHeader"),
            muted = Colour("Dui.Text.Secondary"),
            selection = Colour("Editor.Selection"),
            inactiveSelection = Colour("Editor.InactiveSelection"),
            lineNumber = Colour("Dui.Text.Secondary"),
            scrollThumb = Colour("Dui.Scroll.Thumb"),
            scrollHover = Colour("Dui.Scroll.ThumbHover"),
            scrollPressed = Colour("Dui.Scroll.ThumbPressed"),
            railDivider = Colour("Diff.Rail.Divider"),
            railBand = Colour("Diff.Rail.Band"),
            railBandHover = Colour("Diff.Rail.BandHover"),
            railEdge = Colour("Diff.Rail.Edge"),
            railAdded = Colour("Diff.Overview.Added.Brush"),
            railDeleted = Colour("Diff.Overview.Deleted.Brush"),
            railCurrent = Colour("Dui.Text.Primary"),
            indent = Colour("Editor.Indent"),
            missing = Colour("Editor.Missing"),
            fontSize = (double)element.FindResource("Editor.FontSize") * readingScale,
            lineHeight = (double)element.FindResource("Editor.LineHeight") * readingScale,
            added = Colour("Diff.Row.Added.Bg"),
            deleted = Colour("Diff.Row.Deleted.Bg"),
            addedMarker = Colour("Diff.Row.Added.Border"),
            deletedMarker = Colour("Diff.Row.Deleted.Border"),
            addedText = Colour("Diff.Inline.Added.Brush"),
            deletedText = Colour("Diff.Inline.Deleted.Brush")
        };
    }

    internal static string ToCssHex(SolidColorBrush brush)
    {
        Color colour = brush.Color;
        byte alpha = (byte)Math.Round(colour.A * brush.Opacity, MidpointRounding.AwayFromZero);
        return alpha == byte.MaxValue
            ? $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}"
            : $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}{alpha:X2}";
    }
}
