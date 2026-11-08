using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;

namespace Differ.App;

public partial class MainWindow : Window
{
    private readonly IThemeService _themeService;

    public MainWindow(
        IntegrationWindowViewModel viewModel,
        IThemeService themeService)
    {
        _themeService = themeService;
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
        MergeCommandBox.Text = Differ.Svn.TortoiseMergeCommand.Create(Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "Differ.App.exe"));
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private IntegrationWindowViewModel ViewModel { get; }

    public Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            e.Handled = true;
            _themeService.Toggle();
        }
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _themeService.Toggle();
    }

    private void OpenCompare_Click(object sender, RoutedEventArgs e) => OpenLocalFiles(false);
    private void OpenMerge_Click(object sender, RoutedEventArgs e) => OpenLocalFiles(true);

    private void OpenLocalFiles(bool merge)
    {
        var picker = new OpenFilesWindow(merge) { Owner = this };
        if (picker.ShowDialog() != true) return;
        var paths = picker.Paths;
        var start = new ProcessStartInfo(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Differ.App.exe"))
        {
            UseShellExecute = false
        };
        if (merge) start.ArgumentList.Add("--merge");
        var options = merge ? new[] { "--base", "--mine", "--theirs", "--merged" } : ["--base", "--mine"];
        for (var i = 0; i < options.Length; i++)
        {
            start.ArgumentList.Add(options[i]);
            start.ArgumentList.Add(paths[i]);
        }
        try { using var process = Process.Start(start); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, FileOpenStrings.LaunchFailed + exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
