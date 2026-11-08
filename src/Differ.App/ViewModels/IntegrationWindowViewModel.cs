using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.Svn;
using Differ.Svn.Abstractions;
using Differ.Svn.Models;

namespace Differ.App.ViewModels;

public sealed class IntegrationWindowViewModel : ObservableObject
{
    private static readonly CompositeFormat TortoiseFoundMessageFormat =
        CompositeFormat.Parse(Strings.TortoiseFoundFormat);
    private static readonly CompositeFormat OpenSettingsFailedMessageFormat =
        CompositeFormat.Parse(Strings.OpenTortoiseSettingsFailedFormat);
    private static readonly CompositeFormat InstallCompleteMessageFormat =
        CompositeFormat.Parse(Strings.InstallGlobalCompleteFormat);
    private static readonly CompositeFormat RestoreCompleteMessageFormat =
        CompositeFormat.Parse(Strings.RestoreGlobalCompleteFormat);
    private static readonly CompositeFormat RestoreSkippedMessageFormat =
        CompositeFormat.Parse(Strings.RestoreGlobalSkippedFormat);
    private static readonly CompositeFormat IntegrationFailedMessageFormat =
        CompositeFormat.Parse(Strings.IntegrationOperationFailedFormat);

    private readonly ISvnClientLocator _clientLocator;
    private readonly IProcessRunner _processRunner;
    private readonly TortoiseDiffIntegrationService _integrationService;
    private string? _tortoiseExecutablePath;
    private string _tortoiseStatus = Strings.IntegrationChecking;
    private string _integrationStatusText = Strings.IntegrationChecking;
    private string _configurationNotice = string.Empty;
    private string _operationResult = string.Empty;
    private string _errorText;
    private bool _hasBackup;
    private bool _isBusy;
    private bool _isInitialized;
    private string _diagnosticInfo = ApplicationDiagnostics.Describe(null);

    public IntegrationWindowViewModel(
        ISvnClientLocator clientLocator,
        IProcessRunner processRunner,
        TortoiseDiffIntegrationService integrationService,
        string? startupError = null)
    {
        _clientLocator = clientLocator;
        _processRunner = processRunner;
        _integrationService = integrationService;
        _errorText = startupError ?? string.Empty;

        ExecutablePath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "Differ.App.exe"));
        ExternalCommand = TortoiseDiffToolRegistration.BuildCommand(ExecutablePath);
        SupportedTypes = string.Join(
            "  ",
            TortoiseDiffToolRegistration.SupportedExtensions);

        OpenTortoiseSettingsCommand = new RelayCommand(
            OpenTortoiseSettings,
            CanOpenTortoiseSettings);
        InstallIntegrationCommand = new AsyncRelayCommand(
            InstallIntegrationAsync,
            CanInstallIntegration);
        RestoreIntegrationCommand = new AsyncRelayCommand(
            RestoreIntegrationAsync,
            CanRestoreIntegration);
    }

    public IRelayCommand OpenTortoiseSettingsCommand { get; }

    public IAsyncRelayCommand InstallIntegrationCommand { get; }

    public IAsyncRelayCommand RestoreIntegrationCommand { get; }

    public string ExecutablePath { get; }

    public string ExternalCommand { get; }

    public string TortoiseRegistryValue { get; } =
        @"HKEY_CURRENT_USER\Software\TortoiseSVN\Diff";

    public string SupportedTypes { get; }

    public string DiagnosticInfo
    {
        get => _diagnosticInfo;
        private set => SetProperty(ref _diagnosticInfo, value);
    }

    public string? TortoiseExecutablePath
    {
        get => _tortoiseExecutablePath;
        private set => SetProperty(ref _tortoiseExecutablePath, value);
    }

    public string TortoiseStatus
    {
        get => _tortoiseStatus;
        private set => SetProperty(ref _tortoiseStatus, value);
    }

    public string IntegrationStatusText
    {
        get => _integrationStatusText;
        private set => SetProperty(ref _integrationStatusText, value);
    }

    public string ConfigurationNotice
    {
        get => _configurationNotice;
        private set
        {
            if (SetProperty(ref _configurationNotice, value))
            {
                OnPropertyChanged(nameof(HasConfigurationNotice));
            }
        }
    }

    public bool HasConfigurationNotice =>
        !string.IsNullOrWhiteSpace(ConfigurationNotice);

    public string OperationResult
    {
        get => _operationResult;
        private set
        {
            if (SetProperty(ref _operationResult, value))
            {
                OnPropertyChanged(nameof(HasOperationResult));
            }
        }
    }

    public bool HasOperationResult =>
        !string.IsNullOrWhiteSpace(OperationResult);

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public async Task InitializeAsync()
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        await RefreshStatusAsync();
    }

    private async Task InstallIntegrationAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        OperationResult = string.Empty;
        try
        {
            var result = await Task.Run(
                () => _integrationService.Install(ExecutablePath));
            OperationResult = string.Format(
                CultureInfo.CurrentCulture,
                InstallCompleteMessageFormat,
                result.BackedUpExistingCount,
                result.ClearedExtensionOverrideCount);
            await RefreshStatusCoreAsync();
        }
        catch (Exception exception)
        {
            SetOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreIntegrationAsync()
    {
        IsBusy = true;
        ErrorText = string.Empty;
        OperationResult = string.Empty;
        try
        {
            var result = await Task.Run(_integrationService.Restore);
            OperationResult = result.SkippedChangedCount == 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    RestoreCompleteMessageFormat,
                    result.RestoredCount)
                : string.Format(
                    CultureInfo.CurrentCulture,
                    RestoreSkippedMessageFormat,
                    result.RestoredCount,
                    result.SkippedChangedCount);
            await RefreshStatusCoreAsync();
        }
        catch (Exception exception)
        {
            SetOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshStatusAsync()
    {
        IsBusy = true;
        try
        {
            await RefreshStatusCoreAsync();
        }
        catch (Exception exception)
        {
            SetOperationError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshStatusCoreAsync()
    {
        var inspection = await Task.Run(
            () =>
            {
                var tortoisePath = _clientLocator.FindTortoiseProc();
                return new IntegrationInspection(
                    tortoisePath,
                    _integrationService.GetStatus(ExecutablePath),
                    ApplicationDiagnostics.Describe(tortoisePath));
            });

        TortoiseExecutablePath = inspection.TortoiseExecutablePath;
        DiagnosticInfo = inspection.DiagnosticInfo;
        TortoiseStatus = inspection.TortoiseExecutablePath is null
            ? Strings.TortoiseNotFound
            : string.Format(
                CultureInfo.CurrentCulture,
                TortoiseFoundMessageFormat,
                inspection.TortoiseExecutablePath);
        ApplyIntegrationStatus(inspection.Status);
        NotifyCommandStates();
    }

    private void ApplyIntegrationStatus(TortoiseDiffIntegrationStatus status)
    {
        IntegrationStatusText = status.IsFullyRegistered
            ? Strings.GlobalIntegrationRegistered
            : Strings.GlobalIntegrationNotRegistered;
        ConfigurationNotice = status.ExistingOtherToolCount == 0
            ? string.Empty
            : Strings.ExistingGlobalToolBackup;
        _hasBackup = status.HasBackup;
    }

    private bool CanOpenTortoiseSettings()
    {
        return !IsBusy && TortoiseExecutablePath is not null;
    }

    private bool CanInstallIntegration()
    {
        return !IsBusy && TortoiseExecutablePath is not null;
    }

    private bool CanRestoreIntegration()
    {
        return !IsBusy && _hasBackup;
    }

    private void OpenTortoiseSettings()
    {
        if (TortoiseExecutablePath is null)
        {
            return;
        }

        try
        {
            _processRunner.StartDetached(
                TortoiseExecutablePath,
                ["/command:settings"],
                workingDirectory: null);
        }
        catch (Exception exception)
        {
            ErrorText = string.Format(
                CultureInfo.CurrentCulture,
                OpenSettingsFailedMessageFormat,
                exception.Message);
        }
    }

    private void SetOperationError(Exception exception)
    {
        ErrorText = string.Format(
            CultureInfo.CurrentCulture,
            IntegrationFailedMessageFormat,
            exception.Message);
    }

    private void NotifyCommandStates()
    {
        OpenTortoiseSettingsCommand.NotifyCanExecuteChanged();
        InstallIntegrationCommand.NotifyCanExecuteChanged();
        RestoreIntegrationCommand.NotifyCanExecuteChanged();
    }

    private sealed record IntegrationInspection(
        string? TortoiseExecutablePath,
        TortoiseDiffIntegrationStatus Status,
        string DiagnosticInfo);
}
