using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode agents</c>.</summary>
internal sealed class NodeAgentCommandOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the spaces named with <c>--space</c>.</summary>
    public List<string> Spaces { get; } = [];

    /// <summary>Gets or sets the sandbox named with <c>--sandbox</c>: a profile, or <c>none</c>.</summary>
    public string? Sandbox { get; set; }

    /// <summary>Gets or sets whether <c>--bypass</c> (true) or <c>--no-bypass</c> (false) was given.</summary>
    public bool? Bypass { get; set; }

    /// <summary>Gets or sets whether <c>--all</c> was given.</summary>
    public bool All { get; set; }

    /// <summary>Gets the words: the subcommand.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// Whether agents of other machines may start sessions on the node of a profile and send prompts to its sessions, per space
/// (<c>doc/fleet/access-control.md</c>, "What the node decides"): <c>altanode agents</c> shows it, <c>allow</c> and <c>deny</c>
/// change it. Off in every space until the owner allows a space. A node that runs reads it again at each request; while one
/// listens, the change goes through its local control, which checks the spaces and the sandbox profile.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
internal sealed class NodeAgentCommands(TextWriter output, TextWriter error)
{
    /// <summary>Gets or sets what stops a call to the node, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    public async ValueTask<int> RunAsync(NodeAgentCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var command = options.Words is [var first] ? first : options.Words.Count == 0 ? "show" : null;
        if (command is not ("show" or "allow" or "deny")) return await UsageAsync("agents takes show, allow or deny.").ConfigureAwait(false);
        if (command == "show" && (options.Spaces.Count > 0 || options.Sandbox is not null || options.Bypass is not null || options.All))
            return await UsageAsync("agents show takes no option: allow and deny change the policy.").ConfigureAwait(false);
        if (command == "allow" && options.Spaces.Count == 0 && options.Sandbox is null && options.Bypass is null)
            return await UsageAsync("agents allow names a space (--space <id>), or changes --sandbox or --bypass.").ConfigureAwait(false);
        if (command == "deny" && (options.Spaces.Count == 0) == !options.All)
            return await UsageAsync("agents deny names the spaces (--space <id>), or --all.").ConfigureAwait(false);

        var control = NodeLocalControl.Read(profile);
        var store = new FleetAgentSessions(control.Catalog.FleetRoot);
        try
        {
            var policy = control.NodeListens ? await CallAsync(control, static (client, token) => client.GetAgentPolicyAsync(token)).ConfigureAwait(false) : store.ReadPolicy();
            if (policy is null) return NodeExitCodes.Failure;
            if (command != "show")
            {
                var spaces = command == "allow" ? policy.Spaces.Union(options.Spaces, StringComparer.OrdinalIgnoreCase)
                    : options.All ? [] : policy.Spaces.Except(options.Spaces, StringComparer.OrdinalIgnoreCase);
                var sandbox = options.Sandbox is null ? policy.Sandbox : options.Sandbox.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : options.Sandbox;
                var changed = new FleetAgentPolicyInfo([.. spaces], sandbox, options.Bypass ?? policy.AllowBypass);
                if (control.NodeListens)
                {
                    policy = await CallAsync(control, (client, token) => client.SetAgentPolicyAsync(changed, token)).ConfigureAwait(false);
                    if (policy is null) return NodeExitCodes.Failure;
                }
                else
                {
                    store.WritePolicy(changed);
                    policy = store.ReadPolicy();
                }
            }

            await WriteAsync(policy).ConfigureAwait(false);
            return NodeExitCodes.Success;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"altanode agents: the policy could not be written: {exception.Message}").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
    }

    private async Task WriteAsync(FleetAgentPolicyInfo policy)
    {
        if (policy.Spaces.Count == 0)
        {
            await output.WriteLineAsync("Agents of other machines may start no session here and send no prompt (altanode agents allow --space <id> allows a space).").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync($"Agents of other machines may start sessions here and send prompts to its sessions in: {string.Join(", ", policy.Spaces)}.").ConfigureAwait(false);
        await output.WriteLineAsync(policy.Sandbox is { } sandbox
            ? $"Their sessions run in the sandbox '{sandbox}' at least."
            : "Their sessions run in the sandbox of their parent, if it has one.").ConfigureAwait(false);
        await output.WriteLineAsync(policy.AllowBypass
            ? "Their sessions may bypass permissions when their parent does."
            : "Their sessions never bypass permissions: they ask before commands.").ConfigureAwait(false);
    }

    private async Task<T?> CallAsync<T>(NodeLocalControl control, Func<Fleet.Client.FleetNodeClient, CancellationToken, Task<T>> call) where T : class
    {
        var (client, identity) = await control.ConnectAsync(Stop).ConfigureAwait(false);
        using (identity)
        await using (client.ConfigureAwait(false))
        {
            try
            {
                return await call(client, Stop).ConfigureAwait(false);
            }
            catch (FleetException exception) when (exception.Error.Code is FleetNodeErrorCodes.Unavailable or FleetErrorCodes.InvalidParams)
            {
                await error.WriteLineAsync("altanode agents: " + exception.Error.Message).ConfigureAwait(false);
                return null;
            }
        }
    }

    private async ValueTask<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
