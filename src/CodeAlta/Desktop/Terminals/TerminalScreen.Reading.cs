using System.Text;

namespace CodeAlta.Desktop.Terminals;

// Reading the text of a terminal, and giving it another size.
internal sealed partial class TerminalScreen
{
    /// <summary>The rows of the screen, from the top, each without the spaces at its end.</summary>
    public string[] ScreenRows()
    {
        var rows = new string[_rows];
        for (var index = 0; index < _rows; index++) rows[index] = Text(_active.Rows[index], 0, _active.Rows[index].Used).TrimEnd();
        return rows;
    }

    /// <summary>
    /// The last lines the terminal showed: the rows that left the screen, then those of the screen down to the
    /// last one written or the cursor. A line too long for one row is one line here.
    /// </summary>
    /// <param name="maximum">The most lines to return.</param>
    public List<string> Lines(int maximum)
    {
        var lines = new List<string>();
        var parts = new List<string>();
        for (var index = _keptCount + Extent() - 1; index >= 0 && lines.Count < maximum; index--)
        {
            var (text, continues) = RowAt(index);
            parts.Add(text);
            if (continues && index > 0) continue;
            parts.Reverse();
            lines.Add(string.Concat(parts).TrimEnd());
            parts.Clear();
        }
        lines.Reverse();
        return lines;
    }

    /// <summary>What a command printed, as far as the terminal still has it.</summary>
    /// <param name="command">One of <see cref="Commands"/>.</param>
    /// <param name="truncated">Whether the start of it scrolled past what the terminal keeps.</param>
    public string Output(TerminalCommand command, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(command);
        truncated = command.Output is null or { Lost: true };
        var from = command.Output is { Lost: false } start ? Locate(start) : null;
        var to = command.End is { Lost: false } end ? Locate(end) : null;
        if (command.Finished && to is null) return string.Empty;
        return Between(from ?? (0, 0), to ?? (_keptCount + Extent() - 1, int.MaxValue)).TrimEnd();
    }

    /// <summary>Marks where the cursor is, to read later what was written from there on: see <see cref="Since"/>.</summary>
    /// <returns>The place, or null while a program that draws the whole screen has its own screen up.</returns>
    public TerminalMark? Place() => Mark();

    /// <summary>
    /// What the terminal showed from a place on, and forgets the place. Without a place, or once its row is
    /// gone, the last lines of the terminal instead.
    /// </summary>
    /// <param name="place">What <see cref="Place"/> returned.</param>
    /// <param name="maximum">The most lines to return: the last ones.</param>
    /// <param name="truncated">Whether the text does not start at the place.</param>
    public string Since(TerminalMark? place, int maximum, out bool truncated)
    {
        var from = place is { Lost: false } ? Locate(place) : null;
        if (place is not null) Release(place);
        truncated = from is null;
        if (from is not { } start) return string.Join('\n', Lines(maximum)).TrimEnd();
        var lines = Between(start, (_keptCount + Extent() - 1, int.MaxValue)).TrimEnd().Split('\n');
        if (lines.Length <= maximum) return string.Join('\n', lines);
        truncated = true;
        return string.Join('\n', lines[^Math.Max(0, maximum)..]);
    }

    /// <summary>Forgets a place nobody reads from any more: it leaves its row.</summary>
    /// <param name="place">What <see cref="Place"/> returned.</param>
    public void Release(TerminalMark place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (!place.Lost)
        {
            for (var index = place.Kept ? _keptCount - 1 : -1; index >= 0; index--)
            {
                if (KeptAt(index).Marks is { } marks && marks.Remove(place)) break;
            }
            foreach (var row in _normal.Rows)
            {
                if (row.Marks is { } marks && marks.Remove(place)) break;
            }
        }
        place.Lost = true;
    }

    private string? Between(TerminalMark? from, TerminalMark? to)
    {
        if (from is null or { Lost: true } || to is null or { Lost: true }) return null;
        return Locate(from) is { } start && Locate(to) is { } end ? Between(start, end) : null;
    }

    // The text from a place in a row to a place in a later row; rows of one line are not separated.
    private string Between((int Row, int Offset) from, (int Row, int Offset) to)
    {
        var text = new StringBuilder();
        var last = Math.Min(to.Row, _keptCount + _rows - 1);
        for (var index = Math.Max(0, from.Row); index <= last; index++)
        {
            var row = RowAt(index).Text;
            var start = index == from.Row ? Math.Min(from.Offset, row.Length) : 0;
            var end = index == to.Row ? Math.Min(to.Offset, row.Length) : row.Length;
            if (end > start) text.Append(row, start, end - start);
            if (index < last && !RowAt(index + 1).Continues) text.Append('\n');
        }
        return text.ToString();
    }

    // Where a mark is now: its row among the kept rows followed by those of the screen, and the offset in its text.
    private (int Row, int Offset)? Locate(TerminalMark mark)
    {
        if (mark.Kept)
        {
            for (var index = _keptCount - 1; index >= 0; index--)
            {
                if (KeptAt(index).Marks is { } marks && marks.Contains(mark)) return (index, mark.Column);
            }
            return null;
        }
        for (var index = 0; index < _rows; index++)
        {
            var row = _normal.Rows[index];
            if (row.Marks is { } marks && marks.Contains(mark)) return (_keptCount + index, Text(row, 0, Math.Min(mark.Column, row.Cells.Length)).Length);
        }
        return null;
    }

    // A row of the kept rows followed by those of the screen that scrolls into them.
    private (string Text, bool Continues) RowAt(int index)
    {
        if (index < _keptCount)
        {
            var kept = KeptAt(index);
            return (kept.Text, kept.Continues);
        }
        var row = _normal.Rows[index - _keptCount];
        return (Text(row, 0, row.Used), row.Continues);
    }

    // The rows of the screen that hold something: down to the last one written or the cursor.
    private int Extent()
    {
        var last = Math.Min(_normal.Y, _normal.Rows.Length - 1);
        for (var index = _normal.Rows.Length - 1; index > last; index--)
        {
            if (_normal.Rows[index].Used > 0) last = index;
        }
        return last + 1;
    }

    /// <summary>Gives the screen another size, as the terminal of the window does with its own.</summary>
    public void Resize(int columns, int rows)
    {
        columns = PseudoTerminal.Clamp(columns);
        rows = PseudoTerminal.Clamp(rows);
        if (columns == _columns && rows == _rows) return;
        // A program that draws the whole screen draws it again: its screen is only cut or extended.
        Fit(_alternate, columns, rows);
        if (rows != _rows) FitHeight(rows);
        if (columns != _columns)
        {
            if (_reflow) Reflow(columns);
            else Fit(_normal, columns, rows);
        }
        (_columns, _rows) = (columns, rows);
        _tabs = Tabs(columns);
        foreach (var grid in (Grid[])[_normal, _alternate])
        {
            (grid.Top, grid.Bottom, grid.Pending) = (0, rows - 1, false);
            (grid.X, grid.Y) = (Math.Min(grid.X, columns - 1), Math.Min(grid.Y, rows - 1));
            (grid.SavedX, grid.SavedY) = (Math.Min(grid.SavedX, columns - 1), Math.Min(grid.SavedY, rows - 1));
        }
    }

    // Cuts or extends a screen without moving its text; the row of the cursor stays on it.
    private void Fit(Grid grid, int columns, int rows)
    {
        var source = grid.Rows;
        var skipped = Math.Max(0, Math.Min(grid.Y, source.Length - 1) - rows + 1);
        var fitted = new Row[rows];
        for (var index = 0; index < rows; index++)
        {
            fitted[index] = index + skipped < source.Length ? Fit(source[index + skipped], columns) : new Row(columns);
        }
        for (var index = 0; index < source.Length; index++)
        {
            if (index < skipped || index >= skipped + rows) Lose(source[index].Marks);
        }
        grid.Rows = fitted;
        grid.Y = Math.Max(0, grid.Y - skipped);
    }

    private static Row Fit(Row row, int columns)
    {
        if (row.Cells.Length == columns) return row;
        var kept = Math.Min(row.Cells.Length, columns);
        var cells = new Cell[columns];
        Array.Copy(row.Cells, cells, kept);
        // Half of a wide character does not stay at the cut.
        if (kept > 0 && cells[kept - 1].Width == 2) cells[kept - 1] = default;
        row.Cells = cells;
        if (row.Joined is { } joined)
        {
            foreach (var column in joined.Keys.Where(column => column >= columns).ToArray()) joined.Remove(column);
        }
        return row;
    }

    // The height of the screen that keeps what leaves it. Shorter: the rows under the cursor go first, then
    // those at the top leave the screen. Taller: rows come back from those that left, unless the program is
    // behind a pseudoconsole, which draws its own view of the screen under what is there.
    private void FitHeight(int rows)
    {
        var grid = _normal;
        var list = new List<Row>(grid.Rows);
        while (list.Count > rows)
        {
            if (list.Count - 1 > grid.Y)
            {
                Lose(list[^1].Marks);
                list.RemoveAt(list.Count - 1);
                continue;
            }
            Keep(list[0]);
            list.RemoveAt(0);
            grid.Y = Math.Max(0, grid.Y - 1);
        }
        while (list.Count < rows)
        {
            if (!_console && _keptCount > 0 && grid.Y == list.Count - 1)
            {
                list.Insert(0, Fit(Restore(TakeKept(), _columns), _columns));
                grid.Y++;
            }
            else list.Add(new Row(_columns));
        }
        grid.Rows = [.. list];
    }

    private KeptRow TakeKept()
    {
        var index = (_keptStart + _keptCount - 1) % _kept.Length;
        var kept = _kept[index];
        _kept[index] = default;
        _keptCount--;
        return kept;
    }

    // A row that left the screen, back as a row of cells: as wide as the screen, or wider when it left a wider screen.
    private static Row Restore(KeptRow kept, int columns)
    {
        columns = Math.Max(columns, kept.Text.Length * 2);
        var row = new Row(columns) { Continues = kept.Continues, Marks = kept.Marks };
        var column = 0;
        var offset = 0;
        var places = new Dictionary<int, int>();
        foreach (var rune in kept.Text.EnumerateRunes())
        {
            places[offset] = column;
            offset += rune.Utf16SequenceLength;
            var width = rune.Value < 0x300 ? 1 : TerminalWidth.Of(rune.Value);
            if (width == 0)
            {
                if (column == 0) continue;
                var target = row.Cells[column - 1].Rune == Trailing ? column - 2 : column - 1;
                row.Joined ??= [];
                row.Joined[target] = (row.Joined.TryGetValue(target, out var joined) ? joined : null) + rune;
                continue;
            }
            if (column + width > columns) break;
            row.Cells[column] = new Cell { Rune = rune.Value, Width = (byte)width };
            if (width == 2) row.Cells[column + 1] = new Cell { Rune = Trailing };
            column += width;
        }
        if (kept.Marks is not null)
        {
            foreach (var mark in kept.Marks)
            {
                mark.Column = places.TryGetValue(mark.Column, out var place) ? place : column;
                mark.Kept = false;
            }
        }
        return row;
    }

    // The width of the screen that keeps what leaves it: each line is cut into rows of the new width. The rows
    // above what then fits the screen leave it, and the cursor stays on the character it was on.
    private void Reflow(int columns)
    {
        var grid = _normal;
        var height = grid.Rows.Length;
        var source = new List<Row>(grid.Rows.Take(Math.Max(Extent(), Math.Min(grid.Y, height - 1) + 1)));
        var cursorRow = Math.Min(grid.Y, height - 1);
        // The first row of the screen can be the rest of a row that left it: that row comes back to be cut again.
        while (source[0].Continues && _keptCount > 0)
        {
            source.Insert(0, Restore(TakeKept(), _columns));
            cursorRow++;
        }
        var cursorColumn = grid.X + (grid.Pending ? 1 : 0);
        var result = new List<Row>();
        (int Row, int Column) cursor = (0, 0);
        for (var first = 0; first < source.Count;)
        {
            var last = first;
            while (last + 1 < source.Count && source[last + 1].Continues) last++;
            // The cells of the line, with what is attached to them: joined characters, marks, the cursor.
            var cells = new List<Cell>();
            var joined = new Dictionary<int, string>();
            var marks = new List<(int Offset, TerminalMark Mark)>();
            var cursorOffset = -1;
            for (var index = first; index <= last; index++)
            {
                var row = source[index];
                var start = cells.Count;
                var used = row.Used;
                if (index == cursorRow) cursorOffset = start + (index == last ? cursorColumn : Math.Min(cursorColumn, used));
                if (row.Marks is not null)
                {
                    foreach (var mark in row.Marks) marks.Add((start + (index == last ? mark.Column : Math.Min(mark.Column, used)), mark));
                }
                for (var column = 0; column < used; column++)
                {
                    if (row.Joined is not null && row.Joined.TryGetValue(column, out var text)) joined[start + column] = text;
                    cells.Add(row.Cells[column]);
                }
            }
            var length = Math.Max(cells.Count, Math.Max(cursorOffset, marks.Count == 0 ? 0 : marks.Max(static mark => mark.Offset)));
            while (cells.Count < length) cells.Add(default);

            var current = new Row(columns) { Continues = source[first].Continues };
            var place = 0;
            var places = new (int Row, int Column)[cells.Count + 1];
            for (var offset = 0; offset < cells.Count; offset++)
            {
                var cell = cells[offset];
                if (cell.Rune == Trailing)
                {
                    places[offset] = (result.Count, Math.Max(0, place - 1));
                    continue;
                }
                var width = cell.Width == 2 ? 2 : 1;
                if (place + width > columns)
                {
                    result.Add(current);
                    current = new Row(columns) { Continues = true };
                    place = 0;
                }
                places[offset] = (result.Count, place);
                current.Cells[place] = cell;
                if (width == 2 && place + 1 < columns) current.Cells[place + 1] = new Cell { Rune = Trailing };
                if (joined.TryGetValue(offset, out var text)) (current.Joined ??= [])[place] = text;
                place += width;
            }
            places[cells.Count] = (result.Count, place);
            result.Add(current);
            if (cursorOffset >= 0) cursor = places[Math.Min(cursorOffset, cells.Count)];
            foreach (var (offset, mark) in marks)
            {
                var (row, column) = places[Math.Min(offset, cells.Count)];
                mark.Column = column;
                (result[row].Marks ??= []).Add(mark);
            }
            first = last + 1;
        }

        // What does not fit the screen leaves it by the top; the row of the cursor always stays.
        var skipped = Math.Max(0, Math.Min(result.Count - height, cursor.Row));
        for (var index = 0; index < skipped; index++) Keep(result[index]);
        var rows = new Row[height];
        for (var index = 0; index < height; index++)
        {
            rows[index] = index + skipped < result.Count ? result[index + skipped] : new Row(columns);
        }
        for (var index = skipped + height; index < result.Count; index++) Lose(result[index].Marks);
        grid.Rows = rows;
        grid.Y = cursor.Row - skipped;
        grid.X = Math.Min(cursor.Column, columns - 1);
        grid.Pending = false;
    }
}
