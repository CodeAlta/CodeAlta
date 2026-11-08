using System.Security.Cryptography;
using CodeAlta.Catalog;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Plugin.Mcp;

namespace CodeAlta.Node;

/// <summary>
/// The sign-ins a node keeps for itself, made from the desktop that manages it (<c>control.signins.*</c>, <c>doc/fleet/node.md</c>,
/// credentials on a node): the providers that sign in with an account, and the HTTP MCP servers that authorize with OAuth. The
/// node starts each sign-in and keeps its secrets: the code verifier of a browser sign-in, and the tokens. The desktop shows a
/// device code, or opens the page in its own browser, catches the redirect on its loopback and forwards the callback.
/// </summary>
/// <param name="host">The runtime of the node.</param>
/// <param name="providers">The providers of the node, which a provider just signed in is made ready by.</param>
/// <param name="mcp">What reads and signs in the MCP servers of the node; null for those of its profile, as its MCP plugin reads them.</param>
internal sealed partial class NodeSignIns(CodeAltaHost host, NodeProviders providers, McpManagementService? mcp = null) : IFleetNodeSignIns
{
    /// <summary>How long an attempt waits for the person.</summary>
    internal static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(15);

    /// <summary>The most attempts that wait at a time.</summary>
    internal const int MaxAttempts = 16;

    // How long the start of a sign-in waits for its first prompt.
    private static readonly TimeSpan PromptWait = TimeSpan.FromSeconds(60);

    private readonly McpManagementService _mcp = mcp ?? new McpManagementService(NodeBuiltInPlugins.McpOptions(host.CatalogOptions.GlobalRoot));
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    private string Root => host.CatalogOptions.GlobalRoot;

    private CodeAltaConfigStore Store => new(host.CatalogOptions);

    /// <summary>Gets the sign-in of a provider by a device code or on a page (<see cref="ConfiguredProviderLogin.LoginAsync"/>); replaced by tests.</summary>
    internal Func<CodeAltaProviderDocument, string, string, Func<ProviderLoginPrompt, CancellationToken, ValueTask>, CancellationToken, Task<ProviderLoginStatus>> ProviderLogin
    { get; init; } = ConfiguredProviderLogin.LoginAsync;

    /// <inheritdoc />
    public async Task<FleetMachineSignInList> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<FleetMachineSignInItem>();
        foreach (var definition in Store.LoadGlobalProviderDefinitions(includeDisabled: true).Where(static value => ConfiguredProviderLogin.SupportsLogin(value.ProviderType)).Take(32))
        {
            ProviderLoginStatus? status = null;
            try { status = await ConfiguredProviderLogin.GetStatusAsync(definition, Root, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or InvalidDataException
                or System.Text.Json.JsonException)
            {
                // A credential that cannot be read is not a sign-in: the provider is listed as unknown.
            }

            var ways = ProviderWays(definition.ProviderType);
            items.Add(new FleetMachineSignInItem(FleetMachineSignInKinds.Provider, definition.ProviderKey, null, definition.DisplayName ?? definition.ProviderKey,
                status is null ? FleetMachineSignInStates.Unknown : status.SignedIn ? FleetMachineSignInStates.SignedIn : FleetMachineSignInStates.SignedOut,
                Bounded(status?.Account), status?.ExpiresAt, ways, ways.Count == 0 ? "It signs in with a browser on the machine itself." : null));
        }

        foreach (var server in McpServers().Take(64))
        {
            items.Add(new FleetMachineSignInItem(FleetMachineSignInKinds.Mcp, server.Key, null, server.DisplayName,
                server.OAuthTokenCached ? FleetMachineSignInStates.SignedIn : FleetMachineSignInStates.SignedOut, null, server.OAuthTokenExpiresAt,
                [FleetMachineSignInWays.Browser], null));
        }

        items.AddRange(ClaudeItems());
        items.AddRange(await PluginItemsAsync(cancellationToken).ConfigureAwait(false));
        items.AddRange(await CliItemsAsync(cancellationToken).ConfigureAwait(false));
        return new FleetMachineSignInList(items);
    }

    /// <inheritdoc />
    public async Task<FleetMachineSignInPrompt> StartAsync(FleetMachineSignInStartParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters = parameters with { Space = CanonicalSpace(parameters.Kind, parameters.Space) };
        var targetKey = TargetKey(parameters.Kind, parameters.Space, parameters.Key);
        Attempt attempt;
        lock (_gate)
        {
            Sweep();
            // One sign-in of a target at a time: a start while one waits gives its prompt again.
            var running = _attempts.Values.FirstOrDefault(value => value.Target == targetKey && !value.Completion.IsCompleted);
            if (running is null)
            {
                if (_attempts.Count >= MaxAttempts) throw new InvalidOperationException("Too many sign-ins wait on this machine: finish or cancel one first.");
                running = new Attempt(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), targetKey);
                running.Completion = parameters.Kind switch
                {
                    FleetMachineSignInKinds.Provider => RunProvider(running, parameters),
                    FleetMachineSignInKinds.Mcp => RunMcp(running, parameters),
                    FleetMachineSignInKinds.Cli => RunCli(running, parameters),
                    _ => throw new NotSupportedException($"A {parameters.Kind} does not sign in from another computer yet."),
                };
                _attempts[running.Id] = running;
            }

            attempt = running;
        }

        var first = await Task.WhenAny(attempt.Prompt.Task, attempt.Completion).WaitAsync(PromptWait, cancellationToken).ConfigureAwait(false);
        // A failure cancels the prompt before the attempt ends: the reason is in how it ended.
        if (first == attempt.Prompt.Task && attempt.Prompt.Task.IsCompletedSuccessfully) return await attempt.Prompt.Task.ConfigureAwait(false);
        var ended = await attempt.Completion.ConfigureAwait(false);
        // What fails before a prompt (no such target, a way it does not have) is said as the start's own failure.
        if (attempt.StartFailure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        throw new InvalidOperationException(ended.Status == FleetMachineSignInStates.Completed ? "It is signed in already." : ended.Message ?? $"The sign-in did not start ({ended.Code}).");
    }

    /// <inheritdoc />
    public async Task<FleetMachineSignInState> CompleteAsync(FleetMachineSignInCompleteParams parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Attempt? attempt;
        lock (_gate) _attempts.TryGetValue(parameters.AttemptId, out attempt);
        if (attempt is null) return None(parameters.AttemptId);
        if (parameters.Callback is { } callback)
        {
            if (attempt.Forwarded is not { } forwarded) throw new ArgumentException("This sign-in does not wait for the callback of a browser.", nameof(parameters));
            if (!Uri.TryCreate(callback, UriKind.Absolute, out var address) || !forwarded.TryComplete(address))
                throw new ArgumentException("The callback is not the one this sign-in waits for.", nameof(parameters));
        }
        else if (attempt.AcceptCode is { } accept && parameters.Code is { Length: > 0 } code && !code.Any(char.IsControl))
        {
            accept(code);
        }
        else
        {
            throw new ArgumentException("This sign-in does not take a pasted code.", nameof(parameters));
        }

        return await WaitAsync(parameters.AttemptId, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FleetMachineSignInState> WaitAsync(string attemptId, TimeSpan wait, CancellationToken cancellationToken)
    {
        Attempt? attempt;
        lock (_gate) _attempts.TryGetValue(attemptId, out attempt);
        if (attempt is null) return None(attemptId);
        try
        {
            return await attempt.Completion.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new FleetMachineSignInState(attemptId, FleetMachineSignInStates.Waiting, null, null, null);
        }
    }

    /// <inheritdoc />
    public async Task<FleetMachineSignInState> CancelAsync(string attemptId)
    {
        Attempt? attempt;
        lock (_gate) _attempts.TryGetValue(attemptId, out attempt);
        if (attempt is null) return None(attemptId);
        attempt.Forwarded?.Cancel();
        await attempt.Lifetime.CancelAsync().ConfigureAwait(false);
        try { return await attempt.Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { return new FleetMachineSignInState(attemptId, FleetMachineSignInStates.Failed, null, "canceled", null); }
    }

    /// <inheritdoc />
    public async Task<FleetMachineSignInItem> SignOutAsync(FleetMachineSignInTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        target = target with { Space = CanonicalSpace(target.Kind, target.Space) };
        // A sign-in of the target that still waits would store a credential again after the sign-out: it ends first.
        var targetKey = TargetKey(target.Kind, target.Space, target.Key);
        string[] pending;
        lock (_gate)
        {
            pending = [.. _attempts.Values.Where(attempt => !attempt.Completion.IsCompleted && attempt.Target == targetKey).Select(static attempt => attempt.Id)];
        }

        foreach (var id in pending) await CancelAsync(id).ConfigureAwait(false);
        switch (target.Kind)
        {
            case FleetMachineSignInKinds.Provider when ClaudeProvider(target.Key) is { } claude:
                SignOutClaude(claude);
                break;
            case FleetMachineSignInKinds.Provider:
            {
                var definition = Provider(target.Key);
                await ConfiguredProviderLogin.SignOutAsync(definition, Root, cancellationToken).ConfigureAwait(false);
                break;
            }
            case FleetMachineSignInKinds.Mcp:
                _ = McpServer(target.Key);
                _mcp.LogoutOAuth(target.Key);
                break;
            case FleetMachineSignInKinds.Cli:
                throw new NotSupportedException("A tool keeps its own login: sign it out on the machine.");
            default:
                throw new NotSupportedException($"A {target.Kind} is not signed out from another computer yet.");
        }

        // The item of this target, by the rule its kind compares keys with.
        return (await ListAsync(cancellationToken).ConfigureAwait(false)).Items
            .First(item => item.Kind == target.Kind && item.Space == target.Space && TargetKey(item.Kind, item.Space, item.Key) == targetKey);
    }

    // A provider and an MCP server are the machine's, whatever space a request names; a tool is of a space, the default one when none is named.
    private static string? CanonicalSpace(string kind, string? space)
        => kind == FleetMachineSignInKinds.Cli ? (string.IsNullOrWhiteSpace(space) ? SpaceDescriptor.DefaultId : space) : null;

    // One identity per target, by the rule its kind compares keys with: a provider's key is one key whatever its case (the
    // invariant upper case, which OrdinalIgnoreCase compares by), an MCP server's and a tool's are compared as written.
    private static string TargetKey(string kind, string? space, string key)
        => $"{kind}\n{space}\n{(kind == FleetMachineSignInKinds.Provider ? key.ToUpperInvariant() : key)}";

    /// <summary>The ways the desktop signs a provider type in.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Browser first when it has it, else page, then device.</returns>
    internal static IReadOnlyList<string> ProviderWays(string? type)
    {
        var ways = new List<string>(2);
        if (ConfiguredProviderLogin.SupportsForwardedBrowser(type)) ways.Add(FleetMachineSignInWays.Browser);
        else if (ConfiguredProviderLogin.SupportsPolledBrowser(type)) ways.Add(FleetMachineSignInWays.Page);
        if (ConfiguredProviderLogin.GetLoginModes(type).Contains("device", StringComparer.Ordinal)) ways.Add(FleetMachineSignInWays.Device);
        return ways;
    }

    // A provider: a device code the desktop shows, a page the desktop opens while the node asks the service until it is done, or a
    // browser of the desktop whose callback it forwards. Never throws.
    private Task<FleetMachineSignInState> RunProvider(Attempt attempt, FleetMachineSignInStartParams parameters) => Task.Run(async () =>
    {
        try
        {
            if (PluginAccount(parameters.Key) is { } plugin) return await RunPluginAsync(attempt, parameters, plugin).ConfigureAwait(false);
            if (ClaudeProvider(parameters.Key) is { } claude) return await RunClaudeAsync(attempt, parameters, claude).ConfigureAwait(false);
            var definition = Provider(parameters.Key);
            var ways = ProviderWays(definition.ProviderType);
            var way = parameters.Way ?? ways.FirstOrDefault()
                ?? throw new NotSupportedException($"{definition.DisplayName ?? definition.ProviderKey} signs in with a browser on the machine itself: sign in there, or over SSH.");
            if (!ways.Contains(way, StringComparer.Ordinal)) throw new NotSupportedException($"{definition.DisplayName ?? definition.ProviderKey} does not sign in with a {way} from another computer.");
            ProviderLoginStatus status;
            if (way is FleetMachineSignInWays.Device or FleetMachineSignInWays.Page)
            {
                // A page signs in with the browser sign-in of the provider: its verifier stays here, and nothing comes back but the end of the wait.
                status = await ProviderLogin(definition, Root, way == FleetMachineSignInWays.Device ? "device" : "browser", (prompt, _) =>
                {
                    attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, way, prompt.Uri.AbsoluteUri, null, prompt.UserCode, null,
                        prompt.ExpiresAt is { } expires && expires < attempt.ExpiresAt ? expires : attempt.ExpiresAt));
                    return ValueTask.CompletedTask;
                }, attempt.Lifetime.Token).ConfigureAwait(false);
            }
            else
            {
                var forwarded = new ForwardedBrowserCallback(ConfiguredProviderLogin.ForwardedRedirect(definition.ProviderType, Port()));
                attempt.Forwarded = forwarded;
                status = await ConfiguredProviderLogin.LoginWithForwardedBrowserAsync(definition, Root, forwarded, (prompt, _) =>
                {
                    attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, way, prompt.Uri.AbsoluteUri, forwarded.RedirectUri.AbsoluteUri, null, null,
                        attempt.ExpiresAt));
                    return ValueTask.CompletedTask;
                }, attempt.Lifetime.Token).ConfigureAwait(false);
            }

            if (status.Usable) providers.SignedIn(definition);
            return new FleetMachineSignInState(attempt.Id, status.SignedIn ? FleetMachineSignInStates.Completed : FleetMachineSignInStates.Failed, Bounded(status.Account),
                status.SignedIn ? null : "login_failed", status.SignedIn ? null : status.Detail);
        }
        catch (Exception exception)
        {
            return attempt.Failed(exception);
        }
    });

    // An MCP server: the browser of the desktop, whose callback it forwards; the SDK keeps the verifier and the tokens here. Never throws.
    private Task<FleetMachineSignInState> RunMcp(Attempt attempt, FleetMachineSignInStartParams parameters) => Task.Run(async () =>
    {
        try
        {
            var server = McpServer(parameters.Key);
            if (parameters.Way is { } way && way != FleetMachineSignInWays.Browser) throw new NotSupportedException($"{server.DisplayName} authorizes with a browser only.");
            var result = await _mcp.LoginOAuthForwardedAsync(server.Key, async (page, redirect, token) =>
            {
                var forwarded = new ForwardedBrowserCallback(redirect);
                attempt.Forwarded = forwarded;
                attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, FleetMachineSignInWays.Browser, page.AbsoluteUri, redirect.AbsoluteUri, null, null,
                    attempt.ExpiresAt));
                try
                {
                    return await forwarded.WaitAsync(AttemptLifetime, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return null;
                }
            }, null, null, attempt.Lifetime.Token).ConfigureAwait(false);
            var signedIn = result.Status == McpManagementTestStatus.Succeeded;
            return new FleetMachineSignInState(attempt.Id, signedIn ? FleetMachineSignInStates.Completed : FleetMachineSignInStates.Failed, null,
                signedIn ? null : "login_failed", signedIn ? null : Bounded(string.Join(" ", result.Diagnostics.Take(2))));
        }
        catch (Exception exception)
        {
            return attempt.Failed(exception);
        }
    });

    private CodeAltaProviderDocument Provider(string key)
        => Store.LoadGlobalProviderDefinitions(includeDisabled: true).FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase)
               && ConfiguredProviderLogin.SupportsLogin(value.ProviderType))
           ?? throw new KeyNotFoundException($"No provider of this machine that signs in with an account has the key {key}.");

    private IEnumerable<McpManagementServerSnapshot> McpServers()
        => _mcp.RefreshSnapshot(new McpManagementRequest { ProbeWritability = false }).Servers
            .Where(static server => server.State == McpManagementServerState.Configured && server.Transport == McpManagementTransport.Http && server.OAuthAvailable);

    private McpManagementServerSnapshot McpServer(string key)
        => McpServers().FirstOrDefault(server => string.Equals(server.Key, key, StringComparison.Ordinal))
           ?? throw new KeyNotFoundException($"No HTTP MCP server of this machine that authorizes with OAuth has the key {key}.");

    // A port of the loopback of the desktop: any port above the well-known ones, which the desktop listens on for this sign-in only.
    private static int Port() => RandomNumberGenerator.GetInt32(49152, 65535);

    private static FleetMachineSignInState None(string attemptId) => new(attemptId, FleetMachineSignInStates.None, null, null, null);

    private static string? Bounded(string? value) => value is null ? null : value.Length <= 512 ? value : value[..512];

    // Attempts that ended a while ago are forgotten.
    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (id, attempt) in _attempts.ToArray())
        {
            if (attempt.Completion.IsCompleted && now - attempt.StartedAt > TimeSpan.FromMinutes(1) || now - attempt.StartedAt > AttemptLifetime + TimeSpan.FromMinutes(1))
                _attempts.Remove(id);
        }
    }

    // An attempt: its first prompt, how it ends, and the browser callback it waits for.
    private sealed class Attempt
    {
        public Attempt(string id, string target)
        {
            Id = id;
            Target = target;
            Lifetime.CancelAfter(AttemptLifetime);
        }

        public string Id { get; }

        public string Target { get; }

        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset ExpiresAt => StartedAt + AttemptLifetime;

        public CancellationTokenSource Lifetime { get; } = new();

        public TaskCompletionSource<FleetMachineSignInPrompt> Prompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FleetMachineSignInState> Completion { get; set; } = Task.FromResult(new FleetMachineSignInState("", FleetMachineSignInStates.None, null, null, null));

        public ForwardedBrowserCallback? Forwarded { get; set; }

        // What a tool that asks for the code its page shows is given: written to its input.
        public Action<string>? AcceptCode { get; set; }

        public Exception? StartFailure { get; private set; }

        // How an attempt that threw ends: a failure before its prompt is the start's own, said to its caller as it is.
        public FleetMachineSignInState Failed(Exception exception)
        {
            if (!Prompt.Task.IsCompleted && exception is KeyNotFoundException or NotSupportedException or ArgumentException) StartFailure = exception;
            Prompt.TrySetCanceled();
            var code = exception switch
            {
                OperationCanceledException when Lifetime.IsCancellationRequested && DateTimeOffset.UtcNow >= ExpiresAt => "timeout",
                OperationCanceledException => "canceled",
                TimeoutException => "timeout",
                _ => "login_failed",
            };
            return new FleetMachineSignInState(Id, FleetMachineSignInStates.Failed, null, code, Bounded(exception.Message));
        }
    }
}
