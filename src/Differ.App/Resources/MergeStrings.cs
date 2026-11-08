using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.App.Resources;

public static class MergeStrings
{
    private static readonly ResourceManager Manager = new("Differ.App.Resources.MergeStrings", typeof(MergeStrings).Assembly);
    public static string Title => Get();
    public static string Loading => Get();
    public static string Mine => Get();
    public static string Base => Get();
    public static string Theirs => Get();
    public static string Result => Get();
    public static string ReadOnly => Get();
    public static string Editable => Get();
    public static string Previous => Get();
    public static string AutoAdvance => Get();
    public static string AutoAdvanceHint => Get();
    public static string CurrentConflictFormat => Get();
    public static string Next => Get();
    public static string TakeMine => Get();
    public static string TakeBase => Get();
    public static string TakeTheirs => Get();
    public static string Confirm => Get();
    public static string Undo => Get();
    public static string Redo => Get();
    public static string Save => Get();
    public static string SaveAs => Get();
    public static string Finish => Get();
    public static string Find => Get();
    public static string Theme => Get();
    public static string ShowBase => Get();
    public static string ShowBaseHint => Get();
    public static string PreferencesWarning => Get();
    public static string ZoomOut => Get();
    public static string ZoomIn => Get();
    public static string All => Get();
    public static string Conflicts => Get();
    public static string CountFormat => Get();
    public static string NoConflicts => Get();
    public static string Conflict => Get();
    public static string Resolved => Get();
    public static string Automatic => Get();
    public static string Manual => Get();
    public static string Unsaved => Get();
    public static string Saved => Get();
    public static string Pending => Get();
    public static string InitialHint => Get();
    public static string ReadyHint => Get();
    public static string ManualHint => Get();
    public static string Error => Get();
    public static string Stale => Get();
    public static string ClosePrompt => Get();
    public static string UnresolvedClose => Get();
    public static string StartupFailed => Get();
    public static string SyncFailed => Get();
    public static string SaveFilter => Get();
    public static string SelectSource => Get();
    public static string Close => Get();
    public static string Warnings => Get();
    public static string SavedTo => Get();
    public static string OnlyConflicts => Get();
    public static string Resize => Get();
    public static string Retry => Get();
    public static string UnresolvedSave => Get();
    public static string SearchHint => Get();
    public static string ConfirmHint => Get();
    public static string FinishHint => Get();
    public static IReadOnlyDictionary<string, string> Labels { get; } = typeof(MergeStrings).GetProperties()
        .Where(property => property.PropertyType == typeof(string))
        .ToDictionary(property => property.Name, property => (string)property.GetValue(null)!, StringComparer.Ordinal);
    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
}
