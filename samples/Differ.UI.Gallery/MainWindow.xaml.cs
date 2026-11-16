using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Differ.UI.Gallery;

public partial class MainWindow : Window
{
    private const int DifferenceCount = 5;
    private const double NarrowWidth = 720;
    private const double NormalWidth = 1024;

    private readonly ObservableCollection<SampleFile> files = [];
    private int difference = 1;

    public MainWindow()
    {
        InitializeComponent();
        ThemePicker.SelectedIndex = 0;
        Files.ItemsSource = files;
        ResetFiles();
        UpdatePosition();
    }

    public string Quantity { get; set; } = "120";

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedIndex: >= 0 } picker && Application.Current is App app)
        {
            app.ApplyTheme(picker.SelectedIndex == 1);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            ThemePicker.SelectedIndex = 1 - ThemePicker.SelectedIndex;
            e.Handled = true;
        }
    }

    private void OnToggleNarrow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
        bool narrow = Width > NarrowWidth;
        Width = narrow ? NarrowWidth : NormalWidth;
        Height = narrow ? 560 : 720;
        NarrowButton.Content = FindResource(narrow ? "Gallery.Wide" : "Gallery.Narrow");
    }

    private void OnPrevious(object sender, RoutedEventArgs e)
    {
        difference = Math.Max(difference - 1, 1);
        UpdatePosition();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        difference = Math.Min(difference + 1, DifferenceCount);
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        Position.Text = string.Format(CultureInfo.CurrentCulture, ResourceText("Gallery.PositionFormat"), difference, DifferenceCount);
        PreviousButton.IsEnabled = difference > 1;
        NextButton.IsEnabled = difference < DifferenceCount;
    }

    private void OnSampleAction(object sender, RoutedEventArgs e)
    {
        string? name = sender switch
        {
            MenuItem { Header: string header } => header,
            ContentControl { Content: string content } => content,
            _ => null,
        };
        Activity.Text = string.Format(CultureInfo.CurrentCulture, ResourceText("Gallery.ActionFormat"), name);
    }

    private void OnShowDialog(object sender, RoutedEventArgs e)
    {
        var dialog = new SampleDialog { Owner = this };
        bool confirmed = dialog.ShowDialog() == true;
        Activity.Text = ResourceText(confirmed ? "Gallery.DialogAccepted" : "Gallery.DialogCanceled");
        if (sender is Button button)
        {
            Keyboard.Focus(button);
        }
    }

    private async void OnStartBusy(object sender, RoutedEventArgs e)
    {
        BusyButton.IsEnabled = false;
        BusyProgress.IsIndeterminate = SystemParameters.ClientAreaAnimation;
        BusyProgress.Value = 50;
        Activity.Text = ResourceText("Gallery.Busy");

        await Task.Delay(TimeSpan.FromSeconds(1.8));

        BusyProgress.IsIndeterminate = false;
        BusyProgress.Value = 100;
        BusyButton.IsEnabled = true;
        Activity.Text = ResourceText("Gallery.BusyDone");
    }

    private void OnCloseFile(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SampleFile file })
        {
            files.Remove(file);
            if (files.Count == 0)
            {
                Activity.Text = ResourceText("Gallery.FilesClosed");
            }
        }
    }

    private void OnResetFiles(object sender, RoutedEventArgs e)
    {
        ResetFiles();
        Activity.Text = ResourceText("Gallery.Ready");
    }

    private void ResetFiles()
    {
        string repeatedRows = string.Join(Environment.NewLine, Enumerable.Range(1, 36)
            .Select(index => string.Create(CultureInfo.InvariantCulture, $"  review_item_{index:D2} = verified")));
        string longLine = string.Concat(Enumerable.Repeat("很长的样例字段 · review context · ", 12));
        files.Clear();
        files.Add(new SampleFile(
            "比较工作区示例-用于检查较长中文文件名与长行省略.md",
            $"# 文本预览样例{Environment.NewLine}{Environment.NewLine}  mode = compact{Environment.NewLine}  long_value = {longLine}{Environment.NewLine}{repeatedRows}",
            $"# 文本预览样例{Environment.NewLine}{Environment.NewLine}  mode = professional{Environment.NewLine}  long_value = {longLine}{Environment.NewLine}{repeatedRows}",
            string.Empty));
        files.Add(new SampleFile("另一组示例.txt", "enabled = false", "enabled = true", ResourceText("Gallery.Dirty")));
        Files.SelectedIndex = 0;
    }

    private string ResourceText(string key) => (string)FindResource(key);

    private sealed record SampleFile(string Name, string BaseText, string CurrentText, string DirtyLabel);
}
