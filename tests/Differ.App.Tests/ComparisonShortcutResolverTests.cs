using System.Windows;
using System.Windows.Input;

namespace Differ.App.Tests;

public sealed class ComparisonShortcutResolverTests
{
    public static TheoryData<Key, Key, ModifierKeys, int>
        Mappings => new()
        {
            { Key.F7, Key.None, ModifierKeys.None, (int)ComparisonShortcut.PreviousDifference },
            { Key.F8, Key.None, ModifierKeys.None, (int)ComparisonShortcut.NextDifference },
            { Key.F9, Key.None, ModifierKeys.None, (int)ComparisonShortcut.ToggleTheme },
            { Key.U, Key.None, ModifierKeys.Control, (int)ComparisonShortcut.ToggleUnchanged },
            { Key.System, Key.Left, ModifierKeys.Alt, (int)ComparisonShortcut.FocusBase },
            { Key.System, Key.Right, ModifierKeys.Alt, (int)ComparisonShortcut.FocusMine },
            { Key.W, Key.None, ModifierKeys.Control, (int)ComparisonShortcut.CloseTab },
            { Key.F, Key.None, ModifierKeys.Control, (int)ComparisonShortcut.Find },
            { Key.G, Key.None, ModifierKeys.Control, (int)ComparisonShortcut.GoTo },
            { Key.S, Key.None, ModifierKeys.Control, (int)ComparisonShortcut.Save },
            { Key.S, Key.None, ModifierKeys.Control | ModifierKeys.Shift, (int)ComparisonShortcut.SaveAs },
            { Key.Z, Key.None, ModifierKeys.Control, (int)ComparisonShortcut.Undo },
            { Key.Y, Key.None, ModifierKeys.Control, (int)ComparisonShortcut.Redo },
            { Key.Z, Key.None, ModifierKeys.Control | ModifierKeys.Shift, (int)ComparisonShortcut.Redo },
            { Key.F5, Key.None, ModifierKeys.None, (int)ComparisonShortcut.Refresh },
            { Key.Tab, Key.None, ModifierKeys.Control, (int)ComparisonShortcut.NextTab },
            { Key.Tab, Key.None, ModifierKeys.Control | ModifierKeys.Shift, (int)ComparisonShortcut.PreviousTab },
            { Key.Escape, Key.None, ModifierKeys.None, (int)ComparisonShortcut.CloseWindow }
        };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void ResolveReturnsExpectedShortcut(
        Key key,
        Key systemKey,
        ModifierKeys modifiers,
        int expected)
    {
        Assert.Equal(
            (ComparisonShortcut)expected,
            ComparisonShortcutResolver.Resolve(key, systemKey, modifiers));
    }

    [Theory]
    [InlineData(Key.C, ModifierKeys.Control)]
    [InlineData(Key.F9, ModifierKeys.Control)]
    [InlineData(Key.Escape, ModifierKeys.Shift)]
    [InlineData(Key.F, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData(Key.F5, ModifierKeys.Control)]
    [InlineData(Key.Tab, ModifierKeys.None)]
    [InlineData(Key.Z, ModifierKeys.Alt)]
    [InlineData(Key.Y, ModifierKeys.Control | ModifierKeys.Shift)]
    public void ResolveDoesNotInterceptUnrelatedSystemOperations(
        Key key,
        ModifierKeys modifiers)
    {
        Assert.Equal(
            ComparisonShortcut.None,
            ComparisonShortcutResolver.Resolve(key, Key.None, modifiers));
    }

    [Theory]
    [InlineData(-5000, -5000, 1500, 860, 0, 0, 1500, 860)]
    [InlineData(1800, 1000, 1500, 860, 420, 220, 1500, 860)]
    [InlineData(500, 500, 600, 400, 500, 460, 980, 620)]
    [InlineData(0, 0, 9000, 9000, 0, 0, 1920, 1080)]
    public void RestoredWindowBoundsStayVisibleAndRespectMinimumSize(
        double left, double top, double width, double height,
        double expectedLeft, double expectedTop, double expectedWidth, double expectedHeight)
    {
        var result = DiffWindow.ClampWindowBounds(new Rect(left, top, width, height),
            new Rect(0, 0, 1920, 1080), new Size(980, 620));
        Assert.Equal(new Rect(expectedLeft, expectedTop, expectedWidth, expectedHeight), result);
    }

    [Fact]
    public void RestoredWindowRetainsValidNegativeMonitorCoordinates()
    {
        var requested = new Rect(-1600, 80, 1000, 700);
        Assert.Equal(requested, DiffWindow.ClampWindowBounds(requested,
            new Rect(-1920, 0, 3840, 1080), new Size(980, 620)));
    }
}
