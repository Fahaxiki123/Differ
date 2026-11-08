using Differ.Core.Models;

namespace Differ.Core.Abstractions;

public interface IFileDiffAdapter
{
    FileKind FileKind { get; }

    bool CanHandle(string path);

    Task<DiffDocument> CompareAsync(
        DiffRequest request,
        CancellationToken cancellationToken);
}
