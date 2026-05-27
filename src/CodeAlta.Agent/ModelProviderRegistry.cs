using System.Diagnostics.CodeAnalysis;

namespace CodeAlta.Agent;

/// <summary>
/// In-memory model provider registry keyed by configured provider id.
/// </summary>
public sealed class ModelProviderRegistry : IModelProviderRegistry, IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers or replaces a provider runtime factory.
    /// </summary>
    /// <param name="descriptor">The provider descriptor.</param>
    /// <param name="runtimeFactory">The runtime factory.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> or <paramref name="runtimeFactory" /> is <see langword="null" />.</exception>
    public void RegisterOrReplace(ModelProviderDescriptor descriptor, Func<IModelProviderRuntime> runtimeFactory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(runtimeFactory);

        var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
        lock (_lock)
        {
            if (_registrations.Remove(key, out var previous))
            {
                DisposeRuntime(previous.Runtime);
            }

            _registrations[key] = new Registration(descriptor, runtimeFactory, null);
        }
    }

    /// <summary>
    /// Registers or replaces a provider runtime factory backed by a transitional agent backend.
    /// </summary>
    /// <param name="descriptor">The provider descriptor.</param>
    /// <param name="backendFactory">The backend factory.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> or <paramref name="backendFactory" /> is <see langword="null" />.</exception>
    public void RegisterOrReplaceBackendRuntime(ModelProviderDescriptor descriptor, Func<IAgentBackend> backendFactory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(backendFactory);

        // Keep this bridge for transitional plugin/external backend registrations while
        // configured raw-API providers register native IModelProviderRuntime instances.
        // The descriptor is supplied by provider configuration rather than derived from
        // the backend instance created below.
        RegisterOrReplace(descriptor, () => new AgentBackendModelProviderRuntime(descriptor, backendFactory()));
    }

    /// <summary>
    /// Removes a provider registration.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <returns><see langword="true" /> when removed; otherwise <see langword="false" />.</returns>
    public bool Unregister(ModelProviderId providerId)
    {
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        Registration? removed = null;
        lock (_lock)
        {
            if (_registrations.Remove(key, out var registration))
            {
                removed = registration;
            }
        }

        if (removed is null)
        {
            return false;
        }

        DisposeRuntime(removed.Value.Runtime);
        return true;
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
    public ValueTask<IModelProviderRuntime> GetOrCreateRuntimeAsync(
        ModelProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        lock (_lock)
        {
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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        IModelProviderRuntime[] runtimes;
        lock (_lock)
        {
            runtimes = _registrations.Values
                .Select(static registration => registration.Runtime)
                .Where(static runtime => runtime is not null)
                .Cast<IModelProviderRuntime>()
                .ToArray();
            _registrations.Clear();
        }

        foreach (var runtime in runtimes)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
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
        IModelProviderRuntime? Runtime);

    private sealed class AgentBackendModelProviderRuntime : IModelProviderRuntime
    {
        private readonly IAgentBackend _backend;

        public AgentBackendModelProviderRuntime(ModelProviderDescriptor descriptor, IAgentBackend backend)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        }

        public ModelProviderDescriptor Descriptor { get; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _backend.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _backend.StopAsync(cancellationToken);

        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            var models = await _backend.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            return new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = models,
                SelectedModelId = Descriptor.DefaultModelId,
                SelectedReasoningEffort = Descriptor.DefaultReasoningEffort,
            };
        }

        public IModelProviderTurnExecutor CreateTurnExecutor()
        {
            throw new NotSupportedException($"Model provider '{Descriptor.ProviderId.Value}' uses transitional backend runtime scaffolding and does not expose a provider turn executor yet.");
        }

        public ValueTask DisposeAsync() => _backend.DisposeAsync();
    }
}
