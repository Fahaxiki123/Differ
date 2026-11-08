using System.Windows;
using System.Windows.Input;
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
}
