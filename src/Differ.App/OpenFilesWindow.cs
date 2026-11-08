using System.IO;
using System.Windows;
using System.Windows.Controls;
using Differ.App.Resources;
using Microsoft.Win32;

namespace Differ.App;

/// <summary>A local file picker only; no source is changed by accepting this dialog.</summary>
public sealed class OpenFilesWindow : Window
{
    private readonly bool _merge;
    private readonly List<TextBox> _paths = [];

    public OpenFilesWindow(bool merge)
    {
        _merge = merge;
        Title = merge ? FileOpenStrings.MergeTitle : FileOpenStrings.CompareTitle;
        Width = 720;
        MinWidth = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "InkBrush");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = merge ? FileOpenStrings.MergeHint : FileOpenStrings.CompareHint,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });
        var labels = merge
            ? new[] { FileOpenStrings.Base, FileOpenStrings.Mine, FileOpenStrings.Theirs, FileOpenStrings.Output }
            : [FileOpenStrings.Base, FileOpenStrings.Mine];
        for (var i = 0; i < labels.Length; i++)
        {
            var index = i;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(label);
            var box = new TextBox { MinHeight = 30, VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(box, labels[i]);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            _paths.Add(box);
            var browse = new Button { Content = FileOpenStrings.Browse, Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
            browse.Click += (_, _) => Browse(index);
            Grid.SetColumn(browse, 2);
            row.Children.Add(browse);
            panel.Children.Add(row);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var open = new Button { Content = FileOpenStrings.Open, IsDefault = true, MinWidth = 84 };
        open.Click += (_, _) => Accept();
        buttons.Children.Add(open);
        buttons.Children.Add(new Button { Content = FileOpenStrings.Cancel, IsCancel = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) });
        panel.Children.Add(buttons);
        Content = panel;
    }

    public IReadOnlyList<string> Paths => _paths.Select(box => box.Text.Trim()).ToArray();

    private void Browse(int index)
    {
        FileDialog dialog = _merge && index == 3
            ? new SaveFileDialog { Title = FileOpenStrings.SelectOutput, OverwritePrompt = true, FileName = "merged.txt" }
            : new OpenFileDialog { Title = FileOpenStrings.SelectInput, CheckFileExists = true };
        dialog.Filter = FileOpenStrings.AllFiles;
        if (dialog.ShowDialog(this) == true) _paths[index].Text = dialog.FileName;
    }

    private void Accept()
    {
        if (Paths.Any(string.IsNullOrWhiteSpace))
        {
            MessageBox.Show(this, FileOpenStrings.MissingPath, Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (Paths.Take(_merge ? 3 : 2).Any(path => !File.Exists(path)))
        {
            MessageBox.Show(this, FileOpenStrings.InputMissing, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
