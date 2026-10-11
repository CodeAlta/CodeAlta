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
    private long _lastVersion;
    private bool _closed;

    /// <summary>
    /// Registers or replaces a provider runtime factory.
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

        var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ThrowIfPreparing(key);
            if (_registrations.Remove(key, out var previous))
            {
                DisposeRuntime(previous.Runtime);
            }

            _registrations[key] = new Registration(descriptor, runtimeFactory, null, ++_lastVersion);
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

}
