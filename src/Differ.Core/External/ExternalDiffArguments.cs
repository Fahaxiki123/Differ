namespace Differ.Core.External;

public enum ExternalDiffArgumentStatus
{
    NoArguments,
    Success,
    Error
}

public enum ExternalDiffArgumentError
{
    None,
    UnknownOption,
    MissingOptionValue,
    DuplicateOption,
    MissingRequiredOption,
    InvalidPositionalCount,
    MixedArgumentStyles
}

public sealed record ExternalDiffArgumentResult
{
    public required ExternalDiffArgumentStatus Status { get; init; }

    public ExternalDiffRequest? Request { get; init; }

    public ExternalDiffArgumentError Error { get; init; }

    public string? ErrorArgument { get; init; }

    public static ExternalDiffArgumentResult NoArguments()
    {
        return new ExternalDiffArgumentResult
        {
            Status = ExternalDiffArgumentStatus.NoArguments
        };
    }

    public static ExternalDiffArgumentResult Success(ExternalDiffRequest request)
    {
        return new ExternalDiffArgumentResult
        {
            Status = ExternalDiffArgumentStatus.Success,
            Request = request
        };
    }

    public static ExternalDiffArgumentResult Failure(
        ExternalDiffArgumentError error,
        string? errorArgument = null)
    {
        return new ExternalDiffArgumentResult
        {
            Status = ExternalDiffArgumentStatus.Error,
            Error = error,
            ErrorArgument = errorArgument
        };
    }
}

public static class ExternalDiffArguments
{
    private static readonly Dictionary<string, string> OptionNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["--base"] = "base",
            ["--mine"] = "mine",
            ["--base-name"] = "base-name",
            ["--mine-name"] = "mine-name",
            ["--base-revision"] = "base-revision",
            ["--mine-revision"] = "mine-revision",
            ["--file-name"] = "file-name",
            ["--source"] = "source"
        };

    public static ExternalDiffArgumentResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            return ExternalDiffArgumentResult.NoArguments();
        }

        var containsNamedOption = arguments.Any(IsOption);
        var containsPositional = arguments.Any(argument => !IsOption(argument));

        if (!containsNamedOption)
        {
            return ParsePositional(arguments);
        }

        if (containsPositional && !ValuesBelongToNamedOptions(arguments))
        {
            return ExternalDiffArgumentResult.Failure(
                ExternalDiffArgumentError.MixedArgumentStyles);
        }

        return ParseNamed(arguments);
    }

    private static ExternalDiffArgumentResult ParsePositional(
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 2)
        {
            return ExternalDiffArgumentResult.Success(new ExternalDiffRequest
            {
                BaseFilePath = arguments[0],
                MineFilePath = arguments[1]
            });
        }

        // TortoiseSVN appends six positional arguments when the configured
        // command contains no % placeholders: base, mine, both display
        // titles, and both revisions. Accept that legacy protocol so users
        // can point TortoiseSVN at Differ without a parameter template.
        if (arguments.Count == 6)
        {
            return ExternalDiffArgumentResult.Success(new ExternalDiffRequest
            {
                BaseFilePath = arguments[0],
                MineFilePath = arguments[1],
                BaseDisplayName = ToOptional(arguments[2]),
                MineDisplayName = ToOptional(arguments[3]),
                BaseRevision = ToOptional(arguments[4]),
                MineRevision = ToOptional(arguments[5])
            });
        }

        return ExternalDiffArgumentResult.Failure(
            ExternalDiffArgumentError.InvalidPositionalCount);
    }

    private static string? ToOptional(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static ExternalDiffArgumentResult ParseNamed(
        IReadOnlyList<string> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var separatorIndex = argument.IndexOf('=');
            var option = separatorIndex > 0
                ? argument[..separatorIndex]
                : argument;

            if (!OptionNames.TryGetValue(option, out var canonicalName))
            {
                return ExternalDiffArgumentResult.Failure(
                    ExternalDiffArgumentError.UnknownOption,
                    option);
            }

            if (values.ContainsKey(canonicalName))
            {
                return ExternalDiffArgumentResult.Failure(
                    ExternalDiffArgumentError.DuplicateOption,
                    option);
            }

            string value;
            if (separatorIndex > 0)
            {
                value = argument[(separatorIndex + 1)..];
            }
            else
            {
                if (index + 1 >= arguments.Count || IsOption(arguments[index + 1]))
                {
                    return ExternalDiffArgumentResult.Failure(
                        ExternalDiffArgumentError.MissingOptionValue,
                        option);
                }

                value = arguments[++index];
            }

            values.Add(canonicalName, value);
        }

        if (!values.TryGetValue("base", out var basePath)
            || string.IsNullOrWhiteSpace(basePath))
        {
            return ExternalDiffArgumentResult.Failure(
                ExternalDiffArgumentError.MissingRequiredOption,
                "--base");
        }

        if (!values.TryGetValue("mine", out var minePath)
            || string.IsNullOrWhiteSpace(minePath))
        {
            return ExternalDiffArgumentResult.Failure(
                ExternalDiffArgumentError.MissingRequiredOption,
                "--mine");
        }

        return ExternalDiffArgumentResult.Success(new ExternalDiffRequest
        {
            BaseFilePath = basePath,
            MineFilePath = minePath,
            BaseDisplayName = GetOptional(values, "base-name"),
            MineDisplayName = GetOptional(values, "mine-name"),
            BaseRevision = GetOptional(values, "base-revision"),
            MineRevision = GetOptional(values, "mine-revision"),
            OriginalFileName = GetOptional(values, "file-name"),
            InvocationSource = GetOptional(values, "source")
        });
    }

    private static bool ValuesBelongToNamedOptions(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!IsOption(argument))
            {
                return false;
            }

            if (argument.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            index++;
            if (index >= arguments.Count)
            {
                return true;
            }
        }

        return true;
    }

    private static bool IsOption(string argument)
    {
        return argument.StartsWith("--", StringComparison.Ordinal);
    }

    private static string? GetOptional(
        Dictionary<string, string> values,
        string name)
    {
        return values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }
}
