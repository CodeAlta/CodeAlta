using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace CodeAlta.Agent;

/// <summary>
/// Default model-provider initialization and model-catalog service.
/// </summary>
public sealed class ModelProviderInitializationService : IModelProviderInitializationService
{
    private readonly IModelProviderRegistry _registry;
    private readonly ModelProviderInitializationOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelProviderStateSnapshot> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _refreshTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<ModelProviderStateChanged> _changes = Channel.CreateUnbounded<ModelProviderStateChanged>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    /// <summary>
    /// Initializes a new instance of the <see cref="ModelProviderInitializationService" /> class.
    /// </summary>
    /// <param name="registry">The model provider registry.</param>
    /// <param name="options">Optional initialization options.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the configured probe timeout is not positive.</exception>
    public ModelProviderInitializationService(
        IModelProviderRegistry registry,
        ModelProviderInitializationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _options = options ?? new ModelProviderInitializationOptions();
        if (_options.DefaultProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.DefaultProbeTimeout, "The default provider probe timeout must be positive.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelProviderStateSnapshot> CurrentStates
    {
        get
        {
            EnsureDescriptorStates();
            var registeredKeys = _registry.ListProviders(includeDisabled: true)
                .Select(static descriptor => ModelProviderId.NormalizeValue(descriptor.ProviderId.Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                foreach (var key in _states.Keys.Where(key => !registeredKeys.Contains(key)).ToArray())
                {
                    _states.Remove(key);
                }

                return _states.Values
                    .OrderBy(static state => state.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static state => state.ProviderId.Value, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelProviderStateChanged> StreamStateChangesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _changes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_changes.Reader.TryRead(out var change))
            {
                yield return change;
            }
        }
    }

    /// <inheritdoc />
    public async Task InitializeAllAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _registry.ListProviders(includeDisabled: true)
            .Select(descriptor => StartRefreshAsync(descriptor, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RefreshProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken = default)
    {
        var descriptor = GetProviderDescriptor(providerId);
        return StartRefreshAsync(descriptor, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(
        ModelProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        _ = GetProviderDescriptor(providerId);
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        var state = TryGetState(key);
        if (state is not null)
        {
            if (state.Availability is ModelProviderAvailability.Ready or
                ModelProviderAvailability.Failed or
                ModelProviderAvailability.Unsupported or
                ModelProviderAvailability.Disabled)
            {
                return state.Models;
            }
        }

        await RefreshProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
        return TryGetState(key)?.Models ?? [];
    }

    private Task StartRefreshAsync(ModelProviderDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
        Task refreshTask;
        lock (_gate)
        {
            if (_refreshTasks.TryGetValue(key, out refreshTask!))
            {
                return refreshTask.WaitAsync(cancellationToken);
            }

            refreshTask = RefreshProviderCoreAsync(descriptor, key);
            _refreshTasks[key] = refreshTask;
        }

        return refreshTask.WaitAsync(cancellationToken);
    }

    private async Task RefreshProviderCoreAsync(ModelProviderDescriptor descriptor, string key)
    {
        try
        {
            if (!descriptor.IsEnabled)
            {
                PublishState(new ModelProviderStateSnapshot
                {
                    Descriptor = descriptor,
                    Availability = ModelProviderAvailability.Disabled,
                    StatusMessage = "Provider is disabled.",
                    SelectedModelId = descriptor.DefaultModelId,
                    SelectedReasoningEffort = descriptor.DefaultReasoningEffort,
                    ObservedAt = DateTimeOffset.UtcNow,
                });
                return;
            }

            PublishState(new ModelProviderStateSnapshot
            {
                Descriptor = descriptor,
                Availability = ModelProviderAvailability.Probing,
                StatusMessage = "Detecting provider...",
                SelectedModelId = descriptor.DefaultModelId,
                SelectedReasoningEffort = descriptor.DefaultReasoningEffort,
                ObservedAt = DateTimeOffset.UtcNow,
            });

            using var timeout = new CancellationTokenSource(_options.DefaultProbeTimeout);
            var runtime = await _registry.GetOrCreateRuntimeAsync(descriptor.ProviderId, timeout.Token).ConfigureAwait(false);
            await runtime.StartAsync(timeout.Token).ConfigureAwait(false);
            var probe = await runtime.ProbeAsync(timeout.Token).ConfigureAwait(false);
            var availability = probe.Availability is ModelProviderAvailability.Unknown or ModelProviderAvailability.Probing
                ? ModelProviderAvailability.Ready
                : probe.Availability;
            var models = probe.Models ?? [];
            if (descriptor.SortModels)
            {
                models = models
                    .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            PublishState(new ModelProviderStateSnapshot
            {
                Descriptor = runtime.Descriptor,
                Availability = availability,
                StatusMessage = BuildStatusMessage(runtime.Descriptor, availability, probe.StatusMessage, models.Count),
                Models = models,
                SelectedModelId = string.IsNullOrWhiteSpace(probe.SelectedModelId)
                    ? runtime.Descriptor.DefaultModelId
                    : probe.SelectedModelId,
                SelectedReasoningEffort = probe.SelectedReasoningEffort ?? runtime.Descriptor.DefaultReasoningEffort,
                ErrorCategory = probe.ErrorCategory,
                ObservedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (OperationCanceledException)
        {
            PublishFailureState(descriptor, ModelProviderAvailability.Failed, $"Provider probe timed out after {_options.DefaultProbeTimeout}.", "Timeout");
        }
        catch (Exception ex)
        {
            var (availability, message, category) = ClassifyFailure(ex);
            PublishFailureState(descriptor, availability, message, category);
        }
        finally
        {
            lock (_gate)
            {
                _refreshTasks.Remove(key);
            }
        }
    }

    private void EnsureDescriptorStates()
    {
        foreach (var descriptor in _registry.ListProviders(includeDisabled: true))
        {
            var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
            lock (_gate)
            {
                if (_states.ContainsKey(key))
                {
                    continue;
                }
            }

            PublishState(new ModelProviderStateSnapshot
            {
                Descriptor = descriptor,
                Availability = descriptor.IsEnabled ? ModelProviderAvailability.Unknown : ModelProviderAvailability.Disabled,
                StatusMessage = descriptor.IsEnabled ? "Not initialized." : "Provider is disabled.",
                SelectedModelId = descriptor.DefaultModelId,
                SelectedReasoningEffort = descriptor.DefaultReasoningEffort,
                ObservedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private ModelProviderDescriptor GetProviderDescriptor(ModelProviderId providerId)
    {
        if (_registry.TryGetProvider(providerId, out var descriptor))
        {
            return descriptor;
        }

        var key = ModelProviderId.NormalizeValue(providerId.Value);
        throw new KeyNotFoundException($"Model provider '{key}' is not registered.");
    }

    private ModelProviderStateSnapshot? TryGetState(string key)
    {
        lock (_gate)
        {
            return _states.TryGetValue(key, out var state) ? state : null;
        }
    }

    private void PublishFailureState(
        ModelProviderDescriptor descriptor,
        ModelProviderAvailability availability,
        string statusMessage,
        string? errorCategory)
    {
        PublishState(new ModelProviderStateSnapshot
        {
            Descriptor = descriptor,
            Availability = availability,
            StatusMessage = statusMessage,
            SelectedModelId = descriptor.DefaultModelId,
            SelectedReasoningEffort = descriptor.DefaultReasoningEffort,
            ErrorCategory = errorCategory,
            ObservedAt = DateTimeOffset.UtcNow,
        });
    }

    private void PublishState(ModelProviderStateSnapshot state)
    {
        var key = ModelProviderId.NormalizeValue(state.ProviderId.Value);
        lock (_gate)
        {
            _states[key] = state;
        }

        _changes.Writer.TryWrite(new ModelProviderStateChanged(state));
    }

    private static string BuildStatusMessage(
        ModelProviderDescriptor descriptor,
        ModelProviderAvailability availability,
        string? statusMessage,
        int modelCount)
    {
        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            return statusMessage.Trim();
        }

        return availability switch
        {
            ModelProviderAvailability.Ready => modelCount == 0
                ? $"{descriptor.DisplayName} is ready. No models were reported."
                : $"{descriptor.DisplayName} is ready. {modelCount} model{(modelCount == 1 ? string.Empty : "s")} available.",
            ModelProviderAvailability.Disabled => "Provider is disabled.",
            ModelProviderAvailability.Unsupported => $"{descriptor.DisplayName} is not supported on this system.",
            ModelProviderAvailability.Failed => $"{descriptor.DisplayName} failed to initialize.",
            _ => "Provider state changed.",
        };
    }

    private static (ModelProviderAvailability Availability, string StatusMessage, string? ErrorCategory) ClassifyFailure(Exception exception)
    {
        var root = exception.GetBaseException();
        if (root is FileNotFoundException or DirectoryNotFoundException)
        {
            return (ModelProviderAvailability.Unsupported, CleanMessage(root.Message), root.GetType().Name);
        }

        if (root is Win32Exception win32Exception && win32Exception.NativeErrorCode == 2)
        {
            return (ModelProviderAvailability.Unsupported, CleanMessage(root.Message), nameof(Win32Exception));
        }

        var message = CleanMessage(root.Message);
        if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return (ModelProviderAvailability.Unsupported, message, root.GetType().Name);
        }

        return (ModelProviderAvailability.Failed, message, root.GetType().Name);
    }

    private static string CleanMessage(string? message)
        => string.IsNullOrWhiteSpace(message) ? "Provider initialization failed." : message.Trim();
}
