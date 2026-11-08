using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Differ.App.Controls;

internal sealed class SpreadsheetValueColumn : DataGridColumn
{
    private readonly Binding _cellBinding;

    public SpreadsheetValueColumn(string cellsPropertyName, int columnIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cellsPropertyName);
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);
        IsReadOnly = true;
        _cellBinding = new Binding($"{cellsPropertyName}[{columnIndex}]")
        {
            Mode = BindingMode.OneWay
        };
    }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var presenter = new SpreadsheetCellPresenter();
        BindingOperations.SetBinding(presenter, SpreadsheetCellPresenter.CellProperty, _cellBinding);
        return presenter;
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => GenerateElement(cell, dataItem);
}
