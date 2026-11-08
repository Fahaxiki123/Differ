namespace Differ.Svn;

/// <summary>Display/copy only. Generating this template never changes the user's registry.</summary>
public static class TortoiseMergeCommand
{
    public static string Create(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new ArgumentException("Invalid executable path.", nameof(executablePath));
        return $"\"{executablePath}\" --merge --base %base --mine %mine --theirs %theirs --merged %merged "
            + "--base-name %bname --mine-name %yname --theirs-name %tname --merged-name %mname --file-name %fname";
    }
}
