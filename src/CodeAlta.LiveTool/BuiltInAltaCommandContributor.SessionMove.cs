using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

internal sealed partial class BuiltInAltaCommandContributor
{
    private const string SessionMoveCommandPath = "alta session move";

    private static Command CreateSessionMoveCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        string? machine = null;
        var clone = false;
        var list = false;
        var command = Leaf("move", "Move a session to another machine paired with this one, with its conversation and its work in progress.");
        command.Add("<session-id>?", "CodeAlta session id. Defaults to the calling agent session when omitted.", value => sessionId = value);
        command.Add("machine=", "The machine: its name or its id, as `--list` shows them; this computer's name brings a session of a machine here.",
            value => machine = value);
        command.Add("clone", "Let the machine clone the repository of the session when it has no project of it.", value => clone = value is not null);
        command.Add("list", "List the machines a session can move to.", value => list = value is not null);
        command.Add(async (_, _) => await HandleSessionMoveAsync(context, FirstNonEmpty(sessionId, context.Caller.SourceSessionId), machine, clone, list, SessionMoveCommandPath)
            .ConfigureAwait(false));
        AddHelpText(
            command,
            "The session keeps its id. Where it ran it stays read-only; on the machine it works in a worktree of its own of",
            "the project of the same repository (found by its remote), on its branch, with the commits that are not pushed",
            "and the changes that are not committed. Files that look like secrets (.env, *.pem, *.key, id_*, *.tfvars) and",
            "ignored files stay behind; background jobs, reminders and terminals do not move. The machine must let this",
            "computer move sessions to it; a session comes to this computer only when it receives moved sessions",
            "(Settings > Fleet > Machines). A session moves while it is idle. The calling session moves when its turn ends:",
            "the answer is then `scheduled`, and its next prompt runs on the machine.",
            "Examples:",
            "  `alta session move --list`",
            "  `alta session move <session-id> --machine mac-mini`",
            "  `alta session move --machine build-server --clone`");
        return command;
    }

    private static async ValueTask<int> HandleSessionMoveAsync(AltaCommandContext context, string? sessionId, string? machine, bool clone, bool list, string commandPath)
    {
        if (!context.TryGetRequired<IAltaSessionMachineMoves>(nameof(IAltaSessionMachineMoves), out var moves)) return AltaExitCodes.ServiceUnavailable;
        if (list)
        {
            foreach (var value in moves.ListMachines())
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.session.move_machine",
                    version = 1,
                    correlationId = context.CorrelationId,
                    id = value.IsThisComputer ? null : value.Id,
                    name = value.Name,
                    thisComputer = value.IsThisComputer,
                    state = value.State,
                });
            }

            return AltaExitCodes.Success;
        }

        context.ClaimSessionWork(sessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
            return UsageError(context, "usage.missingSession", "Session id is required outside a session caller.", commandPath);
        if (NormalizeOptionalText(machine) is not { } target)
            return UsageError(context, "usage.missingMachine", "Name the machine with --machine <name> (`alta session move --list` lists them).", commandPath);
        sessionId = sessionId.Trim();
        if (await RefuseGuardedCallerAsync(context, "moves no session to another machine, itself included").ConfigureAwait(false) is { } guarded) return guarded;
        if (await RefuseMoveOfOtherAsync(context, sessionId).ConfigureAwait(false) is { } refused) return refused;

        // A session of this computer keeps to the space of the caller; one of a machine is checked there.
        if (context.Services.Get<SessionRuntimeService>() is { } runtime
            && await runtime.TryGetRecoverableSessionAsync(sessionId, context.CancellationToken).ConfigureAwait(false) is not null
            && await RefuseSessionOfOtherSpaceAsync(context, sessionId, "moves to another machine").ConfigureAwait(false) is { } otherSpace)
            return otherSpace;

        // The calling session moves when its turn ends: nothing moves in the middle of a turn.
        var result = IsCaller(context, sessionId)
            ? moves.Schedule(sessionId, target, clone)
            : await moves.MoveAsync(sessionId, target, clone, context.CancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case "finished" or "scheduled":
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.session.moved",
                    version = 1,
                    correlationId = context.CorrelationId,
                    sessionId,
                    status = result.Status,
                    machine = result.Machine,
                    workFolder = result.WorkFolder,
                    note = result.Status == "scheduled"
                        ? "The session moves when its current turn ends: end your turn. What comes next runs on the machine, in a worktree of its own."
                        : null,
                    warnings = result.Warnings,
                });
                return AltaExitCodes.Success;
            case "offered":
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, "session.moveUnconfirmed", AltaExitCodes.Failure,
                    $"The connection to {result.Machine ?? target} was lost after the session was frozen: the move goes on when the machine is back, and the session stays read-only here meanwhile. {result.Message}".TrimEnd());
                return AltaExitCodes.Failure;
            default:
                var (exitCode, code) = result.Code switch
                {
                    "machine_not_found" or "session_missing" => (AltaExitCodes.NotFound, "session.moveNotFound"),
                    "forbidden" or "receive_disabled" => (AltaExitCodes.PolicyDenied, "session.moveForbidden"),
                    "machine_offline" or "unreachable" => (AltaExitCodes.ServiceUnavailable, "session.moveUnreachable"),
                    _ => (AltaExitCodes.Unsupported, "session.moveRefused"),
                };
                AltaJsonlWriter.WriteError(context.Stderr, context.CorrelationId, code, exitCode,
                    $"Session '{sessionId}' did not move to {result.Machine ?? target} ({result.Code ?? result.Status}): {result.Message ?? "it stays where it was."}");
                return exitCode;
        }
    }
}
