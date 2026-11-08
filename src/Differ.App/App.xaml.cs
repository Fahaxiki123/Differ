using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.App.ViewModels;
using Differ.Core.Abstractions;
using Differ.Core.External;
using Differ.Core.Services;
using Differ.Formats.Binary;
using Differ.Formats.Csv;
using Differ.Formats.Excel;
using Differ.Formats.Rtf;
using Differ.Formats.Text;
using Differ.Formats.Word;
using Differ.Svn;
using Differ.Svn.Abstractions;
using Differ.Svn.Process;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Differ.App;

public partial class App : Application, IDisposable
{
    private static readonly CompositeFormat SessionSnapshotFailedMessageFormat =
        CompositeFormat.Parse(Strings.SessionSnapshotFailedFormat);

    private static readonly Action<ILogger, Exception?> LogForwardFailure =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3001, nameof(LogForwardFailure)),
            "Could not forward an external comparison to the primary instance");

    private static readonly Action<ILogger, Exception?> LogListenerFailure =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(3002, nameof(LogListenerFailure)),
            "The external comparison listener rejected a request");

    private ServiceProvider? _serviceProvider;
    private ExternalDiffInstanceCoordinator? _instanceCoordinator;
    private ComparisonWorkspaceViewModel? _comparisonWorkspace;
    private bool _isDisposed;
    private readonly ComparisonMetricsListener _comparisonMetrics = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var maintenanceOperation = IntegrationMaintenanceArguments.Parse(e.Args);
        if (maintenanceOperation != IntegrationMaintenanceOperation.None)
        {
            var maintenance = new IntegrationMaintenanceService(
                new SvnClientLocator(),
                new RegistryTortoiseDiffToolConfigurationStore(),
                new JsonTortoiseDiffToolBackupStore(throwOnInvalidBackup: true));
            var executablePath = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Differ.App.exe");
            var exitCode = await Task.Run(() =>
                maintenance.Execute(maintenanceOperation, executablePath));
            Shutdown(exitCode);
            return;
        }

        var services = new ServiceCollection();
        services.AddSingleton(Resources);
        services.AddSingleton(Dispatcher);
        services.AddSingleton<ISystemThemeMonitor, WindowsSystemThemeMonitor>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddLogging(builder => builder
            .AddDebug()
            .AddProvider(new DiagnosticLogProvider()));
        services.AddSingleton<ISvnClientLocator, SvnClientLocator>();
        services.AddSingleton<IProcessRunner, SafeProcessRunner>();
        services.AddSingleton<
            ITortoiseDiffToolConfigurationStore,
            RegistryTortoiseDiffToolConfigurationStore>();
        services.AddSingleton<
            ITortoiseDiffToolBackupStore,
            JsonTortoiseDiffToolBackupStore>();
        services.AddSingleton<TortoiseDiffIntegrationService>();
        services.AddSingleton<IFileDiffAdapter, WordDiffAdapter>();
        services.AddSingleton<IFileDiffAdapter, DocDiffAdapter>();
        services.AddSingleton<IFileDiffAdapter, ExcelDiffAdapter>();
        services.AddSingleton<IFileDiffAdapter, XlsDiffAdapter>();
        services.AddSingleton<ITextFileReader, TextFileReader>();
        services.AddSingleton<IFileDiffAdapter, CsvDiffAdapter>();
        services.AddSingleton<IFileDiffAdapter, RtfDiffAdapter>();
        services.AddSingleton<IFileDiffAdapter, TextDiffAdapter>();
        services.AddSingleton<IFileDiffAdapter, BinaryDiffAdapter>();
        services.AddSingleton<DiffCoordinator>();
        services.AddSingleton<ComparisonLoadScheduler>();
        services.AddSingleton<UserPreferencesService>();
        services.AddSingleton(
            new ExternalDiffSessionStore(
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "Differ",
                    "Sessions")));

        _serviceProvider = services.BuildServiceProvider();
        _serviceProvider.GetRequiredService<IThemeService>().InitializeFromSystem();
        await _serviceProvider.GetRequiredService<UserPreferencesService>().LoadAsync();
        var parsedArguments = ExternalDiffArguments.Parse(e.Args);

        if (parsedArguments.Status == ExternalDiffArgumentStatus.Success
            && parsedArguments.Request is not null)
        {
            await ShowOrForwardDiffAsync(parsedArguments.Request);
            return;
        }

        var startupError = parsedArguments.Status == ExternalDiffArgumentStatus.Error
            ? FormatArgumentError(parsedArguments)
            : null;
        ShowIntegrationWindow(startupError);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_instanceCoordinator is not null)
        {
            _instanceCoordinator.ListenerError -= OnListenerError;
            _instanceCoordinator.Dispose();
        }

        _comparisonWorkspace?.Dispose();
        _comparisonMetrics.Dispose();
        _serviceProvider?.Dispose();
        PerformanceLog.Flush(TimeSpan.FromSeconds(1));
        GC.SuppressFinalize(this);
    }

    private async Task ShowOrForwardDiffAsync(ExternalDiffRequest request)
    {
        var provider = _serviceProvider
            ?? throw new InvalidOperationException("Application services are unavailable.");
        var sessionStore = provider.GetRequiredService<ExternalDiffSessionStore>();
        var logger = provider.GetRequiredService<ILogger<App>>();
        var session = await PrepareSessionAsync(sessionStore, request);
        var instanceKey =
            ExternalDiffInstanceCoordinator.CreateCurrentDesktopSessionKey();
        var coordinator = new ExternalDiffInstanceCoordinator(instanceKey);

        if (!coordinator.IsPrimary)
        {
            var forwarded = false;
            try
            {
                forwarded = await coordinator.ForwardAsync(
                    session,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException)
            {
                LogForwardFailure(logger, exception);
            }

            coordinator.Dispose();
            if (forwarded)
            {
                Shutdown();
                return;
            }

            coordinator = new ExternalDiffInstanceCoordinator(instanceKey);
            if (!coordinator.IsPrimary)
            {
                coordinator.Dispose();
                // The primary may have accepted the request before the ACK timed out.
                // Leave uncertain snapshots for the owner or the expiry cleanup.
                ShowIntegrationWindow(Strings.InstanceForwardFailed);
                return;
            }
        }

        StartPrimaryDiffWindow(provider, coordinator, session);
    }

    private void StartPrimaryDiffWindow(
        ServiceProvider provider,
        ExternalDiffInstanceCoordinator coordinator,
        ExternalDiffSession firstSession)
    {
        var sessionStore = provider.GetRequiredService<ExternalDiffSessionStore>();
        try
        {
            sessionStore.CleanupExpired(TimeSpan.FromDays(1));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            LogListenerFailure(
                provider.GetRequiredService<ILogger<App>>(),
                exception);
        }

        _comparisonWorkspace = new ComparisonWorkspaceViewModel(
            provider.GetRequiredService<DiffCoordinator>(),
            provider.GetRequiredService<ComparisonLoadScheduler>(),
            sessionStore,
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<UserPreferencesService>());
        _instanceCoordinator = coordinator;
        _instanceCoordinator.ListenerError += OnListenerError;

        var window = new DiffWindow(
            _comparisonWorkspace,
            provider.GetRequiredService<IThemeService>(),
            persistWindowLayout: true);
        MainWindow = window;
        _instanceCoordinator.StartListening(ReceiveForwardedSessionAsync);
        _comparisonWorkspace.AddSession(firstSession);
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        window.Show();
        window.Activate();
    }

    private Task ReceiveForwardedSessionAsync(
        ExternalDiffSession session,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Dispatcher.InvokeAsync(
                () =>
                {
                    _comparisonWorkspace?.AddSession(session);
                    if (MainWindow is not Window window)
                    {
                        return;
                    }

                    if (window.WindowState == WindowState.Minimized)
                    {
                        window.WindowState = WindowState.Normal;
                    }

                    window.Show();
                    window.Activate();
                },
                DispatcherPriority.Normal,
                cancellationToken)
            .Task;
    }

    private void OnListenerError(object? sender, Exception exception)
    {
        if (_serviceProvider is not null)
        {
            LogListenerFailure(
                _serviceProvider.GetRequiredService<ILogger<App>>(),
                exception);
        }
    }

    private static async Task<ExternalDiffSession> PrepareSessionAsync(
        ExternalDiffSessionStore sessionStore,
        ExternalDiffRequest request)
    {
        try
        {
            return await sessionStore.CreateAsync(
                request,
                CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return ExternalDiffSession.CreateFailed(
                request,
                string.Format(
                    CultureInfo.CurrentCulture,
                    SessionSnapshotFailedMessageFormat,
                    exception.Message));
        }
    }

    private void ShowIntegrationWindow(string? startupError)
    {
        var provider = _serviceProvider
            ?? throw new InvalidOperationException("Application services are unavailable.");
        var viewModel = new IntegrationWindowViewModel(
            provider.GetRequiredService<ISvnClientLocator>(),
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<TortoiseDiffIntegrationService>(),
            startupError);
        var window = new MainWindow(
            viewModel,
            provider.GetRequiredService<IThemeService>());
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnLastWindowClose;
        window.Show();
        _ = window.InitializeAsync();
    }

    private static string FormatArgumentError(ExternalDiffArgumentResult result)
    {
        return result.Error switch
        {
            ExternalDiffArgumentError.UnknownOption => FormatWithArgument(
                Strings.ArgumentErrorUnknownOptionFormat,
                result.ErrorArgument),
            ExternalDiffArgumentError.MissingOptionValue => FormatWithArgument(
                Strings.ArgumentErrorMissingValueFormat,
                result.ErrorArgument),
            ExternalDiffArgumentError.DuplicateOption => FormatWithArgument(
                Strings.ArgumentErrorDuplicateFormat,
                result.ErrorArgument),
            ExternalDiffArgumentError.MissingRequiredOption => FormatWithArgument(
                Strings.ArgumentErrorMissingRequiredFormat,
                result.ErrorArgument),
            ExternalDiffArgumentError.InvalidPositionalCount =>
                Strings.ArgumentErrorPositionalCount,
            ExternalDiffArgumentError.MixedArgumentStyles =>
                Strings.ArgumentErrorMixedStyles,
            _ => Strings.ArgumentErrorPositionalCount
        };
    }

    private static string FormatWithArgument(string format, string? argument)
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            format,
            argument ?? string.Empty);
    }
}
