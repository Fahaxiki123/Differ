using System.Windows.Media;
using Differ.UI.Controls;

namespace Differ.UI.Icons;

internal static class IconGeometry
{
    // Original 16 DIP line drawings. Application branding stays in Differ.App.
    private static readonly Dictionary<AppIconKind, Geometry> Shapes = new()
    {
        [AppIconKind.Previous] = Parse("M3 10 L8 5 13 10"),
        [AppIconKind.Next] = Parse("M3 6 L8 11 13 6"),
        [AppIconKind.Search] = Parse("M10.8 10.8 L14 14 M12 7 A5 5 0 1 1 2 7 A5 5 0 1 1 12 7"),
        [AppIconKind.Refresh] = Parse("M13.6 7 A5.7 5.7 0 1 0 12.4 11.5 M13.6 3.2 V7 H9.8"),
        [AppIconKind.Theme] = Parse("M10.2 2.2 A6 6 0 1 0 13.8 11 A5.4 5.4 0 0 1 10.2 2.2 Z"),
        [AppIconKind.More] = Parse("M4 8 h.1 M8 8 h.1 M12 8 h.1"),
        [AppIconKind.Filter] = Parse("M2 3 H14 L9.5 8 V12.5 L6.5 14 V8 Z"),
        [AppIconKind.Undo] = Parse("M5.6 3.4 L2.6 6.5 5.6 9.6 M2.6 6.5 H9.7 A3.6 3.6 0 0 1 9.7 13.7 H8"),
        [AppIconKind.Redo] = Parse("M10.4 3.4 L13.4 6.5 10.4 9.6 M13.4 6.5 H6.3 A3.6 3.6 0 0 0 6.3 13.7 H8"),
        [AppIconKind.Edit] = Parse("M3 10.5 L10.7 2.8 A1.1 1.1 0 0 1 12.3 2.8 L13.2 3.7 A1.1 1.1 0 0 1 13.2 5.3 L5.5 13 L2.4 13.6 Z M9.5 4 L12 6.5"),
        [AppIconKind.Save] = Parse("M2.8 2.8 H11.5 L13.2 4.5 V13.2 H2.8 Z M5.1 3 V6.2 H10.2 V3 M5.1 13 V9.4 H10.9 V13"),
        [AppIconKind.ChevronDown] = Parse("M4 6.2 L8 10.2 12 6.2"),
        [AppIconKind.ChevronRight] = Parse("M6 4 L10 8 6 12"),
        [AppIconKind.Close] = Parse("M4.2 4.2 L11.8 11.8 M11.8 4.2 L4.2 11.8"),
        [AppIconKind.File] = Parse("M4 2 H9.2 L12 4.8 V14 H4 Z M9.2 2 V4.8 H12 M6 8 H10 M6 10.5 H10"),
        [AppIconKind.Check] = Parse("M3.5 8.2 L6.6 11.2 12.5 5.1"),
        [AppIconKind.Add] = Parse("M3.5 8 H12.5 M8 3.5 V12.5"),
        [AppIconKind.Remove] = Parse("M3.5 8 H12.5"),
        [AppIconKind.Warning] = Parse("M8 2.1 L14.4 13.4 H1.6 Z M8 6 V9 M8 11.4 h.1"),
        [AppIconKind.Info] = Parse("M14 8 A6 6 0 1 1 2 8 A6 6 0 1 1 14 8 M8 7.3 V11.3 M8 4.8 h.1"),
    };

    public static Geometry Get(AppIconKind kind) =>
        Shapes.TryGetValue(kind, out var geometry) ? geometry : Shapes[AppIconKind.File];

    private static Geometry Parse(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }
}
