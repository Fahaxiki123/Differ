using Differ.App.Resources;
using Differ.Core.Models;

namespace Differ.App.ViewModels;

public sealed record DiffRowViewModel(
    string StableId,
    string Path,
    DiffKind Kind,
    int? OldIndex,
    string OldValue,
    int? NewIndex,
    string NewValue,
    string? SectionKey,
    int Depth)
{
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string KindLabel => Localize(Kind);

    public string DisplayPath => Depth == 0
        ? Path
        : $"{new string(' ', Depth * 2)}{Path}";

    public double OverviewWidth
    {
        get
        {
            var valueLength = Math.Max(OldValue.Length, NewValue.Length);
            return Math.Clamp(8d + (valueLength * 0.28d), 8d, 36d);
        }
    }

    internal static string Localize(DiffKind kind)
    {
        return kind switch
        {
            DiffKind.Unchanged => Strings.DiffUnchanged,
            DiffKind.Added => Strings.DiffAdded,
            DiffKind.Deleted => Strings.DiffDeleted,
            DiffKind.Modified => Strings.DiffModified,
            DiffKind.Moved => Strings.DiffMoved,
            DiffKind.Conflict => Strings.DiffConflict,
            _ => Strings.DiffUnsupported
        };
    }
}

public sealed record UnifiedDiffRowViewModel(
    string StableId,
    string Path,
    DiffKind Kind,
    string Prefix,
    int? OldIndex,
    int? NewIndex,
    string Value,
    int Depth)
{
    public string KindLabel => DiffRowViewModel.Localize(Kind);
}
