using System.Text.RegularExpressions;

namespace CodeAlta.Orchestration.Runtime;

// The text an assistant streamed so far for one content, told on as its sanitized form grows. The sanitized form of a
// text is the text without its schedule blocks, trimmed; each delta tells what that form gained since the last one, as
// stripping the whole text again would tell it, but without stripping the whole text again for each delta.
//
// While the text holds no start of a schedule block, stripping leaves it as it is, and its trimmed form only grows at
// its end: what is told is the text up to its last character that is not white space, a delta costs its own length.
// Once a block may start, the whole text is stripped again for each delta, as it always was.
internal sealed class StreamedAssistantText
{
    private static readonly Regex ScheduleBlockRegex = new(
        @"```codealta_schedule\s*\n.*?```",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    // What a schedule block starts with, matched with the options of ScheduleBlockRegex: a text that holds none at any of
    // its positions is not changed by ScheduleBlockRegex.
    private static readonly Regex ScheduleBlockStartRegex = new(
        "^```codealta_schedule",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

    // The length of "```codealta_schedule".
    private const int BlockStartLength = 20;

    private char[] _text = new char[256];
    private int _length;
    // Where the trimmed text starts (-1 while the text is white space only), and where what was told so far ends.
    private int _start = -1;
    private int _told;
    // The positions below this one start no schedule block.
    private int _scanned;
    // Once a block may start: the sanitized form the last delta told, as the whole-text stripping compares with it.
    private string? _previousStripped;

    /// <summary>Removes the schedule blocks of a text and trims it, as the sanitized form of an assistant text is made.</summary>
    public static string StripScheduleBlocks(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        var stripped = ScheduleBlockRegex.Replace(content, string.Empty);
        return stripped.Trim();
    }

    /// <summary>Appends a delta and returns what the sanitized form gained, or <see langword="null"/> when it gained nothing.</summary>
    public string? Append(string? delta)
    {
        if (delta is { Length: > 0 })
        {
            if (_text.Length - _length < delta.Length)
            {
                Array.Resize(ref _text, Math.Max(_text.Length * 2, _length + delta.Length));
            }

            delta.CopyTo(0, _text, _length, delta.Length);
            _length += delta.Length;
        }

        if (_previousStripped is null && MayStartBlock())
        {
            // What the last delta told was the trimmed text up to there (nothing while it was white space only).
            _previousStripped = _start < 0 ? string.Empty : new string(_text, _start, _told - _start);
        }

        if (_previousStripped is not null)
        {
            var stripped = StripScheduleBlocks(new string(_text, 0, _length));
            if (stripped.Length == 0)
            {
                return null;
            }

            var told = stripped.StartsWith(_previousStripped, StringComparison.Ordinal)
                ? stripped[_previousStripped.Length..]
                : stripped;
            _previousStripped = stripped;
            return told.Length == 0 ? null : told;
        }

        var end = _length;
        while (end > _told && char.IsWhiteSpace(_text[end - 1]))
        {
            end--;
        }

        if (_start < 0)
        {
            var start = 0;
            while (start < end && char.IsWhiteSpace(_text[start]))
            {
                start++;
            }

            if (start == end)
            {
                return null;
            }

            _start = start;
            _told = start;
        }

        if (end <= _told)
        {
            return null;
        }

        var text = new string(_text, _told, end - _told);
        _told = end;
        return text;
    }

    // Whether the text holds the start of a schedule block, checked once at each "```" when the characters that follow it came.
    private bool MayStartBlock()
    {
        for (; _scanned + BlockStartLength <= _length; _scanned++)
        {
            if (_text[_scanned] == '`' && _text[_scanned + 1] == '`' && _text[_scanned + 2] == '`' &&
                ScheduleBlockStartRegex.IsMatch(_text.AsSpan(_scanned, BlockStartLength)))
            {
                return true;
            }
        }

        return false;
    }
}
