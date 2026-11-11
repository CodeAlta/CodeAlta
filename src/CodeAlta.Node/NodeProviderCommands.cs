using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode providers</c>.</summary>
internal sealed class NodeProviderCommandOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the words: the subcommand and its argument.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The providers of the running node, for its owner in a terminal of the machine (#414), as the page of the machine in CodeAlta
/// Desktop manages them: <c>altanode providers list</c>, <c>available</c> (what it can add), <c>add</c> (installs the program the
/// provider runs first), <c>update</c> and <c>sign-in</c>. They go through the local control of the node, so a change applies at once.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
/// <param name="input">Standard input: the code a sign-in page shows, typed back.</param>
internal sealed class NodeProviderCommands(TextWriter output, TextWriter error, TextReader input)
{
    /// <summary>Gets or sets what stops a call to the node, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    public async ValueTask<int> RunAsync(NodeProviderCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var (command, arguments) = options.Words is [var first, .. var rest] ? (first, rest) : ("list", []);
        if (command is not ("list" or "available" or "add" or "update" or "sign-in"))
            return await UsageAsync($"'{command}' is not a command of providers: list, available, add, update or sign-in.").ConfigureAwait(false);
        if (command is "list" or "available" ? arguments.Count != 0 : arguments.Count != 1)
            return await UsageAsync(command switch
            {
                "list" or "available" => $"providers {command} takes no argument.",
                "add" => "providers add takes what to add, as altanode providers available lists it: altanode providers add claude-code.",
                _ => $"providers {command} takes the key of a provider, as altanode providers list lists it.",
            }).ConfigureAwait(false);

        var control = NodeLocalControl.Read(profile);
        if (!control.NodeListens)
        {
            await error.WriteLineAsync("altanode providers: no node runs on this profile. Start it (altanode run, or its service), then try again.").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        var (client, identity) = await control.ConnectAsync(Stop).ConfigureAwait(false);
        using (identity)
        await using (client.ConfigureAwait(false))
        {
            try
            {
                return command switch
                {
                    "list" => await ListAsync(client).ConfigureAwait(false),
                    "available" => await AvailableAsync(client).ConfigureAwait(false),
                    "add" => await FollowAsync(client, await client.AddProviderAsync(new FleetProviderAddParams(arguments[0]), Stop).ConfigureAwait(false), "added").ConfigureAwait(false),
                    "update" => await FollowAsync(client, await client.UpdateProviderAsync(arguments[0], Stop).ConfigureAwait(false), "updated").ConfigureAwait(false),
                    _ => await SignInAsync(client, arguments[0]).ConfigureAwait(false),
                };
            }
            catch (FleetException exception) when (exception.Error.Code is not (FleetErrorCodes.ConnectionClosed or FleetErrorCodes.TransportError or FleetErrorCodes.InternalError))
            {
                await error.WriteLineAsync("altanode providers: " + exception.Error.Message).ConfigureAwait(false);
                return NodeExitCodes.Failure;
            }
        }
    }

    private async Task<int> ListAsync(Fleet.Client.FleetNodeClient client)
    {
        var list = await client.ListProvidersAsync(Stop).ConfigureAwait(false);
        if (list.ConfigurationError is { } problem) await output.WriteLineAsync($"warning: the configuration file does not read since it last changed: {problem}").ConfigureAwait(false);
        if (list.Providers.Count == 0)
        {
            await output.WriteLineAsync("This machine has no provider: altanode providers available lists what it can add.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        var keyWidth = Math.Min(32, list.Providers.Max(static provider => provider.Key.Length));
        await output.WriteLineAsync($"{"KEY".PadRight(keyWidth)}  {"TYPE",-16}  {"STATE",-10}  DETAIL").ConfigureAwait(false);
        foreach (var provider in list.Providers)
        {
            var detail = string.Join(" · ", new[] { Why(provider), Program(provider.Program) }.Where(static value => value is not null));
            await output.WriteLineAsync($"{Fit(provider.Key, keyWidth)}  {Fit(provider.Type, 16)}  {provider.State,-10}  {detail}".TrimEnd()).ConfigureAwait(false);
        }

        return NodeExitCodes.Success;
    }

    private async Task<int> AvailableAsync(Fleet.Client.FleetNodeClient client)
    {
        var list = await client.ListProviderOffersAsync(refresh: false, Stop).ConfigureAwait(false);
        if (list.Error is { } problem) await output.WriteLineAsync("warning: " + problem).ConfigureAwait(false);
        var idWidth = Math.Min(40, Math.Max(2, list.Offers.Max(static offer => offer.Id.Length)));
        await output.WriteLineAsync($"{"ADD".PadRight(idWidth)}  {"NAME",-24}  DETAIL").ConfigureAwait(false);
        foreach (var offer in list.Offers)
        {
            var detail = offer.Unavailable is { } unavailable ? "cannot run here: " + unavailable
                : string.Join(" · ", new[] { offer.ConfiguredKey is { } key ? $"added as {key}" : null, Program(offer.Program) }.Where(static value => value is not null));
            await output.WriteLineAsync($"{Fit(offer.Id, idWidth)}  {Fit(offer.Name, 24)}  {detail}".TrimEnd()).ConfigureAwait(false);
        }

        return NodeExitCodes.Success;
    }

    // An addition or an update: what the node says it does, until it ended.
    private async Task<int> FollowAsync(Fleet.Client.FleetNodeClient client, FleetProviderOperationState state, string done)
    {
        string? said = null;
        while (state.Status == "running")
        {
            var line = state.Message is { } message ? state is { Received: { } received, Total: > 0 and var total } ? $"{message} {received * 100 / total}%" : message : null;
            if (line is not null && line != said) await output.WriteLineAsync(said = line).ConfigureAwait(false);
            state = await client.WaitProviderOperationAsync(state.Id, 20, Stop).ConfigureAwait(false);
        }

        if (state.Status != "completed")
        {
            await error.WriteLineAsync("altanode providers: " + (state.Message ?? "it did not complete.")).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        await output.WriteLineAsync(state.Key is { } key ? $"The provider {key} was {done}." : state.Message ?? "Done.").ConfigureAwait(false);
        if (done == "added" && state.Key is { } added) await output.WriteLineAsync($"altanode providers sign-in {added} signs it in, when it needs it.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    // A sign-in from the terminal of the machine: a device code (Copilot, xAI), or the sign-in the node keeps (Claude Code, an ACP
    // agent's key): its page is printed, and the code or the key it asks for is read here.
    private async Task<int> SignInAsync(Fleet.Client.FleetNodeClient client, string key)
    {
        var provider = (await client.ListProvidersAsync(Stop).ConfigureAwait(false)).Providers.FirstOrDefault(value => string.Equals(value.Key, key, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            await error.WriteLineAsync($"altanode providers: no provider of this machine has the key {key}.").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        if (provider.SignIn)
        {
            var prompt = await client.StartProviderSignInAsync(provider.Key, Stop).ConfigureAwait(false);
            await output.WriteLineAsync($"Open {prompt.Url} in a browser and enter the code {prompt.UserCode}.").ConfigureAwait(false);
            FleetProviderSignInState waited;
            do waited = await client.WaitProviderSignInAsync(provider.Key, 20, Stop).ConfigureAwait(false);
            while (waited.Status == "waiting");
            return await EndAsync(waited.Status == "completed", provider.Name, waited.Account, waited.Code).ConfigureAwait(false);
        }

        var item = (await client.ListSignInsAsync(Stop).ConfigureAwait(false)).Items
            .FirstOrDefault(value => value.Kind == FleetMachineSignInKinds.Provider && string.Equals(value.Key, provider.Key, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.Ways.Count == 0)
        {
            await error.WriteLineAsync($"altanode providers: {provider.Name} signs in on its own here{(item?.Detail is { } detail ? ": " + detail : ".")}").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        var way = item.Ways.FirstOrDefault(static value => value is FleetMachineSignInWays.Code or FleetMachineSignInWays.Device or FleetMachineSignInWays.Secret);
        if (way is null)
        {
            await error.WriteLineAsync($"altanode providers: {provider.Name} signs in with a browser that comes back to CodeAlta Desktop: sign it in from the page of this machine there.")
                .ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        var started = await client.StartSignInAsync(new FleetMachineSignInStartParams(item.Kind, item.Key, item.Space, way), Stop).ConfigureAwait(false);
        try
        {
            if (way == FleetMachineSignInWays.Device)
                await output.WriteLineAsync($"Open {started.Page} in a browser and enter the code {started.UserCode}.").ConfigureAwait(false);
            else
            {
                await output.WriteLineAsync(way == FleetMachineSignInWays.Secret
                    ? $"{started.Ask ?? "The value"}{(started.Page is { } where ? $" (where to get it: {where})" : "")}:"
                    : $"Open {started.Page} in a browser, sign in, then paste here the code it shows:").ConfigureAwait(false);
                var typed = (await input.ReadLineAsync(Stop).ConfigureAwait(false))?.Trim();
                if (string.IsNullOrEmpty(typed))
                {
                    await error.WriteLineAsync("altanode providers: nothing was typed: the sign-in stops.").ConfigureAwait(false);
                    return NodeExitCodes.Failure;
                }

                await client.CompleteSignInAsync(new FleetMachineSignInCompleteParams(started.AttemptId, null, typed), Stop).ConfigureAwait(false);
            }

            FleetMachineSignInState state;
            do state = await client.WaitSignInAsync(started.AttemptId, 20, Stop).ConfigureAwait(false);
            while (state.Status == FleetMachineSignInStates.Waiting);
            return await EndAsync(state.Status == FleetMachineSignInStates.Completed, provider.Name, state.Account, state.Message ?? state.Code).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await client.CancelSignInAsync(started.AttemptId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> EndAsync(bool signedIn, string name, string? account, string? why)
    {
        if (signedIn)
        {
            await output.WriteLineAsync(account is null ? $"{name} is signed in on this machine." : $"{name} is signed in on this machine as {account}.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        await error.WriteLineAsync($"altanode providers: {name} is not signed in{(why is null ? "." : $" ({why}).")}").ConfigureAwait(false);
        return NodeExitCodes.Failure;
    }

    private static string? Why(FleetProviderStatus provider) => provider.Reason switch
    {
        null => null,
        FleetProviderReasons.SecretMissing => $"waits for the secret {provider.Detail}",
        FleetProviderReasons.KeyMissing => provider.Detail is { } variable ? $"no key: {variable} is not set" : "no key",
        FleetProviderReasons.SignInNeeded => "not signed in: altanode providers sign-in " + provider.Key,
        FleetProviderReasons.Pending => "the node applies it in a moment",
        FleetProviderReasons.StartFailed => provider.Detail ?? "it did not start",
        _ => "its configuration cannot start it",
    };

    private static string? Program(FleetProviderProgram? program) => program switch
    {
        null => null,
        { Update: { } update } => $"update to {update}: altanode providers update",
        { State: "installed", Version: { } version } => $"version {version}, installed by the node",
        { State: "found", Detail: { } path } => "runs " + path,
        { State: "missing", Version: { } version } => $"installs version {version}",
        { State: "runner", Detail: { } runner } => "runs with " + runner,
        { State: "unsupported", Detail: { } why } => "cannot run here: " + why,
        _ => null,
    };

    private static string Fit(string text, int width) => text.Length <= width ? text.PadRight(width) : text[..(width - 1)] + "…";

    private async Task<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
