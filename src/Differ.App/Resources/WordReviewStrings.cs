using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.App.Resources;

public static class WordReviewStrings
{
    private static readonly ResourceManager Manager = new("Differ.App.Resources.WordReviewStrings", typeof(WordReviewStrings).Assembly);
    public static string GroupUnresolved => Get();
    public static string GroupSplitMerge => Get();
    public static string GroupSummary => Get();
    public static string GroupPreview => Get();
    public static string GroupWindow => Get();
    public static string Expand => Get();
    public static string Collapse => Get();
    public static string Previous => Get();
    public static string Next => Get();
    public static string Missing => Get();
    public static string ApproximateInline => Get();
    public static string SelectCharacters => Get();
    public static string SelectCharactersHint => Get();
    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
}
