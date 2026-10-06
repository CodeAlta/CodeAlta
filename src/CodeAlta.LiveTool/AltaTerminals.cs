namespace CodeAlta.LiveTool;

/// <summary>
/// The terminals of a host that has some: in the desktop app, the shells the user also sees in the window. A
/// host without terminals registers no such service, and <c>alta terminal</c> is then not part of its commands.
/// </summary>
public interface IAltaTerminals
{
    /// <summary>
    /// Gets whether a session may type in a terminal. A host that has the user review the commands of its
    /// sessions does not let them type: what is typed in a shell is a command nobody reviewed.
    /// </summary>
    bool AcceptsInput { get; }

    /// <summary>Lists the command interpreters a terminal can start, the default one first.</summary>
    /// <returns>The interpreters found on this system.</returns>
    IReadOnlyList<AltaTerminalShell> ListShells();

    /// <summary>Lists the terminals, in the order they were created.</summary>
    /// <returns>The terminals there are now.</returns>
    IReadOnlyList<AltaTerminal> List();

    /// <summary>Creates a terminal and starts its interpreter.</summary>
    /// <param name="request">Where it starts and what it runs.</param>
    /// <returns>The terminal with the status <c>ok</c>, or the reason there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    AltaTerminalCreation Create(AltaTerminalRequest request);

    /// <summary>Reads the text a terminal shows.</summary>
    /// <param name="id">The identifier of the terminal.</param>
    /// <param name="lines">How many of its last lines to read, with those that scrolled off its screen; 0 for the rows of its screen.</param>
    /// <returns>The text, or null when there is no such terminal.</returns>
    AltaTerminalText? Read(string id, int lines);

    /// <summary>Reads the commands the shell of a terminal reported, the oldest first.</summary>
    /// <param name="id">The identifier of the terminal.</param>
    /// <param name="maximum">The most commands to return, counted from the last one.</param>
    /// <param name="output">Whether what each command printed is read too.</param>
    /// <returns>The commands, none for a shell that reports none, or null when there is no such terminal.</returns>
    IReadOnlyList<AltaTerminalCommand>? ReadCommands(string id, int maximum, bool output);

    /// <summary>Types in a terminal as its keyboard would and, when asked, waits until the terminal is at rest.</summary>
    /// <param name="id">The identifier of the terminal.</param>
    /// <param name="input">What to type, and whether to wait.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>What happened: see <see cref="AltaTerminalSent.Status"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled during the wait.</exception>
    Task<AltaTerminalSent> SendAsync(string id, AltaTerminalInput input, CancellationToken cancellationToken);

    /// <summary>Gives a terminal a title.</summary>
    /// <param name="id">The identifier of the terminal.</param>
    /// <param name="title">The title; null or empty for the title the terminal has by itself.</param>
    /// <returns>The terminal with its title, or null when there is no such terminal.</returns>
    AltaTerminal? SetTitle(string id, string? title);

    /// <summary>Closes a terminal: its program is ended, and the terminal is gone once the program is.</summary>
    /// <param name="id">The identifier of the terminal.</param>
    /// <returns>The terminal as it was, or null when there is no such terminal.</returns>
    AltaTerminal? Close(string id);

    /// <summary>Asks the host to show the tab of a terminal to the user.</summary>
    /// <param name="id">The identifier of the terminal.</param>
    /// <returns><c>ok</c>; <c>not_found</c> when there is no such terminal; <c>no_window</c> when no window is there to show it.</returns>
    string Show(string id);
}

/// <summary>A command interpreter a terminal can start.</summary>
/// <param name="Id">The name to ask for it with.</param>
/// <param name="Name">The name shown to the user.</param>
/// <param name="Path">The full path of its program.</param>
public sealed record AltaTerminalShell(string Id, string Name, string Path);

/// <summary>What is known of a terminal at one moment.</summary>
public sealed record AltaTerminal
{
    /// <summary>Gets the identifier of the terminal.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the title of the terminal: the one it was given, or the folder it is in.</summary>
    public required string Title { get; init; }

    /// <summary>Gets a value indicating whether the title was given to the terminal.</summary>
    public bool Titled { get; init; }

    /// <summary>Gets the id of the project the terminal is listed under, when any.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the id of the session the terminal was opened from, when any.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the folder the shell is in, as far as the shell said, or the one it started in.</summary>
    public required string Folder { get; init; }

    /// <summary>Gets the name its interpreter is asked for with.</summary>
    public required string Shell { get; init; }

    /// <summary>Gets the name of its interpreter as shown to the user.</summary>
    public required string ShellName { get; init; }

    /// <summary>Gets the title the program of the terminal gave it, when any.</summary>
    public string? ProgramTitle { get; init; }

    /// <summary>Gets the process id of the program of the terminal.</summary>
    public int ProcessId { get; init; }

    /// <summary>Gets a value indicating whether the program of the terminal is running.</summary>
    public bool Running { get; init; }

    /// <summary>Gets the exit code of the program once it has ended.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets a value indicating whether the shell reports its prompts and its commands.</summary>
    public bool ReportsCommands { get; init; }

    /// <summary>Gets a value indicating whether the shell runs a command, as far as it reports it.</summary>
    public bool Busy { get; init; }

    /// <summary>Gets the command line that runs, when the shell reported it.</summary>
    public string? Command { get; init; }

    /// <summary>Gets the exit code of the last command that ended, when the shell reported one.</summary>
    public int? LastExitCode { get; init; }

    /// <summary>Gets the width of the screen, in characters.</summary>
    public int Columns { get; init; }

    /// <summary>Gets the height of the screen, in lines.</summary>
    public int Rows { get; init; }

    /// <summary>Gets when the terminal was created.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Gets a value indicating whether a window shows the terminal in a tab.</summary>
    public bool TabOpen { get; init; }

    /// <summary>Gets a value indicating whether that tab is the one the user sees.</summary>
    public bool TabVisible { get; init; }

    /// <summary>Gets a value indicating whether the program rang the bell since the user last looked at the terminal.</summary>
    public bool Attention { get; init; }

    /// <summary>Gets a value indicating whether a session created the terminal, rather than the user.</summary>
    public bool CreatedBySession { get; init; }
}

/// <summary>What a new terminal is asked for.</summary>
public sealed record AltaTerminalRequest
{
    /// <summary>Gets the folder the interpreter starts in: a full path.</summary>
    public required string Folder { get; init; }

    /// <summary>Gets the id of the project the terminal is listed under, when any.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the id of the session that asks for the terminal, when any.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the interpreter to start, one of <see cref="IAltaTerminals.ListShells"/>; null for the default one.</summary>
    public string? Shell { get; init; }

    /// <summary>Gets the title of the terminal; null for the one it has by itself.</summary>
    public string? Title { get; init; }
}

/// <summary>The outcome of creating a terminal.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>no_folder</c> when the folder does not exist; <c>unknown_shell</c> when no interpreter has the name
/// asked for; <c>no_shell</c> when the system has none; <c>limit</c> when too many terminals are open;
/// <c>failed</c> when the interpreter could not be started; <c>closed</c> while the host exits.
/// </param>
/// <param name="Terminal">The terminal, with <c>ok</c>.</param>
public sealed record AltaTerminalCreation(string Status, AltaTerminal? Terminal);

/// <summary>The text a terminal shows.</summary>
public sealed record AltaTerminalText
{
    /// <summary>Gets the terminal as it was when its text was read.</summary>
    public required AltaTerminal Terminal { get; init; }

    /// <summary>Gets the lines, from the top; none has the spaces at its end.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>Gets a value indicating whether the lines are the rows of the screen, rather than the last lines the terminal showed.</summary>
    public bool Screen { get; init; }

    /// <summary>Gets a value indicating whether a program that draws the whole screen (an editor, a pager) has its own screen up.</summary>
    public bool FullScreen { get; init; }

    /// <summary>Gets the row of the cursor on the screen, from 0.</summary>
    public int CursorRow { get; init; }

    /// <summary>Gets the column of the cursor on the screen, from 0.</summary>
    public int CursorColumn { get; init; }
}

/// <summary>A command a shell ran in a terminal, as the shell reported it.</summary>
public sealed record AltaTerminalCommand
{
    /// <summary>Gets the rank of the command among those the terminal saw, from 1.</summary>
    public long Number { get; init; }

    /// <summary>Gets the command line, when the shell reported it.</summary>
    public string? CommandLine { get; init; }

    /// <summary>Gets the folder the shell was in.</summary>
    public string? Folder { get; init; }

    /// <summary>Gets a value indicating whether the command has ended.</summary>
    public bool Finished { get; init; }

    /// <summary>Gets the exit code, once the command has ended and when the shell reported one.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets what the command printed, as far as the terminal still has it; null when it was not read.</summary>
    public string? Output { get; init; }

    /// <summary>Gets a value indicating whether the start of the output is gone.</summary>
    public bool Truncated { get; init; }
}

/// <summary>What to type in a terminal.</summary>
public sealed record AltaTerminalInput
{
    /// <summary>Gets the text to type first; an end of line in it is the Enter key.</summary>
    public string? Text { get; init; }

    /// <summary>Gets the keys to press after the text, by name: <c>enter</c>, <c>tab</c>, <c>up</c>, <c>ctrl+c</c>, <c>f5</c>…</summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>Gets a value indicating whether the Enter key is pressed last.</summary>
    public bool Enter { get; init; }

    /// <summary>
    /// Gets how long to wait for the terminal to be at rest: the command the shell then ran has ended, when the
    /// shell reports its commands, or nothing was written for a moment otherwise. Null not to wait.
    /// </summary>
    public TimeSpan? Wait { get; init; }
}

/// <summary>The outcome of typing in a terminal.</summary>
public sealed record AltaTerminalSent
{
    /// <summary>
    /// Gets the status: <c>ok</c>; <c>not_found</c> when there is no such terminal; <c>ended</c> when its program has
    /// ended; <c>refused</c> when too much is already waiting for a program that does not read its keyboard;
    /// <c>unknown_key</c> when <see cref="Key"/> names no key; <c>denied</c> when the host lets no session type.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>Gets the name that is not the one of a key, with <c>unknown_key</c>.</summary>
    public string? Key { get; init; }

    /// <summary>Gets the terminal as it was after the wait, or after the text was typed.</summary>
    public AltaTerminal? Terminal { get; init; }

    /// <summary>Gets a value indicating whether the terminal came to rest; false when it was not waited for or the time ran out.</summary>
    public bool Settled { get; init; }

    /// <summary>Gets the command the shell ran and what it printed, when the shell reported it.</summary>
    public AltaTerminalCommand? Command { get; init; }

    /// <summary>Gets what the terminal showed from where the text was typed, when no command was reported and the terminal was waited for.</summary>
    public string? Text { get; init; }

    /// <summary>Gets a value indicating whether the start of <see cref="Text"/> is gone.</summary>
    public bool Truncated { get; init; }
}
