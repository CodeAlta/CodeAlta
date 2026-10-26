namespace CodeAlta.Agent;

/// <summary>
/// Orders model names for a list: by name, with the highest version first among the names that differ only by their
/// numbers. "Claude Fable 5.1" comes before "Claude Fable 5", "Gemini 4 Argon" before "Gemini 3.8 Flash", and
/// "GPT-5.1" before "GPT-5.1 Codex", which comes before "GPT-5".
/// </summary>
/// <remarks>
/// A name is read as words and numbers: the words are compared without case, the numbers by value, the higher first,
/// part by part for a dotted number (<c>5.10</c> is above <c>5.9</c>, <c>5.1</c> above <c>5</c>). Spaces and
/// punctuation only separate them, so <c>gpt-5</c> and <c>GPT 5</c> are the same name. A name that is the start of
/// another comes before it.
/// </remarks>
public sealed class ModelNameComparer : IComparer<string?>
{
    private ModelNameComparer()
    {
    }

    /// <summary>Gets the comparer.</summary>
    public static ModelNameComparer Instance { get; } = new();

    /// <summary>Returns the models in the order of their names (their display name, else their id), then of their ids.</summary>
    /// <param name="models">The models; not changed.</param>
    /// <returns>A new array of the models, the ones of the same name in their order of <paramref name="models"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="models"/> is null.</exception>
    public static AgentModelInfo[] Sort(IEnumerable<AgentModelInfo> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        return [.. models
            .OrderBy(static model => model.DisplayName ?? model.Id, Instance)
            .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var left = new Tokens(x);
        var right = new Tokens(y);
        while (true)
        {
            var hasLeft = left.Next(out var leftToken, out var leftNumber);
            var hasRight = right.Next(out var rightToken, out var rightNumber);
            if (!hasLeft || !hasRight) return hasLeft == hasRight ? 0 : hasLeft ? 1 : -1;
            // A version comes before a word at the same place: "Grok 4" before "Grok Code".
            if (leftNumber != rightNumber) return leftNumber ? -1 : 1;
            var order = leftNumber ? CompareVersions(rightToken, leftToken) : leftToken.CompareTo(rightToken, StringComparison.OrdinalIgnoreCase);
            if (order != 0) return order;
        }
    }

    // Dotted numbers, part by part; a number with more parts is the higher when the others are equal.
    private static int CompareVersions(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        while (true)
        {
            if (x.IsEmpty || y.IsEmpty) return x.IsEmpty == y.IsEmpty ? 0 : x.IsEmpty ? -1 : 1;
            var order = CompareNumbers(Part(ref x), Part(ref y));
            if (order != 0) return order;
        }

        static ReadOnlySpan<char> Part(ref ReadOnlySpan<char> number)
        {
            var dot = number.IndexOf('.');
            var part = dot < 0 ? number : number[..dot];
            number = dot < 0 ? [] : number[(dot + 1)..];
            return part;
        }
    }

    // Digits of any length (a date such as 20251001 included), without parsing: no overflow.
    private static int CompareNumbers(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        x = x.TrimStart('0');
        y = y.TrimStart('0');
        return x.Length != y.Length ? x.Length.CompareTo(y.Length) : x.SequenceCompareTo(y);
    }

    // The words and the numbers of a name, in their order.
    private ref struct Tokens(ReadOnlySpan<char> text)
    {
        private ReadOnlySpan<char> _rest = text;

        public bool Next(out ReadOnlySpan<char> token, out bool number)
        {
            var start = 0;
            while (start < _rest.Length && !char.IsLetterOrDigit(_rest[start])) start++;
            _rest = _rest[start..];
            token = default;
            number = false;
            if (_rest.IsEmpty) return false;
            number = char.IsAsciiDigit(_rest[0]);
            var end = 1;
            if (number)
            {
                // A dot between two digits is part of the number: 4.5, 2.0.1.
                while (end < _rest.Length && (char.IsAsciiDigit(_rest[end]) || (_rest[end] == '.' && end + 1 < _rest.Length && char.IsAsciiDigit(_rest[end + 1])))) end++;
            }
            else
            {
                while (end < _rest.Length && char.IsLetterOrDigit(_rest[end]) && !char.IsAsciiDigit(_rest[end])) end++;
            }

            token = _rest[..end];
            _rest = _rest[end..];
            return true;
        }
    }
}
