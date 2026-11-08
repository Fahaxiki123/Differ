using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace Differ.Core.Resources;

public static class CoreStrings
{
    private static readonly ResourceManager Manager =
        new("Differ.Core.Resources.CoreStrings", typeof(CoreStrings).Assembly);

    public static string CoordinatorUnsupportedReason => Get();
    public static string CoordinatorUnsupportedWarning => Get();
    public static string CoordinatorParseFallbackWarning => Get();
    public static string GuardTooManyPartsFormat => Get();
    public static string GuardExpandedTooLargeFormat => Get();
    public static string GuardSuspiciousRatioFormat => Get();
    public static string GuardInvalidPackageStructure => Get();

    private static string Get([CallerMemberName] string name = "")
    {
        return Manager.GetString(name, CultureInfo.CurrentCulture) ?? name;
    }
}
