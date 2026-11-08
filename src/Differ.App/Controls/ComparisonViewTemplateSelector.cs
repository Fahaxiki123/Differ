using System.Windows;
using System.Windows.Controls;
using Differ.App.ViewModels;

namespace Differ.App.Controls;

public sealed class ComparisonViewTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SpreadsheetTemplate { get; set; }

    public DataTemplate? TextTemplate { get; set; }

    public DataTemplate? WordTemplate { get; set; }

    public DataTemplate? StandardTemplate { get; set; }

    public override DataTemplate? SelectTemplate(
        object item,
        DependencyObject container)
    {
        if (item is not DiffWindowViewModel viewModel)
        {
            return base.SelectTemplate(item, container);
        }

        var viewKind = GetViewKind(
            viewModel.IsSpreadsheetView,
            viewModel.IsTextView,
            viewModel.IsWordView);
        if (viewKind == ComparisonViewKind.Spreadsheet)
        {
            return SpreadsheetTemplate;
        }

        if (viewKind == ComparisonViewKind.Word)
        {
            return WordTemplate;
        }

        return viewKind == ComparisonViewKind.Text
            ? TextTemplate
            : StandardTemplate ?? TextTemplate;
    }

    internal static ComparisonViewKind GetViewKind(
        bool isSpreadsheet,
        bool isText,
        bool isWord)
    {
        if (isSpreadsheet)
        {
            return ComparisonViewKind.Spreadsheet;
        }

        if (isWord)
        {
            return ComparisonViewKind.Word;
        }

        return isText ? ComparisonViewKind.Text : ComparisonViewKind.Standard;
    }
}

internal enum ComparisonViewKind
{
    Standard,
    Text,
    Word,
    Spreadsheet
}
