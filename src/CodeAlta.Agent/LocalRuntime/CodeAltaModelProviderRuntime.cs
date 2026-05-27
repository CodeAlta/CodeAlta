namespace CodeAlta.Agent.LocalRuntime;

/// <summary>
/// Native CodeAlta model-provider runtime backed by a provider descriptor, model catalog, and turn executor.
/// </summary>
public sealed class CodeAltaModelProviderRuntime : ICodeAltaModelProviderRuntime
{
    private readonly IModelProviderTurnExecutor _turnExecutor;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodeAltaModelProviderRuntime" /> class.
    /// </summary>
    /// <param name="descriptor">The public provider descriptor.</param>
    /// <param name="runtimeDescriptor">The session-runtime provider descriptor.</param>
    /// <param name="turnExecutor">The provider turn executor.</param>
    /// <param name="modelCatalog">The optional model catalog used for probing.</param>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is <see langword="null" />.</exception>
    public CodeAltaModelProviderRuntime(
        ModelProviderDescriptor descriptor,
        ModelProviderRuntimeDescriptor runtimeDescriptor,
        IModelProviderTurnExecutor turnExecutor,
        IModelProviderModelCatalog? modelCatalog = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        RuntimeDescriptor = runtimeDescriptor ?? throw new ArgumentNullException(nameof(runtimeDescriptor));
        _turnExecutor = turnExecutor ?? throw new ArgumentNullException(nameof(turnExecutor));
        ModelCatalog = modelCatalog ?? turnExecutor as IModelProviderModelCatalog;
    }

    /// <inheritdoc />
    public ModelProviderDescriptor Descriptor { get; }

    /// <inheritdoc />
    public ModelProviderRuntimeDescriptor RuntimeDescriptor { get; }

    /// <inheritdoc />
    public IModelProviderModelCatalog? ModelCatalog { get; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _started = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var models = ModelCatalog is null
            ? []
            : await ModelCatalog.ListModelsAsync(RuntimeDescriptor, cancellationToken).ConfigureAwait(false);

        return new ModelProviderProbeResult
        {
            ProviderId = Descriptor.ProviderId,
            Availability = Descriptor.IsEnabled ? ModelProviderAvailability.Ready : ModelProviderAvailability.Disabled,
            Models = models,
            SelectedModelId = Descriptor.DefaultModelId,
            SelectedReasoningEffort = Descriptor.DefaultReasoningEffort,
        };
    }

    /// <inheritdoc />
    public IModelProviderTurnExecutor CreateTurnExecutor() => _turnExecutor;

    /// <inheritdoc />
    public CodeAltaAgentRuntimeProviderRegistration CreateProviderRegistration()
    {
        return new CodeAltaAgentRuntimeProviderRegistration
        {
            Provider = RuntimeDescriptor,
            TurnExecutor = _turnExecutor,
            ModelCatalog = ModelCatalog,
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            await StopAsync().ConfigureAwait(false);
        }

        switch (_turnExecutor)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
