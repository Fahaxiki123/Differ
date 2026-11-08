namespace Differ.Core.Security;

/// <summary>
/// A comparison exceeded its safe materialization budget. This is not a
/// corrupt format and must never trigger another, potentially costly parser.
/// </summary>
public sealed class ComparisonResourceLimitException : IOException
{
    public ComparisonResourceLimitException()
    {
    }

    public ComparisonResourceLimitException(string message)
        : base(message)
    {
    }

    public ComparisonResourceLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
