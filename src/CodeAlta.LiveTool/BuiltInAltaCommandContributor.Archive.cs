using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

internal sealed partial class BuiltInAltaCommandContributor
{
    private static Command CreateSessionArchiveCommand(AltaCommandContext context, bool archive)
    {
        string? sessionId = null;
        var withChildren = false;
        var command = Leaf(archive ? "archive" : "restore", archive
            ? "Archive a session: it leaves the lists of the window and keeps its history, its project and its space."
            : "Take an archived session out of the archive: it is listed again where it was.");
        command.Add("<session-id>", "CodeAlta session id.", value => sessionId = value);
        command.Add("with-children", archive ? "Archive the sessions below it too." : "Restore the archived sessions below it too.",
            value => withChildren = value is not null);
        command.Add(async (_, _) => await HandleSessionArchiveAsync(context, sessionId, archive, withChildren).ConfigureAwait(false));
        AddHelpText(command, archive
            ? [
                "The session and the sessions below it must be idle: no run, no queued prompt,",
                "no question or permission waiting, no background work. An agent archives",
                "only sessions of its own space. `alta session list --archived` lists them.",
                "Examples:",
                "  `alta session archive <session-id>`",
                "  `alta session archive <session-id> --with-children`",
            ]
            : [
                "An agent restores only sessions of its own space.",
                "Examples:",
                "  `alta session restore <session-id>`",
                "  `alta session restore <session-id> --with-children`",
            ]);
        return command;
    }

    private static async ValueTask<int> HandleSessionArchiveAsync(AltaCommandContext context, string? sessionId, bool archive, bool withChildren)
    {
        var commandPath = archive ? "alta session archive" : "alta session restore";
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return UsageError(context, "usage.missingSession", "Session id is required.", commandPath);
        }

        sessionId = sessionId.Trim();
        context.ClaimSessionWork(sessionId);
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime))
        {
            return AltaExitCodes.ServiceUnavailable;
        }

        // The session that calls runs: it is refused as any session that runs.
        if (archive && IsTargetingCallingAgentSession(context, sessionId))
        {
            return ArchiveRefused(context, "session_in_use", sessionId, sessionId, archive);
        }

        var info = await ResolveSessionInfoAsync(context, sessionId, includeChildren: withChildren).ConfigureAwait(false);
        if (info.ExitCode != AltaExitCodes.Success)
        {
            return info.ExitCode;
        }

        var session = info.Info!;
        var verb = archive ? "archives" : "restores";
        IEnumerable<AltaSessionInfo> members = withChildren ? [session, .. GetChildren(info.Infos, session, recursive: true)] : [session];
        foreach (var member in members)
        {
            if (await RefuseSessionOfOtherSpaceAsync(context, member.Session.SessionId, verb).ConfigureAwait(false) is { } otherSpace)
            {
                return otherSpace;
            }
        }

        var result = context.Services.Get<ISessionArchive>() is { } owner
            ? await owner.SetSessionArchivedAsync(session.Session.SessionId, archive, withChildren).WaitAsync(context.CancellationToken).ConfigureAwait(false)
            : await runtime.SetSessionArchivedAsync(session.Session.SessionId, archive, withChildren, context.CancellationToken).ConfigureAwait(false);
        if (result.Status != "ok")
        {
            return ArchiveRefused(context, result.Status, session.Session.SessionId, result.BlockingSessionId ?? session.Session.SessionId, archive);
        }

        // The window reads its lists again: the session leaves them, or comes back.
        if (result.Changed.Count != 0) NotifySpacesChanged(context);
        AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = archive ? "alta.session.archived" : "alta.session.restored",
            version = 1,
            correlationId = context.CorrelationId,
            sessionId = session.Session.SessionId,
            withChildren,
            changed = result.Changed,
            by = CreateProvenance(context),
        });
        return AltaExitCodes.Success;
    }

    private static int ArchiveRefused(AltaCommandContext context, string status, string sessionId, string blockingId, bool archive)
    {
        var (exitCode, code, message) = DescribeArchiveRefusal(status, sessionId, blockingId, archive);
        AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, exitCode, message);
        return exitCode;
    }

    // Why a session was not archived or restored, as the error of the command.
    internal static (int ExitCode, string Code, string Message) DescribeArchiveRefusal(string status, string sessionId, string blockingId, bool archive)
    {
        var done = archive ? "archived" : "restored";
        var which = string.Equals(blockingId, sessionId, StringComparison.OrdinalIgnoreCase) ? $"Session '{sessionId}'" : $"Session '{blockingId}', below '{sessionId}',";
        return status switch
        {
            "session_missing" => (AltaExitCodes.NotFound, "session.notFound", $"Session '{sessionId}' was not found."),
            "session_in_use" => (AltaExitCodes.Unsupported, "session.running",
                $"{which} is at work: a session is archived when it and the sessions below it are idle, and not by itself. Wait for its run to end, or stop it with `alta session abort {blockingId}`, then archive."),
            "queue_pending" => (AltaExitCodes.Unsupported, "session.queuePending",
                $"{which} has queued prompts: wait until they are sent, or remove them, then archive."),
            "interaction_pending" => (AltaExitCodes.Unsupported, "session.interactionPending",
                $"{which} waits for an answer to a question or to a permission request: answer it, then archive."),
            "busy" => (AltaExitCodes.Unsupported, "session.busy",
                "Another change of sessions (a deletion, a move, an archive) is under way: run the command again once it has ended."),
            "closed" => (AltaExitCodes.ServiceUnavailable, "service.closed", "CodeAlta is closing: nothing was done."),
            "io_failed" => (AltaExitCodes.Failure, "session.ioFailed", $"The journal of session '{blockingId}' could not be read or written: it was not {done}."),
            "archive_partial" => (AltaExitCodes.Failure, "session.archivePartial",
                $"Only some sessions were {done}: session '{blockingId}' was not. Check them with `alta session list --archived`, then run the command again."),
            _ => (AltaExitCodes.Failure, "session.archiveUnconfirmed",
                $"Session '{sessionId}' may not have been {done}: check it with `alta session info {sessionId}` before trying again."),
        };
    }
}
