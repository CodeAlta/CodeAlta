using System.Globalization;
using System.Text;
using CodeAlta.Catalog;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta terminal` commands: the terminals of a host that has some (the desktop window).
internal sealed partial class BuiltInAltaCommandContributor
{
    /// <summary>The longest text one call types in a terminal, in UTF-16 units.</summary>
    internal const int MaximumTerminalInput = 1024 * 1024;

    /// <summary>The longest wait of one call, in seconds.</summary>
    internal const int MaximumTerminalWaitSeconds = 3600;

    // What a call keeps of its byte budget for the records around a text.
    private const int TerminalRecordReserve = 2048;

    private static readonly AltaCommandPolicy[] TerminalPolicies =
    [
        Read("terminal list"),
        Read("terminal shells"),
        Mutating("terminal create"),
        Read("terminal read"),
        Read("terminal commands"),
        Mutating("terminal send"),
        Mutating("terminal rename"),
        // It changes nothing but what the window shows.
        Read("terminal show"),
        Disruptive("terminal close"),
    ];

    private static Command CreateTerminalCommand(AltaCommandContext context)
    {
        var group = Group("terminal", "Use the terminals of the CodeAlta window: list them, create one, read what it shows, type in it, close it.");
        group.Add(CreateTerminalListCommand(context));
        group.Add(CreateTerminalShellsCommand(context));
        group.Add(CreateTerminalCreateCommand(context));
        group.Add(CreateTerminalReadCommand(context));
        group.Add(CreateTerminalCommandsCommand(context));
        group.Add(CreateTerminalSendCommand(context));
        group.Add(CreateTerminalRenameCommand(context));
        group.Add(CreateTerminalShowCommand(context));
        group.Add(CreateTerminalCloseCommand(context));
        AddHelpText(
            group,
            "A terminal is a shell that keeps running in the window, where the user sees it and can type in it too: use one for what has to stay up (a dev server, a watcher, a REPL) or what the user should watch.",
            "For a command that runs and ends, your own shell tool is simpler.",
            "Examples: `alta terminal list`; `alta terminal create --title \"dev server\" --command \"npm run dev\"`; `alta terminal read <id>`; `alta terminal send <id> --text \"npm test\" --enter --wait 60`; `alta terminal send <id> --key ctrl+c`; `alta terminal close <id>`.");
        return group;
    }

    private static Command CreateTerminalListCommand(AltaCommandContext context)
    {
        string? project = null;
        var command = Leaf("list", "List the terminals: id, title, state, folder, shell and whether the window shows them.");
        command.Add("project=", "Only the terminals of this project: id, slug or path.", value => project = value);
        command.Add(async (_, _) => await HandleTerminalListAsync(context, project).ConfigureAwait(false));
        AddHelpText(
            command,
            "`state` is `idle` (the shell is at its prompt), `busy` (it runs `command`), `running` (the shell does not say which) or `exited`.",
            "`tab` is `visible`, `open` or `closed`: a terminal runs with or without a tab in the window.");
        return command;
    }

    private static Command CreateTerminalShellsCommand(AltaCommandContext context)
    {
        var command = Leaf("shells", "List the command interpreters a terminal can start, the default one first.");
        command.Add((_, _) => ValueTask.FromResult(HandleTerminalShells(context)));
        return command;
    }

    private static Command CreateTerminalCreateCommand(AltaCommandContext context)
    {
        var options = new TerminalCreateOptions();
        var command = Leaf("create", "Create a terminal and start its shell.");
        command.Add("project=", "Project the terminal is listed under: id, slug or path. Defaults to the project of the calling session, then to the cwd.", value => options.Project = value);
        command.Add("cwd=", "Folder the shell starts in. Defaults to the working directory of the calling session, then to the project folder.", value => options.Folder = value);
        command.Add("shell=", "Interpreter to start, an id from `alta terminal shells`. Defaults to the first one.", value => options.Shell = value);
        command.Add("title=", "Title shown to the user. Defaults to the folder.", value => options.Title = value);
        command.Add("command=", "Command line to type in the new terminal, followed by Enter.", value => options.Command = value);
        command.Add("show", "Open the tab of the terminal in the window.", value => options.Show = value is not null);
        command.Add(async (_, _) => await HandleTerminalCreateAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "The terminal is listed in the window under its project. It stays until it is closed, even once its shell has exited: close it when you are done with it.",
            "Examples: `alta terminal create`; `alta terminal create --title \"dev server\" --command \"npm run dev\" --show`; `alta terminal create --shell cmd --cwd src`.");
        return command;
    }

    private static Command CreateTerminalReadCommand(AltaCommandContext context)
    {
        string? id = null;
        string? lines = null;
        var command = Leaf("read", "Read the text a terminal shows: its screen, or its last lines.");
        command.Add("<terminal-id>", "Terminal id from `alta terminal list`.", value => id = value);
        command.Add("lines=", "Read this many of the last lines, with those that scrolled off the screen. Without it, the rows of the screen.", value => lines = value);
        command.Add((_, _) => ValueTask.FromResult(HandleTerminalRead(context, id, lines)));
        AddHelpText(
            command,
            "The text has no colors. When more is asked for than one call returns, the last lines are kept and `truncated` is true.",
            "Examples: `alta terminal read <id>`; `alta terminal read <id> --lines 200`.");
        return command;
    }

    private static Command CreateTerminalCommandsCommand(AltaCommandContext context)
    {
        string? id = null;
        var last = 10;
        var output = false;
        var command = Leaf("commands", "List the last commands the shell of a terminal ran, with their exit codes.");
        command.Add("<terminal-id>", "Terminal id from `alta terminal list`.", value => id = value);
        command.Add("last=", "Number of commands to return, counted from the last one. Defaults to 10.", (int value) => last = value);
        command.Add("output", "Include what each command printed.", value => output = value is not null);
        command.Add((_, _) => ValueTask.FromResult(HandleTerminalCommands(context, id, last, output)));
        AddHelpText(
            command,
            "Only a shell that reports its commands has some: a terminal whose `state` is `idle` or `busy` in `alta terminal list`.",
            "Example: `alta terminal commands <id> --last 1 --output`.");
        return command;
    }

    private static Command CreateTerminalSendCommand(AltaCommandContext context)
    {
        var options = new TerminalSendOptions();
        var command = Leaf("send", "Type in a terminal as its keyboard would, and optionally wait for what it prints.");
        command.Add("<terminal-id>", "Terminal id from `alta terminal list`.", value => options.Id = value);
        command.Add("text=", "Text to type. An end of line in it is the Enter key.", value => options.Text = value);
        command.Add("stdin", "Read the text to type from stdin.", value => options.UseStdin = value is not null);
        command.Add("key=", "Key to press after the text; repeat for several. Names: enter, tab, escape, space, backspace, delete, up, down, left, right, home, end, pageup, pagedown, f1 to f12, or one character, with ctrl+, alt+ and shift+ (ctrl+c, shift+tab).", value =>
        {
            if (value is not null) options.Keys.Add(value);
        });
        command.Add("enter", "Press Enter last.", value => options.Enter = value is not null);
        command.Add("wait=", "Wait up to this many seconds for the terminal to be at rest, and return what it printed.", value => options.Wait = value);
        command.Add(async (_, _) => await HandleTerminalSendAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "With `--wait`, a shell that reports its commands is waited for until the command has ended, and its output and exit code are returned; any other terminal until it has printed nothing for a moment.",
            "When the time runs out the command keeps running and `settled` is false: read the terminal later with `alta terminal read`.",
            "The user sees what you type. Never type a password or another secret.",
            "Examples: `alta terminal send <id> --text \"git status\" --enter --wait 30`; `alta terminal send <id> --key ctrl+c`; `alta terminal send <id> --key up --key enter`; `alta terminal send <id> --stdin --enter`.");
        return command;
    }

    private static Command CreateTerminalRenameCommand(AltaCommandContext context)
    {
        string? id = null;
        string? title = null;
        var command = Leaf("rename", "Give a terminal a title.");
        command.Add("<terminal-id>", "Terminal id from `alta terminal list`.", value => id = value);
        command.Add("<title>?", "The title. Without one the terminal takes back the title it has by itself: its folder.", value => title = value);
        command.Add((_, _) => ValueTask.FromResult(HandleTerminalRename(context, id, title)));
        AddHelpText(command, "Examples: `alta terminal rename <id> \"dev server\"`; `alta terminal rename <id>`.");
        return command;
    }

    private static Command CreateTerminalShowCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("show", "Open the tab of a terminal in the CodeAlta window, for the user to see it.");
        command.Add("<terminal-id>", "Terminal id from `alta terminal list`.", value => id = value);
        command.Add((_, _) => ValueTask.FromResult(HandleTerminalShow(context, id)));
        AddHelpText(command, "It only shows the terminal to the user: to read it yourself, use `alta terminal read`.");
        return command;
    }

    private static Command CreateTerminalCloseCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("close", "Close a terminal: its shell and what runs in it are ended.");
        command.Add("<terminal-id>", "Terminal id from `alta terminal list`.", value => id = value);
        command.Add((_, _) => ValueTask.FromResult(HandleTerminalClose(context, id)));
        AddHelpText(command, "Close the terminals you created once you are done with them. Leave the ones of the user alone unless asked.");
        return command;
    }

    private static bool TryGetTerminals(AltaCommandContext context, out IAltaTerminals terminals)
    {
        if (context.Services.Get<IAltaTerminals>() is { } found)
        {
            terminals = found;
            return true;
        }

        terminals = null!;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'IAltaTerminals' is unavailable.");
        return false;
    }

    private static async ValueTask<int> HandleTerminalListAsync(AltaCommandContext context, string? projectRef)
    {
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        string? projectId = null;
        if (NormalizeOptionalText(projectRef) is { } reference)
        {
            if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
            {
                return AltaExitCodes.ServiceUnavailable;
            }

            if (await ResolveProjectAsync(catalog, reference, context, includeArchived: true).ConfigureAwait(false) is not { } project)
            {
                return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
            }

            projectId = project.Id;
        }

        var listed = terminals.List().Where(terminal => projectId is null || string.Equals(terminal.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var terminal in listed)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, TerminalRecord(context, "alta.terminal", terminal));
        }

        WriteSummary(context, "alta.terminal.summary", listed.Length, truncated: false);
        return AltaExitCodes.Success;
    }

    private static int HandleTerminalShells(AltaCommandContext context)
    {
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var shells = terminals.ListShells();
        for (var index = 0; index < shells.Count; index++)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.terminal.shell",
                version = 1,
                correlationId = context.CorrelationId,
                id = shells[index].Id,
                name = shells[index].Name,
                path = shells[index].Path,
                @default = index == 0,
            });
        }

        WriteSummary(context, "alta.terminal.shell.summary", shells.Count, truncated: false);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleTerminalCreateAsync(AltaCommandContext context, TerminalCreateOptions options)
    {
        const string path = "alta terminal create";
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        var commandLine = options.Command;
        if (commandLine is not null)
        {
            if (string.IsNullOrWhiteSpace(commandLine) || commandLine.Length > MaximumTerminalInput)
            {
                return UsageError(context, "usage.invalidCommand", "The command line is empty or too long.", path);
            }

            // Before anything is created: a terminal that is not given its command is of no use to the caller.
            if (!terminals.AcceptsInput)
            {
                return TerminalInputDenied(context);
            }
        }

        if (options.Title is { Length: > 256 })
        {
            return UsageError(context, "usage.invalidTitle", "The title is too long.", path);
        }

        // The project the terminal is listed under: the one named, the one of the calling session, the one of the cwd, or none.
        var reference = NormalizeOptionalText(options.Project);
        ProjectDescriptor? project;
        if (reference is not null)
        {
            project = await ResolveProjectAsync(catalog, reference, context, includeArchived: false).ConfigureAwait(false);
            if (project is null)
            {
                return NotFound(context, "project.notFound", $"Project '{reference}' was not found.");
            }
        }
        else
        {
            project = await ResolveProjectAsync(catalog, NormalizeOptionalText(context.Caller.SourceProjectId), context, includeArchived: false).ConfigureAwait(false)
                ?? (NormalizeOptionalText(context.Cwd) is { } cwd ? await catalog.GetByPathAsync(ResolvePath(context, cwd), context.CancellationToken).ConfigureAwait(false) : null);
            if (project is { Archived: true })
            {
                project = null;
            }
        }

        // The folder: the one named, the one the calling session works in unless another project was named, the one of the project.
        string? folder;
        try
        {
            folder = NormalizeOptionalText(options.Folder) is { } named
                ? Path.IsPathFullyQualified(named) ? ResolvePath(context, named) : ResolvePath(context with { Cwd = NormalizeOptionalText(context.Cwd) ?? project?.ProjectPath }, named)
                : reference is null && NormalizeOptionalText(context.Cwd) is { } working ? ResolvePath(context, working)
                : project is not null ? Path.GetFullPath(project.ProjectPath)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return UsageError(context, "usage.invalidCwd", "The folder is not a valid path.", path);
        }

        if (folder is null)
        {
            return UsageError(context, "usage.missingCwd", "No folder to start in: add `--cwd` or `--project`.", path);
        }

        var created = terminals.Create(new AltaTerminalRequest
        {
            Folder = folder,
            ProjectId = project?.Id,
            SessionId = NormalizeOptionalText(context.Caller.SourceSessionId),
            Shell = NormalizeOptionalText(options.Shell),
            Title = NormalizeOptionalText(options.Title),
        });
        if (created.Terminal is not { } terminal)
        {
            return created.Status switch
            {
                "no_folder" => NotFound(context, "folder.notFound", $"Folder '{folder}' does not exist."),
                "unknown_shell" => NotFound(context, "shell.notFound", $"No interpreter is named '{options.Shell}'. List them with `alta terminal shells`."),
                "no_shell" => TerminalFailure(context, "terminal.noShell", "No command interpreter was found on this system."),
                "limit" => TerminalFailure(context, "terminal.limit", "Too many terminals are open. Close one first with `alta terminal close`."),
                "closed" => TerminalUnavailable(context, "CodeAlta is exiting."),
                _ => TerminalFailure(context, "terminal.startFailed", "The interpreter could not be started."),
            };
        }

        var record = TerminalRecord(context, "alta.terminal.created", terminal);
        if (commandLine is not null)
        {
            var sent = await terminals.SendAsync(terminal.Id, new AltaTerminalInput { Text = commandLine, Enter = true }, context.CancellationToken).ConfigureAwait(false);
            record["commandSent"] = sent.Status == "ok";
        }

        if (options.Show)
        {
            record["shown"] = terminals.Show(terminal.Id) == "ok";
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static int HandleTerminalRead(AltaCommandContext context, string? id, string? lines)
    {
        const string path = "alta terminal read";
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } terminalId)
        {
            return UsageError(context, "usage.missingTerminal", "A terminal id is required.", path);
        }

        var count = 0;
        if (NormalizeOptionalText(lines) is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1))
        {
            return UsageError(context, "usage.invalidLines", "The number of lines must be a number, starting at 1.", path);
        }

        if (terminals.Read(terminalId, count) is not { } read)
        {
            return TerminalNotFound(context, terminalId);
        }

        var shown = TerminalTail(context, read.Lines, out var kept, out var truncated);
        var record = TerminalRecord(context, "alta.terminal.text", read.Terminal, brief: true);
        record["source"] = read.Screen ? "screen" : "lines";
        if (read.FullScreen) record["fullScreen"] = true;
        record["lineCount"] = kept;
        record["truncated"] = truncated;
        if (read.Screen) record["cursor"] = new { row = read.CursorRow, column = read.CursorColumn };
        record["text"] = shown;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static int HandleTerminalCommands(AltaCommandContext context, string? id, int last, bool output)
    {
        const string path = "alta terminal commands";
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } terminalId)
        {
            return UsageError(context, "usage.missingTerminal", "A terminal id is required.", path);
        }

        if (last < 1)
        {
            return UsageError(context, "usage.invalidLast", "The number of commands must be at least 1.", path);
        }

        var limit = Math.Min(last, Math.Max(1, (context.MaxOutputRecords ?? 200) - 1));
        if (terminals.ReadCommands(terminalId, limit, output) is not { } commands)
        {
            return TerminalNotFound(context, terminalId);
        }

        if (commands.Count == 0 && terminals.List().FirstOrDefault(terminal => string.Equals(terminal.Id, terminalId, StringComparison.OrdinalIgnoreCase)) is { ReportsCommands: false })
        {
            AltaJsonlWriter.WriteWarning(context.Stderr, context.CorrelationId, "terminal.noCommandReports",
                "The shell of this terminal reports no command: read what it shows with `alta terminal read`.", path);
        }

        // What the commands printed shares what one call returns.
        var share = Math.Max(512, (TerminalBudget(context) - commands.Count * 256) / Math.Max(1, commands.Count));
        foreach (var command in commands)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, TerminalCommandRecord(context, terminalId, command, share));
        }

        WriteSummary(context, "alta.terminal.command.summary", commands.Count, truncated: limit < last && commands.Count == limit);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleTerminalSendAsync(AltaCommandContext context, TerminalSendOptions options)
    {
        const string path = "alta terminal send";
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(options.Id) is not { } terminalId)
        {
            return UsageError(context, "usage.missingTerminal", "A terminal id is required.", path);
        }

        if (options.Text is not null && options.UseStdin)
        {
            return UsageError(context, "usage.textConflict", "Use either --text or --stdin, not both.", path);
        }

        var text = options.UseStdin ? await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false) : options.Text;
        if (string.IsNullOrEmpty(text) && options.Keys.Count == 0 && !options.Enter)
        {
            return UsageError(context, "usage.missingInput", "Nothing to type: use --text, --stdin, --key or --enter.", path);
        }

        if (text is { Length: > MaximumTerminalInput } || options.Keys.Count > 256)
        {
            return UsageError(context, "usage.inputTooLong", "There is too much to type in one call.", path);
        }

        TimeSpan? wait = null;
        if (NormalizeOptionalText(options.Wait) is { } waitText)
        {
            if (!int.TryParse(waitText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > MaximumTerminalWaitSeconds)
            {
                return UsageError(context, "usage.invalidWait", $"The wait is a number of seconds from 1 to {MaximumTerminalWaitSeconds.ToString(CultureInfo.InvariantCulture)}.", path);
            }

            wait = TimeSpan.FromSeconds(seconds);
        }

        if (!terminals.AcceptsInput)
        {
            return TerminalInputDenied(context);
        }

        var sent = await terminals.SendAsync(terminalId, new AltaTerminalInput { Text = text, Keys = options.Keys, Enter = options.Enter, Wait = wait }, context.CancellationToken).ConfigureAwait(false);
        switch (sent.Status)
        {
            case "ok" when sent.Terminal is not null:
                break;
            case "not_found":
                return TerminalNotFound(context, terminalId);
            case "denied":
                return TerminalInputDenied(context);
            case "unknown_key":
                return UsageError(context, "usage.unknownKey", $"'{sent.Key}' is not the name of a key.", path);
            case "ended":
                return TerminalFailure(context, "terminal.ended", "The program of this terminal has ended: nothing can be typed in it.");
            default:
                return TerminalFailure(context, "terminal.inputRefused", "The terminal takes no more input: its program does not read what was typed before.");
        }

        var record = TerminalRecord(context, "alta.terminal.sent", sent.Terminal, brief: true);
        record["waited"] = wait is not null;
        if (wait is not null)
        {
            record["settled"] = sent.Settled;
            if (sent.Command is { } command)
            {
                var output = TerminalTail(context, (command.Output ?? string.Empty).Split('\n'), out _, out var cut);
                if (command.CommandLine is not null) record["commandLine"] = command.CommandLine;
                if (command.ExitCode is { } exitCode) record["commandExitCode"] = exitCode;
                record["outputTruncated"] = cut || command.Truncated;
                record["output"] = output;
            }
            else
            {
                var output = TerminalTail(context, (sent.Text ?? string.Empty).Split('\n'), out _, out var cut);
                record["outputTruncated"] = cut || sent.Truncated;
                record["output"] = output;
            }

            if (!sent.Settled)
            {
                record["nextStep"] = $"The terminal was still busy when the wait ended. Read it later with `alta terminal read {sent.Terminal.Id}`.";
            }
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static int HandleTerminalRename(AltaCommandContext context, string? id, string? title)
    {
        const string path = "alta terminal rename";
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } terminalId)
        {
            return UsageError(context, "usage.missingTerminal", "A terminal id is required.", path);
        }

        if (title is { Length: > 256 })
        {
            return UsageError(context, "usage.invalidTitle", "The title is too long.", path);
        }

        if (terminals.SetTitle(terminalId, NormalizeOptionalText(title)) is not { } terminal)
        {
            return TerminalNotFound(context, terminalId);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, TerminalRecord(context, "alta.terminal.renamed", terminal, brief: true));
        return AltaExitCodes.Success;
    }

    private static int HandleTerminalShow(AltaCommandContext context, string? id)
    {
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } terminalId)
        {
            return UsageError(context, "usage.missingTerminal", "A terminal id is required.", "alta terminal show");
        }

        switch (terminals.Show(terminalId))
        {
            case "ok":
                AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.terminal.shown", version = 1, correlationId = context.CorrelationId, id = terminalId });
                return AltaExitCodes.Success;
            case "not_found":
                return TerminalNotFound(context, terminalId);
            default:
                return TerminalUnavailable(context, "No CodeAlta window is open to show the terminal.");
        }
    }

    private static int HandleTerminalClose(AltaCommandContext context, string? id)
    {
        if (!TryGetTerminals(context, out var terminals))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } terminalId)
        {
            return UsageError(context, "usage.missingTerminal", "A terminal id is required.", "alta terminal close");
        }

        if (terminals.Close(terminalId) is not { } terminal)
        {
            return TerminalNotFound(context, terminalId);
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.terminal.closed",
            version = 1,
            correlationId = context.CorrelationId,
            id = terminal.Id,
            title = terminal.Title,
            wasRunning = terminal.Running,
        });
        return AltaExitCodes.Success;
    }

    private static int TerminalNotFound(AltaCommandContext context, string id)
        => NotFound(context, "terminal.notFound", $"Terminal '{id}' was not found. List the terminals with `alta terminal list`.");

    private static int TerminalInputDenied(AltaCommandContext context)
        => PermissionDenied(context, "terminal.inputDenied", "This host has the user review the commands of its sessions: a session cannot type in a terminal.");

    private static int TerminalFailure(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Failure, message);
        return AltaExitCodes.Failure;
    }

    private static int TerminalUnavailable(AltaCommandContext context, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "view.unavailable", AltaExitCodes.ServiceUnavailable, message);
        return AltaExitCodes.ServiceUnavailable;
    }

    // One word for what a terminal does: the shell is at its prompt, runs a command, does not say, or has ended.
    private static string TerminalState(AltaTerminal terminal)
        => !terminal.Running ? "exited" : !terminal.ReportsCommands ? "running" : terminal.Busy ? "busy" : "idle";

    // What a record says of a terminal; `brief` for the records that are about something else.
    private static Dictionary<string, object?> TerminalRecord(AltaCommandContext context, string type, AltaTerminal terminal, bool brief = false)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = terminal.Id,
            ["title"] = terminal.Title,
            ["state"] = TerminalState(terminal),
        };
        Add("exitCode", terminal.ExitCode);
        Add("command", terminal.Command);
        Add("lastExitCode", terminal.LastExitCode);
        Add("folder", terminal.Folder);
        if (brief) return record;
        Add("shell", terminal.Shell);
        Add("shellName", terminal.ShellName);
        Add("programTitle", terminal.ProgramTitle);
        Add("projectId", terminal.ProjectId);
        Add("sessionId", terminal.SessionId);
        Add("processId", terminal.ProcessId);
        Add("columns", terminal.Columns);
        Add("rows", terminal.Rows);
        Add("tab", terminal.TabVisible ? "visible" : terminal.TabOpen ? "open" : "closed");
        if (terminal.Attention) Add("attention", true);
        Add("createdBy", terminal.CreatedBySession ? "session" : "user");
        Add("createdAt", terminal.Created);
        return record;

        void Add(string name, object? value)
        {
            if (value is not null) record.Add(name, value);
        }
    }

    private static Dictionary<string, object?> TerminalCommandRecord(AltaCommandContext context, string terminalId, AltaTerminalCommand command, int budget)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.terminal.command",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["terminalId"] = terminalId,
            ["number"] = command.Number,
            ["state"] = command.Finished ? "finished" : "running",
        };
        if (command.CommandLine is not null) record["commandLine"] = command.CommandLine;
        if (command.ExitCode is { } exitCode) record["exitCode"] = exitCode;
        if (command.Folder is not null) record["folder"] = command.Folder;
        if (command.Output is { } output)
        {
            var shown = TerminalTail(output.Split('\n'), budget, out _, out var cut);
            record["outputTruncated"] = cut || command.Truncated;
            record["output"] = shown;
        }

        return record;
    }

    // The bytes one call has for the text of a terminal.
    private static int TerminalBudget(AltaCommandContext context)
        => Math.Max(1024, (context.MaxOutputBytes ?? 64 * 1024) - TerminalRecordReserve);

    private static string TerminalTail(AltaCommandContext context, IReadOnlyList<string> lines, out int kept, out bool truncated)
        => TerminalTail(lines, TerminalBudget(context), out kept, out truncated);

    // The last lines that fit a number of bytes once written in a record: what a terminal showed last is what matters.
    private static string TerminalTail(IReadOnlyList<string> lines, int budget, out int kept, out bool truncated)
    {
        var first = lines.Count;
        var used = 0;
        while (first > 0)
        {
            // The line as the record writes it, without its quotes, and the two characters of its end of line.
            var cost = Encoding.UTF8.GetByteCount(AltaJsonlWriter.Serialize(lines[first - 1]));
            if (used + cost > budget) break;
            used += cost;
            first--;
        }

        kept = lines.Count - first;
        truncated = first > 0;
        if (kept == 0 && lines.Count > 0)
        {
            // One line longer than everything a call returns: its end.
            var last = lines[^1];
            kept = 1;
            return last[^Math.Min(last.Length, Math.Max(1, budget / 6))..];
        }

        return string.Join('\n', lines.Skip(first));
    }

    private sealed class TerminalCreateOptions
    {
        public string? Project { get; set; }
        public string? Folder { get; set; }
        public string? Shell { get; set; }
        public string? Title { get; set; }
        public string? Command { get; set; }
        public bool Show { get; set; }
    }

    private sealed class TerminalSendOptions
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public bool UseStdin { get; set; }
        public List<string> Keys { get; } = [];
        public bool Enter { get; set; }
        public string? Wait { get; set; }
    }
}
