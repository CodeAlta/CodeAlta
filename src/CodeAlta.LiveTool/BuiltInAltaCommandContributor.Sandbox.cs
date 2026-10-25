using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;

namespace CodeAlta.LiveTool;

internal sealed partial class BuiltInAltaCommandContributor
{
    // Checks the sandbox a command asks for a session: a new one (no target) or one that exists. The user chooses any;
    // an agent turns a sandbox on for a session that has none, and a sandboxed agent chooses none (the sessions it
    // creates have its own). Null when allowed, with the error written otherwise.
    private static async Task<int?> RefuseSandboxChoiceAsync(AltaCommandContext context, SessionRuntimeService runtime, string? asked, string? targetSessionId, string commandPath)
    {
        if (NormalizeOptionalText(asked) is not { } wanted) return null;
        var profiles = SandboxProfiles.Names(runtime.ReadSandboxSettings().Profiles);
        var scope = wanted.Equals(OwnedSessionCommandService.ScopeSandbox, StringComparison.OrdinalIgnoreCase);
        var off = wanted.Equals(SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase);
        if ((scope && targetSessionId is null) || (!scope && !off && !profiles.Contains(wanted, StringComparer.OrdinalIgnoreCase)))
        {
            return UsageError(context, "usage.invalidSandbox",
                $"--sandbox is `off`{(targetSessionId is null ? string.Empty : ", `scope`")} or a profile: {string.Join(", ", profiles)}.", commandPath);
        }

        if (NormalizeOptionalText(context.Caller.SourceSessionId) is not { } callerId) return null;
        if (await AltaSandboxGate.OfAsync(context, callerId).ConfigureAwait(false) is { } own)
        {
            return PermissionDenied(context, "sandbox.choiceDenied",
                $"This session is sandboxed (profile '{own.Profile}'): the sessions it creates have its sandbox, and it chooses no other. Leave --sandbox out.");
        }

        if (scope || off)
        {
            return PermissionDenied(context, "sandbox.choiceDenied",
                "An agent turns a sandbox on, never off: the user chooses where sessions run without one. Leave --sandbox out, or name a profile.");
        }

        if (targetSessionId is not null
            && await runtime.GetSessionSandboxAsync(targetSessionId, context.CancellationToken).ConfigureAwait(false) is { IsSandboxed: true } current
            && !string.Equals(current.Choice.Profile, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return PermissionDenied(context, "sandbox.choiceDenied",
                $"Session '{targetSessionId}' runs in the sandbox '{current.Choice.Profile}': an agent does not replace the sandbox of a session. Leave --sandbox out.");
        }

        return null;
    }

    // The sandbox of a session as the records of `alta session` show it: its profile (null when it is not sandboxed),
    // where that comes from, and what is wrong when the profile cannot be used.
    internal static object SandboxRecord(SessionSandbox sandbox) => new
    {
        profile = sandbox.Choice.Profile,
        source = SandboxSourceName(sandbox.Choice.Source),
        problem = sandbox.Problem,
    };

    private static string SandboxSourceName(SandboxScope scope) => scope switch
    {
        SandboxScope.Session => "session",
        SandboxScope.Model => "model",
        SandboxScope.Provider => "provider",
        SandboxScope.Project => "project",
        SandboxScope.Space => "space",
        SandboxScope.AllSessions => "all_sessions",
        _ => "none",
    };

    // The sandbox of a session, its source and the choices of `alta session send --sandbox`, for `alta session choices`.
    private static async Task<object> SandboxChoicesAsync(AltaCommandContext context, string sessionId)
    {
        if (context.Services.Get<SessionRuntimeService>() is not { } runtime) return new { current = (string?)null, source = "none", choices = new[] { SandboxProfiles.Off } };
        var sandbox = await runtime.GetSessionSandboxAsync(sessionId, context.CancellationToken).ConfigureAwait(false);
        return new
        {
            current = sandbox.Choice.Profile,
            source = SandboxSourceName(sandbox.Choice.Source),
            problem = sandbox.Problem,
            choices = (string[])[SandboxProfiles.Off, .. SandboxProfiles.Names(runtime.ReadSandboxSettings().Profiles)],
        };
    }
}
