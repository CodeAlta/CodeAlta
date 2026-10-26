using System.Collections.Frozen;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.LiveTool;

/// <summary>
/// What a sandboxed session may do with <c>alta</c>. Its commands run in CodeAlta, with the rights of the user, outside
/// its sandbox: it runs only the commands listed here, which read what it may read, or act on itself and on the
/// sessions it created, and every other command is refused, those of plugins included.
/// </summary>
internal static class AltaSandboxGate
{
    // Paths of commands under `alta`; a path allows the commands under it. What is not here is refused: a command that
    // starts a program (terminals, plugin builds, automations, MCP servers), changes what runs outside the sandbox
    // (prompts, spaces, projects, settings), drives the window, or uses the accounts of the user to change something.
    private static readonly FrozenSet<string> Allowed = new[]
    {
        "version", "ask", "notes", "note", "skill", "skills", "skills_activate", "tool", "provider", "model", "documentation",
        "task", "plan", "review", "reminder", "job", "diff", "editor", "search",
        "prompt list", "prompt show", "plugin list", "plugin status", "plugin api",
        "project list", "project show", "project resolve", "project current", "space list", "space show", "space current",
        "issue trackers", "issue list", "issue filters", "issue views", "issue show",
        "issues trackers", "issues list", "issues filters", "issues views", "issues show",
        "mcp activate", "mcp list", "mcp status", "mcp tool", "mcp skill",
        "session current", "session create", "session fork", "session send", "session queue", "session steer", "session request",
        "session message", "session tail", "session events", "session result", "session info", "session status", "session choices",
        "session abort", "session compact", "session title", "session metrics",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the sandbox of a session, or null when it is not sandboxed.</summary>
    public static async Task<AltaSessionSandbox?> OfAsync(AltaCommandContext context, string sessionId)
    {
        if (context.Services.Get<AltaSandboxedSessions>() is { } sessions) return await sessions.Of(sessionId, context.CancellationToken).ConfigureAwait(false);
        if (context.Services.Get<SessionRuntimeService>() is not { } runtime) return null;
        var sandbox = await runtime.GetSessionSandboxAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
        return sandbox.IsSandboxed ? new AltaSessionSandbox(sandbox.Profile?.Name ?? sandbox.Choice.Profile ?? "?", sandbox.Profile?.McpServers ?? []) : null;
    }

    /// <summary>Gets the sandbox of the calling session, or null when it is not sandboxed or no session calls.</summary>
    public static async Task<AltaSessionSandbox?> OfCallerAsync(AltaCommandContext context)
        => context.Caller.SourceSessionId?.Trim() is { Length: > 0 } callerId ? await OfAsync(context, callerId).ConfigureAwait(false) : null;

    /// <summary>Gets the folder the calling session works in, as CodeAlta knows it: its worktree, else its working directory; null when unknown.</summary>
    public static async Task<string?> FolderOfCallerAsync(AltaCommandContext context)
    {
        if (context.Caller.SourceSessionId?.Trim() is not { Length: > 0 } callerId || context.Services.Get<SessionRuntimeService>() is not { } runtime) return null;
        // A session that calls runs: it is attached.
        var session = await runtime.TryGetActiveSessionDescriptorAsync(callerId, context.CancellationToken).ConfigureAwait(false);
        return session is null ? null : session.WorktreeDirectory ?? session.WorkingDirectory;
    }

    /// <summary>Gets whether a sandboxed session may run a command.</summary>
    /// <param name="commandPath">The path of the command, as the parser resolves it (<c>alta session send</c>).</param>
    public static bool Allows(string commandPath)
    {
        var words = commandPath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The path starts with the name of the application.
        for (var count = words.Length; count > 1; count--)
        {
            if (Allowed.Contains(string.Join(' ', words, 1, count - 1))) return true;
        }

        return false;
    }

    /// <summary>The refusal of a command to a sandboxed session.</summary>
    public static string Refusal(AltaSessionSandbox sandbox, string what)
        => $"This session is sandboxed (profile '{sandbox.Profile}'): it cannot {what}, which would run outside its sandbox. Do the work with the tools of the session, or ask the user.";
}
