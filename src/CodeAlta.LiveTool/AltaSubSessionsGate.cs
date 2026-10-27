using System.Collections.Frozen;
using CodeAlta.Orchestration.Runtime;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

/// <summary>
/// What a session without sub-sessions (<see cref="CodeAlta.Agent.AgentSessionOptions.SubSessions"/> off) may not do with
/// <c>alta</c>: start a session. The commands that start one are hidden from its help and from the declarations of its
/// scripts, and refused when it calls them anyway.
/// </summary>
internal static class AltaSubSessionsGate
{
    // Paths of commands under `alta` that start a session now or later: a child, a fork, the session of a script or of an
    // automation, and an automation, which starts sessions by itself.
    private static readonly FrozenSet<string> Gated = new[]
    {
        "session create", "session fork", "script run", "automation run", "automation create",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the context with the options of the calling session: a caller whose session has no sub-sessions says so.</summary>
    /// <param name="context">The context of the call.</param>
    /// <returns>The context, with <see cref="AltaCallerIdentity.SubSessions"/> off when the calling session has no sub-sessions.</returns>
    public static async Task<AltaCommandContext> ResolveAsync(AltaCommandContext context)
    {
        if (!context.Caller.SubSessions || context.Caller.SourceSessionId?.Trim() is not { Length: > 0 } callerId
            || context.Services.Get<SessionRuntimeService>() is not { } runtime)
            return context;
        var options = await runtime.GetSessionOptionsAsync(callerId, context.CancellationToken).ConfigureAwait(false);
        return options.SubSessions ? context : context with { Caller = context.Caller with { SubSessions = false } };
    }

    /// <summary>Gets whether a command starts a session.</summary>
    /// <param name="commandPath">The path of the command, as the parser resolves it (<c>alta session create</c>).</param>
    public static bool Gates(string commandPath)
    {
        var words = commandPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The path starts with the name of the application.
        return words.Length > 2 && Gated.Contains(string.Join(' ', words, 1, 2));
    }

    /// <summary>Creates a command that starts a session: hidden from a caller without sub-sessions.</summary>
    /// <param name="context">The context of the call.</param>
    /// <param name="name">The name of the command.</param>
    /// <param name="description">Its description.</param>
    /// <returns>The command.</returns>
    public static Command Leaf(AltaCommandContext context, string name, string description)
    {
        var command = new Command(name, description) { Hidden = !context.Caller.SubSessions };
        command.Add(new CommandUsage());
        command.Add(new HelpOption());
        return command;
    }

    /// <summary>The refusal of a command that starts a session to a caller without sub-sessions.</summary>
    /// <param name="commandPath">The path of the command.</param>
    public static string Refusal(string commandPath)
        => $"`{commandPath}` starts a session. {SessionRuntimeService.SubSessionsOffMessage}";
}
