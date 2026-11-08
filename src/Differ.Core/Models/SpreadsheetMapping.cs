namespace Differ.Core.Models;

[Flags]
public enum SpreadsheetCellChange
{
    None = 0,
    Value = 1,
    Formula = 2,
    Type = 4,
    Formatting = 8,
    Comment = 16,
    StructuralReference = 32,
    Ambiguous = 64
}

public enum MappingConfidence
{
    Confirmed,
    Inferred,
    Ambiguous
}

/// <summary>Logical identity is independent of field labels and physical coordinates.</summary>
public sealed record SpreadsheetColumnMatch
{
    public required string LogicalId { get; init; }
    public string? OldColumn { get; init; }
    public string? NewColumn { get; init; }
    public string? FieldName { get; init; }
    public DiffKind Kind { get; init; }
    public MappingConfidence Confidence { get; init; }
    public string Basis { get; init; } = string.Empty;
}

public sealed record SpreadsheetRowMatch
{
    public required string LogicalId { get; init; }
    public int? OldRow { get; init; }
    public int? NewRow { get; init; }
    public DiffKind Kind { get; init; }
    public MappingConfidence Confidence { get; init; }
    public string Basis { get; init; } = string.Empty;
    public bool IsHeader { get; init; }
}

public sealed record SpreadsheetColumnOverride(string? OldColumn, string? NewColumn);

/// <summary>Explicit layout rule, selected by file name and worksheet. No workbook data is persisted.</summary>
public sealed record SpreadsheetRule
{
    public string FilePattern { get; init; } = "*";
    public string SheetName { get; init; } = "*";
    public int HeaderRow { get; init; }
    public IReadOnlyList<int> AuxiliaryHeaderRows { get; init; } = [];
    public int DataStartRow { get; init; } = 1;
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
    public IReadOnlyList<SpreadsheetColumnOverride> ColumnOverrides { get; init; } = [];
    public int FrozenColumns { get; init; } = 1;
}
