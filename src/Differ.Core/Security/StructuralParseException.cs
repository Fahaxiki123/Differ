namespace Differ.Core.Security;

/// <summary>
/// A format adapter recognized damaged or unsupported structure and could
/// not produce a reliable model. The original bytes may still be compared.
/// </summary>
public sealed class StructuralParseException : IOException
{
    // Only the package guard can attest that ZipArchive reported invalid
    // structure after the file was successfully opened. This permits its
    // internal seek IOException without accepting arbitrary adapter I/O.
    internal bool IsArchiveStructureFailure { get; init; }

    public StructuralParseException()
    {
    }

    public StructuralParseException(string message)
        : base(message)
    {
    }

    public StructuralParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
