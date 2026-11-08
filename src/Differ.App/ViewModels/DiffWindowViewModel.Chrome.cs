using System.ComponentModel;

namespace Differ.App.ViewModels;

public sealed partial class DiffWindowViewModel
{
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

    public string ChromeTabBadge => HasError ? "!" : IsEditableText ? (IsEditorDirty ? "●" : string.Empty) : HasTabBadge ? TabBadgeText : string.Empty;

    public bool HasChromeTabBadge => ChromeTabBadge.Length != 0;

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
    }
}
