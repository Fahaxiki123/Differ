using System.Collections.ObjectModel;

namespace Differ.Svn;

public static class TortoiseDiffToolRegistration
{
    private static readonly ReadOnlyCollection<string> Extensions =
        Array.AsReadOnly(
        [
            ".txt", ".md", ".log", ".ini", ".config", ".csv",
            ".cs", ".java", ".cpp", ".c", ".h",
            ".js", ".ts", ".jsx", ".tsx", ".vue",
            ".py", ".lua", ".go", ".rs",
            ".xml", ".json", ".yaml", ".yml",
            ".html", ".css", ".scss", ".sql",
            ".docx", ".doc", ".rtf", ".xlsx", ".xlsm", ".xls"
        ]);

    public static IReadOnlyList<string> SupportedExtensions => Extensions;

    public static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var fullPath = Path.GetFullPath(executablePath);
        if (fullPath.Contains('"'))
        {
            throw new ArgumentException(
                "The executable path cannot contain a quotation mark.",
                nameof(executablePath));
        }

        return $"\"{fullPath}\" --base %base --mine %mine"
            + " --base-name %bname --mine-name %yname"
            + " --base-revision %brev --mine-revision %yrev"
            + " --file-name %fname --source tortoisesvn";
    }
}
