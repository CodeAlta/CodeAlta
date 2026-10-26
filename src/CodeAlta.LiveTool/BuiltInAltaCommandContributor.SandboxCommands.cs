using CodeAlta.Orchestration.Runtime;
using CodeAlta.Sandbox;
using XenoAtom.CommandLine;

namespace CodeAlta.LiveTool;

// The `alta sandbox` commands: what the sandbox profiles allow, and the sandbox of a session. They only read: the
// sandbox of a session is chosen by `alta session create` and `alta session send --sandbox`, the profiles by the user.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static Command CreateSandboxCommand(AltaCommandContext context)
    {
        var group = Group("sandbox", "Read the sandbox profiles, what each one allows, and the sandbox of a session.");
        group.Add(CreateSandboxListCommand(context));
        group.Add(CreateSandboxShowCommand(context));
        group.Add(CreateSandboxStatusCommand(context));
        AddHelpText(
            group,
            "A sandboxed session runs its commands and file tools in a container built from its profile. Every sandbox writes the working directory, the folders of the project, the scratchpad and a temporary folder; it reads the system and the developer tools; it never reads the home folder nor the files of CodeAlta.",
            "A profile adds to that: folders it reads or writes, the network (`off`, the `hosts` it lists, or `on`), ports of this computer, variables of the user, MCP servers, and whether a command may ask the user to run outside (`outside`). Built in: `workspace` reaches the package registries and may ask to run outside; `strict` has no network and never runs outside.",
            "These commands only read. A session gets a sandbox with `alta session create --sandbox <profile>` or `alta session send <id> --sandbox <profile>`; an agent turns a sandbox on, never off, and a sandboxed agent chooses none. The user defines the profiles in Settings > Sandbox. A sandboxed session sees its own profile alone.",
            "Examples: `alta sandbox list`; `alta sandbox show strict`; `alta sandbox status`.");
        return group;
    }

    private static Command CreateSandboxListCommand(AltaCommandContext context)
    {
        var command = Leaf("list", "List the sandbox profiles: their network, hosts, folders, and whether a command may ask to run outside.");
        command.Add(async (_, _) => await HandleSandboxListAsync(context).ConfigureAwait(false));
        AddHelpText(command,
            "`builtIn` marks `workspace` and `strict`; `changed` says that the user changed one. `extends` is the profile a profile of the user builds on. `current` marks the profile of the calling session.",
            "`problem` says why a profile cannot be used: a session with it runs no command, it never runs without its sandbox.",
            "`allSessions`, in the summary, is the profile the user gives every session, when there is one.",
            "Example: `alta sandbox list`.");
        return command;
    }

    private static Command CreateSandboxShowCommand(AltaCommandContext context)
    {
        string? name = null;
        var command = Leaf("show", "Show what a sandbox profile allows, with what it takes from the profile it extends.");
        command.Add("<profile>?", "The profile. Without it, the one of the calling session.", value => name = value);
        command.Add(async (_, _) => await HandleSandboxShowAsync(context, name).ConfigureAwait(false));
        AddHelpText(command,
            "`write` and `read` are the folders the profile adds to every sandbox (`~` is the home folder); `deny` are never read, even inside them.",
            "`network` is `off`, `hosts` or `on`. `hosts` are reached with `hosts`, through a proxy of CodeAlta (`*.example.org` for its subdomains); `on` reaches the internet, never this computer nor its network. `localPorts` are ports of this computer it reaches.",
            "`env` names the variables of the user it receives (their values are not shown); `mcpServers` the MCP servers its sessions may use; `outside` is `ask` when a command may ask the user to run outside the sandbox, `never` otherwise.",
            "Examples: `alta sandbox show workspace`; `alta sandbox show` (the profile of this session).");
        return command;
    }

    private static Command CreateSandboxStatusCommand(AltaCommandContext context)
    {
        string? sessionId = null;
        var command = Leaf("status", "Show the sandbox of a session: its profile, where it comes from, whether it runs on this computer, and the hosts it refused last.");
        command.Add("session=", "The session. Without it, the calling session; without either, only whether a sandbox runs on this computer.", value => sessionId = value);
        command.Add(async (_, _) => await HandleSandboxStatusAsync(context, sessionId).ConfigureAwait(false));
        AddHelpText(command,
            "`source` is the scope that decided the sandbox: `session` (its own choice), `model`, `provider`, `project`, `space`, `all_sessions`, or `none`.",
            "`problem` says why its profile cannot be used, and `canRun` false with `reason` why the sandbox cannot run it here: a sandboxed session then runs no command.",
            "`refusedHosts` are the hosts its proxy refused last, the newest last: a host a command needs and its profile does not list.",
            "A sandboxed session reaches only itself and the sessions it created that run in its sandbox.",
            "Examples: `alta sandbox status`; `alta sandbox status --session <id>`.");
        return command;
    }

    private static async ValueTask<int> HandleSandboxListAsync(AltaCommandContext context)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)) return AltaExitCodes.ServiceUnavailable;
        var (allSessions, profiles) = runtime.ReadSandboxSettings();
        var own = await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false);
        var current = own?.Profile ?? await CallerProfileAsync(context, runtime).ConfigureAwait(false);
        var count = 0;
        foreach (var name in SandboxProfiles.Names(profiles))
        {
            // A sandboxed session sees its own profile alone: the others name folders and hosts of the user.
            if (own is not null && !string.Equals(name, own.Profile, StringComparison.OrdinalIgnoreCase)) continue;
            AltaJsonlWriter.WriteRecord(context.Stdout, SandboxProfileRecord(context, "alta.sandbox.profile", name, profiles, current, detailed: false));
            count++;
        }

        AltaJsonlWriter.WriteRecord(context.Stdout, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.sandbox.summary",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["count"] = count,
            ["allSessions"] = own is null ? NormalizeOptionalText(allSessions) : null,
        });
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSandboxShowAsync(AltaCommandContext context, string? name)
    {
        const string CommandPath = "alta sandbox show";
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)) return AltaExitCodes.ServiceUnavailable;
        var (_, profiles) = runtime.ReadSandboxSettings();
        var own = await AltaSandboxGate.OfCallerAsync(context).ConfigureAwait(false);
        var current = own?.Profile ?? await CallerProfileAsync(context, runtime).ConfigureAwait(false);
        if (NormalizeOptionalText(name) is not { } wanted)
        {
            if (current is null) return UsageError(context, "usage.missingProfile", "Name a profile: the calling session is not sandboxed. List them with `alta sandbox list`.", CommandPath);
            wanted = current;
        }

        var names = SandboxProfiles.Names(profiles);
        if (names.FirstOrDefault(candidate => candidate.Equals(wanted, StringComparison.OrdinalIgnoreCase)) is not { } found)
            return NotFound(context, "sandbox.profileNotFound", $"There is no sandbox profile '{wanted}': {string.Join(", ", names)}.");
        if (own is not null && !found.Equals(own.Profile, StringComparison.OrdinalIgnoreCase))
            return PermissionDenied(context, "sandbox.profileDenied", AltaSandboxGate.Refusal(own, $"read the profile '{found}'"));
        AltaJsonlWriter.WriteRecord(context.Stdout, SandboxProfileRecord(context, "alta.sandbox.profile", found, profiles, current, detailed: true));
        return AltaExitCodes.Success;
    }

    private static async ValueTask<int> HandleSandboxStatusAsync(AltaCommandContext context, string? sessionId)
    {
        if (!context.TryGetRequired<SessionRuntimeService>(nameof(SessionRuntimeService), out var runtime)) return AltaExitCodes.ServiceUnavailable;
        var availability = runtime.GetSandboxAvailability();
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "alta.sandbox.status",
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
        };
        var target = NormalizeOptionalText(sessionId) ?? NormalizeOptionalText(context.Caller.SourceSessionId);
        if (target is not null)
        {
            // The rules of the other session commands: a sandboxed caller reaches itself and the sessions it created in its sandbox.
            var infoResult = await ResolveSessionInfoAsync(context, target).ConfigureAwait(false);
            if (infoResult.ExitCode != AltaExitCodes.Success) return infoResult.ExitCode;
            var id = infoResult.Info!.Session.SessionId;
            var sandbox = await runtime.GetSessionSandboxAsync(id, context.CancellationToken).ConfigureAwait(false);
            record["sessionId"] = id;
            record["sandboxed"] = sandbox.IsSandboxed;
            record["profile"] = sandbox.Choice.Profile;
            record["source"] = SandboxSourceName(sandbox.Choice.Source);
            if (sandbox.Problem is { } problem) record["problem"] = problem;
            if (sandbox.ProjectSetsSandbox) record["projectSettingIgnored"] = "The `[sandbox]` table of the project's `.alta/config.toml` is ignored: a repository never changes its own sandbox.";
            if (sandbox.Profile is { } profile)
            {
                record["network"] = NetworkName(profile.Network);
                record["outside"] = OutsideName(profile.Outside);
                var refusal = availability.Refusal(profile.Network);
                record["canRun"] = refusal is null;
                if (refusal is not null) record["reason"] = refusal;
                record["refusedHosts"] = runtime.GetSandboxRefusals(id);
            }
        }

        record["available"] = availability.IsAvailable;
        record["backend"] = availability.Backend;
        record["networks"] = availability.Networks.Order().Select(NetworkName).ToArray();
        if (availability.Reason is { } reason) record["availabilityReason"] = reason;
        if (availability.Setup is { } setup) record["setup"] = setup;
        AltaJsonlWriter.WriteRecord(context.Stdout, record);
        return AltaExitCodes.Success;
    }

    // The profile of the calling session, or null when no session calls or it is not sandboxed.
    private static async Task<string?> CallerProfileAsync(AltaCommandContext context, SessionRuntimeService runtime)
        => NormalizeOptionalText(context.Caller.SourceSessionId) is { } callerId
            ? (await runtime.GetSessionSandboxAsync(callerId, context.CancellationToken).ConfigureAwait(false)).Choice.Profile
            : null;

    private static Dictionary<string, object?> SandboxProfileRecord(AltaCommandContext context, string type, string name, IReadOnlyList<SandboxProfile> profiles, string? current,
        bool detailed)
    {
        var written = profiles.LastOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var builtIn = SandboxProfiles.BuiltIn.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["version"] = 1,
            ["correlationId"] = context.CorrelationId,
            ["name"] = name,
            ["builtIn"] = builtIn,
        };
        if (builtIn && written is not null) record["changed"] = true;
        if (NormalizeOptionalText(written?.Extends) is { } extends) record["extends"] = extends;
        if (current is not null) record["current"] = name.Equals(current, StringComparison.OrdinalIgnoreCase);
        ResolvedSandboxProfile resolved;
        try
        {
            resolved = SandboxProfiles.Resolve(name, profiles);
        }
        catch (SandboxProfileException exception)
        {
            record["problem"] = exception.Message;
            return record;
        }

        record["network"] = NetworkName(resolved.Network);
        record["outside"] = OutsideName(resolved.Outside);
        if (!detailed)
        {
            record["hostCount"] = resolved.Network == SandboxNetwork.Hosts ? resolved.Hosts.Count : 0;
            record["writeCount"] = resolved.Write.Count;
            record["readCount"] = resolved.Read.Count;
            record["mcpServerCount"] = resolved.McpServers.Count;
            return record;
        }

        // The hosts apply to the network `hosts` alone: `off` reaches none, `on` every host of the internet.
        record["hosts"] = resolved.Network == SandboxNetwork.Hosts ? resolved.Hosts : Array.Empty<string>();
        record["localPorts"] = resolved.Network == SandboxNetwork.Off ? Array.Empty<int>() : resolved.LocalPorts;
        record["write"] = resolved.Write;
        record["read"] = resolved.Read;
        record["deny"] = resolved.Deny;
        record["env"] = resolved.Env;
        record["mcpServers"] = resolved.McpServers;
        return record;
    }

    private static string NetworkName(SandboxNetwork network) => network switch
    {
        SandboxNetwork.Off => "off",
        SandboxNetwork.Hosts => "hosts",
        _ => "on",
    };

    private static string OutsideName(SandboxOutside outside) => outside == SandboxOutside.Ask ? "ask" : "never";
}
