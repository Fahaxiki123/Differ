using System.Windows;
using System.Windows.Controls;

namespace Differ.App.Controls;

internal sealed class SpreadsheetValueColumn : DataGridColumn
{
    private readonly bool _isBaseSide;
    private readonly int _columnIndex;

    public SpreadsheetValueColumn(bool isBaseSide, int columnIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(columnIndex);
        _isBaseSide = isBaseSide;
        _columnIndex = columnIndex;
        IsReadOnly = true;
    }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var presenter = new SpreadsheetCellPresenter(_isBaseSide, _columnIndex);
        presenter.UpdateRow(dataItem);
        return presenter;
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => GenerateElement(cell, dataItem);
}
