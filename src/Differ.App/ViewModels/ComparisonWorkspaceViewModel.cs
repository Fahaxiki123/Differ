using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Differ.App.Resources;
using Differ.App.Services;
using Differ.Core.External;
using Differ.Core.Services;
using Microsoft.Extensions.Logging;

namespace Differ.App.ViewModels;

public sealed class ComparisonWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly DiffCoordinator _diffCoordinator;
    private readonly ComparisonLoadScheduler _loadScheduler;
    private readonly ExternalDiffSessionStore _sessionStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SnapshotCleanupService _snapshotCleanup;
    private DiffWindowViewModel? _selectedTab;
    private bool _isDisposed;
    private bool _cleanupDisposed;
    private string _filterText = string.Empty;
    private IReadOnlyList<DiffWindowViewModel> _visibleTabs = [];
    private readonly HashSet<string> _retiredPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<DiffWindowViewModel> _closingTabs = [];

    public ComparisonWorkspaceViewModel(
        DiffCoordinator diffCoordinator,
        ComparisonLoadScheduler loadScheduler,
        ExternalDiffSessionStore sessionStore,
        ILoggerFactory loggerFactory,
        UserPreferencesService? preferences = null)
    {
        _diffCoordinator = diffCoordinator;
        _loadScheduler = loadScheduler;
        _sessionStore = sessionStore;
        _loggerFactory = loggerFactory;
        Preferences = preferences ?? new UserPreferencesService();
        _snapshotCleanup = new SnapshotCleanupService(
            sessionStore,
            loggerFactory.CreateLogger<SnapshotCleanupService>());
        NextTabCommand = new RelayCommand(() => MoveTab(1), () => VisibleTabs.Count > 1);
        PreviousTabCommand = new RelayCommand(() => MoveTab(-1), () => VisibleTabs.Count > 1);
        CloseOtherTabsCommand = new RelayCommand(CloseOtherTabs, () => SelectedTab is not null && Tabs.Count > 1);
        CloseAllTabsCommand = new RelayCommand(CloseAllTabs, () => Tabs.Count > 0);
        RefreshSelectedCommand = new AsyncRelayCommand(
            token => SelectedTab?.RefreshMineAsync(token) ?? Task.CompletedTask,
            () => SelectedTab is not null);
    }

    public ObservableCollection<DiffWindowViewModel> Tabs { get; } = [];

    public UserPreferencesService Preferences { get; }
    public IRelayCommand NextTabCommand { get; }
    public IRelayCommand PreviousTabCommand { get; }
    public IRelayCommand CloseOtherTabsCommand { get; }
    public IRelayCommand CloseAllTabsCommand { get; }
    public IAsyncRelayCommand RefreshSelectedCommand { get; }
    public IReadOnlyList<DiffWindowViewModel> VisibleTabs
    {
        get => _visibleTabs;
        private set => SetProperty(ref _visibleTabs, value);
    }
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value ?? string.Empty)) RefreshVisibleTabs();
        }
    }

    public DiffWindowViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                foreach (var tab in Tabs)
                {
                    tab.IsActive = ReferenceEquals(tab, value);
                }

                OnPropertyChanged(nameof(WindowTitle));
                NotifyTabCommands();
            }
        }
    }

    public string WindowTitle => SelectedTab?.WindowTitle
        ?? Strings.ExternalDiffAppTitle;

    public event EventHandler? AllTabsClosed;

    public DiffWindowViewModel AddSession(ExternalDiffSession session)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(session);

        var existing = Tabs.FirstOrDefault(tab =>
            string.Equals(
                tab.ComparisonKey,
                session.ComparisonKey,
                StringComparison.Ordinal));
        if (existing is not null)
        {
            if (existing.Session.SessionId != session.SessionId)
            {
                DeleteSnapshot(session);
            }
            _sessionStore.Touch(existing.Session);
            FilterText = string.Empty;
            SelectedTab = existing;
            return existing;
        }

        var viewModel = new DiffWindowViewModel(
            _diffCoordinator,
            _loadScheduler,
            session,
            _loggerFactory.CreateLogger<DiffWindowViewModel>(),
            CloseTab);
        viewModel.ConfigureActions(_sessionStore, DeleteSnapshot, Preferences, GetProtectedPaths);
        viewModel.EnableDocumentEditor();
        Tabs.Add(viewModel);
        FilterText = string.Empty;
        RefreshVisibleTabs();
        SelectedTab = viewModel;
        _sessionStore.Touch(session);
        _ = viewModel.InitializeRulesAndLoadAsync();
        return viewModel;
    }

    public void CloseSelectedTab()
    {
        if (SelectedTab is not null)
        {
            CloseTab(SelectedTab);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        foreach (var tab in Tabs.ToArray())
        {
            tab.Dispose();
            DeleteSnapshot(tab.Session);
        }

        Tabs.Clear();
        VisibleTabs = [];
        SelectedTab = null;
        _snapshotCleanup.Dispose();
        _cleanupDisposed = true;
    }

    private void CloseTab(DiffWindowViewModel tab)
    {
        if (tab.IsEditableText && tab.ConfirmDiscardEditorAsync is not null)
        {
            if (_closingTabs.Add(tab)) _ = ConfirmAndCloseTabAsync(tab);
            return;
        }
        if (tab.IsEditorDirty) return;
        CloseTabCore(tab);
    }

    private async Task ConfirmAndCloseTabAsync(DiffWindowViewModel tab)
    {
        try
        {
            if (tab.ConfirmDiscardEditorAsync is { } confirm && await confirm()) CloseTabCore(tab);
        }
        finally { _closingTabs.Remove(tab); }
    }

    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var tab in Tabs.ToArray())
        {
            if (tab.ConfirmDiscardEditorAsync is { } confirm)
            {
                if (!await confirm()) return false;
            }
            else if (tab.IsEditorDirty) return false;
        }
        return true;
    }

    private void CloseTabCore(DiffWindowViewModel tab)
    {
        var stopwatch = Stopwatch.StartNew();
        if (_isDisposed || !Tabs.Remove(tab))
        {
            return;
        }

        tab.Dispose();
        DeleteSnapshot(tab.Session);
        PerformanceLog.Write(
            "tab-session-release",
            tab.FileKindGlyph,
            stopwatch.Elapsed,
            cacheHit: true);
        var wasSelected = ReferenceEquals(SelectedTab, tab);
        RefreshVisibleTabs();
        if (wasSelected) SelectedTab = VisibleTabs.Count > 0 ? VisibleTabs[^1] : null;
        if (Tabs.Count == 0)
        {
            AllTabsClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DeleteSnapshot(ExternalDiffSession session)
    {
        _retiredPaths.Add(session.BaseSnapshotPath);
        _retiredPaths.Add(session.MineSnapshotPath);
        if (_cleanupDisposed)
        {
            _sessionStore.TryDelete(session);
            return;
        }
        _snapshotCleanup.DeleteOrQueue(session);
    }

    private IReadOnlyList<string> GetProtectedPaths() =>
        Tabs.SelectMany(tab => tab.GetProtectedPaths()).Concat(_retiredPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void MoveTab(int direction)
    {
        if (VisibleTabs.Count == 0) return;
        var index = Array.IndexOf(VisibleTabs.ToArray(), SelectedTab);
        SelectedTab = VisibleTabs[(index + direction + VisibleTabs.Count) % VisibleTabs.Count];
    }

    private void CloseOtherTabs()
    {
        foreach (var tab in Tabs.Where(tab => !ReferenceEquals(tab, SelectedTab)).ToArray()) CloseTab(tab);
    }

    private void CloseAllTabs()
    {
        foreach (var tab in Tabs.ToArray()) CloseTab(tab);
    }

    private void RefreshVisibleTabs()
    {
        VisibleTabs = Tabs.Where(tab => tab.FileName.Contains(FilterText.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (SelectedTab is not null && !VisibleTabs.Contains(SelectedTab)) SelectedTab = VisibleTabs.Count > 0 ? VisibleTabs[0] : null;
        if (SelectedTab is null && VisibleTabs.Count > 0) SelectedTab = VisibleTabs[0];
        NotifyTabCommands();
    }

    private void NotifyTabCommands()
    {
        NextTabCommand.NotifyCanExecuteChanged();
        PreviousTabCommand.NotifyCanExecuteChanged();
        CloseOtherTabsCommand.NotifyCanExecuteChanged();
        CloseAllTabsCommand.NotifyCanExecuteChanged();
        RefreshSelectedCommand.NotifyCanExecuteChanged();
    }
}
