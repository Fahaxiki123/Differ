namespace Differ.App.ViewModels;

public enum DiffDisplayMode
{
    All,
    DifferencesOnly,
    Context
}

/// <summary>Projects complete logical rows; folding never splits a table row.</summary>
internal sealed class ContextFoldProjection<T> where T : class
{
    private const int ContextSize = 3;
    private readonly IReadOnlyList<T> _source;
    private readonly IReadOnlyList<T> _changed;
    private readonly bool[] _contextVisible;
    private readonly bool[] _expanded;
    private readonly Func<T, bool> _hasDifference;

    public ContextFoldProjection(IReadOnlyList<T> source, Func<T, bool> hasDifference,
        CancellationToken cancellationToken = default)
    {
        _source = source;
        _hasDifference = hasDifference;
        var changed = new List<T>();
        _contextVisible = new bool[source.Count];
        _expanded = new bool[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasDifference(source[index]))
            {
                continue;
            }

            changed.Add(source[index]);
            Array.Fill(_contextVisible, true, Math.Max(0, index - ContextSize),
                Math.Min(source.Count - 1, index + ContextSize) - Math.Max(0, index - ContextSize) + 1);
        }

        _changed = changed;
    }

    public IReadOnlyList<T> Build(DiffDisplayMode mode, Func<int, int, T> createCollapsed)
    {
        if (mode == DiffDisplayMode.All)
        {
            return _source;
        }

        if (mode == DiffDisplayMode.DifferencesOnly)
        {
            return _changed;
        }

        var result = new List<T>();
        for (var index = 0; index < _source.Count;)
        {
            if (_contextVisible[index] || _expanded[index])
            {
                result.Add(_source[index++]);
                continue;
            }

            var start = index;
            while (index < _source.Count && !_contextVisible[index] && !_expanded[index])
            {
                index++;
            }

            result.Add(createCollapsed(start, index - start));
        }

        return result;
    }

    public bool IsVisible(int index, DiffDisplayMode mode) => index >= 0 && index < _source.Count
        && (mode == DiffDisplayMode.All || mode == DiffDisplayMode.DifferencesOnly
            ? mode == DiffDisplayMode.All || _hasDifference(_source[index])
            : _contextVisible[index] || _expanded[index]);

    public bool Reveal(int index)
    {
        if (index < 0 || index >= _source.Count || _contextVisible[index] || _expanded[index])
        {
            return false;
        }

        var start = index;
        var end = index + 1;
        while (start > 0 && !_contextVisible[start - 1] && !_expanded[start - 1])
        {
            start--;
        }

        while (end < _source.Count && !_contextVisible[end] && !_expanded[end])
        {
            end++;
        }

        Array.Fill(_expanded, true, start, end - start);
        return true;
    }
}
