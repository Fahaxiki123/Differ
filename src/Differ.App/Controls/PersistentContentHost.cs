using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Differ.App.Services;

namespace Differ.App.Controls;

public sealed class PersistentContentHost : Grid
{
    private const int RetainedInactiveLayoutLimit = 2;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(PersistentContentHost),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectedItem),
            typeof(object),
            typeof(PersistentContentHost),
            new PropertyMetadata(null, OnSelectedItemChanged));

    public static readonly DependencyProperty ContentTemplateSelectorProperty =
        DependencyProperty.Register(
            nameof(ContentTemplateSelector),
            typeof(DataTemplateSelector),
            typeof(PersistentContentHost),
            new PropertyMetadata(null, OnTemplateSelectorChanged));

    public static readonly DependencyProperty ContentTemplateProperty =
        DependencyProperty.Register(
            nameof(ContentTemplate),
            typeof(DataTemplate),
            typeof(PersistentContentHost),
            new PropertyMetadata(null, OnContentTemplateChanged));

    public static readonly DependencyProperty TraceCategoryProperty =
        DependencyProperty.Register(
            nameof(TraceCategory),
            typeof(string),
            typeof(PersistentContentHost),
            new PropertyMetadata("persistent-view"));

    private readonly Dictionary<object, ContentPresenter> _presenters =
        new(ReferenceEqualityComparer.Instance);
    private List<ContentPresenter>? _recentInactivePresenters;
    private ContentPresenter? _activePresenter;
    private INotifyCollectionChanged? _observableItems;

    public PersistentContentHost()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public DataTemplateSelector? ContentTemplateSelector
    {
        get => (DataTemplateSelector?)GetValue(ContentTemplateSelectorProperty);
        set => SetValue(ContentTemplateSelectorProperty, value);
    }

    public DataTemplate? ContentTemplate
    {
        get => (DataTemplate?)GetValue(ContentTemplateProperty);
        set => SetValue(ContentTemplateProperty, value);
    }

    public string TraceCategory
    {
        get => (string)GetValue(TraceCategoryProperty);
        set => SetValue(TraceCategoryProperty, value);
    }

    public int CachedContentCount => _presenters.Count;

    public bool PreserveRecentLayout { get; set; }

    private static void OnItemsSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var host = (PersistentContentHost)dependencyObject;
        host.AttachItemsSource(e.NewValue as IEnumerable);
    }

    private static void OnSelectedItemChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        ((PersistentContentHost)dependencyObject).ShowSelectedItem(e.NewValue);
    }

    private static void OnTemplateSelectorChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var host = (PersistentContentHost)dependencyObject;
        foreach (var presenter in host._presenters.Values)
        {
            presenter.ContentTemplateSelector = e.NewValue as DataTemplateSelector;
        }
    }

    private static void OnContentTemplateChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        var host = (PersistentContentHost)dependencyObject;
        foreach (var presenter in host._presenters.Values)
        {
            presenter.ContentTemplate = e.NewValue as DataTemplate;
        }
    }

    private void AttachItemsSource(IEnumerable? items)
    {
        if (_observableItems is not null)
        {
            _observableItems.CollectionChanged -= OnItemsCollectionChanged;
        }

        _observableItems = items as INotifyCollectionChanged;
        if (_observableItems is not null)
        {
            _observableItems.CollectionChanged += OnItemsCollectionChanged;
        }

        RemoveMissingPresenters(items);
        ShowSelectedItem(SelectedItem);
    }

    private void OnItemsCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Remove
            or NotifyCollectionChangedAction.Replace)
        {
            RemovePresenters(e.OldItems);
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            RemoveMissingPresenters(ItemsSource);
        }

        ShowSelectedItem(SelectedItem);
    }

    private void ShowSelectedItem(object? selectedItem)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!PreserveRecentLayout)
        {
            foreach (var presenter in _presenters.Values)
            {
                presenter.Visibility = Visibility.Collapsed;
            }
        }

        if (selectedItem is null || !ContainsItem(selectedItem))
        {
            ClearRecentLayout();
            return;
        }

        var cacheHit = _presenters.TryGetValue(
            selectedItem,
            out var selectedPresenter);
        if (!cacheHit)
        {
            selectedPresenter = new ContentPresenter
            {
                Content = selectedItem,
                ContentTemplate = ContentTemplate,
                ContentTemplateSelector = ContentTemplateSelector,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _presenters.Add(selectedItem, selectedPresenter);
            Children.Add(selectedPresenter);
        }

        if (PreserveRecentLayout)
        {
            PreservePreviousLayout(selectedPresenter!);
        }
        selectedPresenter!.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(
            () => PerformanceLog.Write(
                TraceCategory,
                selectedItem.GetType().Name,
                stopwatch.Elapsed,
                cacheHit),
            DispatcherPriority.Render);
    }

    private void PreservePreviousLayout(ContentPresenter selectedPresenter)
    {
        if (ReferenceEquals(_activePresenter, selectedPresenter)) return;

        _recentInactivePresenters ??= new List<ContentPresenter>(RetainedInactiveLayoutLimit + 1);
        _recentInactivePresenters.Remove(selectedPresenter);
        if (_activePresenter is { } previous)
        {
            // Hidden retains its last measurement without painting or taking input.
            previous.Visibility = Visibility.Hidden;
            _recentInactivePresenters.Add(previous);
            if (_recentInactivePresenters.Count > RetainedInactiveLayoutLimit)
            {
                _recentInactivePresenters[0].Visibility = Visibility.Collapsed;
                _recentInactivePresenters.RemoveAt(0);
            }
        }
        _activePresenter = selectedPresenter;
    }

    private void ClearRecentLayout()
    {
        if (_activePresenter is { } active) active.Visibility = Visibility.Collapsed;
        _activePresenter = null;
        if (_recentInactivePresenters is not { } recent) return;
        foreach (var presenter in recent) presenter.Visibility = Visibility.Collapsed;
        recent.Clear();
    }

    private void RemoveMissingPresenters(IEnumerable? items)
    {
        var activeItems = items is null
            ? new HashSet<object>(ReferenceEqualityComparer.Instance)
            : new HashSet<object>(
                items.Cast<object>(),
                ReferenceEqualityComparer.Instance);
        foreach (var key in _presenters.Keys
                     .Where(key => !activeItems.Contains(key))
                     .ToArray())
        {
            RemovePresenter(key);
        }
    }

    private void RemovePresenters(IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var item in items.Cast<object>())
        {
            RemovePresenter(item);
        }
    }

    private void RemovePresenter(object item)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!_presenters.Remove(item, out var presenter))
        {
            return;
        }

        Children.Remove(presenter);
        presenter.Content = null;
        if (ReferenceEquals(_activePresenter, presenter)) _activePresenter = null;
        _recentInactivePresenters?.Remove(presenter);
        PerformanceLog.Write(
            $"{TraceCategory}-release",
            item.GetType().Name,
            stopwatch.Elapsed,
            cacheHit: true);
    }

    private bool ContainsItem(object selectedItem)
    {
        return ItemsSource is null
            || ItemsSource.Cast<object>().Any(
                item => ReferenceEquals(item, selectedItem));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachItemsSource(ItemsSource);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_observableItems is not null)
        {
            _observableItems.CollectionChanged -= OnItemsCollectionChanged;
            _observableItems = null;
        }

        foreach (var presenter in _presenters.Values)
        {
            presenter.Content = null;
        }

        _presenters.Clear();
        _activePresenter = null;
        _recentInactivePresenters?.Clear();
        Children.Clear();
    }
}
