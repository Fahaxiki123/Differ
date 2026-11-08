using System.Collections;
using Differ.Core.Algorithms;

namespace Differ.App.ViewModels;

/// <summary>One lazy result shared by both panes; invisible Word text does not allocate character diffs.</summary>
internal sealed class DeferredInlineSegments(Lazy<InlineDiffResult> result, bool useBaseSide) : IReadOnlyList<InlineDiffSegment>
{
    private IReadOnlyList<InlineDiffSegment> Value => useBaseSide ? result.Value.OldSegments : result.Value.NewSegments;
    public int Count => Value.Count;
    public InlineDiffSegment this[int index] => Value[index];
    public IEnumerator<InlineDiffSegment> GetEnumerator() => Value.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
