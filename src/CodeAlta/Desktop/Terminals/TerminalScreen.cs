using System.Globalization;
using System.Text;

namespace CodeAlta.Desktop.Terminals;

/// <summary>Something a terminal learned from what its program wrote, beside the text it shows.</summary>
internal enum TerminalSignal
{
    /// <summary>The program gave the terminal another title.</summary>
    Title,
    /// <summary>The shell is in another folder.</summary>
    Folder,
    /// <summary>The shell shows its prompt: nothing runs.</summary>
    Prompt,
    /// <summary>The shell runs a command.</summary>
    CommandStarted,
    /// <summary>The command the shell ran has ended.</summary>
    CommandFinished,
    /// <summary>The program rang the bell.</summary>
    Bell,
}

/// <summary>A place in the text of a terminal that follows its row as the row scrolls.</summary>
internal sealed class TerminalMark
{
    /// <summary>The column in a row of the screen, then the offset in the text of a row that left the screen.</summary>
    internal int Column;
    /// <summary>Whether the row left the screen.</summary>
    internal bool Kept;
    /// <summary>Whether the row is gone: it scrolled past what the terminal keeps, or was dropped.</summary>
    internal bool Lost;
}

/// <summary>A command a shell ran, as its integration script reported it.</summary>
internal sealed class TerminalCommand
{
    /// <summary>The rank of the command among those the terminal saw, from 1.</summary>
    public required long Number { get; init; }
    /// <summary>The command line, when the shell reported it.</summary>
    public string? CommandLine { get; internal set; }
    /// <summary>The folder the shell was in.</summary>
    public string? Folder { get; internal set; }
    /// <summary>The exit code, once the command has ended and when the shell reported one.</summary>
    public int? ExitCode { get; internal set; }
    /// <summary>Whether the command has ended.</summary>
    public bool Finished { get; internal set; }
    internal TerminalMark? Input, Output, End;
}

/// <summary>
/// The text a terminal shows, kept beside its program: what is on the screen and what scrolled off it. It
/// reads what the terminal of the window reads, so that a terminal can be read while no window shows it.
/// It follows xterm.js where the two could differ (wrapping, resizing); colors and styles are not kept.
/// </summary>
/// <remarks>Not thread-safe: its owner gives it one caller at a time.</remarks>
internal sealed partial class TerminalScreen
{
    /// <summary>How many rows that left the screen are kept, unless told otherwise.</summary>
    internal const int DefaultScrollback = 10_000;
    /// <summary>How many commands are remembered.</summary>
    internal const int MaximumCommands = 200;
    private const int MaximumString = 16 * 1024;
    private const int MaximumParameters = 32;
    // The second half of a wide character.
    private const int Trailing = -1;
    // The line-drawing characters a program selects instead of the ASCII ones from '_' to '~'.
    private const string Graphics = " ◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

    private enum State { Ground, Escape, EscapeIntermediate, Control, ControlIgnore, Command, CommandEscape, Text, TextEscape }

    private struct Cell
    {
        public int Rune;
        public byte Width;
    }

    private sealed class Row(int columns)
    {
        public Cell[] Cells = new Cell[columns];
        /// <summary>Whether this row goes on from the row above: the line was too long for one row.</summary>
        public bool Continues;
        /// <summary>The characters that join the one of a cell (accents, variation selectors), by column.</summary>
        public Dictionary<int, string>? Joined;
        public List<TerminalMark>? Marks;

        /// <summary>The number of columns up to the last one written.</summary>
        public int Used
        {
            get
            {
                var used = Cells.Length;
                while (used > 0 && Cells[used - 1].Rune == 0) used--;
                return used;
            }
        }
    }

    private readonly record struct KeptRow(string Text, bool Continues, List<TerminalMark>? Marks);

    private sealed class Grid(int columns, int rows)
    {
        public Row[] Rows = NewRows(columns, rows);
        public int X, Y, Top, Bottom = rows - 1;
        /// <summary>The cursor is after the last column: the next character starts the next row.</summary>
        public bool Pending;
        public bool Origin;
        public int SavedX, SavedY;
        public bool SavedOrigin;

        public static Row[] NewRows(int columns, int rows)
        {
            var created = new Row[rows];
            for (var index = 0; index < rows; index++) created[index] = new Row(columns);
            return created;
        }
    }

    private readonly int _scrollback;
    private readonly bool _console;
    private readonly bool _reflow;
    private readonly Grid _normal;
    private readonly Grid _alternate;
    private readonly SortedSet<int> _modes = [7, 25];
    private readonly List<int> _parameters = [];
    private readonly StringBuilder _string = new();
    private readonly List<TerminalCommand> _commands = [];
    private Grid _active;
    private KeptRow[] _kept;
    private int _keptStart, _keptCount;
    private int _columns, _rows;
    private bool[] _tabs;
    private State _state;
    private char _high;
    private int _current = -1;
    private char _prefix, _intermediate;
    private bool _autoWrap = true, _insert, _keypad;
    private bool _graphics0, _graphics1, _shifted;
    private int _last;
    private long _commandNumber;
    private TerminalCommand? _command;

    /// <summary>Creates the screen of a new terminal.</summary>
    /// <param name="columns">Its width.</param>
    /// <param name="rows">Its height.</param>
    /// <param name="scrollback">How many rows that left the screen are kept.</param>
    /// <param name="console">Whether the program is behind a Windows pseudoconsole, which repaints its own view of the screen.</param>
    /// <param name="reflow">Whether lines are wrapped again when the width changes (a pseudoconsole before Windows 11 does it alone).</param>
    internal TerminalScreen(int columns, int rows, int scrollback = DefaultScrollback, bool console = false, bool reflow = true)
    {
        _columns = PseudoTerminal.Clamp(columns);
        _rows = PseudoTerminal.Clamp(rows);
        _scrollback = Math.Max(0, scrollback);
        _console = console;
        _reflow = reflow;
        _normal = new Grid(_columns, _rows);
        _alternate = new Grid(_columns, _rows);
        _active = _normal;
        _kept = new KeptRow[Math.Min(_scrollback, 256)];
        _tabs = Tabs(_columns);
    }

    /// <summary>The width of the screen.</summary>
    public int Columns => _columns;

    /// <summary>The height of the screen.</summary>
    public int Rows => _rows;

    /// <summary>The title the program gave the terminal, or null.</summary>
    public string? Title { get; private set; }

    /// <summary>The folder the shell said it is in, or null.</summary>
    public string? Folder { get; private set; }

    /// <summary>Whether a program that draws the whole screen (an editor, a pager) has its own screen up.</summary>
    public bool Alternate => _active == _alternate;

    /// <summary>The row and the column of the cursor, from 0.</summary>
    public (int Row, int Column) Cursor => (_active.Y, _active.X);

    /// <summary>Whether the shell reported a prompt: it says when it is back at it.</summary>
    public bool Integrated { get; private set; }

    /// <summary>Whether the shell reports the commands it runs too, as a shell started with an integration script does.</summary>
    public bool Tracks { get; private set; }

    /// <summary>Whether a command runs, as far as the shell reported.</summary>
    public bool Busy => _command is { Finished: false, Output: not null };

    /// <summary>How many times the shell reported that it shows its prompt.</summary>
    public long Prompts { get; private set; }

    /// <summary>Whether the shell reported its prompt and no command since: what is typed now is a line for the shell.</summary>
    public bool AtPrompt { get; private set; }

    /// <summary>Whether the program turned on one of the modes a terminal has a number for: 1 for the arrow keys of full-screen programs, 2004 for pastes that are told apart from typing.</summary>
    public bool Has(int mode) => _modes.Contains(mode);

    /// <summary>The commands the shell reported, the oldest first.</summary>
    public IReadOnlyList<TerminalCommand> Commands => _commands;

    /// <summary>The number of rows that left the screen and are kept.</summary>
    public int KeptRows => _keptCount;

    /// <summary>Whether what was read so far ends between two sequences and two characters: what is read next can be read alone.</summary>
    public bool AtRest => _state == State.Ground && _high == '\0';

    /// <summary>Receives what the terminal answers to a question of the program (where the cursor is, what the terminal is).</summary>
    public Action<string>? Reply { get; set; }

    /// <summary>Receives what the terminal learned beside its text.</summary>
    public Action<TerminalSignal>? Signal { get; set; }

    /// <summary>Reads what the program wrote.</summary>
    public void Write(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (_high != 0)
            {
                var high = _high;
                _high = '\0';
                if (char.IsLowSurrogate(character))
                {
                    Accept(char.ConvertToUtf32(high, character));
                    continue;
                }
            }
            if (char.IsHighSurrogate(character)) _high = character;
            else if (!char.IsLowSurrogate(character)) Accept(character);
        }
    }

    /// <summary>
    /// The sequences that put a new terminal in the modes this one is in: the keys it sends, the mouse it
    /// reports, the screen it shows. What is replayed to a terminal that joins late starts from them.
    /// </summary>
    public string Modes()
    {
        var text = new StringBuilder();
        foreach (var mode in _modes)
        {
            // What a program is in the middle of (a screen update it will end) is not carried over.
            if (mode is not (7 or 25 or 2026)) text.Append(CultureInfo.InvariantCulture, $"\u001b[?{mode}h");
        }
        if (!_modes.Contains(7)) text.Append("\u001b[?7l");
        if (!_modes.Contains(25)) text.Append("\u001b[?25l");
        if (_keypad) text.Append("\u001b=");
        return text.ToString();
    }

    private void Accept(int rune)
    {
        switch (_state)
        {
            case State.Ground:
                if (rune < 0x20) Execute(rune);
                else if (rune is >= 0x80 and < 0xA0) ExecuteHigh(rune);
                else if (rune != 0x7F) Print(rune);
                break;
            case State.Escape:
                if (rune < 0x20) ExecuteInside(rune);
                else if (rune == '[') EnterControl();
                else if (rune == ']') EnterString(State.Command);
                else if (rune is 'P' or 'X' or '^' or '_') EnterString(State.Text);
                else if (rune is >= 0x20 and < 0x30)
                {
                    _intermediate = (char)rune;
                    _state = State.EscapeIntermediate;
                }
                else
                {
                    _state = State.Ground;
                    Escape(rune);
                }
                break;
            case State.EscapeIntermediate:
                if (rune < 0x20) ExecuteInside(rune);
                else if (rune >= 0x30)
                {
                    _state = State.Ground;
                    EscapeWith(_intermediate, rune);
                }
                break;
            case State.Control:
                if (rune < 0x20) ExecuteInside(rune);
                else if (rune is >= '0' and <= '9') _current = Math.Min(Math.Max(_current, 0) * 10 + (rune - '0'), 65535);
                else if (rune is ';' or ':')
                {
                    if (_parameters.Count < MaximumParameters) _parameters.Add(_current);
                    _current = -1;
                }
                else if (rune is >= '<' and <= '?')
                {
                    if (_parameters.Count == 0 && _current < 0 && _prefix == '\0') _prefix = (char)rune;
                    else _state = State.ControlIgnore;
                }
                else if (rune is >= 0x20 and < 0x30) _intermediate = (char)rune;
                else if (rune is >= 0x40 and < 0x7F)
                {
                    if (_parameters.Count < MaximumParameters) _parameters.Add(_current);
                    _state = State.Ground;
                    Control((char)rune);
                }
                else if (rune >= 0x80) _state = State.Ground;
                break;
            case State.ControlIgnore:
                if (rune < 0x20) ExecuteInside(rune);
                else if (rune is >= 0x40 and < 0x7F || rune >= 0x80) _state = State.Ground;
                break;
            case State.Command:
                if (rune is 0x07 or 0x9C)
                {
                    _state = State.Ground;
                    Command(_string.ToString());
                }
                else if (rune == 0x1B) _state = State.CommandEscape;
                else if (rune is 0x18 or 0x1A) _state = State.Ground;
                else if (rune >= 0x20 && _string.Length < MaximumString) _string.Append(char.ConvertFromUtf32(rune));
                break;
            case State.CommandEscape:
                _state = State.Ground;
                if (rune == '\\') Command(_string.ToString());
                else
                {
                    // Another sequence starts: this one was not ended, and is dropped.
                    _state = State.Escape;
                    Accept(rune);
                }
                break;
            case State.Text:
                if (rune == 0x1B) _state = State.TextEscape;
                else if (rune is 0x9C or 0x18 or 0x1A) _state = State.Ground;
                break;
            default:
                _state = State.Ground;
                if (rune != '\\')
                {
                    _state = State.Escape;
                    Accept(rune);
                }
                break;
        }
    }

    private void EnterControl()
    {
        _parameters.Clear();
        _current = -1;
        _prefix = '\0';
        _intermediate = '\0';
        _state = State.Control;
    }

    private void EnterString(State state)
    {
        _string.Clear();
        _state = state;
    }

    // A control character inside a sequence acts at once; some of them end the sequence.
    private void ExecuteInside(int rune)
    {
        if (rune == 0x1B) _state = State.Escape;
        else if (rune is 0x18 or 0x1A) _state = State.Ground;
        else Execute(rune);
    }

    private void Execute(int rune)
    {
        var grid = _active;
        switch (rune)
        {
            case 0x07:
                Signal?.Invoke(TerminalSignal.Bell);
                break;
            case 0x08:
                grid.Pending = false;
                if (grid.X > 0) grid.X--;
                break;
            case 0x09:
                if (!grid.Pending) grid.X = NextTab(grid.X);
                break;
            case 0x0A or 0x0B or 0x0C:
                LineFeed();
                break;
            case 0x0D:
                grid.X = 0;
                grid.Pending = false;
                break;
            case 0x0E:
                _shifted = true;
                break;
            case 0x0F:
                _shifted = false;
                break;
            case 0x1B:
                _state = State.Escape;
                break;
        }
    }

    // The control characters that stand for an escape sequence in one character.
    private void ExecuteHigh(int rune)
    {
        switch (rune)
        {
            case 0x84: Index(); break;
            case 0x85: _active.X = 0; LineFeed(); break;
            case 0x88: _tabs[_active.X] = true; break;
            case 0x8D: ReverseIndex(); break;
            case 0x90 or 0x98 or 0x9E or 0x9F: EnterString(State.Text); break;
            case 0x9B: EnterControl(); break;
            case 0x9D: EnterString(State.Command); break;
        }
    }

    private void Print(int rune)
    {
        if ((_shifted ? _graphics1 : _graphics0) && rune is >= 0x5F and <= 0x7E) rune = Graphics[rune - 0x5F];
        var width = rune < 0x300 ? 1 : TerminalWidth.Of(rune);
        var grid = _active;
        if (width == 0)
        {
            Join(grid, rune);
            return;
        }
        if (grid.Pending)
        {
            grid.Pending = false;
            if (_autoWrap) Wrap();
        }
        if (width == 2 && grid.X == _columns - 1)
        {
            // A wide character does not start in the last column: that one stays empty.
            if (_autoWrap)
            {
                Erase(grid.Rows[grid.Y], grid.X, _columns);
                Wrap();
            }
            else grid.X = Math.Max(0, _columns - 2);
        }
        var row = grid.Rows[grid.Y];
        if (_insert) Shift(row, grid.X, width);
        Put(row, grid.X, rune, width);
        _last = rune;
        grid.X += width;
        if (grid.X >= _columns)
        {
            grid.X = _columns - 1;
            grid.Pending = _autoWrap;
        }
    }

    // A character without a width belongs to the character before the cursor.
    private void Join(Grid grid, int rune)
    {
        var column = grid.Pending ? grid.X : grid.X - 1;
        if (column < 0) return;
        var row = grid.Rows[grid.Y];
        if (row.Cells[column].Rune == Trailing && column > 0) column--;
        if (row.Cells[column].Rune <= 0) return;
        row.Joined ??= [];
        row.Joined.TryGetValue(column, out var joined);
        if ((joined?.Length ?? 0) < 16) row.Joined[column] = joined + char.ConvertFromUtf32(rune);
    }

    private void Put(Row row, int column, int rune, int width)
    {
        Erase(row, column, Math.Min(_columns, column + width));
        if (width == 2 && column + 1 >= _columns) return;
        row.Cells[column] = new Cell { Rune = rune, Width = (byte)width };
        if (width == 2) row.Cells[column + 1] = new Cell { Rune = Trailing };
    }

    // The next row, as the continuation of this one.
    private void Wrap()
    {
        var grid = _active;
        grid.X = 0;
        if (grid.Y == grid.Bottom) ScrollUp(1);
        else if (grid.Y < _rows - 1) grid.Y++;
        else return;
        grid.Rows[grid.Y].Continues = true;
    }

    private void LineFeed()
    {
        var grid = _active;
        grid.Pending = false;
        if (grid.Y == grid.Bottom) ScrollUp(1);
        else if (grid.Y < _rows - 1)
        {
            grid.Y++;
            // An explicit new line: the row is not the rest of the row above, whatever was there before.
            grid.Rows[grid.Y].Continues = false;
        }
    }

    private void Index()
    {
        var grid = _active;
        grid.Pending = false;
        if (grid.Y == grid.Bottom) ScrollUp(1);
        else if (grid.Y < _rows - 1) grid.Y++;
    }

    private void ReverseIndex()
    {
        var grid = _active;
        grid.Pending = false;
        if (grid.Y == grid.Top) ScrollDown(1);
        else if (grid.Y > 0) grid.Y--;
    }

    // The rows between the margins move up; the first one leaves the screen, and is kept when it is the first of the screen.
    private void ScrollUp(int count)
    {
        var grid = _active;
        for (; count > 0; count--)
        {
            var first = grid.Rows[grid.Top];
            if (grid == _normal && grid.Top == 0) Keep(first);
            else Lose(first.Marks);
            Array.Copy(grid.Rows, grid.Top + 1, grid.Rows, grid.Top, grid.Bottom - grid.Top);
            grid.Rows[grid.Bottom] = new Row(_columns);
        }
    }

    private void ScrollDown(int count)
    {
        var grid = _active;
        for (; count > 0; count--)
        {
            Lose(grid.Rows[grid.Bottom].Marks);
            Array.Copy(grid.Rows, grid.Top, grid.Rows, grid.Top + 1, grid.Bottom - grid.Top);
            grid.Rows[grid.Top] = new Row(_columns);
        }
    }

    private void Keep(Row row)
    {
        if (_scrollback == 0)
        {
            Lose(row.Marks);
            return;
        }
        if (row.Marks is not null)
        {
            foreach (var mark in row.Marks)
            {
                mark.Column = Text(row, 0, Math.Min(mark.Column, row.Cells.Length)).Length;
                mark.Kept = true;
            }
        }
        if (_keptCount == _kept.Length && _kept.Length < _scrollback)
        {
            // The rows are in order from the start: the array only has to grow.
            var larger = new KeptRow[Math.Min(_scrollback, _kept.Length * 2)];
            for (var index = 0; index < _keptCount; index++) larger[index] = _kept[(_keptStart + index) % _kept.Length];
            _kept = larger;
            _keptStart = 0;
        }
        var kept = new KeptRow(Text(row, 0, row.Used), row.Continues, row.Marks);
        if (_keptCount < _kept.Length)
        {
            _kept[(_keptStart + _keptCount) % _kept.Length] = kept;
            _keptCount++;
            return;
        }
        Lose(_kept[_keptStart].Marks);
        _kept[_keptStart] = kept;
        _keptStart = (_keptStart + 1) % _kept.Length;
    }

    private static void Lose(List<TerminalMark>? marks)
    {
        if (marks is null) return;
        foreach (var mark in marks) mark.Lost = true;
    }

    private KeptRow KeptAt(int index) => _kept[(_keptStart + index) % _kept.Length];

    // Empties the columns of a row; half of a wide character cannot stay alone.
    private void Erase(Row row, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(row.Cells.Length, to);
        if (from >= to) return;
        if (row.Cells[from].Rune == Trailing && from > 0) row.Cells[from - 1] = default;
        if (row.Cells[to - 1].Width == 2 && to < row.Cells.Length) row.Cells[to] = default;
        Array.Clear(row.Cells, from, to - from);
        if (row.Joined is { Count: > 0 } joined)
        {
            for (var column = from; column < to; column++) joined.Remove(column);
        }
    }

    // A row emptied from its first column is no longer the rest of the row above.
    private void EraseFrom(Row row, int from)
    {
        Erase(row, from, _columns);
        if (from == 0) row.Continues = false;
    }

    // Makes room for new columns at a place of a row; what is pushed past the end is gone.
    private void Shift(Row row, int column, int count)
    {
        count = Math.Min(count, _columns - column);
        if (count <= 0) return;
        Erase(row, _columns - count, _columns);
        Array.Copy(row.Cells, column, row.Cells, column + count, _columns - column - count);
        Array.Clear(row.Cells, column, count);
        Move(row, column, count);
        if (row.Cells[column + count].Rune == Trailing) row.Cells[column + count] = default;
    }

    // Removes columns at a place of a row; the rest of the row comes closer.
    private void Delete(Row row, int column, int count)
    {
        count = Math.Min(count, _columns - column);
        if (count <= 0) return;
        Erase(row, column, column + count);
        Array.Copy(row.Cells, column + count, row.Cells, column, _columns - column - count);
        Array.Clear(row.Cells, _columns - count, count);
        Move(row, column + count, -count);
        if (row.Cells[column].Rune == Trailing) row.Cells[column] = default;
    }

    // The joined characters of the columns from one on move with their cells.
    private void Move(Row row, int from, int by)
    {
        if (row.Joined is not { Count: > 0 } joined) return;
        var moved = new Dictionary<int, string>();
        foreach (var (column, text) in joined)
        {
            var target = column >= from ? column + by : column;
            if (target >= 0 && target < _columns) moved[target] = text;
        }
        row.Joined = moved;
    }

    private int NextTab(int column)
    {
        do column++;
        while (column < _columns - 1 && !_tabs[column]);
        return Math.Min(column, _columns - 1);
    }

    private int PreviousTab(int column)
    {
        do column--;
        while (column > 0 && !_tabs[column]);
        return Math.Max(column, 0);
    }

    private static bool[] Tabs(int columns)
    {
        var tabs = new bool[columns];
        for (var column = 8; column < columns; column += 8) tabs[column] = true;
        return tabs;
    }

    private void Escape(int rune)
    {
        var grid = _active;
        switch (rune)
        {
            case '7': Save(); break;
            case '8': Restore(); break;
            case 'D': Index(); break;
            case 'E': grid.X = 0; LineFeed(); break;
            case 'H': _tabs[grid.X] = true; break;
            case 'M': ReverseIndex(); break;
            case 'Z': Reply?.Invoke("\u001b[?1;2c"); break;
            case 'c': Reset(); break;
            case '=': _keypad = true; break;
            case '>': _keypad = false; break;
        }
    }

    private void EscapeWith(char intermediate, int rune)
    {
        // The character set of each of the two slots: line drawing, or anything else taken as text.
        if (intermediate == '(') _graphics0 = rune == '0';
        else if (intermediate == ')') _graphics1 = rune == '0';
    }

    private void Save()
    {
        var grid = _active;
        (grid.SavedX, grid.SavedY, grid.SavedOrigin) = (grid.X, grid.Y, grid.Origin);
    }

    private void Restore()
    {
        var grid = _active;
        (grid.X, grid.Y, grid.Origin) = (Math.Min(grid.SavedX, _columns - 1), Math.Min(grid.SavedY, _rows - 1), grid.SavedOrigin);
        grid.Pending = false;
    }

    // The terminal as it was when it started, with what left the screen still kept.
    private void Reset()
    {
        _active = _normal;
        foreach (var grid in (Grid[])[_normal, _alternate])
        {
            foreach (var row in grid.Rows) Lose(row.Marks);
            grid.Rows = Grid.NewRows(_columns, _rows);
            (grid.X, grid.Y, grid.Top, grid.Bottom, grid.Pending, grid.Origin) = (0, 0, 0, _rows - 1, false, false);
            (grid.SavedX, grid.SavedY, grid.SavedOrigin) = (0, 0, false);
        }
        _modes.Clear();
        _modes.Add(7);
        _modes.Add(25);
        _autoWrap = true;
        _insert = _keypad = _graphics0 = _graphics1 = _shifted = false;
        _tabs = Tabs(_columns);
    }

    private int Count(int index) => index < _parameters.Count && _parameters[index] > 0 ? _parameters[index] : 1;

    private int Choice(int index) => index < _parameters.Count && _parameters[index] > 0 ? _parameters[index] : 0;

    private void Control(char final)
    {
        var grid = _active;
        if (_prefix == '?')
        {
            if (final is 'h' or 'l')
            {
                foreach (var mode in _parameters) SetMode(mode, final == 'h');
            }
            else if (final == 'p' && _intermediate == '$')
            {
                var mode = Choice(0);
                Reply?.Invoke(string.Create(CultureInfo.InvariantCulture, $"\u001b[?{mode};{(_modes.Contains(mode) ? 1 : 2)}$y"));
            }
            else if (final == 'n' && Choice(0) == 6) ReportCursor(extended: true);
            else if (final is 'J' or 'K') Clear(final);
            return;
        }
        if (_prefix == '>')
        {
            if (final == 'c' && Choice(0) == 0) Reply?.Invoke("\u001b[>0;276;0c");
            return;
        }
        if (_prefix != '\0') return;
        if (_intermediate != '\0')
        {
            if (final == 'p' && _intermediate == '!') SoftReset();
            return;
        }
        switch (final)
        {
            case '@':
                Shift(grid.Rows[grid.Y], grid.X, Count(0));
                grid.Pending = false;
                break;
            case 'A':
                MoveTo(grid.X, Math.Max(grid.Y - Count(0), grid.Y >= grid.Top ? grid.Top : 0));
                break;
            case 'B' or 'e':
                MoveTo(grid.X, Math.Min(grid.Y + Count(0), grid.Y <= grid.Bottom ? grid.Bottom : _rows - 1));
                break;
            case 'C' or 'a':
                MoveTo(grid.X + Count(0), grid.Y);
                break;
            case 'D':
                MoveTo(grid.X - Count(0), grid.Y);
                break;
            case 'E':
                MoveTo(0, Math.Min(grid.Y + Count(0), grid.Y <= grid.Bottom ? grid.Bottom : _rows - 1));
                break;
            case 'F':
                MoveTo(0, Math.Max(grid.Y - Count(0), grid.Y >= grid.Top ? grid.Top : 0));
                break;
            case 'G' or '`':
                MoveTo(Count(0) - 1, grid.Y);
                break;
            case 'H' or 'f':
                Place(Count(0) - 1, Count(1) - 1);
                break;
            case 'I':
                for (var count = Count(0); count > 0; count--) grid.X = NextTab(grid.X);
                grid.Pending = false;
                break;
            case 'J' or 'K':
                Clear(final);
                break;
            case 'L':
                if (grid.Y >= grid.Top && grid.Y <= grid.Bottom) InsertRows(Count(0));
                break;
            case 'M':
                if (grid.Y >= grid.Top && grid.Y <= grid.Bottom) DeleteRows(Count(0));
                break;
            case 'P':
                Delete(grid.Rows[grid.Y], grid.X, Count(0));
                grid.Pending = false;
                break;
            case 'S':
                ScrollUpInside(Count(0));
                break;
            case 'T':
                ScrollDown(Math.Min(Count(0), _rows));
                break;
            case 'X':
                Erase(grid.Rows[grid.Y], grid.X, grid.X + Count(0));
                grid.Pending = false;
                break;
            case 'Z':
                for (var count = Count(0); count > 0; count--) grid.X = PreviousTab(grid.X);
                grid.Pending = false;
                break;
            case 'b':
                if (_last != 0)
                {
                    for (var count = Math.Min(Count(0), _columns * _rows); count > 0; count--) Print(_last);
                }
                break;
            case 'c':
                if (Choice(0) == 0) Reply?.Invoke("\u001b[?1;2c");
                break;
            case 'd':
                Place(Count(0) - 1, grid.X, keepColumn: true);
                break;
            case 'g':
                if (Choice(0) == 0) _tabs[grid.X] = false;
                else if (Choice(0) == 3) Array.Clear(_tabs);
                break;
            case 'h' or 'l':
                foreach (var mode in _parameters)
                {
                    if (mode == 4) _insert = final == 'h';
                }
                break;
            case 'n':
                if (Choice(0) == 5) Reply?.Invoke("\u001b[0n");
                else if (Choice(0) == 6) ReportCursor(extended: false);
                break;
            case 'r':
                SetMargins(Count(0) - 1, _parameters.Count > 1 && _parameters[1] > 0 ? _parameters[1] - 1 : _rows - 1);
                break;
            case 's':
                Save();
                break;
            case 'u':
                Restore();
                break;
        }
    }

    private void ReportCursor(bool extended)
    {
        var grid = _active;
        var row = grid.Y + 1 - (grid.Origin ? grid.Top : 0);
        Reply?.Invoke(string.Create(CultureInfo.InvariantCulture, $"\u001b[{(extended ? "?" : string.Empty)}{row};{grid.X + 1}R"));
    }

    private void MoveTo(int column, int row)
    {
        var grid = _active;
        grid.X = Math.Clamp(column, 0, _columns - 1);
        grid.Y = Math.Clamp(row, 0, _rows - 1);
        grid.Pending = false;
    }

    // A position given by the program: counted from the top margin when the program asked for that.
    private void Place(int row, int column, bool keepColumn = false)
    {
        var grid = _active;
        if (grid.Origin) row = Math.Min(row + grid.Top, grid.Bottom);
        MoveTo(keepColumn ? grid.X : column, row);
    }

    private void SetMargins(int top, int bottom)
    {
        var grid = _active;
        bottom = Math.Min(bottom, _rows - 1);
        if (top >= bottom) return;
        (grid.Top, grid.Bottom) = (top, bottom);
        Place(0, 0);
    }

    private void Clear(char final)
    {
        var grid = _active;
        var choice = Choice(0);
        var row = grid.Rows[grid.Y];
        grid.Pending = false;
        if (final == 'K')
        {
            if (choice == 0) EraseFrom(row, grid.X);
            else if (choice == 1) Erase(row, 0, grid.X + 1);
            else if (choice == 2) EraseFrom(row, 0);
            return;
        }
        switch (choice)
        {
            case 0:
                EraseFrom(row, grid.X);
                for (var index = grid.Y + 1; index < _rows; index++) EraseFrom(grid.Rows[index], 0);
                break;
            case 1:
                Erase(row, 0, grid.X + 1);
                // The row under a row that was emptied whole is not the rest of it.
                if (grid.X + 1 >= _columns && grid.Y + 1 < _rows) grid.Rows[grid.Y + 1].Continues = false;
                for (var index = 0; index < grid.Y; index++) EraseFrom(grid.Rows[index], 0);
                break;
            case 2:
                foreach (var each in grid.Rows) EraseFrom(each, 0);
                break;
            case 3:
                for (var index = 0; index < _keptCount; index++) Lose(KeptAt(index).Marks);
                Array.Clear(_kept);
                _keptStart = _keptCount = 0;
                break;
        }
    }

    private void InsertRows(int count)
    {
        var grid = _active;
        for (count = Math.Min(count, grid.Bottom - grid.Y + 1); count > 0; count--)
        {
            Lose(grid.Rows[grid.Bottom].Marks);
            Array.Copy(grid.Rows, grid.Y, grid.Rows, grid.Y + 1, grid.Bottom - grid.Y);
            grid.Rows[grid.Y] = new Row(_columns);
        }
        grid.X = 0;
        grid.Pending = false;
    }

    private void DeleteRows(int count)
    {
        var grid = _active;
        for (count = Math.Min(count, grid.Bottom - grid.Y + 1); count > 0; count--)
        {
            Lose(grid.Rows[grid.Y].Marks);
            Array.Copy(grid.Rows, grid.Y + 1, grid.Rows, grid.Y, grid.Bottom - grid.Y);
            grid.Rows[grid.Bottom] = new Row(_columns);
        }
        grid.X = 0;
        grid.Pending = false;
    }

    // A program that scrolls the rows itself throws them away: they are not what a reader scrolls back to.
    private void ScrollUpInside(int count)
    {
        var grid = _active;
        for (count = Math.Min(count, _rows); count > 0; count--)
        {
            Lose(grid.Rows[grid.Top].Marks);
            Array.Copy(grid.Rows, grid.Top + 1, grid.Rows, grid.Top, grid.Bottom - grid.Top);
            grid.Rows[grid.Bottom] = new Row(_columns);
        }
    }

    private void SoftReset()
    {
        var grid = _active;
        (grid.Top, grid.Bottom, grid.Origin, grid.Pending) = (0, _rows - 1, false, false);
        (grid.SavedX, grid.SavedY, grid.SavedOrigin) = (0, 0, false);
        _autoWrap = true;
        _insert = _graphics0 = _graphics1 = _shifted = false;
        _modes.Add(7);
        _modes.Add(25);
        _modes.Remove(6);
    }

    private void SetMode(int mode, bool on)
    {
        if (mode <= 0) return;
        switch (mode)
        {
            case 6:
                _active.Origin = on;
                Place(0, 0);
                break;
            case 7:
                _autoWrap = on;
                break;
            case 47 or 1047:
                UseAlternate(on);
                break;
            case 1048:
                if (on) Save();
                else Restore();
                return;
            case 1049:
                if (on) Save();
                UseAlternate(on);
                if (!on) Restore();
                break;
        }
        // One name for the screen of full-screen programs, whichever way it was asked for.
        if (mode is 47 or 1047) mode = 1049;
        if (on) _modes.Add(mode);
        else _modes.Remove(mode);
    }

    private void UseAlternate(bool on)
    {
        if (on == Alternate) return;
        if (on)
        {
            foreach (var row in _alternate.Rows) Lose(row.Marks);
            _alternate.Rows = Grid.NewRows(_columns, _rows);
            (_alternate.X, _alternate.Y, _alternate.Pending) = (_normal.X, _normal.Y, false);
            (_alternate.Top, _alternate.Bottom, _alternate.Origin) = (0, _rows - 1, false);
            _active = _alternate;
            return;
        }
        (_normal.X, _normal.Y, _normal.Pending) = (_alternate.X, _alternate.Y, false);
        _active = _normal;
    }

    private void Command(string text)
    {
        var separator = text.IndexOf(';');
        var number = separator < 0 ? text : text[..separator];
        var value = separator < 0 ? string.Empty : text[(separator + 1)..];
        switch (number)
        {
            case "0" or "2":
                if (!string.Equals(Title, value, StringComparison.Ordinal))
                {
                    Title = value.Length == 0 ? null : value;
                    Signal?.Invoke(TerminalSignal.Title);
                }
                break;
            case "7":
                SetFolder(TerminalIntegration.FolderOfUrl(value));
                break;
            case "9":
                if (value.StartsWith("9;", StringComparison.Ordinal)) SetFolder(value[2..].Trim('"'));
                break;
            case "133":
                Integration(value, extended: false);
                break;
            case "633":
                Integration(value, extended: true);
                break;
            case "1337":
                if (value.StartsWith("CurrentDir=", StringComparison.Ordinal)) SetFolder(value["CurrentDir=".Length..]);
                break;
        }
    }

    private void SetFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.Equals(Folder, folder, StringComparison.Ordinal)) return;
        Folder = folder;
        Signal?.Invoke(TerminalSignal.Folder);
    }

    // What a shell integration script reports: A the prompt starts, B the command line starts, C the command
    // runs, D it ended (with its exit code); E gives the command line and P a property, in the extended form.
    private void Integration(string value, bool extended)
    {
        var parts = value.Split(';');
        switch (parts[0])
        {
            case "A":
                Integrated = true;
                // The sequences of the scripts report commands; a prompt that carries the older marks may report nothing else.
                Tracks |= extended;
                // A prompt after a command that reported no end (an older script, an interrupted line) ends it.
                Finish(null);
                Prompts++;
                AtPrompt = true;
                Signal?.Invoke(TerminalSignal.Prompt);
                break;
            case "B":
                Integrated = true;
                Finish(null);
                _command = new TerminalCommand { Number = ++_commandNumber, Folder = Folder, Input = Mark() };
                break;
            case "C":
                Integrated = Tracks = true;
                // Said twice by a shell that has two scripts (its own and the one of its prompt): it started once.
                if (_command is { Finished: false, Output: not null }) break;
                if (_command is null or { Output: not null }) _command = new TerminalCommand { Number = ++_commandNumber, Folder = Folder };
                _command.Output = Mark();
                AtPrompt = false;
                _commands.Add(_command);
                if (_commands.Count > MaximumCommands) _commands.RemoveAt(0);
                Signal?.Invoke(TerminalSignal.CommandStarted);
                break;
            case "D":
                Integrated = true;
                Finish(parts.Length > 1 && int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) ? code : null);
                break;
            case "E" when extended && parts.Length > 1:
                // The line of the command that is about to run, or of the one whose line was not marked.
                if (_command is null or { Output: not null }) _command = new TerminalCommand { Number = ++_commandNumber, Folder = Folder };
                _command.CommandLine = TerminalIntegration.Unescape(parts[1]);
                break;
            case "P" when extended && value.StartsWith("P;Cwd=", StringComparison.Ordinal):
                SetFolder(TerminalIntegration.Unescape(value["P;Cwd=".Length..]));
                break;
        }
    }

    private void Finish(int? exitCode)
    {
        if (_command is not { Finished: false } command) return;
        if (command.Output is null)
        {
            // A line that was never run: an empty one, or one that was abandoned.
            _command = null;
            return;
        }
        command.End = Mark();
        command.ExitCode = exitCode;
        command.Finished = true;
        command.CommandLine ??= Between(command.Input, command.Output)?.Trim();
        Signal?.Invoke(TerminalSignal.CommandFinished);
    }

    // A mark where the cursor is, on the screen that keeps what leaves it.
    private TerminalMark? Mark()
    {
        if (_active != _normal) return null;
        var mark = new TerminalMark { Column = _normal.X + (_normal.Pending ? 1 : 0) };
        (_normal.Rows[_normal.Y].Marks ??= []).Add(mark);
        return mark;
    }

    // The text of some columns of a row: an empty cell inside it is a space.
    private static string Text(Row row, int from, int to)
    {
        to = Math.Min(to, row.Cells.Length);
        if (from >= to) return string.Empty;
        var text = new StringBuilder(to - from);
        for (var column = from; column < to; column++)
        {
            var rune = row.Cells[column].Rune;
            if (rune == Trailing) continue;
            if (rune == 0) text.Append(' ');
            else if (rune < 0x10000) text.Append((char)rune);
            else text.Append(char.ConvertFromUtf32(rune));
            if (row.Joined is not null && row.Joined.TryGetValue(column, out var joined)) text.Append(joined);
        }
        return text.ToString();
    }
}
