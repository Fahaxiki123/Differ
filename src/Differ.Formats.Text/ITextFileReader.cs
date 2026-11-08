namespace Differ.Formats.Text;

public interface ITextFileReader
{
    Task<TextFileContent> ReadAsync(
        string? path,
        CancellationToken cancellationToken = default);

    Task<TextFileContent> ReadWithEncodingAsync(
        string? path,
        string? encodingName,
        CancellationToken cancellationToken = default)
    {
        return ReadAsync(path, cancellationToken);
    }
}
