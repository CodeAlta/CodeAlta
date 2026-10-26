using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime.Sandbox;

/// <summary>The system whose container runs a sandbox: what tools cannot do there differs.</summary>
internal enum SandboxPlatform
{
    /// <summary>Linux, with Bubblewrap.</summary>
    Linux,

    /// <summary>Windows, with its process container.</summary>
    Windows,

    /// <summary>macOS, with Seatbelt.</summary>
    MacOS,
}

/// <summary>The platform of this process.</summary>
internal static class SandboxPlatforms
{
    /// <summary>Gets the platform this process runs on.</summary>
    public static SandboxPlatform Current => OperatingSystem.IsWindows() ? SandboxPlatform.Windows : OperatingSystem.IsMacOS() ? SandboxPlatform.MacOS : SandboxPlatform.Linux;
}

/// <summary>
/// What a sandboxed session is told of its sandbox: the parameter of <c>shell_command</c> that asks to run a command
/// outside it, the note after a command it blocked, and the note of a run that says what it allows.
/// </summary>
internal static class SandboxOutsideCommands
{
    /// <summary>The parameter of <c>shell_command</c> that asks to run the command outside the sandbox, with the reason.</summary>
    public const string Parameter = "outside_sandbox";

    private static readonly string[] DenialMarkers =
    [
        "Access is denied", "Access to the path", "Permission denied", "EACCES", "EPERM", "Operation not permitted", "Read-only file system",
        "UnauthorizedAccessException",
    ];

    // What a program writes when the container keeps it from the network: a socket refused (Windows), no route or no
    // name resolution (Linux, macOS).
    private static readonly string[] NetworkMarkers =
    [
        "forbidden by its access permissions", "Network is unreachable", "ENETUNREACH", "Could not resolve host", "Temporary failure in name resolution",
        "No such host is known", "Name or service not known", "nodename nor servname", "getaddrinfo", "EAI_AGAIN", "ENOTFOUND", "NU1301",
    ];

    /// <summary>
    /// Adds a note after the output of a command, on a line of its own at the end of its text: the timelines show the
    /// first text of a result, and read the note there.
    /// </summary>
    public static AgentToolResult WithNote(AgentToolResult result, string note)
    {
        var index = result.Items.ToList().FindIndex(static item => item is AgentToolResultItem.Text { Value.Length: > 0 });
        if (index < 0) return result with { Items = [.. result.Items, new AgentToolResultItem.Text(note)] };
        var items = result.Items.ToArray();
        items[index] = new AgentToolResultItem.Text(((AgentToolResultItem.Text)items[index]).Value.TrimEnd() + "\n" + note);
        return result with { Items = items };
    }

    /// <summary>Gives the specification of <c>shell_command</c> the parameter that asks to run a command outside the sandbox.</summary>
    public static AgentToolSpec WithParameter(AgentToolSpec spec)
    {
        if (JsonNode.Parse(spec.InputSchema.GetRawText()) is not JsonObject schema || schema["properties"] is not JsonObject properties) return spec;
        properties[Parameter] = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Empty, or left out, for a command that runs in the sandbox of this session, as most do. Only when the command cannot work there (it needs a file, a credential or a host the sandbox keeps out): why it must run outside, in a sentence the user reads before allowing it. The user is asked every time.",
        };
        return spec with { InputSchema = JsonSerializer.SerializeToElement(schema) };
    }

    /// <summary>
    /// Takes the parameter out of a call, and says whether it asks to run the command outside: a reason that is not
    /// empty. A model whose functions have every parameter required (strict schemas) sends an empty one for a command
    /// that runs in the sandbox.
    /// </summary>
    /// <param name="invocation">The call.</param>
    /// <param name="inside">The call without the parameter.</param>
    /// <param name="reason">Why the command must run outside.</param>
    public static bool TryTake(AgentToolInvocation invocation, out AgentToolInvocation inside, out string reason)
    {
        inside = invocation;
        reason = string.Empty;
        if (invocation.Arguments.ValueKind != JsonValueKind.Object || !invocation.Arguments.TryGetProperty(Parameter, out var value)) return false;
        reason = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
        var arguments = new JsonObject();
        foreach (var property in invocation.Arguments.EnumerateObject())
        {
            if (property.Name != Parameter) arguments[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        inside = invocation with { Arguments = JsonSerializer.SerializeToElement(arguments) };
        return reason.Length > 0;
    }

    /// <summary>
    /// The note after a command that failed in the sandbox, when what blocked it can be told: a host its network
    /// refused, or a file it could not reach. Null otherwise.
    /// </summary>
    public static string? FailureNote(ResolvedSandboxProfile profile, AgentToolResult result, IReadOnlyList<string> refusedBefore, IReadOnlyList<string> refusedAfter)
        => FailureNote(profile, result, refusedBefore, refusedAfter, SandboxPlatforms.Current);

    /// <summary>The note after a command that failed in the sandbox, for the given platform.</summary>
    internal static string? FailureNote(ResolvedSandboxProfile profile, AgentToolResult result, IReadOnlyList<string> refusedBefore, IReadOnlyList<string> refusedAfter, SandboxPlatform platform)
    {
        var then = profile.Outside == SandboxOutside.Ask ? " Ask the user, or run the command again with `outside_sandbox`." : " Ask the user to run it.";
        var refused = (refusedAfter.Count > refusedBefore.Count ? refusedAfter.Skip(refusedBefore.Count) : refusedAfter.Except(refusedBefore, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        if (refused.Length > 0)
            return $"[sandbox: {profile.Name}] The network of the sandbox refused {string.Join(", ", refused)}: it reaches only the hosts of its profile.{then}";
        var output = string.Concat(result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value)) + result.Error;
        if (profile.Network != SandboxNetwork.On && NetworkMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            return profile.Network == SandboxNetwork.Off
                ? $"[sandbox: {profile.Name}] The sandbox of this session has no network: its profile reaches no host.{then}"
                : $"[sandbox: {profile.Name}] The sandbox of this session reaches only the hosts of its profile, through its proxy (`HTTPS_PROXY`): a program that does not use the proxy reaches no host.{then}";
        if (platform == SandboxPlatform.MacOS)
        {
            // Seatbelt lets no process of the sandbox listen on a port, and `/tmp`, where the test hosts put their pipes,
            // is not written: VSTest and Microsoft.Testing.Platform under `dotnet test` stop at their first socket.
            if (output.Contains("SocketException (13)", StringComparison.Ordinal)
                && (output.Contains("Test Run Aborted", StringComparison.Ordinal) || output.Contains("Process terminated.", StringComparison.Ordinal)))
                return $"[sandbox: {profile.Name}] On macOS, the processes of a sandbox cannot listen on a port nor create sockets in `/tmp`, as `dotnet test` needs. " +
                       $"A test project of Microsoft.Testing.Platform runs as a program: `dotnet build <tests> -m:1`, then `dotnet run --project <tests> --no-build`.{then}";
        }

        if (platform == SandboxPlatform.Windows)
        {
            // The Windows container refuses the named pipes of MSBuild's worker nodes (they admit the user alone, not
            // the container): the build fails without an error. One node, in the process of the build, works.
            if (output.Contains("Build FAILED.", StringComparison.Ordinal) && output.Contains(" 0 Error(s)", StringComparison.Ordinal))
                return $"[sandbox: {profile.Name}] MSBuild cannot start its worker nodes in the sandbox on Windows: run the command again with `-m:1`.";
            // Through the proxy, processes of the container cannot connect to each other on Windows before build .9550.
            if (profile.Network == SandboxNetwork.Hosts && output.Contains("failed to connect to testhost", StringComparison.OrdinalIgnoreCase))
                return $"[sandbox: {profile.Name}] On this version of Windows, processes of a sandbox that reaches hosts through its proxy cannot connect to each other, as VSTest needs.{then}";
        }

        return DenialMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? $"[sandbox: {profile.Name}] The command ran in the sandbox: it writes only in the folders of the session (its project, its worktree, its scratchpad and temp), and reads neither the secrets of this computer nor the configuration that runs programs (`.git/config`, hooks).{then}"
            : null;
    }

    /// <summary>What the agent is told of its sandbox, and the value that says it was told.</summary>
    /// <param name="sandbox">The sandbox of the session, as it applies now.</param>
    /// <param name="scratchpad">The scratchpad folder of the sandboxed session, or null.</param>
    /// <returns>The text of the note, and a short value that changes with it (<c>off</c> for a session that is not sandboxed).</returns>
    public static (string Text, string Noted) Note(SessionSandbox sandbox, string? scratchpad) => Note(sandbox, scratchpad, SandboxPlatforms.Current);

    /// <summary>What the agent is told of its sandbox on the given platform.</summary>
    internal static (string Text, string Noted) Note(SessionSandbox sandbox, string? scratchpad, SandboxPlatform platform)
    {
        if (!sandbox.IsSandboxed) return ("<codealta-context>Sandbox: off. Commands and file tools run on this computer, as the permissions of the session allow.</codealta-context>", "off");
        string text;
        if (sandbox.Profile is not { } profile)
        {
            text = $"<codealta-context>Sandbox: on, profile `{sandbox.Choice.Profile}`, which cannot be used: {sandbox.Problem} No command and no file tool runs until the user fixes it.</codealta-context>";
        }
        else
        {
            var network = profile.Network switch
            {
                SandboxNetwork.Off => "have no network",
                SandboxNetwork.On => "reach the internet, but not this computer nor its network",
                _ => profile.Hosts.Count == 0 ? "reach no host"
                    : $"reach {string.Join(", ", profile.Hosts.Take(4))}{(profile.Hosts.Count > 4 ? " and the other hosts of the profile" : string.Empty)}, and no other host",
            };
            var outside = profile.Outside == SandboxOutside.Ask
                ? $" A command that needs more can run outside with `{Parameter}: \"<why>\"` on `shell_command`: the user is asked each time."
                : " No command runs outside it: ask the user for what needs more.";
            var platformNote = platform switch
            {
                SandboxPlatform.Windows => " Give `dotnet build`, `dotnet test` and `msbuild` the option `-m:1`: MSBuild cannot start its worker nodes in this sandbox.",
                // The worker nodes of MSBuild put their pipes in `/tmp`: without `-m:1`, the build waits for them for minutes.
                SandboxPlatform.MacOS => " Give `dotnet restore`, `dotnet build` and `msbuild` the option `-m:1`, and run with `dotnet run --no-build` after such a build (`dotnet run` gives `-m:1` to the program): MSBuild cannot start its worker nodes in this sandbox, and waits for them for minutes." +
                                         " No process of this sandbox listens on a port, so `dotnet test` does not run here: a test project of Microsoft.Testing.Platform runs as a program (`dotnet run --project <tests> --no-build`).",
                _ => string.Empty,
            };
            text = $"<codealta-context>Sandbox: on, profile `{profile.Name}`. Commands and file tools run in a container: they write in the working directory, the folders of the project{(scratchpad is null ? string.Empty : $", the scratchpad folder `{scratchpad}` (use it instead of the scratchpad file named above)")} and temp; they read the system and the developer tools, not the home folder; they {network}. `.git/config`, git hooks and the configuration files of CodeAlta are read-only.{outside}{platformNote} `alta` reaches only this session and the sessions it creates.</codealta-context>";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 4).ToLowerInvariant();
        return (text, $"{sandbox.Choice.Profile}#{hash}");
    }
}
