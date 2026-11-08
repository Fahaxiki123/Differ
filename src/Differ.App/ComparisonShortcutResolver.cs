using System.Windows.Input;

namespace Differ.App;

internal enum ComparisonShortcut
{
    None,
    PreviousDifference,
    NextDifference,
    ToggleTheme,
    ToggleUnchanged,
    FocusBase,
    FocusMine,
    CloseTab,
    CloseWindow,
    Find,
    GoTo,
    Refresh,
    NextTab,
    PreviousTab,
    Save,
    SaveAs,
    Undo,
    Redo
}

internal static class ComparisonShortcutResolver
{
    public static ComparisonShortcut Resolve(
        Key key,
        Key systemKey,
        ModifierKeys modifiers)
    {
        var resolvedKey = key == Key.System ? systemKey : key;
        return (resolvedKey, modifiers) switch
        {
            (Key.F7, ModifierKeys.None) =>
                ComparisonShortcut.PreviousDifference,
            (Key.F8, ModifierKeys.None) =>
                ComparisonShortcut.NextDifference,
            (Key.F9, ModifierKeys.None) => ComparisonShortcut.ToggleTheme,
            (Key.U, ModifierKeys.Control) => ComparisonShortcut.ToggleUnchanged,
            (Key.Left, ModifierKeys.Alt) => ComparisonShortcut.FocusBase,
            (Key.Right, ModifierKeys.Alt) => ComparisonShortcut.FocusMine,
            (Key.W, ModifierKeys.Control) => ComparisonShortcut.CloseTab,
            (Key.F, ModifierKeys.Control) => ComparisonShortcut.Find,
            (Key.G, ModifierKeys.Control) => ComparisonShortcut.GoTo,
            (Key.S, ModifierKeys.Control) => ComparisonShortcut.Save,
            (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => ComparisonShortcut.SaveAs,
            (Key.Z, ModifierKeys.Control) => ComparisonShortcut.Undo,
            (Key.Y, ModifierKeys.Control) => ComparisonShortcut.Redo,
            (Key.Z, ModifierKeys.Control | ModifierKeys.Shift) => ComparisonShortcut.Redo,
            (Key.F5, ModifierKeys.None) => ComparisonShortcut.Refresh,
            (Key.Tab, ModifierKeys.Control) => ComparisonShortcut.NextTab,
            (Key.Tab, ModifierKeys.Control | ModifierKeys.Shift) => ComparisonShortcut.PreviousTab,
            (Key.Escape, ModifierKeys.None) => ComparisonShortcut.CloseWindow,
            _ => ComparisonShortcut.None
        };
    }
}
