using System.Collections;
using Differ.Core.Models;

namespace Differ.Formats.Word.Tests;

public sealed class WordCancellationTests
{
    [Fact]
    public void CancelledComparisonNeverProducesDocument()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var empty = new WordLogicalDocument([], []);

        var exception = Assert.Throws<OperationCanceledException>(() => WordBlockComparer.BuildDocument(
            new DiffRequest { RelativePath = "cancelled.docx" }, empty, empty, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void CancellationDuringLastNodeMaterializationNeverProducesDocument()
    {
        using var cancellation = new CancellationTokenSource();
        // Enumeration occurs while merging node metadata, after block alignment.
        // Trigger cancellation at this exact boundary without relying on timers.
        var metadata = new CancellingMetadata(cancellation);
        var oldDocument = new WordLogicalDocument(
            [new WordBlock("Paragraph", "paragraph", "same text", metadata)], []);
        var newDocument = new WordLogicalDocument(
            [new WordBlock("Paragraph", "paragraph", "same text", new Dictionary<string, string>())], []);

        var exception = Assert.Throws<OperationCanceledException>(() => WordBlockComparer.BuildDocument(
            new DiffRequest { RelativePath = "cancelled.docx" }, oldDocument, newDocument, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public void CancellationDuringLargeParagraphPreparationStopsBeforeResult()
    {
        using var cancellation = new CancellationTokenSource();
        var blocks = Enumerable.Range(0, 5000).Select(index => new WordBlock("Paragraph", $"正文/段落{index}", $"内容{index}",
            index == 100 ? new CancelOnLookupMetadata(cancellation) : new Dictionary<string, string>())).ToArray();
        var input = new WordLogicalDocument(blocks, []);
        var exception = Assert.Throws<OperationCanceledException>(() => WordBlockComparer.BuildDocument(
            new DiffRequest { RelativePath = "large.docx" }, input, input, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private sealed class CancelOnLookupMetadata(CancellationTokenSource cancellation) : IReadOnlyDictionary<string, string>
    {
        public int Count => 0;
        public IEnumerable<string> Keys => [];
        public IEnumerable<string> Values => [];
        public string this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out string value)
        {
            cancellation.Cancel();
            value = string.Empty;
            return false;
        }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancellingMetadata(CancellationTokenSource cancellation)
        : IReadOnlyDictionary<string, string>
    {
        public int Count => 0;
        public IEnumerable<string> Keys => [];
        public IEnumerable<string> Values => [];
        public string this[string key] => throw new KeyNotFoundException();
        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            cancellation.Cancel();
            return Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
