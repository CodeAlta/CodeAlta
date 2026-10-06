using System.Text;
using System.Threading.Channels;

namespace CodeAlta.Desktop.Terminals;

/// <summary>What a terminal is created with.</summary>
/// <param name="Id">Its identifier.</param>
/// <param name="ProjectId">The project it is listed under, or null.</param>
/// <param name="SessionId">The session it was opened from, or null.</param>
/// <param name="Folder">The folder its program starts in.</param>
/// <param name="Profile">What it starts.</param>
/// <param name="Title">The title its creator gave it, or null.</param>
/// <param name="Keep">Whether it stays once its program has ended, until it is closed: its text can still be read.</param>
/// <param name="Agent">Whether a session created it, rather than the user.</param>
/// <param name="Columns">The width of its screen.</param>
/// <param name="Rows">The height of its screen.</param>
internal sealed record TerminalDescription(string Id, string? ProjectId, string? SessionId, string Folder, TerminalProfile Profile, string? Title, bool Keep, bool Agent, int Columns, int Rows);

/// <summary>What is known of a terminal at one moment.</summary>
/// <param name="Id">Its identifier.</param>
/// <param name="ProjectId">The project it is listed under, or null.</param>
/// <param name="SessionId">The session it was opened from, or null.</param>
/// <param name="Title">Its title: the one it was given, or the folder it is in.</param>
/// <param name="Titled">Whether the title was given to it.</param>
/// <param name="Folder">The folder its shell is in, as far as the shell said, or the one it started in.</param>
/// <param name="Profile">The identifier of what it runs.</param>
/// <param name="ProfileName">The name of what it runs.</param>
/// <param name="ProgramTitle">The title its program gave it, or null.</param>
/// <param name="ProcessId">The process of its program.</param>
/// <param name="Running">Whether its program is running.</param>
/// <param name="ExitCode">The exit code of its program once it has ended.</param>
/// <param name="Integrated">Whether its shell reports its prompts and its commands.</param>
/// <param name="Busy">Whether its shell runs a command, as far as it reports it.</param>
/// <param name="Command">The command line that runs, when the shell reported it.</param>
/// <param name="LastExitCode">The exit code of the last command that ended, when the shell reported one.</param>
/// <param name="Columns">The width of its screen.</param>
/// <param name="Rows">The height of its screen.</param>
/// <param name="Created">When it was created.</param>
/// <param name="Open">Whether a window shows it in a tab.</param>
/// <param name="Visible">Whether that tab is the one shown.</param>
/// <param name="Attention">Whether its program rang the bell since it was last looked at.</param>
/// <param name="Agent">Whether a session created it.</param>
internal sealed record TerminalInfo(
    string Id, string? ProjectId, string? SessionId, string Title, bool Titled, string Folder, string Profile, string ProfileName, string? ProgramTitle,
    int ProcessId, bool Running, int? ExitCode, bool Integrated, bool Busy, string? Command, int? LastExitCode,
    int Columns, int Rows, DateTimeOffset Created, bool Open, bool Visible, bool Attention, bool Agent);

/// <summary>A command a terminal ran, with what it printed.</summary>
/// <param name="Number">Its rank among the commands the terminal saw.</param>
/// <param name="CommandLine">The command line, when the shell reported it.</param>
/// <param name="Folder">The folder the shell was in.</param>
/// <param name="Finished">Whether it has ended.</param>
/// <param name="ExitCode">Its exit code, when the shell reported one.</param>
/// <param name="Output">What it printed, as far as the terminal still has it.</param>
/// <param name="Truncated">Whether the start of the output is gone.</param>
internal sealed record TerminalCommandText(long Number, string? CommandLine, string? Folder, bool Finished, int? ExitCode, string Output, bool Truncated);

/// <summary>What came of typing in a terminal.</summary>
/// <param name="Sent">Whether the program was given the text: false when it has ended, or when too much already waits for it.</param>
/// <param name="Settled">Whether the terminal came to rest; false when it was not waited for, or when the time ran out.</param>
/// <param name="Command">The command the shell ran, with what it printed, when the shell reported it.</param>
/// <param name="Text">What the terminal showed from where the text was typed, when it was waited for and no command was reported.</param>
/// <param name="Truncated">Whether the start of <paramref name="Text"/> is gone.</param>
internal sealed record TerminalTyped(bool Sent, bool Settled, TerminalCommandText? Command, string? Text, bool Truncated);

/// <summary>The screen of a terminal as text.</summary>
/// <param name="Rows">Its rows, from the top.</param>
/// <param name="CursorRow">The row of the cursor, from 0.</param>
/// <param name="CursorColumn">The column of the cursor, from 0.</param>
/// <param name="Alternate">Whether a program that draws the whole screen has its own screen up.</param>
internal sealed record TerminalScreenText(IReadOnlyList<string> Rows, int CursorRow, int CursorColumn, bool Alternate);

/// <summary>
/// One terminal of the application: a program behind a pseudo-terminal, the text it shows, and what it wrote
/// last for the windows that show it. It lives until it is closed, with or without a tab that shows it.
/// </summary>
internal sealed class DesktopTerminal : IDisposable
{
    /// <summary>How many bytes of typed text can wait for a program that does not read its keyboard.</summary>
    internal const int MaximumPendingInput = 8 * 1024 * 1024;
    /// <summary>The longest title a terminal is given.</summary>
    internal const int MaximumTitleLength = 256;

    private readonly Lock _gate = new();
    private readonly TerminalDescription _description;
    private readonly PseudoTerminal _program;
    private readonly TerminalScreen _screen;
    private readonly TerminalHistory _history;
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<DesktopTerminal, bool> _changed;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _created;
    private readonly List<string> _answers = [];
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? _title;
    private int _columns, _rows;
    private int _views, _visible;
    private long _pendingInput;
    private long _lastOutput;
    private bool _listed;
    private bool _running = true;
    private bool _attention;
    private bool _closing;
    private int? _exitCode;

    /// <summary>Creates a terminal around a program that was just started.</summary>
    /// <param name="description">What it was created with.</param>
    /// <param name="program">Its program.</param>
    /// <param name="changed">Told when the terminal has something new: text only (false), or something its list shows (true).</param>
    /// <param name="time">The clock.</param>
    internal DesktopTerminal(TerminalDescription description, PseudoTerminal program, Action<DesktopTerminal, bool> changed, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(time);
        (_description, _program, _changed, _time) = (description, program, changed, time);
        _title = Clean(description.Title);
        (_columns, _rows) = (PseudoTerminal.Clamp(description.Columns), PseudoTerminal.Clamp(description.Rows));
        _created = time.GetUtcNow();
        _lastOutput = time.GetTimestamp();
        // A pseudoconsole wraps lines again by itself before Windows 11; later ones leave it to the terminal.
        var console = OperatingSystem.IsWindows();
        _screen = new TerminalScreen(_columns, _rows, console: console, reflow: !console || Environment.OSVersion.Version.Build >= 21376)
        {
            Reply = Answer,
            Signal = Signaled,
        };
        _history = new TerminalHistory(_columns, _rows);
    }

    /// <summary>The identifier of the terminal.</summary>
    public string Id => _description.Id;

    /// <summary>The project the terminal is listed under, or null.</summary>
    public string? ProjectId => _description.ProjectId;

    /// <summary>Whether the terminal stays once its program has ended, until it is closed.</summary>
    public bool Keep => _description.Keep;

    /// <summary>Completes once the program has ended and everything it wrote was read.</summary>
    public Task Ended => _ended.Task;

    /// <summary>The offset after the last character the program wrote.</summary>
    public long End
    {
        get { lock (_gate) return _history.End; }
    }

    /// <summary>Whether the program ended by itself with a failure right after it started: the terminal then stays, to show why.</summary>
    public bool FailedToStart { get; private set; }

    /// <summary>Whether the terminal was asked to close.</summary>
    public bool Closing
    {
        get { lock (_gate) return _closing; }
    }

    /// <summary>Starts reading what the program writes and sending what is typed.</summary>
    internal void Start()
    {
        new Thread(Read) { IsBackground = true, Name = "terminal output" }.Start();
        new Thread(Send) { IsBackground = true, Name = "terminal input" }.Start();
    }

    /// <summary>What is known of the terminal now.</summary>
    public TerminalInfo Describe()
    {
        lock (_gate)
        {
            var folder = _screen.Folder ?? _description.Folder;
            var last = _screen.Commands.Count > 0 ? _screen.Commands[^1] : null;
            return new TerminalInfo(Id, _description.ProjectId, _description.SessionId, _title ?? folder, _title is not null, folder,
                _description.Profile.Id, _description.Profile.Name, _screen.Title, _program.ProcessId, _running, _exitCode,
                _screen.Tracks, _running && _screen.Busy, _running && _screen.Busy ? last?.CommandLine : null,
                _screen.Commands.LastOrDefault(static command => command.Finished)?.ExitCode,
                _columns, _rows, _created, _views > 0, _visible > 0, _attention, _description.Agent);
        }
    }

    /// <summary>Sends text to the program as its keyboard would.</summary>
    /// <returns>False when the program has ended, or when too much is already waiting for it.</returns>
    public bool Write(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return true;
        var data = Encoding.UTF8.GetBytes(text);
        lock (_gate)
        {
            if (!_running || _pendingInput + data.Length > MaximumPendingInput) return false;
            _pendingInput += data.Length;
        }
        return _input.Writer.TryWrite(data);
    }

    /// <summary>Gives the screen another size.</summary>
    public void Resize(int columns, int rows)
    {
        columns = PseudoTerminal.Clamp(columns);
        rows = PseudoTerminal.Clamp(rows);
        lock (_gate)
        {
            if (columns == _columns && rows == _rows) return;
            (_columns, _rows) = (columns, rows);
            if (_running) _program.Resize(columns, rows);
            _screen.Resize(columns, rows);
            // What the program writes from now on is for the new size.
            _history.Cut(columns, rows, _screen.Modes());
        }
        _changed(this, true);
    }

    /// <summary>Gives the terminal a title, or with null or an empty one the title it has by itself.</summary>
    public void Rename(string? title)
    {
        var cleaned = Clean(title);
        lock (_gate)
        {
            if (string.Equals(_title, cleaned, StringComparison.Ordinal)) return;
            _title = cleaned;
        }
        _changed(this, true);
    }

    /// <summary>Ends the program. The terminal is gone once the program has ended.</summary>
    public void Close()
    {
        bool running;
        lock (_gate)
        {
            if (_closing) return;
            _closing = true;
            running = _running;
        }
        if (running) _program.Kill();
        _changed(this, true);
    }

    /// <summary>A window shows the terminal in a tab, or no longer does.</summary>
    internal void Viewed(bool opened)
    {
        lock (_gate) _views = Math.Max(0, _views + (opened ? 1 : -1));
        _changed(this, true);
    }

    /// <summary>The tab of the terminal is the one shown, or no longer is.</summary>
    internal void Shown(bool visible)
    {
        lock (_gate)
        {
            _visible = Math.Max(0, _visible + (visible ? 1 : -1));
            if (_visible > 0) _attention = false;
        }
        _changed(this, true);
    }

    /// <summary>The piece of what the program wrote that starts at an offset: see <see cref="TerminalHistory.Read"/>.</summary>
    internal TerminalPiece? Piece(long offset, int maximum, out long start, out long end)
    {
        lock (_gate)
        {
            (start, end) = (_history.Start, _history.End);
            return _history.Read(offset, maximum);
        }
    }

    /// <summary>Where a window that starts showing the terminal reads from, and where what was written before it came ends.</summary>
    internal (long Start, long End, int Columns, int Rows) Position()
    {
        lock (_gate) return (_history.Start, _history.End, _columns, _rows);
    }

    /// <summary>The screen as text.</summary>
    public TerminalScreenText ReadScreen()
    {
        lock (_gate)
        {
            var (row, column) = _screen.Cursor;
            return new TerminalScreenText(_screen.ScreenRows(), row, column, _screen.Alternate);
        }
    }

    /// <summary>The last lines the terminal showed.</summary>
    public IReadOnlyList<string> ReadLines(int maximum)
    {
        lock (_gate) return _screen.Lines(Math.Max(0, maximum));
    }

    /// <summary>The commands the shell reported, the oldest first, at most <paramref name="maximum"/> of the last ones.</summary>
    /// <param name="maximum">The most commands to return.</param>
    /// <param name="output">Whether what each printed is read too.</param>
    public IReadOnlyList<TerminalCommandText> ReadCommands(int maximum, bool output)
    {
        lock (_gate)
        {
            var commands = _screen.Commands;
            var texts = new List<TerminalCommandText>();
            for (var index = Math.Max(0, commands.Count - Math.Max(0, maximum)); index < commands.Count; index++) texts.Add(Text(commands[index], output));
            return texts;
        }
    }

    /// <summary>
    /// What the keyboard of the terminal sends for a text, then keys, then Enter. An end of line in the text is
    /// the Enter key; several lines are a paste for a program that asked to be told pastes from typing, which
    /// then takes them as one text rather than as lines to run one by one.
    /// </summary>
    /// <param name="text">The text, or null.</param>
    /// <param name="keys">The keys, by the names <see cref="TerminalKeys.Encode"/> knows.</param>
    /// <param name="enter">Whether Enter is pressed last.</param>
    /// <param name="unknown">The first name that is not the one of a key, when there is one.</param>
    /// <returns>What to <see cref="Write"/>, or null when a key is unknown.</returns>
    public string? Keyboard(string? text, IReadOnlyList<string> keys, bool enter, out string? unknown)
    {
        ArgumentNullException.ThrowIfNull(keys);
        unknown = null;
        bool application, pastes;
        lock (_gate) (application, pastes) = (_screen.Has(1), _screen.Has(2004));
        var sent = new StringBuilder();
        if (!string.IsNullOrEmpty(text))
        {
            var lines = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
            var ends = lines.EndsWith('\r');
            var body = ends ? lines[..^1] : lines;
            if (pastes && body.Contains('\r', StringComparison.Ordinal))
            {
                // The text cannot end the paste by itself.
                sent.Append("\u001b[200~").Append(body.Replace("\u001b[201~", string.Empty, StringComparison.Ordinal)).Append("\u001b[201~");
                if (ends) sent.Append('\r');
            }
            else sent.Append(lines);
        }
        foreach (var key in keys)
        {
            if (TerminalKeys.Encode(key, application) is not { } encoded)
            {
                unknown = key;
                return null;
            }
            sent.Append(encoded);
        }
        if (enter) sent.Append('\r');
        return sent.ToString();
    }

    /// <summary>
    /// Sends text to the program and, when asked, waits until the terminal is at rest: the command the shell
    /// then ran has ended, when the shell reports its commands and was given a line at its prompt, or nothing
    /// was written for a while otherwise.
    /// </summary>
    /// <param name="text">What the keyboard sends.</param>
    /// <param name="wait">How long to wait at most; null not to wait.</param>
    /// <param name="quiet">How long nothing has to be written for a terminal whose shell reports nothing.</param>
    /// <param name="maximumLines">The most lines of what the terminal showed since to return.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>Whether the text was sent, and with a wait what came of it.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled during the wait.</exception>
    public async Task<TerminalTyped> TypeAsync(string text, TimeSpan? wait, TimeSpan quiet, int maximumLines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        TerminalMark? place;
        long after, prompts;
        bool command;
        lock (_gate)
        {
            if (!_running) return new TerminalTyped(false, false, null, null, false);
            after = _screen.Commands.Count > 0 ? _screen.Commands[^1].Number : 0;
            // A shell whose command just ended shows its prompt before it reads what is typed now: that prompt is not an answer.
            prompts = _screen.Prompts + (_screen.Integrated && !_screen.AtPrompt ? 1 : 0);
            // A shell at its prompt that is given a whole line runs it, and says when the command has ended.
            command = _screen.Tracks && !_screen.Busy && !_screen.Alternate && text.Contains('\r', StringComparison.Ordinal);
            place = wait is null ? null : _screen.Place();
        }
        var settled = false;
        TerminalCommandText? ran = null;
        try
        {
            if (!Write(text)) return new TerminalTyped(false, false, null, null, false);
            if (wait is not { } patience) return new TerminalTyped(true, false, null, null, false);
            var sent = _time.GetTimestamp();
            using var limit = new CancellationTokenSource(patience, _time);
            using var either = CancellationTokenSource.CreateLinkedTokenSource(limit.Token, cancellationToken);
            try
            {
                ran = await WaitAsync(after, prompts, command, sent, quiet, either.Token).ConfigureAwait(false);
                settled = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The time ran out: what the terminal showed so far is what there is to say.
            }
            if (ran is not null) return new TerminalTyped(true, settled, ran, null, false);
            lock (_gate)
            {
                // A program that draws the whole screen shows its screen, not lines that follow each other.
                if (_screen.Alternate) return new TerminalTyped(true, settled, null, string.Join('\n', _screen.ScreenRows()).TrimEnd(), false);
                var shown = _screen.Since(place, Math.Max(1, maximumLines), out var truncated);
                return new TerminalTyped(true, settled, null, shown, truncated);
            }
        }
        finally
        {
            // The place is of no use to anyone else.
            if (place is not null)
            {
                lock (_gate) _screen.Release(place);
            }
        }
    }

    // Waits until a command reported after `after` has ended, when the shell was given a command to run and
    // reports those it runs; until nothing was written for a while otherwise. A shell back at its prompt is at rest.
    private async Task<TerminalCommandText?> WaitAsync(long after, long prompts, bool command, long sent, TimeSpan quiet, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_gate)
            {
                var commands = _screen.Commands;
                for (var index = commands.Count - 1; index >= 0 && commands[index].Number > after; index--)
                {
                    if (commands[index].Finished) return Text(commands[index], true);
                }
                if (!_running) return null;
                // A line that ran nothing (an empty one) only brings the prompt back, and so does a shell that reports no command.
                if (_screen.Prompts > prompts && !_screen.Busy) return null;
                if (!command && _time.GetElapsedTime(Math.Max(sent, _lastOutput)) >= quiet) return null;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate) _closing = true;
        _input.Writer.TryComplete();
        _program.Dispose();
    }

    private TerminalCommandText Text(TerminalCommand command, bool output)
    {
        var truncated = false;
        var text = output ? _screen.Output(command, out truncated) : string.Empty;
        return new TerminalCommandText(command.Number, command.CommandLine, command.Folder, command.Finished, command.ExitCode, text, truncated);
    }

    // What the program wrote, until it has ended and nothing is left to read.
    private void Read()
    {
        var bytes = GC.AllocateUninitializedArray<byte>(64 * 1024, pinned: true);
        var characters = new char[bytes.Length + 8];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (_program.Read(bytes) is var read and > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, characters, 0, flush: false);
                if (count > 0) Accept(characters.AsSpan(0, count));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The terminal was disposed of while it read.
        }
        int code;
        try { code = _program.Exited.Wait(TimeSpan.FromSeconds(10)) ? _program.Exited.Result : -1; }
        catch (AggregateException) { code = -1; }
        Finish(code);
    }

    private void Accept(ReadOnlySpan<char> text)
    {
        bool listed;
        string[] answers;
        lock (_gate)
        {
            _lastOutput = _time.GetTimestamp();
            _screen.Write(text);
            _history.Append(text);
            if (_history.Full && _screen.AtRest) _history.Cut(_columns, _rows, _screen.Modes());
            (listed, _listed) = (_listed, false);
            answers = _answers.Count == 0 ? [] : [.. _answers];
            _answers.Clear();
        }
        foreach (var answer in answers) _input.Writer.TryWrite(Encoding.UTF8.GetBytes(answer));
        _changed(this, listed);
    }

    // The terminal of a window answers the questions of the program; without one, this one does.
    private void Answer(string answer)
    {
        if (_views == 0 && _running) _answers.Add(answer);
    }

    private void Signaled(TerminalSignal signal)
    {
        if (signal == TerminalSignal.Bell)
        {
            if (_visible > 0 || _attention) return;
            _attention = true;
        }
        _listed = true;
    }

    private void Finish(int code)
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
            _exitCode = code;
            // A shell that ends at once with a failure could not start: the terminal stays to show what it wrote.
            FailedToStart = !_closing && code != 0 && _time.GetUtcNow() - _created < TimeSpan.FromSeconds(3);
            var notice = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"\r\n\u001b[0m\u001b[2m[process exited with code {code}]\u001b[0m\r\n");
            _screen.Write(notice);
            _history.Append(notice);
        }
        _input.Writer.TryComplete();
        _ended.TrySetResult();
        _changed(this, true);
    }

    private void Send()
    {
        try
        {
            while (_input.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_input.Reader.TryRead(out var data))
                {
                    _program.Write(data);
                    lock (_gate) _pendingInput = Math.Max(0, _pendingInput - data.Length);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The program is gone.
        }
    }

    private static string? Clean(string? title)
    {
        if (title is null) return null;
        var cleaned = new string(title.Where(static character => !char.IsControl(character)).ToArray()).Trim();
        if (cleaned.Length > MaximumTitleLength) cleaned = cleaned[..MaximumTitleLength];
        return cleaned.Length == 0 ? null : cleaned;
    }
}
