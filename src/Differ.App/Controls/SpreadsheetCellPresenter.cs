using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Differ.App.ViewModels;
using Differ.Core.Models;

namespace Differ.App.Controls;

/// <summary>
/// Reuses one text renderer when a virtualized cell receives another row.
/// A single binding updates the value, semantic background and tooltip together.
/// </summary>
internal sealed class SpreadsheetCellPresenter : Border
{
    private static readonly FontFamily CellFont = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    private readonly InlineDiffTextBlock _text;
    private DiffKind _backgroundKind;

    public static readonly DependencyProperty CellProperty = DependencyProperty.Register(
        nameof(Cell), typeof(SpreadsheetCellViewModel), typeof(SpreadsheetCellPresenter),
        new FrameworkPropertyMetadata(null, OnCellChanged));

    public SpreadsheetCellPresenter()
    {
        Name = "CellChrome";
        MinHeight = 27;
        // Match Ui.CellPadding and Ui.SpreadsheetFontSize without allocating a
        // resource dictionary or style for each recycled cell presenter.
        Padding = new Thickness(8, 0, 8, 0);
        ClipToBounds = true;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        _text = new InlineDiffTextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = CellFont,
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap
        };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        Child = _text;
    }

    public SpreadsheetCellViewModel? Cell
    {
        get => (SpreadsheetCellViewModel?)GetValue(CellProperty);
        set => SetValue(CellProperty, value);
    }

    private static void OnCellChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        ((SpreadsheetCellPresenter)source).ApplyCell(args.NewValue as SpreadsheetCellViewModel);
    }

    private void ApplyCell(SpreadsheetCellViewModel? value)
    {
        var kind = value?.Kind ?? DiffKind.Unchanged;
        if (_backgroundKind != kind)
        {
            _backgroundKind = kind;
            SetResourceReference(BackgroundProperty, BackgroundResource(kind));
        }

        ToolTip = value?.ToolTipText;
        _text.DiffKind = kind;
        _text.Segments = value?.Segments;
    }

    private static string BackgroundResource(DiffKind kind) => kind switch
    {
        DiffKind.Added => "Diff.Cell.Added",
        DiffKind.Deleted => "Diff.Cell.Deleted",
        DiffKind.Modified => "Diff.Cell.Modified",
        DiffKind.Moved => "Diff.Cell.Moved",
        DiffKind.Conflict => "Diff.Cell.Conflict",
        DiffKind.Unsupported => "Diff.Cell.Unsupported",
        _ => "PanelBrush"
    };
}
