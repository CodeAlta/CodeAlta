using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime.Sandbox;

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
        => FailureNote(profile, result, refusedBefore, refusedAfter, OperatingSystem.IsWindows());

    /// <summary>The note after a command that failed in the sandbox, for the given platform.</summary>
    internal static string? FailureNote(ResolvedSandboxProfile profile, AgentToolResult result, IReadOnlyList<string> refusedBefore, IReadOnlyList<string> refusedAfter, bool windows)
    {
        var then = profile.Outside == SandboxOutside.Ask ? " Ask the user, or run the command again with `outside_sandbox`." : " Ask the user to run it.";
        var refused = (refusedAfter.Count > refusedBefore.Count ? refusedAfter.Skip(refusedBefore.Count) : refusedAfter.Except(refusedBefore, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        if (refused.Length > 0)
            return $"[sandbox: {profile.Name}] The network of the sandbox refused {string.Join(", ", refused)}: it reaches only the hosts of its profile.{then}";
        var output = string.Concat(result.Items.OfType<AgentToolResultItem.Text>().Select(static item => item.Value)) + result.Error;
        if (windows)
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
    public static (string Text, string Noted) Note(SessionSandbox sandbox, string? scratchpad) => Note(sandbox, scratchpad, OperatingSystem.IsWindows());

    /// <summary>What the agent is told of its sandbox on the given platform.</summary>
    internal static (string Text, string Noted) Note(SessionSandbox sandbox, string? scratchpad, bool windows)
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
            text = $"<codealta-context>Sandbox: on, profile `{profile.Name}`. Commands and file tools run in a container: they write in the working directory, the folders of the project{(scratchpad is null ? string.Empty : $", the scratchpad folder `{scratchpad}` (use it instead of the scratchpad file named above)")} and temp; they read the system and the developer tools, not the home folder; they {network}. `.git/config`, git hooks and the configuration files of CodeAlta are read-only.{outside}{(windows ? " Give `dotnet build`, `dotnet test` and `msbuild` the option `-m:1`: MSBuild cannot start its worker nodes in this sandbox." : string.Empty)} `alta` reaches only this session and the sessions it creates.</codealta-context>";
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 4).ToLowerInvariant();
        return (text, $"{sandbox.Choice.Profile}#{hash}");
    }
}
