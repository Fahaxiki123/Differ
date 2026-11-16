using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.App.Resources;

public static class CodeReviewStrings
{
    private static readonly ResourceManager Manager = new("Differ.App.Resources.CodeReviewStrings", typeof(CodeReviewStrings).Assembly);
    public static string CurrentBlock => Get();
    public static string Line => Get();
    public static string Lines => Get();
    public static string MissingAtStart => Get();
    public static string MissingAfter => Get();
    public static string NotSelected => Get();
    public static string NoDifferences => Get();
    public static string Updating => Get();
    public static string Added => Get();
    public static string Deleted => Get();
    public static string Modified => Get();
    public static string UnchangedLocation => Get();
    public static string UnselectedIndex => Get();
    public static string ResultSide => Get();
    public static string Saved => Get();
    public static string Saving => Get();
    public static string Unsaved => Get();
    public static string NeedsSaveAs => Get();
    public static string UnsavedNeedsSaveAs => Get();
    public static string WorkingCopy => Get();
    public static string LocalFile => Get();
    public static string ResultCopy => Get();
    public static string ReadOnly => Get();
    public static string Editable => Get();
    public static string ModeChangeFailed => Get();
    public static string ModeUnconfirmed => Get();
    public static string AdoptCurrent => Get();
    public static string Location => Get();
    public static string ReadOnlyBaseMessage => Get();
    public static string ReadOnlyMineMessage => Get();
    public static string SaveAsEllipsis => Get();
    public static string SourceFileHint => Get();
    public static string SaveTargetHint => Get();
    public static string SaveTargetUnspecified => Get();
    public static string RefreshUsesSource => Get();
    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
}
