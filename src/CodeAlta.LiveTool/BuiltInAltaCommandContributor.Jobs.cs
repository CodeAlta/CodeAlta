using System.Globalization;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta job` commands: the background jobs of a session, commands it starts without waiting for them.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static Command CreateJobCommand(AltaCommandContext context)
    {
        var group = Group("job", "Run commands in the background of a session: start one without waiting for it, list them, read what they write, cancel them.");
        group.Add(CreateJobStartCommand(context));
        group.Add(CreateJobListCommand(context));
        group.Add(CreateJobStatusCommand(context));
        group.Add(CreateJobOutputCommand(context));
        group.Add(CreateJobCancelCommand(context));
        AddHelpText(
            group,
            "A job is a shell command CodeAlta runs for the session while the session goes on with something else, or ends its turn. When the command ends, whether it succeeded or failed, CodeAlta sends the session a prompt with its exit code and the end of what it wrote (standard output and standard error): a session that had ended its turn is started again by it, a session that runs is given it in its turn.",
            "A job you cancel yourself sends nothing. The user sees the jobs of a session, what they write, and can stop them.",
            "Use a job for what takes long and needs no watching: a long build or test run, a wait such as `gh run watch <id> --exit-status`. For a command that ends in a few seconds your shell tool is simpler, and gives you its result at once.",
            "Jobs end when CodeAlta exits. For something that has to stay up and be typed in (a dev server, a REPL), use a terminal where the host has them.",
            "Examples: `alta job start --command \"gh run watch 123 --exit-status\" --title \"CI of the pull request\"`; `alta job start --timeout 00:30:00 --stdin`; `alta job list`; `alta job status <job-id>`; `alta job output <job-id> --lines 100`; `alta job cancel <job-id>`.");
        return group;
    }

    private static Command CreateJobStartCommand(AltaCommandContext context)
    {
        var options = new JobStartOptions();
        var command = Leaf("start", "Start a shell command in the background and return at once with the id of its job.");
        command.Add("command=", "Command line to run, in the shell of your shell tool.", value => options.Command = value);
        command.Add("stdin", "Read the command from stdin: for a command of several lines, or with quotes.", value => options.UseStdin = value is not null);
        command.Add("title=", "What the job is for, in a few words: the user sees it in the list of the session.", value => options.Title = value);
        command.Add("cwd=", "Folder the command runs in. Defaults to the folder the calling session works in.", value => options.Folder = value);
        command.Add("timeout=", "End the command when it has run this long: seconds, or a time such as `00:30:00`. Without it the command runs as long as it takes.", value => options.Timeout = value);
        command.Add("notify=", "When the end of the job sends the session a prompt: `always` (the default: any exit code, a timeout, a job the user stopped), `success` (exit code 0 only) or `never`.", value => options.Notify = value);
        command.Add("session=", "Session the job belongs to and whose turn its end starts. Defaults to the calling session.", value => options.SessionId = value);
        command.Add(async (_, _) => await HandleJobStartAsync(context, options).ConfigureAwait(false));
        AddHelpText(
            command,
            "The command returns as soon as the job runs. You can go on working, or end your turn: nothing has to be polled, the result comes to you as a prompt of the session.",
            "The result says how the command ended: `succeeded (exit code 0)`, `failed (exit code N)` or `timed out`, with the end of its output. A job that waits for an event (a watcher) needs no timeout; give one to a command that could hang for ever.",
            "`--notify success` is for a command that tells something only when it succeeds; `--notify never` for one you will read yourself.",
            "Examples: `alta job start --command \"dotnet test -c Release\" --timeout 1800 --title \"Full test run\"`; `alta job start --command \"gh run watch 123 --exit-status\"`; `alta job start --cwd src --stdin`.");
        return command;
    }

    private static Command CreateJobListCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var all = false;
        var command = Leaf("list", "List the jobs of a session: the ones that run, then the last ones that ended.");
        command.Add("session=", "Session whose jobs are listed. Defaults to the calling session.", value => sessionId = value);
        command.Add("all", "List the jobs of every session.", value => all = value is not null);
        command.Add((_, _) => ValueTask.FromResult(HandleJobList(context, sessionId, all)));
        AddHelpText(command, "`state` is `running`, `succeeded` (exit code 0), `failed`, `timed_out` or `cancelled`. A job that ended stays listed for a while, with what it wrote.");
        return command;
    }

    private static Command CreateJobStatusCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("status", "Show one job: its state, its exit code, how long it has run and the last line it wrote.");
        command.Add("<job-id>", "Job id from `alta job start` or `alta job list`.", value => id = value);
        command.Add((_, _) => ValueTask.FromResult(HandleJobStatus(context, id)));
        return command;
    }

    private static Command CreateJobOutputCommand(AltaCommandContext context)
    {
        string? id = null;
        string? lines = null;
        var command = Leaf("output", "Read what a job wrote, standard output and standard error together: its last lines.");
        command.Add("<job-id>", "Job id from `alta job start` or `alta job list`.", value => id = value);
        command.Add("lines=", "Read this many of the last lines. Without it, as many as one call returns.", value => lines = value);
        command.Add((_, _) => ValueTask.FromResult(HandleJobOutput(context, id, lines)));
        AddHelpText(
            command,
            "It works while the job runs and after it ended. When more was written than one call returns, the last lines are kept and `truncated` is true.",
            "Examples: `alta job output <job-id>`; `alta job output <job-id> --lines 50`.");
        return command;
    }

    private static Command CreateJobCancelCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("cancel", "End the command of a job, and the processes it started.");
        command.Add("<job-id>", "Job id from `alta job start` or `alta job list`.", value => id = value);
        command.Add(async (_, _) => await HandleJobCancelAsync(context, id).ConfigureAwait(false));
        AddHelpText(command, "A job you cancel sends no prompt. What it wrote can still be read with `alta job output`.");
        return command;
    }

    private static bool TryGetJobs(AltaCommandContext context, out SessionJobService jobs)
    {
        if (context.Services.Get<SessionRuntimeService>() is { } runtime)
        {
            jobs = runtime.Jobs;
            return true;
        }

        jobs = null!;
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "service.unavailable", AltaExitCodes.ServiceUnavailable,
            "Required in-process service 'SessionRuntimeService' is unavailable.");
        return false;
    }

    private static async ValueTask<int> HandleJobStartAsync(AltaCommandContext context, JobStartOptions options)
    {
        const string path = "alta job start";
        if (!TryGetJobs(context, out var jobs))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (options.Command is not null && options.UseStdin)
        {
            return UsageError(context, "usage.commandConflict", "Use either --command or --stdin, not both.", path);
        }

        var commandLine = (options.UseStdin ? await context.Stdin.ReadToEndAsync(context.CancellationToken).ConfigureAwait(false) : options.Command)?.Trim();
        if (string.IsNullOrEmpty(commandLine))
        {
            return UsageError(context, "usage.missingCommand", "A command is required: use --command <text> or --stdin.", path);
        }

        if (commandLine.Length > SessionJobService.MaxCommandCharacters)
        {
            return UsageError(context, "usage.invalidCommand", "The command line is too long.", path);
        }

        if (options.Title is { Length: > 256 })
        {
            return UsageError(context, "usage.invalidTitle", "The title is too long.", path);
        }

        SessionJobNotification notification;
        switch (NormalizeOptionalText(options.Notify)?.ToLowerInvariant())
        {
            case null or "always": notification = SessionJobNotification.Always; break;
            case "success": notification = SessionJobNotification.Success; break;
            case "never": notification = SessionJobNotification.Never; break;
            default: return UsageError(context, "usage.invalidNotify", "--notify is `always`, `success` or `never`.", path);
        }

        TimeSpan? timeout = null;
        if (NormalizeOptionalText(options.Timeout) is { } timeoutText)
        {
            if (!TryParseReminderDuration(timeoutText, out var given, out _) || given > SessionJobService.MaxTimeout)
            {
                return UsageError(context, "usage.invalidTimeout", "--timeout is a positive number of seconds or a time such as `00:30:00`, at most 7 days.", path);
            }

            timeout = given;
        }

        var explicitSession = NormalizeOptionalText(options.SessionId);
        var sessionId = explicitSession ?? NormalizeOptionalText(context.Caller.SourceSessionId);

        // A session whose commands the user reviews starts none that nobody reviewed: the session that calls is the
        // one that acts, whichever session the job is for.
        if (context.Services.Get<AltaJobPolicy>() is { } policy && !policy.Accepts(NormalizeOptionalText(context.Caller.SourceSessionId) ?? sessionId))
        {
            return PermissionDenied(context, "job.startDenied", "The user reviews the commands of this session: it cannot start a command in the background. Run it with your shell tool.");
        }

        if (sessionId is null)
        {
            return UsageError(context, "usage.missingSession", "A job belongs to a session, which is told its end: use --session <session-id> when no session calls.", path);
        }

        var projectId = NormalizeOptionalText(context.Caller.SourceProjectId);
        if (explicitSession is not null && !string.Equals(explicitSession, NormalizeOptionalText(context.Caller.SourceSessionId), StringComparison.OrdinalIgnoreCase))
        {
            var info = await ResolveSessionInfoAsync(context, explicitSession).ConfigureAwait(false);
            if (info.ExitCode != AltaExitCodes.Success)
            {
                return info.ExitCode;
            }

            sessionId = info.Info!.Session.SessionId;
            projectId = NormalizeOptionalText(info.Info.Session.ProjectRef);
        }

        string? folder;
        try
        {
            folder = NormalizeOptionalText(options.Folder) is { } named ? ResolvePath(context, named)
                : NormalizeOptionalText(context.Cwd) is { } working ? ResolvePath(context, working)
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return UsageError(context, "usage.invalidCwd", "The folder is not a valid path.", path);
        }

        if (folder is null)
        {
            return UsageError(context, "usage.missingCwd", "No folder to run in: add `--cwd`.", path);
        }

        var started = jobs.Start(new SessionJobRequest
        {
            SessionId = sessionId,
            ProjectId = projectId,
            Command = commandLine,
            Folder = folder,
            Title = NormalizeOptionalText(options.Title),
            Notification = notification,
            Timeout = timeout,
        });
        if (started.Job is not { } job)
        {
            return started.Status switch
            {
                "no_folder" => NotFound(context, "folder.notFound", $"Folder '{folder}' does not exist."),
                "limit" => JobFailure(context, "job.limit", "Too many jobs run already. Wait for one to end, or cancel one with `alta job cancel`."),
                "closed" => JobFailure(context, "job.closed", "CodeAlta is exiting: no job can be started."),
                _ => JobFailure(context, "job.startFailed", started.Message ?? "The shell could not be started."),
            };
        }

        var record = JobRecord(context, "alta.job.started", job);
        record["nextStep"] = notification switch
        {
            SessionJobNotification.Never => $"The job runs. Nothing is sent when it ends: read it with `alta job status {job.Id}` and `alta job output {job.Id}`.",
            SessionJobNotification.Success => $"The job runs. Its result is sent to the session as a prompt when the command succeeds (exit code 0): go on with other work or end your turn, do not poll. A command that fails sends nothing: `alta job status {job.Id}` says how it ended.",
            _ => "The job runs. Its result (exit code and the end of its output) is sent to the session as a prompt when the command ends, whether it succeeds or fails: go on with other work or end your turn, do not poll.",
        };
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static int HandleJobList(AltaCommandContext context, string? sessionId, bool all)
    {
        if (!TryGetJobs(context, out var jobs))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (all && NormalizeOptionalText(sessionId) is not null)
        {
            return UsageError(context, "usage.sessionConflict", "Use either --session or --all, not both.", "alta job list");
        }

        // A caller that is no session (the user's command line, an MCP client) sees the jobs of every session.
        var scope = all ? null : NormalizeOptionalText(sessionId) ?? NormalizeOptionalText(context.Caller.SourceSessionId);
        var listed = jobs.List(scope);
        var limit = Math.Max(1, (context.MaxOutputRecords ?? 200) - 1);
        foreach (var job in listed.Take(limit))
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, JobRecord(context, "alta.job", job));
        }

        WriteSummary(context, "alta.job.summary", Math.Min(listed.Count, limit), truncated: listed.Count > limit);
        return AltaExitCodes.Success;
    }

    private static int HandleJobStatus(AltaCommandContext context, string? id)
    {
        if (!TryGetJobs(context, out var jobs))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } jobId)
        {
            return UsageError(context, "usage.missingJob", "A job id is required.", "alta job status");
        }

        // The end of what it wrote says where it is: one read, so that the state and the line are of the same moment.
        if (jobs.ReadOutput(jobId, 4096) is not { } read)
        {
            return JobNotFound(context, jobId);
        }

        var record = JobRecord(context, "alta.job.status", read.Job);
        if (LastJobLine(read.Text) is { } last) record["lastLine"] = last;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static int HandleJobOutput(AltaCommandContext context, string? id, string? lines)
    {
        const string path = "alta job output";
        if (!TryGetJobs(context, out var jobs))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } jobId)
        {
            return UsageError(context, "usage.missingJob", "A job id is required.", path);
        }

        var count = 0;
        if (NormalizeOptionalText(lines) is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1))
        {
            return UsageError(context, "usage.invalidLines", "The number of lines must be a number, starting at 1.", path);
        }

        if (jobs.ReadOutput(jobId) is not { } read)
        {
            return JobNotFound(context, jobId);
        }

        var all = read.Text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var asked = count > 0 && count < all.Length ? all[^count..] : all;
        var shown = read.Text.Length == 0 ? string.Empty : TerminalTail(context, asked, out _, out var cut);
        var kept = shown.Length == 0 ? 0 : shown.Split('\n').Length;
        var record = JobRecord(context, "alta.job.output", read.Job, brief: true);
        record["lineCount"] = kept;
        record["truncated"] = read.Truncated || kept < all.Length && read.Text.Length > 0;
        record["text"] = shown;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleJobCancelAsync(AltaCommandContext context, string? id)
    {
        if (!TryGetJobs(context, out var jobs))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        if (NormalizeOptionalText(id) is not { } jobId)
        {
            return UsageError(context, "usage.missingJob", "A job id is required.", "alta job cancel");
        }

        if (jobs.Get(jobId) is not { } before)
        {
            return JobNotFound(context, jobId);
        }

        var after = await jobs.CancelAsync(jobId, byUser: false, context.CancellationToken).ConfigureAwait(false) ?? before;
        var record = JobRecord(context, "alta.job.cancelled", after, brief: true);
        record["wasRunning"] = before.State == SessionJobState.Running;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    private static int JobNotFound(AltaCommandContext context, string id)
        => NotFound(context, "job.notFound", $"Job '{id}' was not found: it never was, or it ended long ago. List the jobs with `alta job list`.");

    private static int JobFailure(AltaCommandContext context, string code, string message)
    {
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, AltaExitCodes.Failure, message);
        return AltaExitCodes.Failure;
    }

    private static string? LastJobLine(string text)
    {
        foreach (var line in text.Split('\n').Reverse())
        {
            // A line that was rewritten in place (a progress bar) is what it showed last.
            var shown = line.TrimEnd('\r');
            var rewritten = shown.LastIndexOf('\r');
            if (rewritten >= 0) shown = shown[(rewritten + 1)..];
            if (!string.IsNullOrWhiteSpace(shown)) return SessionJobService.OneLine(shown, 240);
        }

        return null;
    }

    // What a record says of a job; `brief` for the records that are about something else.
    private static Dictionary<string, object?> JobRecord(AltaCommandContext context, string type, SessionJob job, bool brief = false)
    {
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["id"] = job.Id,
            ["state"] = job.State switch
            {
                SessionJobState.Running => "running",
                SessionJobState.Succeeded => "succeeded",
                SessionJobState.Failed => "failed",
                SessionJobState.TimedOut => "timed_out",
                _ => "cancelled",
            },
        };
        Add("exitCode", job.ExitCode);
        Add("title", job.Title);
        if (brief) return record;
        record["command"] = job.Command;
        record["folder"] = job.Folder;
        record["sessionId"] = job.SessionId;
        Add("projectId", job.ProjectId);
        record["notify"] = job.Notification switch
        {
            SessionJobNotification.Always => "always",
            SessionJobNotification.Never => "never",
            _ => "success",
        };
        if (job.Timeout is { } timeout) record["timeoutSeconds"] = (long)timeout.TotalSeconds;
        Add("processId", job.ProcessId);
        record["startedAt"] = job.StartedAt;
        Add("endedAt", job.EndedAt);
        record["elapsedSeconds"] = Math.Max(0, (int)((job.EndedAt ?? DateTimeOffset.UtcNow) - job.StartedAt).TotalSeconds);
        record["outputCharacters"] = job.OutputCharacters;
        Add("resultPrompt", job.Delivery);
        return record;

        void Add(string name, object? value)
        {
            if (value is not null) record.Add(name, value);
        }
    }

    private sealed class JobStartOptions
    {
        public string? Command { get; set; }
        public bool UseStdin { get; set; }
        public string? Title { get; set; }
        public string? Folder { get; set; }
        public string? Notify { get; set; }
        public string? Timeout { get; set; }
        public string? SessionId { get; set; }
    }
}

/// <summary>What a host lets its sessions do with background jobs. A host that registers none lets them start commands.</summary>
/// <param name="AcceptsCommands">
/// Whether a session may start a command in the background. A host that has the user review the commands of its
/// sessions does not let them: a command started this way is one nobody reviewed.
/// </param>
public sealed record AltaJobPolicy(bool AcceptsCommands)
{
    /// <summary>
    /// Gets or initializes what decides for one session, by its identifier, in a host whose sessions do not all
    /// have the same policy. Null, the default, leaves <see cref="AcceptsCommands"/> to decide for every session.
    /// </summary>
    public Func<string, bool>? AcceptsCommandsOf { get; init; }

    /// <summary>Gets whether a session may start a command in the background.</summary>
    /// <param name="sessionId">The session that acts, or null when no session does.</param>
    /// <returns>False when the commands of that session are reviewed.</returns>
    public bool Accepts(string? sessionId)
        => AcceptsCommands && (sessionId is null || AcceptsCommandsOf?.Invoke(sessionId) != false);
}
