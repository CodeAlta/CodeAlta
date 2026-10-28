using System.Collections.Generic;

namespace CodeAlta.Agent;

/// <summary>
/// Initializes configured model providers, records readiness state, and owns per-provider model catalogs.
/// </summary>
public interface IModelProviderInitializationService
{
    /// <summary>
    /// Gets the latest known provider state snapshots.
    /// </summary>
    IReadOnlyList<ModelProviderStateSnapshot> CurrentStates { get; }

    /// <summary>
    /// Streams provider state changes published by initialization or refresh operations.
    /// </summary>
    /// <param name="cancellationToken">A token to stop reading changes.</param>
    /// <returns>Provider state changes.</returns>
    IAsyncEnumerable<ModelProviderStateChanged> StreamStateChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes all configured providers independently.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel waiting for initialization.</param>
    /// <returns>A task that completes when all currently configured provider probes have completed or the wait is canceled.</returns>
    Task InitializeAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes a single provider state and model catalog.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="cancellationToken">A token to cancel waiting for the refresh.</param>
    /// <returns>A task that completes when the selected provider refresh has completed or the wait is canceled.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="providerId" /> is empty.</exception>
    Task RefreshProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the cached model list for a provider, starting its first initialization when no state exists yet.
    /// </summary>
    /// <param name="providerId">The provider identifier.</param>
    /// <param name="cancellationToken">A token to cancel waiting for first initialization.</param>
    /// <returns>The cached model catalog when available; otherwise an empty list.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="providerId" /> is empty.</exception>
    Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(ModelProviderId providerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Options for <see cref="ModelProviderInitializationService" />.
/// </summary>
public sealed record ModelProviderInitializationOptions
{
    /// <summary>
    /// Gets the default timeout applied to provider startup and probing.
    /// </summary>
    /// <remarks>
    /// A runtime that asks for more time (<see cref="IModelProviderProbeTimeout"/>) has it once it is created.
    /// </remarks>
    public TimeSpan DefaultProbeTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets how long the preparation of a provider keeps its place when the providers are prepared a few at a time: a
    /// slower one goes on, and the next provider starts.
    /// </summary>
    /// <remarks>
    /// Starting a provider loads the computer at first (a process, an agent); a probe that takes longer mostly waits for
    /// an answer, and must not keep the providers after it waiting. Nothing fails at this time: it is long enough that a
    /// computer under load does not start every provider at once.
    /// </remarks>
    public TimeSpan PreparationPlaceTime { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Describes a published model-provider state change.
/// </summary>
/// <param name="State">The latest provider state snapshot.</param>
public sealed record ModelProviderStateChanged(ModelProviderStateSnapshot State);
