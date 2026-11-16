namespace Differ.Core.External;

public sealed record ExternalMergeRequest
{
    public required string BaseFilePath { get; init; }
    public required string MineFilePath { get; init; }
    public required string TheirsFilePath { get; init; }
    public required string MergedFilePath { get; init; }
    public string? BaseDisplayName { get; init; }
    public string? MineDisplayName { get; init; }
    public string? TheirsDisplayName { get; init; }
    public string? MergedDisplayName { get; init; }
    public string? OriginalFileName { get; init; }
}

public sealed record ExternalMergeArgumentResult(
    ExternalMergeRequest? Request,
    ExternalDiffArgumentError Error = ExternalDiffArgumentError.None,
    string? ErrorArgument = null);

/// <summary>Explicit four-path merge protocol; never changes the legacy two-file parser.</summary>
public static class ExternalMergeArguments
{
    private static readonly HashSet<string> Options = new(StringComparer.OrdinalIgnoreCase)
    {
        "--base", "--mine", "--theirs", "--merged", "--base-name", "--mine-name",
        "--theirs-name", "--merged-name", "--file-name", "--source"
    };

    public static bool IsMergeInvocation(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(value =>
        {
            var name = value.Split('=', 2)[0];
            return name.Equals("--merge", StringComparison.OrdinalIgnoreCase)
                || name.Equals("--theirs", StringComparison.OrdinalIgnoreCase)
                || name.Equals("--merged", StringComparison.OrdinalIgnoreCase);
        });
    }

    public static ExternalMergeArgumentResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var merge = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var value = arguments[index];
            if (value.Equals("--merge", StringComparison.OrdinalIgnoreCase))
            {
                if (merge) return new(null, ExternalDiffArgumentError.DuplicateOption, value);
                merge = true;
                continue;
            }
            var separator = value.IndexOf('=', StringComparison.Ordinal);
            var option = separator >= 0 ? value[..separator] : value;
            if (!Options.Contains(option)) return new(null, ExternalDiffArgumentError.UnknownOption, option);
            if (values.ContainsKey(option)) return new(null, ExternalDiffArgumentError.DuplicateOption, option);
            if (separator >= 0) value = value[(separator + 1)..];
            else
            {
                if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    return new(null, ExternalDiffArgumentError.MissingOptionValue, option);
                value = arguments[++index];
            }
            if (string.IsNullOrWhiteSpace(value)) return new(null, ExternalDiffArgumentError.MissingOptionValue, option);
            values.Add(option, value);
        }
        if (!merge) return new(null, ExternalDiffArgumentError.MissingRequiredOption, "--merge");
        foreach (var required in new[] { "--base", "--mine", "--theirs", "--merged" })
            if (!values.ContainsKey(required)) return new(null, ExternalDiffArgumentError.MissingRequiredOption, required);
        return new(new ExternalMergeRequest
        {
            BaseFilePath = values["--base"],
            MineFilePath = values["--mine"],
            TheirsFilePath = values["--theirs"],
            MergedFilePath = values["--merged"],
            BaseDisplayName = values.GetValueOrDefault("--base-name"),
            MineDisplayName = values.GetValueOrDefault("--mine-name"),
            TheirsDisplayName = values.GetValueOrDefault("--theirs-name"),
            MergedDisplayName = values.GetValueOrDefault("--merged-name"),
            OriginalFileName = values.GetValueOrDefault("--file-name")
        });
    }
}
