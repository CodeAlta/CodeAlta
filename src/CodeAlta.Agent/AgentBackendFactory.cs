using System.Diagnostics.CodeAnalysis;

namespace CodeAlta.Agent;

/// <summary>
/// Creates agent backend instances from named registrations.
/// </summary>
public sealed class AgentBackendFactory
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Registration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a backend factory for a backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the backend identifier is already registered.</exception>
    public void Register(AgentBackendId backendId, Func<IAgentBackend> backendFactory)
        => Register(backendId, backendFactory, AgentBackendRegistrationOptions.Default);

    /// <summary>
    /// Registers a backend factory for a backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <param name="options">Registration metadata for the backend.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the backend identifier is already registered.</exception>
    public void Register(AgentBackendId backendId, Func<IAgentBackend> backendFactory, AgentBackendRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(options);
        var normalizedBackendId = NormalizeBackendId(backendId);

        lock (_lock)
        {
            if (_registrations.ContainsKey(normalizedBackendId))
            {
                throw new InvalidOperationException($"Backend '{normalizedBackendId}' is already registered.");
            }

            _registrations.Add(
                normalizedBackendId,
                new Registration(new AgentBackendId(normalizedBackendId), backendFactory, options.UsesSharedSessionMetadataStore));
        }
    }

    /// <summary>
    /// Registers a backend factory for a backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the backend identifier is already registered.</exception>
    public void Register(string backendId, Func<IAgentBackend> backendFactory)
    {
        Register(new AgentBackendId(backendId), backendFactory);
    }

    /// <summary>
    /// Registers a backend factory for a backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <param name="options">Registration metadata for the backend.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the backend identifier is already registered.</exception>
    public void Register(string backendId, Func<IAgentBackend> backendFactory, AgentBackendRegistrationOptions options)
    {
        Register(new AgentBackendId(backendId), backendFactory, options);
    }

    /// <summary>
    /// Registers a backend factory if it is not already registered.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <returns><see langword="true"/> when registration succeeded; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> is <see langword="null"/>.</exception>
    public bool TryRegister(AgentBackendId backendId, Func<IAgentBackend> backendFactory)
        => TryRegister(backendId, backendFactory, AgentBackendRegistrationOptions.Default);

    /// <summary>
    /// Registers a backend factory if it is not already registered.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <param name="options">Registration metadata for the backend.</param>
    /// <returns><see langword="true"/> when registration succeeded; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public bool TryRegister(AgentBackendId backendId, Func<IAgentBackend> backendFactory, AgentBackendRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(options);
        var normalizedBackendId = NormalizeBackendId(backendId);

        lock (_lock)
        {
            if (_registrations.ContainsKey(normalizedBackendId))
                return false;

            _registrations.Add(
                normalizedBackendId,
                new Registration(new AgentBackendId(normalizedBackendId), backendFactory, options.UsesSharedSessionMetadataStore));
            return true;
        }
    }

    /// <summary>
    /// Registers a backend factory if it is not already registered.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <returns><see langword="true"/> when registration succeeded; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> is <see langword="null"/>.</exception>
    public bool TryRegister(string backendId, Func<IAgentBackend> backendFactory)
    {
        return TryRegister(new AgentBackendId(backendId), backendFactory);
    }

    /// <summary>
    /// Registers a backend factory if it is not already registered.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <param name="options">Registration metadata for the backend.</param>
    /// <returns><see langword="true"/> when registration succeeded; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public bool TryRegister(string backendId, Func<IAgentBackend> backendFactory, AgentBackendRegistrationOptions options)
    {
        return TryRegister(new AgentBackendId(backendId), backendFactory, options);
    }

    /// <summary>
    /// Registers or replaces a backend factory for a backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> is <see langword="null"/>.</exception>
    public void RegisterOrReplace(AgentBackendId backendId, Func<IAgentBackend> backendFactory)
        => RegisterOrReplace(backendId, backendFactory, AgentBackendRegistrationOptions.Default);

    /// <summary>
    /// Registers or replaces a backend factory for a backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <param name="options">Registration metadata for the backend.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public void RegisterOrReplace(AgentBackendId backendId, Func<IAgentBackend> backendFactory, AgentBackendRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentNullException.ThrowIfNull(options);
        var normalizedBackendId = NormalizeBackendId(backendId);

        lock (_lock)
        {
            _registrations[normalizedBackendId] = new Registration(
                new AgentBackendId(normalizedBackendId),
                backendFactory,
                options.UsesSharedSessionMetadataStore);
        }
    }

    /// <summary>
    /// Registers or replaces a backend factory for a backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> is <see langword="null"/>.</exception>
    public void RegisterOrReplace(string backendId, Func<IAgentBackend> backendFactory)
    {
        RegisterOrReplace(new AgentBackendId(backendId), backendFactory);
    }

    /// <summary>
    /// Registers or replaces a backend factory for a backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backendFactory">The backend factory delegate.</param>
    /// <param name="options">Registration metadata for the backend.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendFactory"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    public void RegisterOrReplace(string backendId, Func<IAgentBackend> backendFactory, AgentBackendRegistrationOptions options)
    {
        RegisterOrReplace(new AgentBackendId(backendId), backendFactory, options);
    }

    /// <summary>
    /// Returns whether a backend identifier is registered.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <returns><see langword="true"/> when registered; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    public bool IsRegistered(AgentBackendId backendId)
    {
        var normalizedBackendId = NormalizeBackendId(backendId);
        lock (_lock)
        {
            return _registrations.ContainsKey(normalizedBackendId);
        }
    }

    /// <summary>
    /// Returns whether a backend identifier is registered.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <returns><see langword="true"/> when registered; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    public bool IsRegistered(string backendId)
    {
        return IsRegistered(new AgentBackendId(backendId));
    }

    /// <summary>
    /// Removes a backend registration.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <returns><see langword="true"/> when removed; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    public bool Unregister(AgentBackendId backendId)
    {
        var normalizedBackendId = NormalizeBackendId(backendId);
        lock (_lock)
        {
            return _registrations.Remove(normalizedBackendId);
        }
    }

    /// <summary>
    /// Removes a backend registration.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <returns><see langword="true"/> when removed; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    public bool Unregister(string backendId)
    {
        return Unregister(new AgentBackendId(backendId));
    }

    /// <summary>
    /// Lists registered backend identifiers.
    /// </summary>
    /// <returns>The registered backend identifiers.</returns>
    public IReadOnlyList<AgentBackendId> ListRegisteredBackends()
    {
        lock (_lock)
        {
            return _registrations.Values
                .Select(static x => x.BackendId)
                .OrderBy(static x => x.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Returns whether a backend registration declares that session metadata is stored in the shared CodeAlta session store.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <returns><see langword="true"/> when the registration declares shared session metadata; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    public bool UsesSharedSessionMetadataStore(AgentBackendId backendId)
    {
        var normalizedBackendId = NormalizeBackendId(backendId);
        lock (_lock)
        {
            return _registrations.TryGetValue(normalizedBackendId, out var registration) &&
                   registration.UsesSharedSessionMetadataStore;
        }
    }

    /// <summary>
    /// Creates a backend instance for a registered backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <returns>A new backend instance.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the backend identifier is not registered.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the registered factory returns <see langword="null"/> or a backend with a mismatched <see cref="IAgentBackend.BackendId"/>.
    /// </exception>
    public IAgentBackend Create(AgentBackendId backendId)
    {
        var normalizedBackendId = NormalizeBackendId(backendId);
        Registration registration;

        lock (_lock)
        {
            if (!_registrations.TryGetValue(normalizedBackendId, out registration))
            {
                throw new KeyNotFoundException($"Backend '{normalizedBackendId}' is not registered.");
            }
        }

        var backend = registration.Factory();
        return ValidateCreatedBackend(registration.BackendId, backend);
    }

    /// <summary>
    /// Creates a backend instance for a registered backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <returns>A new backend instance.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the backend identifier is not registered.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the registered factory returns <see langword="null"/> or a backend with a mismatched <see cref="IAgentBackend.BackendId"/>.
    /// </exception>
    public IAgentBackend Create(string backendId)
    {
        return Create(new AgentBackendId(backendId));
    }

    /// <summary>
    /// Tries to create a backend instance for a registered backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backend">The created backend instance when successful.</param>
    /// <returns><see langword="true"/> when the backend is registered; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the registered factory returns <see langword="null"/> or a backend with a mismatched <see cref="IAgentBackend.BackendId"/>.
    /// </exception>
    public bool TryCreate(AgentBackendId backendId, [NotNullWhen(true)] out IAgentBackend? backend)
    {
        var normalizedBackendId = NormalizeBackendId(backendId);
        Registration registration;

        lock (_lock)
        {
            if (!_registrations.TryGetValue(normalizedBackendId, out registration))
            {
                backend = null;
                return false;
            }
        }

        backend = ValidateCreatedBackend(registration.BackendId, registration.Factory());
        return true;
    }

    /// <summary>
    /// Tries to create a backend instance for a registered backend identifier.
    /// </summary>
    /// <param name="backendId">The backend identifier.</param>
    /// <param name="backend">The created backend instance when successful.</param>
    /// <returns><see langword="true"/> when the backend is registered; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="backendId"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the registered factory returns <see langword="null"/> or a backend with a mismatched <see cref="IAgentBackend.BackendId"/>.
    /// </exception>
    public bool TryCreate(string backendId, [NotNullWhen(true)] out IAgentBackend? backend)
    {
        return TryCreate(new AgentBackendId(backendId), out backend);
    }

    private static IAgentBackend ValidateCreatedBackend(AgentBackendId expectedBackendId, IAgentBackend? backend)
    {
        if (backend is null)
        {
            throw new InvalidOperationException(
                $"Backend factory for '{expectedBackendId.Value}' returned null.");
        }

        var expected = NormalizeBackendId(expectedBackendId);
        var actual = NormalizeBackendId(backend.BackendId);

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Backend factory for '{expected}' created '{actual}'. These identifiers must match.");
        }

        return backend;
    }

    private static string NormalizeBackendId(AgentBackendId backendId)
    {
        return NormalizeBackendId(backendId.Value);
    }

    private static string NormalizeBackendId(string? backendId)
    {
        if (backendId is null)
            throw new ArgumentException("The backend identifier cannot be null.");

        var normalizedBackendId = backendId.Trim();
        if (normalizedBackendId.Length == 0)
            throw new ArgumentException("The backend identifier cannot be empty or whitespace.");

        return normalizedBackendId;
    }

    private readonly record struct Registration(AgentBackendId BackendId, Func<IAgentBackend> Factory, bool UsesSharedSessionMetadataStore);
}
