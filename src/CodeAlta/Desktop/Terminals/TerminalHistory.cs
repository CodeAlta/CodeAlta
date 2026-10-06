using System.Text;

namespace CodeAlta.Desktop.Terminals;

/// <summary>A piece of what a program wrote, with the size of the screen it was written for.</summary>
/// <param name="Offset">Where the piece starts, counted in characters from the start of the terminal.</param>
/// <param name="Text">The characters.</param>
/// <param name="Columns">The width of the screen when they were written.</param>
/// <param name="Rows">Its height.</param>
/// <param name="Modes">The sequences that put a terminal in the modes this one was in at <paramref name="Offset"/>, when the piece starts where they were noted; null otherwise.</param>
internal readonly record struct TerminalPiece(long Offset, string Text, int Columns, int Rows, string? Modes);

/// <summary>
/// What the program of a terminal wrote last, as it wrote it: what a window that shows the terminal later is
/// given first, so that it shows what the others show. It is kept in parts, each written for one size of the
/// screen and starting between two sequences; the oldest parts go when it grows past its capacity.
/// </summary>
/// <remarks>Not thread-safe: its owner gives it one caller at a time.</remarks>
internal sealed class TerminalHistory
{
    /// <summary>How many characters are kept, unless told otherwise.</summary>
    internal const int DefaultCapacity = 2 * 1024 * 1024;
    /// <summary>The size a part is closed at, as soon as the terminal is between two sequences.</summary>
    internal const int PartSize = 64 * 1024;

    private sealed class Part(long start, int columns, int rows, string modes)
    {
        public readonly long Start = start;
        public readonly int Columns = columns, Rows = rows;
        public readonly string Modes = modes;
        public readonly StringBuilder Text = new();
    }

    private readonly int _capacity;
    private readonly List<Part> _parts = [];
    private int _columns, _rows;
    private string _modes = string.Empty;
    private bool _cut = true;
    private long _end;
    private long _kept;

    /// <summary>Creates the history of a new terminal.</summary>
    internal TerminalHistory(int columns, int rows, int capacity = DefaultCapacity)
    {
        (_columns, _rows) = (columns, rows);
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>The offset of the first character still kept.</summary>
    public long Start => _parts.Count == 0 ? _end : _parts[0].Start;

    /// <summary>The offset after the last character written.</summary>
    public long End => _end;

    /// <summary>Whether the part being written has reached its size: its owner cuts it once the terminal is between two sequences.</summary>
    public bool Full => !_cut && _parts.Count > 0 && _parts[^1].Text.Length >= PartSize;

    /// <summary>What is written next starts a part: it is for a screen of this size, in a terminal in these modes.</summary>
    public void Cut(int columns, int rows, string modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        (_columns, _rows, _modes, _cut) = (columns, rows, modes, true);
    }

    /// <summary>Adds what the program wrote.</summary>
    public void Append(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return;
        if (_cut)
        {
            _parts.Add(new Part(_end, _columns, _rows, _modes));
            _cut = false;
        }
        _parts[^1].Text.Append(text);
        _end += text.Length;
        _kept += text.Length;
        // The part being written always stays, whatever its size.
        while (_kept > _capacity && _parts.Count > 1)
        {
            _kept -= _parts[0].Text.Length;
            _parts.RemoveAt(0);
        }
    }

    /// <summary>
    /// The piece that starts at an offset, of one part and of at most <paramref name="maximum"/> characters.
    /// It starts at <see cref="Start"/> instead when what was at the offset is no longer kept.
    /// </summary>
    /// <returns>The piece, or null when nothing was written from the offset on.</returns>
    public TerminalPiece? Read(long offset, int maximum)
    {
        offset = Math.Max(offset, Start);
        if (offset >= _end || maximum <= 0) return null;
        // The last part that starts at or before the offset holds it.
        var index = _parts.Count - 1;
        while (index > 0 && _parts[index].Start > offset) index--;
        var part = _parts[index];
        var from = (int)(offset - part.Start);
        var count = Math.Min(maximum, part.Text.Length - from);
        // A pair of surrogates is not cut in two.
        if (count > 1 && from + count < part.Text.Length && char.IsHighSurrogate(part.Text[from + count - 1])) count--;
        return new TerminalPiece(offset, part.Text.ToString(from, count), part.Columns, part.Rows, from == 0 ? part.Modes : null);
    }
}
