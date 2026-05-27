using CodeAlta.Agent.LocalRuntime;

namespace CodeAlta.Agent;

/// <summary>
/// Creates runtimes for model provider definitions owned by a host-specific registry builder.
/// </summary>
public interface IModelProviderAdapter
{
    /// <summary>
    /// Gets the provider adapter type, such as <c>openai-chat</c> or <c>anthropic</c>.
    /// </summary>
    string ProviderType { get; }

    /// <summary>
    /// Returns whether this adapter can create a runtime for the provider descriptor.
    /// </summary>
    /// <param name="descriptor">The provider descriptor.</param>
    /// <returns><see langword="true" /> when the adapter supports the provider; otherwise <see langword="false" />.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is <see langword="null" />.</exception>
    bool CanCreateRuntime(ModelProviderDescriptor descriptor);

    /// <summary>
    /// Creates a provider runtime for the descriptor.
    /// </summary>
    /// <param name="descriptor">The provider descriptor.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The created provider runtime.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor" /> is <see langword="null" />.</exception>
    ValueTask<IModelProviderRuntime> CreateRuntimeAsync(
        ModelProviderDescriptor descriptor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents an initialized model provider runtime.
/// </summary>
public interface IModelProviderRuntime : IAsyncDisposable
{
    /// <summary>
    /// Gets the provider descriptor.
    /// </summary>
    ModelProviderDescriptor Descriptor { get; }

    /// <summary>
    /// Starts the provider runtime and performs any required lightweight handshake.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the provider runtime and releases runtime resources.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes the provider for readiness and model metadata.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a turn executor for this provider runtime.
    /// </summary>
    IModelProviderTurnExecutor CreateTurnExecutor();
}

/// <summary>
/// Represents a CodeAlta-owned provider runtime that can be attached to the local session runtime.
/// </summary>
public interface ICodeAltaModelProviderRuntime : IModelProviderRuntime
{
    /// <summary>
    /// Gets the provider descriptor used by CodeAlta session execution.
    /// </summary>
    ModelProviderRuntimeDescriptor RuntimeDescriptor { get; }

    /// <summary>
    /// Gets the optional provider model catalog used for probing.
    /// </summary>
    IModelProviderModelCatalog? ModelCatalog { get; }

    /// <summary>
    /// Creates the provider registration consumed by the CodeAlta session runtime.
    /// </summary>
    /// <returns>The provider registration.</returns>
    CodeAltaAgentRuntimeProviderRegistration CreateProviderRegistration();
}

/// <summary>
/// Executes turns against a model provider.
/// </summary>
public interface IModelProviderTurnExecutor
{
    /// <summary>
    /// Executes a single assistant turn.
    /// </summary>
    /// <param name="request">The turn request.</param>
    /// <param name="onUpdate">Streaming callback used for best-effort progress projection.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The final assistant turn response.</returns>
    Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a single assistant turn with a callback for provider session updates.
    /// </summary>
    /// <param name="request">The turn request.</param>
    /// <param name="onUpdate">Streaming callback used for best-effort progress projection.</param>
    /// <param name="onSessionUpdate">Session update callback used for best-effort status projection.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The final assistant turn response.</returns>
    Task<LocalAgentTurnResponse> ExecuteTurnAsync(
        LocalAgentTurnRequest request,
        Func<LocalAgentTurnDelta, CancellationToken, ValueTask> onUpdate,
        Func<LocalAgentTurnSessionUpdate, CancellationToken, ValueTask> onSessionUpdate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSessionUpdate);
        return ExecuteTurnAsync(request, onUpdate, cancellationToken);
    }
}
