using System.Diagnostics.CodeAnalysis;

namespace CodeAlta.Agent;

/// <summary>
/// In-memory model provider registry keyed by configured provider id.
/// </summary>
public sealed class ModelProviderRegistry : IModelProviderRegistry, IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _preparations = new(StringComparer.OrdinalIgnoreCase);
    // The gate of a provider outlives its registrations: the requests of the sessions that still run on a former runtime
    // count with those of the new one, and a new limit applies to all of them.
    private readonly Dictionary<string, ProviderRequestGate> _requestGates = new(StringComparer.OrdinalIgnoreCase);
    // The providers that wait (a key kept in a locked vault): what asks for it, and what completes when the provider is
    // registered again or removed.
    private readonly Dictionary<string, Waiting> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private long _lastVersion;
    private bool _closed;

    /// <summary>
    /// Gets or initializes where the providers registered here keep what they read of the files of conversations other
    /// programs started (the application database of the instance), across the starts of CodeAlta; null when they keep
    /// it in memory only. The registry borrows it: its owner disposes what it is made over.
    /// </summary>
    public Runtime.IAgentExternalConversationHeaderStore? ExternalConversationHeaders { get; init; }

    /// <summary>
    /// Gets or initializes the CodeAlta application the providers registered here serve: <c>desktop</c>, <c>tui</c> or
    /// <c>cli</c>, or null when none is named. A provider names it to the services that ask an application to say what
    /// it is, such as the categories of the app attribution of OpenRouter.
    /// </summary>
    public string? Application { get; init; }

    /// <summary>
    /// Gets or initializes how a person signs in a provider registered here that runs a CLI with its own login, when this host
    /// is driven from another computer (a node of the fleet): a sentence that replaces the terminal command a provider names
    /// otherwise. Null for a host whose person sits at this computer.
    /// </summary>
    public string? CliSignInHint { get; init; }

    /// <summary>
    /// Gets or sets what is told each value of a secret given to a provider registered here (its key, a variable of its CLI)
    /// before the provider gets it: the masking of the host, which masks it from then on, whatever the store keeps of it later.
    /// Null tells nothing.
    /// </summary>
    public Action<string>? SecretIssued { get; set; }

    /// <summary>
    /// Gets or sets the secrets kept in vaults the host holds, and its confirmations of the secrets that ask before use, which the
    /// providers registered here are given for their own use (<c>provider &lt;key&gt;</c>); null for a host without them.
    /// </summary>
    public Runtime.IBoundSecretSource? BoundSecrets { get; set; }

    /// <summary>
    /// Gets or sets what asks for the secrets of a provider that cannot have them yet, such as a key kept in a locked vault: with
    /// it, such a provider is registered waiting (<see cref="RegisterWaiting"/>) instead of being left out, and a run of it asks
    /// for them. Null leaves such a provider out.
    /// </summary>
    public Runtime.IProviderSecretWaiter? SecretWaiter { get; set; }

    /// <summary>
    /// Gets or initializes how long a run waits, once what a waiting provider needed was given, for the host to register the
    /// provider again (<see cref="WaitForProviderAsync"/>); 30 seconds by default.
    /// </summary>
    public TimeSpan RegistrationWaitTime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets what gives the transport of a provider that names a model server (<c>server = "&lt;machine&gt;/&lt;server&gt;"</c>):
    /// the host knows where its servers and the servers of its machines are. Null knows none.
    /// </summary>
    public Func<string, HttpMessageHandler?>? ModelServerTransports
    {
        get => _modelServerTransports;
        // Each transport carries the updates of the session that sends a request, so that a wait for a slot of the server says so.
        set => _modelServerTransports = value is null ? null : server => value(server) is { } transport ? WaitScope.Wrap(transport) : null;
    }

    private Func<string, HttpMessageHandler?>? _modelServerTransports;

    /// <summary>
    /// Gets where a request that waits below its provider (a slot of a model server) tells its session: the sessions give
    /// their updates for the time of each request, and the transports of the model servers take them.
    /// </summary>
    public Runtime.AgentWaitScope WaitScope { get; } = new();

    /// <summary>
    /// Gets the processes the runtimes of the providers registered here started and still run. It outlives the
    /// registrations: a provider registered again knows the processes of the sessions that still run on its former runtime.
    /// </summary>
    public Runtime.AgentProviderProcesses ProviderProcesses { get; } = new();

    /// <summary>
    /// Registers or replaces a provider runtime factory. The limit of requests at once of the descriptor
    /// (<see cref="ModelProviderDescriptor.MaxConcurrentRequests"/>) is applied in place to the gate of the provider.
    /// </summary>
    /// <param name="descriptor">The provider descriptor.</param>
    /// <param name="runtimeFactory">The runtime factory.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> or <paramref name="runtimeFactory" /> is <see langword="null" />.</exception>
    /// <exception cref="InvalidOperationException">The provider's original model preparation still owns its cached runtime.</exception>
    /// <exception cref="ObjectDisposedException">Registry shutdown has begun.</exception>
    public void RegisterOrReplace(ModelProviderDescriptor descriptor, Func<IModelProviderRuntime> runtimeFactory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        Register(descriptor, runtimeFactory, null);
    }

    /// <summary>
    /// Registers or replaces a provider that waits before it can run (its key kept in a locked vault): it is listed with what it
    /// waits for, anything that would create its runtime sees it as not registered (<see cref="ModelProviderWaitingException"/>),
    /// and a run asks for what it waits for (<see cref="WaitForProviderAsync"/>) until the provider is registered again.
    /// </summary>
    /// <param name="descriptor">The descriptor it would have; its <see cref="ModelProviderDescriptor.WaitsFor"/> says what it waits for.</param>
    /// <param name="waitAsync">Asks for what it waits for, and answers whether it was given.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The descriptor says nothing of what it waits for.</exception>
    /// <exception cref="InvalidOperationException">The provider's original model preparation still owns its cached runtime.</exception>
    /// <exception cref="ObjectDisposedException">Registry shutdown has begun.</exception>
    public void RegisterWaiting(ModelProviderDescriptor descriptor, Func<CancellationToken, Task<bool>> waitAsync)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(waitAsync);
        if (string.IsNullOrWhiteSpace(descriptor.WaitsFor)) throw new ArgumentException("A waiting provider says what it waits for.", nameof(descriptor));
        var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
        var message = descriptor.WaitsFor;
        Register(descriptor, () => throw new ModelProviderWaitingException(key, message), waitAsync);
    }

    /// <summary>
    /// Waits, when a provider is registered waiting, until it can run: asks for what it waits for (a vault to unlock), then waits
    /// for the provider to be registered again. A provider that runs, or that is not registered, returns at once.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="cancellationToken">A token to stop.</param>
    /// <returns>A task that ends once the provider can run.</returns>
    /// <exception cref="ModelProviderWaitingException">What it waits for could not be had, or the provider was not registered again in time.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    public async Task WaitForProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken)
    {
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        Waiting waiting;
        string message;
        lock (_lock)
        {
            if (!_waiting.TryGetValue(key, out waiting)) return;
            message = _registrations.TryGetValue(key, out var registration) ? registration.Descriptor.WaitsFor ?? string.Empty : string.Empty;
        }

        if (!await waiting.WaitAsync(cancellationToken).ConfigureAwait(false)) throw new ModelProviderWaitingException(key, message);
        try
        {
            // The host registers the provider again once what it waited for is there: a few seconds at most.
            await waiting.Registered.Task.WaitAsync(RegistrationWaitTime, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ModelProviderWaitingException(key, message);
        }
    }

    private void Register(ModelProviderDescriptor descriptor, Func<IModelProviderRuntime> runtimeFactory, Func<CancellationToken, Task<bool>>? waitAsync)
    {
        var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
        TaskCompletionSource? registered = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ThrowIfPreparing(key);
            if (waitAsync is not null)
            {
                // A provider that waits again keeps the runs that already wait for it.
                _waiting[key] = _waiting.TryGetValue(key, out var waiting) ? waiting with { WaitAsync = waitAsync }
                    : new Waiting(waitAsync, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            else if (_waiting.Remove(key, out var waiting))
            {
                registered = waiting.Registered;
            }

            if (_registrations.Remove(key, out var previous))
            {
                DisposeRuntime(previous.Runtime);
            }

            _registrations[key] = new Registration(descriptor, runtimeFactory, null, ++_lastVersion);
            if (_requestGates.TryGetValue(key, out var gate))
            {
                gate.SetLimit(descriptor.MaxConcurrentRequests);
            }
            else
            {
                _requestGates[key] = new ProviderRequestGate(descriptor.ProviderId.Value, descriptor.MaxConcurrentRequests) { WaitScope = WaitScope };
            }
        }

        registered?.TrySetResult();
    }

    /// <summary>
    /// Drops the cached runtime of a provider, so that its next probe creates a new one that asks the provider again rather
    /// than answering from what the old runtime kept (its models, its account). The registration stays as it is: its
    /// version does not change, and sessions, which run on runtimes of their own, go on.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns><see langword="true" /> when a cached runtime was dropped.</returns>
    /// <exception cref="KeyNotFoundException">The provider is not registered.</exception>
    /// <exception cref="InvalidOperationException">The provider's original model preparation still owns its cached runtime.</exception>
    /// <exception cref="ObjectDisposedException">Registry shutdown has begun.</exception>
    public bool DropCachedRuntime(ModelProviderId providerId)
    {
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        IModelProviderRuntime? dropped;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_registrations.TryGetValue(key, out var registration))
            {
                throw new KeyNotFoundException($"Model provider '{key}' is not registered.");
            }

            ThrowIfPreparing(key);
            dropped = registration.Runtime;
            if (dropped is null)
            {
                return false;
            }

            _registrations[key] = registration with { Runtime = null };
        }

        DisposeRuntime(dropped);
        return true;
    }

    /// <summary>
    /// Gets the gate that bounds the requests at once of a provider. It is the same across the registrations of the
    /// provider: replacing the provider changes its limit, not its count.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The gate, or <see langword="null"/> when the provider was never registered.</returns>
    public ProviderRequestGate? GetRequestGate(ModelProviderId providerId)
    {
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        lock (_lock)
        {
            return _requestGates.GetValueOrDefault(key);
        }
    }

    /// <summary>
    /// Removes a provider registration.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns><see langword="true" /> when removed; otherwise <see langword="false" />.</returns>
    /// <exception cref="InvalidOperationException">The provider's original model preparation still owns its cached runtime.</exception>
    /// <exception cref="ObjectDisposedException">Registry shutdown has begun.</exception>
    public bool Unregister(ModelProviderId providerId)
    {
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        Registration? removed = null;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ThrowIfPreparing(key);
            if (_registrations.Remove(key, out var registration))
            {
                removed = registration;
            }

            if (_waiting.Remove(key, out var waiting)) waiting.Registered.TrySetResult();
        }

        if (removed is null)
        {
            return false;
        }

        DisposeRuntime(removed.Value.Runtime);
        return true;
    }

    /// <summary>
    /// Gets the version of the registration of a provider. It changes each time the provider is registered or replaced:
    /// a runtime created from an older version runs with the settings the provider had then.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns>The version; 0 when the provider is not registered.</returns>
    public long GetRegistrationVersion(ModelProviderId providerId)
    {
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        lock (_lock)
        {
            return _registrations.TryGetValue(key, out var registration) ? registration.Version : 0;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelProviderDescriptor> ListProviders(bool includeDisabled = false)
    {
        lock (_lock)
        {
            return _registrations.Values
                .Select(static registration => registration.Descriptor)
                .Where(descriptor => includeDisabled || descriptor.IsEnabled)
                .OrderBy(static descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static descriptor => descriptor.ProviderId.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <inheritdoc />
    public bool TryGetProvider(ModelProviderId providerId, [NotNullWhen(true)] out ModelProviderDescriptor? descriptor)
    {
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        lock (_lock)
        {
            if (_registrations.TryGetValue(key, out var registration))
            {
                descriptor = registration.Descriptor;
                return true;
            }
        }

        descriptor = null;
        return false;
    }

    bool IModelProviderRegistry.TryGetProvider(ModelProviderId providerId, out ModelProviderDescriptor descriptor)
    {
        if (TryGetProvider(providerId, out var found))
        {
            descriptor = found;
            return true;
        }

        descriptor = null!;
        return false;
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">Registry shutdown has begun.</exception>
    public ValueTask<IModelProviderRuntime> GetOrCreateRuntimeAsync(
        ModelProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_registrations.TryGetValue(key, out var registration))
            {
                throw new KeyNotFoundException($"Model provider '{key}' is not registered.");
            }

            if (registration.Runtime is null)
            {
                registration = registration with { Runtime = ValidateRuntime(registration.Descriptor, registration.Factory()) };
                _registrations[key] = registration;
            }

            return ValueTask.FromResult(registration.Runtime);
        }
    }

    /// <summary>
    /// Creates a fresh runtime instance for a registered provider.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The provider runtime.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="providerId" /> is empty.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="providerId" /> is not registered.</exception>
    /// <exception cref="ObjectDisposedException">Registry shutdown has begun.</exception>
    public ValueTask<IModelProviderRuntime> CreateRuntimeAsync(
        ModelProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        Registration registration;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_registrations.TryGetValue(key, out registration))
            {
                throw new KeyNotFoundException($"Model provider '{key}' is not registered.");
            }
        }

        return ValueTask.FromResult(ValidateRuntime(registration.Descriptor, registration.Factory()));
    }

    /// <inheritdoc />
    /// <exception cref="AgentDependencyRetentionException">An original preparation still owns cached runtimes; no runtime is disposed.</exception>
    public async ValueTask DisposeAsync()
    {
        IModelProviderRuntime[] runtimes;
        lock (_lock)
        {
            _closed = true;
            if (_preparations.Count != 0)
                throw new AgentDependencyRetentionException("model provider registry", "original preparation",
                    [new InvalidOperationException("Original model preparations have not released their runtimes.")], this);
            runtimes = _registrations.Values
                .Select(static registration => registration.Runtime)
                .Where(static runtime => runtime is not null)
                .Cast<IModelProviderRuntime>()
                .ToArray();
            _registrations.Clear();
            foreach (var waiting in _waiting.Values) waiting.Registered.TrySetCanceled();
            _waiting.Clear();
        }

        foreach (var runtime in runtimes)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    // A preparation uses a cached runtime, unlike AgentHub's separately owned fresh runtimes. Live
    // configuration must not dispose that acquisition while its original Start/Probe is still running.
    internal IDisposable ProtectPreparation(ModelProviderId providerId)
    {
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (!_registrations.ContainsKey(key)) throw new KeyNotFoundException($"Model provider '{key}' is not registered.");
            _preparations.TryGetValue(key, out var count);
            _preparations[key] = checked(count + 1);
            return new Preparation(this, key);
        }
    }

    private void ThrowIfPreparing(string key)
    {
        if (_preparations.ContainsKey(key))
            throw new InvalidOperationException($"Model provider '{key}' still has an original preparation. Its configuration cannot be applied yet.");
    }

    private sealed class Preparation(ModelProviderRegistry owner, string key) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._lock)
            {
                if (owner._preparations[key] == 1) owner._preparations.Remove(key);
                else owner._preparations[key]--;
            }
        }
    }

    private static IModelProviderRuntime ValidateRuntime(ModelProviderDescriptor expectedDescriptor, IModelProviderRuntime? runtime)
    {
        if (runtime is null)
        {
            throw new InvalidOperationException($"Model provider factory for '{expectedDescriptor.ProviderId.Value}' returned null.");
        }

        if (!string.Equals(runtime.Descriptor.ProviderId.Value, expectedDescriptor.ProviderId.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Model provider factory for '{expectedDescriptor.ProviderId.Value}' created '{runtime.Descriptor.ProviderId.Value}'. These identifiers must match.");
        }

        return runtime;
    }

    private static void DisposeRuntime(IModelProviderRuntime? runtime)
    {
        if (runtime is not null)
        {
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private readonly record struct Registration(
        ModelProviderDescriptor Descriptor,
        Func<IModelProviderRuntime> Factory,
        IModelProviderRuntime? Runtime,
        long Version);

    private readonly record struct Waiting(Func<CancellationToken, Task<bool>> WaitAsync, TaskCompletionSource Registered);

}
