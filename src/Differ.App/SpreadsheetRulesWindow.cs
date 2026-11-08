using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Differ.App.Resources;
using Differ.Core.Models;

namespace Differ.App;

/// <summary>Edits layout metadata only. Applying and persisting remain explicit toolbar actions.</summary>
internal sealed class SpreadsheetRulesWindow : Window
{
    private readonly List<SpreadsheetRule> _rules;
    private readonly ListBox _list = new() { MinWidth = 180, Margin = new Thickness(0, 0, 16, 0) };
    private readonly TextBox _file = new();
    private readonly TextBox _sheet = new();
    private readonly TextBox _header = new();
    private readonly TextBox _data = new();
    private readonly TextBox _auxiliary = new();
    private readonly TextBox _keys = new();
    private readonly TextBox _frozen = new();
    private readonly TextBox _columns = new() { AcceptsReturn = true, MinHeight = 64, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private string _loadedFingerprint = string.Empty;

    public SpreadsheetRulesWindow(IReadOnlyList<SpreadsheetRule> rules)
    {
        _rules = rules.ToList();
        Title = Strings.SpreadsheetRulesTitle;
        Width = 760; Height = 640; MinWidth = 620; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(_list);
        _list.SelectionChanged += (_, _) => LoadSelected();
        var content = new StackPanel();
        Grid.SetColumn(content, 1);
        grid.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(grid.Children[^1], 1);
        content.Children.Add(new TextBlock
        {
            Text = Strings.SpreadsheetRulesHint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        Field(Strings.SpreadsheetRuleFileLabel, _file);
        Field(Strings.SpreadsheetRuleSheetLabel, _sheet);
        Field(Strings.SpreadsheetRuleHeaderLabel, _header);
        Field(Strings.SpreadsheetRuleDataLabel, _data);
        Field(Strings.SpreadsheetRuleAuxiliaryLabel, _auxiliary);
        Field(Strings.SpreadsheetRuleKeysLabel, _keys);
        Field(Strings.SpreadsheetRuleFrozenLabel, _frozen);
        Field(Strings.SpreadsheetRuleMappingsLabel, _columns);
        content.Children.Add(_error);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        content.Children.Add(actions);
        Button(Strings.SpreadsheetRuleAdd, () => { _list.SelectedIndex = -1; Load(new SpreadsheetRule()); });
        Button(Strings.SpreadsheetRuleSave, () => SaveSelected());
        Button(Strings.SpreadsheetRuleDelete, () => { if (_list.SelectedIndex >= 0) { _rules.RemoveAt(_list.SelectedIndex); Refresh(); } });
        Button(Strings.SpreadsheetRuleDone, () => { if (EditorFingerprint() == _loadedFingerprint || SaveSelected()) DialogResult = true; });
        Content = grid;
        Refresh();
        if (_rules.Count > 0) _list.SelectedIndex = 0; else Load(new SpreadsheetRule());

        void Field(string label, TextBox box)
        {
            AutomationProperties.SetName(box, label);
            content.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 3) });
            content.Children.Add(box);
        }
        void Button(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 5, 10, 5) };
            button.Click += (_, _) => action();
            actions.Children.Add(button);
        }
    }

    public IReadOnlyList<SpreadsheetRule> Rules => _rules.ToArray();

    private void Refresh() => _list.ItemsSource = _rules.Select(rule => string.Format(CultureInfo.CurrentCulture, Strings.SpreadsheetRuleListFormat, rule.FilePattern, rule.SheetName, rule.HeaderRow)).ToArray();
    private void LoadSelected() { if (_list.SelectedIndex >= 0) Load(_rules[_list.SelectedIndex]); }
    private void Load(SpreadsheetRule rule)
    {
        _file.Text = rule.FilePattern; _sheet.Text = rule.SheetName;
        _header.Text = rule.HeaderRow.ToString(CultureInfo.InvariantCulture); _data.Text = rule.DataStartRow.ToString(CultureInfo.InvariantCulture);
        _auxiliary.Text = string.Join(',', rule.AuxiliaryHeaderRows); _keys.Text = string.Join(',', rule.KeyColumns);
        _frozen.Text = rule.FrozenColumns.ToString(CultureInfo.InvariantCulture);
        _columns.Text = string.Join(Environment.NewLine, rule.ColumnOverrides.Select(item => $"{item.OldColumn ?? "-"}={item.NewColumn ?? "-"}"));
        _error.Text = string.Empty;
        _loadedFingerprint = EditorFingerprint();
    }

    private string EditorFingerprint() => string.Join('\0', _file.Text, _sheet.Text, _header.Text, _data.Text,
        _auxiliary.Text, _keys.Text, _frozen.Text, _columns.Text);

    private bool SaveSelected()
    {
        if (!int.TryParse(_header.Text, out var header) || !int.TryParse(_data.Text, out var data)
            || !int.TryParse(_frozen.Text, out var frozen) || frozen < 0 || frozen > 16384
            || header < 0 || data <= header || data > 100000 || string.IsNullOrWhiteSpace(_file.Text) || string.IsNullOrWhiteSpace(_sheet.Text))
        { _error.Text = Strings.SpreadsheetRuleInvalidRows; return false; }
        var auxiliary = new List<int>();
        foreach (var item in Split(_auxiliary.Text))
        {
            if (!int.TryParse(item, out var number) || number < 1 || number >= data)
            { _error.Text = Strings.SpreadsheetRuleInvalidAuxiliary; return false; }
            auxiliary.Add(number);
        }
        var mappings = new List<SpreadsheetColumnOverride>();
        var oldUsed = new HashSet<string>(StringComparer.Ordinal);
        var newUsed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in _columns.Text.Split(['\r', '\n', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.ToUpperInvariant().Split('=', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !ValidColumn(parts[0]) || !ValidColumn(parts[1]) || parts[0] == "-" && parts[1] == "-"
                || parts[0] != "-" && !oldUsed.Add(parts[0]) || parts[1] != "-" && !newUsed.Add(parts[1]))
            { _error.Text = Strings.SpreadsheetRuleInvalidMapping; return false; }
            mappings.Add(new SpreadsheetColumnOverride(parts[0] == "-" ? null : parts[0], parts[1] == "-" ? null : parts[1]));
        }
        var rule = new SpreadsheetRule
        {
            FilePattern = _file.Text.Trim(),
            SheetName = _sheet.Text.Trim(),
            HeaderRow = header,
            DataStartRow = data,
            AuxiliaryHeaderRows = auxiliary,
            KeyColumns = Split(_keys.Text),
            ColumnOverrides = mappings,
            FrozenColumns = frozen
        };
        var index = _list.SelectedIndex;
        if (index < 0) { _rules.Add(rule); index = _rules.Count - 1; } else _rules[index] = rule;
        Refresh(); _list.SelectedIndex = index;
        return true;
    }

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static bool ValidColumn(string value) => value == "-" || value.Length is > 0 and <= 3 && value.All(char.IsAsciiLetter);
}
