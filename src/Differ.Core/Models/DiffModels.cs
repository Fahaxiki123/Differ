using System.Collections.ObjectModel;

namespace Differ.Core.Models;

public enum FileKind
{
    Text,
    Word,
    Excel,
    Binary,
    Unsupported
}

public enum DiffKind
{
    Unchanged,
    Added,
    Deleted,
    Modified,
    Moved,
    Conflict,
    Unsupported
}

public sealed record DiffNode
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>());
    public required string StableId { get; init; }

    public required string Path { get; init; }

    public required DiffKind Kind { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public int? OldIndex { get; init; }

    public int? NewIndex { get; init; }

    public string? SectionKey { get; init; }

    public IReadOnlyList<DiffNode> Children { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        EmptyMetadata;
}

public sealed record TabularDiffCell
{
    public required string ColumnKey { get; init; }

    public string? OldAddress { get; init; }

    public string? NewAddress { get; init; }

    public string? OldValue { get; init; }

    public string? NewValue { get; init; }

    public string? OldDetails { get; init; }

    public string? NewDetails { get; init; }

    public required DiffKind Kind { get; init; }

    public bool IsStructuralReferenceAdjustment { get; init; }
    public SpreadsheetCellChange ChangeFlags { get; init; }
}

public sealed record TabularDiffRow
{
    public required string StableId { get; init; }

    public int? OldIndex { get; init; }

    public int? NewIndex { get; init; }

    public required DiffKind Kind { get; init; }

    public required IReadOnlyList<TabularDiffCell> Cells { get; init; }
}

public sealed record TabularDiffSection
{
    public required string Key { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required IReadOnlyList<TabularDiffRow> Rows { get; init; }

    public IReadOnlyList<SpreadsheetColumnMatch> ColumnMatches { get; init; } = [];

    public IReadOnlyList<SpreadsheetRowMatch> RowMatches { get; init; } = [];

    public int HeaderRow { get; init; }

    public int DataStartRow { get; init; } = 1;

    public string MappingDescription { get; init; } = string.Empty;
    public int FrozenColumns { get; init; } = 1;
}

public sealed record DiffSummary(
    int Added,
    int Deleted,
    int Modified,
    int Moved,
    int Conflicts,
    int Unsupported,
    int Unchanged)
{
    public int Changed => Added + Deleted + Modified + Moved + Conflicts + Unsupported;

    public static DiffSummary FromNodes(IEnumerable<DiffNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);

        var counts = new int[Enum.GetValues<DiffKind>().Length];
        foreach (var node in Flatten(nodes))
        {
            counts[(int)node.Kind]++;
        }

        return new DiffSummary(
            counts[(int)DiffKind.Added],
            counts[(int)DiffKind.Deleted],
            counts[(int)DiffKind.Modified],
            counts[(int)DiffKind.Moved],
            counts[(int)DiffKind.Conflict],
            counts[(int)DiffKind.Unsupported],
            counts[(int)DiffKind.Unchanged]);
    }

    private static IEnumerable<DiffNode> Flatten(IEnumerable<DiffNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }
}

public sealed record DiffDocument
{
    public required string RelativePath { get; init; }

    public required FileKind FileKind { get; init; }

    public string? BaseRevision { get; init; }

    public required IReadOnlyList<DiffNode> Nodes { get; init; }

    public required DiffSummary Summary { get; init; }

    public IReadOnlyList<string> Sections { get; init; } = [];

    public IReadOnlyList<TabularDiffSection> TabularSections { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record DiffOptions
{
    public string? TextEncodingName { get; init; }

    public bool IgnoreLineEndings { get; init; }

    public bool IgnoreWhitespace { get; init; }

    public bool IgnoreCase { get; init; }

    public bool CompareFormatting { get; init; }

    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    public IReadOnlyList<SpreadsheetRule> SpreadsheetRules { get; init; } = [];

    public bool IncludeUnchanged { get; init; } = true;
}

public sealed record DiffRequest
{
    public required string RelativePath { get; init; }

    public string? BaseFilePath { get; init; }

    public string? CurrentFilePath { get; init; }

    public string? BaseRevision { get; init; }

    public DiffOptions Options { get; init; } = new();
}
