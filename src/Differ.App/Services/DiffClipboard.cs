using System.Windows;
using Differ.App.ViewModels;

namespace Differ.App.Services;

/// <summary>
/// Clipboard helpers for the three comparison views. The views use explicit
/// copy commands instead of <c>DataGrid.ClipboardCopyMode</c> because every
/// column is a template column, which would otherwise copy empty strings.
/// </summary>
internal static class DiffClipboard
{
    private const string SideSeparator = "\t";

    public static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process is holding the clipboard open. Copying is a
            // convenience action, so fail silently rather than tearing down
            // the comparison window.
        }
        catch (OutOfMemoryException)
        {
            // Extremely large payloads can fail to marshal into the clipboard.
        }
    }

    public static void CopyBothSides(string? baseText, string? mineText)
    {
        CopyText(FormatBothSides(baseText, mineText));
    }

    public static string FormatBothSides(string? baseText, string? mineText)
    {
        return string.Concat(
            baseText ?? string.Empty,
            SideSeparator,
            mineText ?? string.Empty);
    }

    public static string FormatWordSide(
        WordDisplayRowViewModel row,
        bool useBaseSide)
    {
        if (row.Paragraph is { } paragraph)
        {
            return useBaseSide ? paragraph.OldText : paragraph.NewText;
        }

        if (row.IsGroup)
        {
            return string.Join(Environment.NewLine, row.ContentRows
                .Where(item => useBaseSide ? item.OldIndex is not null : item.NewIndex is not null)
                .Select(item => useBaseSide ? item.OldText : item.NewText));
        }

        return string.Join(
            SideSeparator,
            row.Cells.Select(
                cell => useBaseSide ? cell.OldText : cell.NewText));
    }

    public static string FormatSpreadsheetSide(
        SpreadsheetRowViewModel row,
        bool useBaseSide)
    {
        var cells = useBaseSide ? row.BaseCells : row.MineCells;
        return string.Join(
            SideSeparator,
            cells.Select(cell => cell.DetailsText));
    }
}
