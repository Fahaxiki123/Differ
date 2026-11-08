using System.Globalization;
using System.Resources;

namespace Differ.Formats.Csv.Resources;

internal static class CsvStrings
{
    private static readonly ResourceManager Manager =
        new("Differ.Formats.Csv.Resources.CsvStrings", typeof(CsvStrings).Assembly);

    public static string InvalidQuotes =>
        Manager.GetString(nameof(InvalidQuotes), CultureInfo.CurrentCulture) ?? nameof(InvalidQuotes);
}
