using System.Windows;

namespace Differ.App;

public partial class DiffWindow
{
    private void EditSpreadsheetRules_Click(object sender, RoutedEventArgs e)
    {
        if (_workspace.SelectedTab is not { } tab) return;
        var dialog = new SpreadsheetRulesWindow(tab.Options.SpreadsheetRules) { Owner = this };
        if (dialog.ShowDialog() == true) tab.Options.SpreadsheetRules = dialog.Rules;
    }
}
