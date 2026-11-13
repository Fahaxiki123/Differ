using System.Windows;
using System.Windows.Documents;
using Differ.Core.Models;

namespace Differ.App.Controls;

/// <summary>
/// Shared foreground palette for inline (character level) diff emphasis in the
/// text and spreadsheet views. Uses <see cref="FrameworkContentElement.SetResourceReference" />
/// so a runtime theme switch re-resolves the brush on every existing
/// <see cref="Run" /> without re-creating the virtualized row cells or leaking
/// event subscriptions. Kept in one place so the read-only selectable detail
/// panes render identically to the virtualized row cells.
/// </summary>
internal static class InlineDiffPalette
{
    public static void ApplyChangedStyle(Run run, DiffKind kind)
    {
        run.FontWeight = FontWeights.SemiBold;
        run.SetResourceReference(TextElement.ForegroundProperty, GetForegroundKey(kind));
    }

    internal static string GetForegroundKey(DiffKind kind)
    {
        return kind switch
        {
            DiffKind.Added => "Diff.Inline.Added.Brush",
            DiffKind.Deleted => "Diff.Inline.Deleted.Brush",
            DiffKind.Moved => "Diff.Inline.Moved.Brush",
            DiffKind.Conflict => "Diff.Inline.Conflict.Brush",
            _ => "Diff.Inline.Modified.Brush"
        };
    }
}
