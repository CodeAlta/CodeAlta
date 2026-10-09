using CodeAlta.Desktop.Terminals;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// Gives the <c>alta terminal</c> commands the terminals of the application: the ones the window lists and
/// shows. A terminal a session creates stays once its program has ended, so that what it wrote can still be read.
/// </summary>
/// <param name="terminals">The terminals of the application.</param>
/// <param name="acceptsInput">Whether a session may type in a terminal: not in a host that has the user review the commands of its sessions.</param>
internal sealed class DesktopAltaTerminals(DesktopTerminals terminals, bool acceptsInput = true) : IAltaTerminals
{
    /// <summary>How long nothing has to be written for a terminal whose shell reports nothing to be at rest.</summary>
    internal static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(1500);

    /// <summary>The most lines of what a terminal showed since something was typed that are read.</summary>
    internal const int MaximumLines = 2000;

    /// <summary>
    /// Gets or initializes whether the user reviews the commands of the sessions now: the setting can change while
    /// the host runs. Null when only <c>acceptsInput</c> decides.
    /// </summary>
    internal Func<bool>? Reviews { get; init; }

    /// <inheritdoc />
    public bool AcceptsInput => acceptsInput && Reviews?.Invoke() != true;

    /// <inheritdoc />
    public IReadOnlyList<AltaTerminalShell> ListShells()
        => [.. terminals.Profiles.Select(static profile => new AltaTerminalShell(profile.Id, profile.Name, profile.FileName))];

    /// <inheritdoc />
    public IReadOnlyList<AltaTerminal> List() => [.. terminals.List().Select(Terminal)];

    /// <inheritdoc />
    public AltaTerminalCreation Create(AltaTerminalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A shell that is asked for by name is that one, never the default one in its place.
        if (request.Shell is { } shell && !terminals.Profiles.Any(profile => string.Equals(profile.Id, shell, StringComparison.OrdinalIgnoreCase))) return new("unknown_shell", null);
        var (status, terminal) = terminals.Create(new TerminalRequest(request.ProjectId, request.SessionId, request.Folder, request.Shell, request.Title, Agent: true));
        return new(status, terminal is null ? null : Terminal(terminal.Describe()));
    }

    /// <inheritdoc />
    public AltaTerminalText? Read(string id, int lines)
    {
        if (terminals.Find(id) is not { } terminal) return null;
        var screen = terminal.ReadScreen();
        // A program that draws the whole screen has no lines that follow each other: its screen is what there is to read.
        var rows = lines <= 0 || screen.Alternate;
        return new AltaTerminalText
        {
            Terminal = Terminal(terminal.Describe()),
            Lines = rows ? Trimmed(screen.Rows) : terminal.ReadLines(lines),
            Screen = rows,
            FullScreen = screen.Alternate,
            CursorRow = screen.CursorRow,
            CursorColumn = screen.CursorColumn,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<AltaTerminalCommand>? ReadCommands(string id, int maximum, bool output)
        => terminals.Find(id) is { } terminal ? [.. terminal.ReadCommands(maximum, output).Select(command => Command(command, output))] : null;

    /// <inheritdoc />
    public async Task<AltaTerminalSent> SendAsync(string id, AltaTerminalInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!AcceptsInput) return new AltaTerminalSent { Status = "denied" };
        if (terminals.Find(id) is not { } terminal) return new AltaTerminalSent { Status = "not_found" };
        if (terminal.Keyboard(input.Text, input.Keys, input.Enter, out var unknown) is not { } keyboard) return new AltaTerminalSent { Status = "unknown_key", Key = unknown };
        var before = terminal.Describe();
        if (!before.Running) return new AltaTerminalSent { Status = "ended", Terminal = Terminal(before) };
        var typed = await terminal.TypeAsync(keyboard, input.Wait, Quiet, MaximumLines, cancellationToken).ConfigureAwait(false);
        var after = terminal.Describe();
        if (!typed.Sent) return new AltaTerminalSent { Status = after.Running ? "refused" : "ended", Terminal = Terminal(after) };
        return new AltaTerminalSent
        {
            Status = "ok",
            Terminal = Terminal(after),
            Settled = typed.Settled,
            Command = typed.Command is { } command ? Command(command, output: true) : null,
            Text = typed.Text,
            Truncated = typed.Truncated,
        };
    }

    /// <inheritdoc />
    public AltaTerminal? SetTitle(string id, string? title)
    {
        if (terminals.Find(id) is not { } terminal) return null;
        terminal.Rename(title);
        return Terminal(terminal.Describe());
    }

    /// <inheritdoc />
    public AltaTerminal? Close(string id)
    {
        if (terminals.Find(id) is not { } terminal) return null;
        var closed = Terminal(terminal.Describe());
        terminal.Close();
        return closed;
    }

    /// <inheritdoc />
    public string Show(string id)
        => terminals.Find(id) is not { } terminal ? "not_found" : terminals.Reveal(terminal) ? "ok" : "no_window";

    // The rows of a screen down to the last one that shows something.
    private static IReadOnlyList<string> Trimmed(IReadOnlyList<string> rows)
    {
        var count = rows.Count;
        while (count > 0 && rows[count - 1].Length == 0) count--;
        return [.. rows.Take(count)];
    }

    private static AltaTerminalCommand Command(TerminalCommandText command, bool output)
        => new()
        {
            Number = command.Number,
            CommandLine = command.CommandLine,
            Folder = command.Folder,
            Finished = command.Finished,
            ExitCode = command.ExitCode,
            Output = output ? command.Output : null,
            Truncated = command.Truncated,
        };

    private static AltaTerminal Terminal(TerminalInfo terminal)
        => new()
        {
            Id = terminal.Id,
            Title = terminal.Title,
            Titled = terminal.Titled,
            ProjectId = terminal.ProjectId,
            SessionId = terminal.SessionId,
            Folder = terminal.Folder,
            Shell = terminal.Profile,
            ShellName = terminal.ProfileName,
            ProgramTitle = terminal.ProgramTitle,
            ProcessId = terminal.ProcessId,
            Running = terminal.Running,
            ExitCode = terminal.ExitCode,
            ReportsCommands = terminal.Integrated,
            Busy = terminal.Busy,
            Command = terminal.Command,
            LastExitCode = terminal.LastExitCode,
            Columns = terminal.Columns,
            Rows = terminal.Rows,
            Created = terminal.Created,
            TabOpen = terminal.Open,
            TabVisible = terminal.Visible,
            Attention = terminal.Attention,
            CreatedBySession = terminal.Agent,
        };
}
