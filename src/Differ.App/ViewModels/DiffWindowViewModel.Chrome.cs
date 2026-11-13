using System.ComponentModel;
using System.Globalization;
using Differ.App.Resources;

namespace Differ.App.ViewModels;

public sealed partial class DiffWindowViewModel
{
    private string _officeSummaryDetail = string.Empty;

    public bool IsDisplayAll
    {
        get => DisplayMode == DiffDisplayMode.All;
        set { if (value) DisplayMode = DiffDisplayMode.All; }
    }

    public bool IsDisplayDifferences
    {
        get => DisplayMode == DiffDisplayMode.DifferencesOnly;
        set { if (value) DisplayMode = DiffDisplayMode.DifferencesOnly; }
    }

    public bool IsDisplayContext
    {
        get => DisplayMode == DiffDisplayMode.Context;
        set { if (value) DisplayMode = DiffDisplayMode.Context; }
    }

    public string ChromeTabBadge => HasTabBadge ? TabBadgeText : string.Empty;

    public bool HasChromeTabBadge => ChromeTabBadge.Length != 0;

    public string OfficeSummaryDetail
    {
        get => _officeSummaryDetail;
        private set => SetProperty(ref _officeSummaryDetail, value);
    }

    public bool HasRoutineSpreadsheetStatus => IsSpreadsheetView && (StatusText == Strings.ComparisonIdentical
        || StatusText == string.Format(CultureInfo.CurrentCulture, Strings.ComparisonCompleteEventsFormat, DifferenceCount));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(DisplayMode))
        {
            OnPropertyChanged(nameof(IsDisplayAll));
            OnPropertyChanged(nameof(IsDisplayDifferences));
            OnPropertyChanged(nameof(IsDisplayContext));
        }
        else if (e.PropertyName is nameof(TabBadgeText) or nameof(IsEditableText) or nameof(IsEditorDirty) or nameof(HasError))
        {
            OnPropertyChanged(nameof(ChromeTabBadge));
            OnPropertyChanged(nameof(HasChromeTabBadge));
        }
        else if (e.PropertyName is nameof(StatusText) or nameof(IsSpreadsheetView) or nameof(DifferenceCount))
        {
            OnPropertyChanged(nameof(HasRoutineSpreadsheetStatus));
        }
        else if (e.PropertyName == nameof(IsBusy))
        {
            _toggleIgnoreWhitespaceCommand.NotifyCanExecuteChanged();
        }
    }
}
