using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using XenoAtom.Logging;

namespace CodeAlta.Agent;

/// <summary>
/// Default model-provider initialization and model-catalog service.
/// </summary>
public sealed class ModelProviderInitializationService : IModelProviderInitializationService
{
    // A provider that fails says why in the log: the window names the reason, the log keeps what the provider said.
    private const string LoggerName = "CodeAlta.App.ModelProviderInitialization";

    private readonly IModelProviderRegistry _registry;
    private readonly ModelProviderInitializationOptions _options;
    private readonly ISessionRetentionAdmission? _retentionAdmission;
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelProviderStateSnapshot> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> _refreshTasks = new(StringComparer.OrdinalIgnoreCase);
    // The registration of the registry each state was last prepared from: a provider registered again (its plugin
    // reloaded, its settings applied) has another runtime, whose models are read again (#296).
    private readonly Dictionary<string, long> _preparedRegistrations = new(StringComparer.OrdinalIgnoreCase);
    // The providers whose running refresh is quiet (RevalidateProviderAsync): it keeps the list it had when it fails.
    private readonly HashSet<string> _quietRefreshes = new(StringComparer.OrdinalIgnoreCase);
    // When the models of each provider were last asked again, so that a provider that does not answer is not asked at each look.
    private readonly Dictionary<string, DateTimeOffset> _revalidations = new(StringComparer.OrdinalIgnoreCase);
    private Task? _drainTask;
    private bool _closed;
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
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the configured probe timeout or place time is not positive.</exception>
    public ModelProviderInitializationService(
        IModelProviderRegistry registry,
        ModelProviderInitializationOptions? options = null)
        : this(registry, options, null)
    {
    }

    /// <summary>Creates a model service whose original provider preparations participate in retention admission.</summary>
    /// <param name="registry">The provider registry.</param>
    /// <param name="options">Optional initialization settings.</param>
    /// <param name="retentionAdmission">The host registry, installed before any provider preparation begins.</param>
    /// <exception cref="ArgumentNullException">The registry is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The configured probe timeout or place time is not positive.</exception>
    public ModelProviderInitializationService(IModelProviderRegistry registry, ModelProviderInitializationOptions? options,
        ISessionRetentionAdmission? retentionAdmission)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _retentionAdmission = retentionAdmission;
        _options = options ?? new ModelProviderInitializationOptions();
        if (_options.DefaultProbeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.DefaultProbeTimeout, "The default provider probe timeout must be positive.");
        }

        if (_options.PreparationPlaceTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.PreparationPlaceTime, "The time a preparation keeps its place must be positive.");
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
                    _preparedRegistrations.Remove(key);
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
    /// <exception cref="ObjectDisposedException">Original preparation admission has closed for shutdown.</exception>
    /// <exception cref="InvalidOperationException">Retention currently owns a session and refuses new preparation.</exception>
    /// <exception cref="AgentDependencyRetentionException">A provider preparation retains unconfirmed dependencies.</exception>
    public async Task InitializeAllAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _registry.ListProviders(includeDisabled: true)
            .Select(descriptor => StartRefreshAsync(descriptor, cancellationToken))
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Prepares every registered provider, at most <paramref name="maxConcurrency"/> at a time: a provider can start a
    /// process (a CLI, an agent) or download its models, and starting them all at once loads the computer when the
    /// application starts. A provider asked for meanwhile (<see cref="GetModelsAsync"/>) is prepared at once.
    /// </summary>
    /// <param name="maxConcurrency">The most providers prepared at the same time.</param>
    /// <param name="cancellationToken">Stops starting providers; the preparations started go on.</param>
    /// <returns>A task that completes when every provider was prepared.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxConcurrency"/> is less than 1.</exception>
    /// <exception cref="ObjectDisposedException">Original preparation admission has closed for shutdown.</exception>
    /// <exception cref="InvalidOperationException">Retention currently owns a session and refuses new preparation.</exception>
    /// <exception cref="AgentDependencyRetentionException">A provider preparation retains unconfirmed dependencies.</exception>
    public async Task InitializeAllAsync(int maxConcurrency, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        using var slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = _registry.ListProviders(includeDisabled: true)
            .Select(async descriptor =>
            {
                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                Task refresh;
                try
                {
                    refresh = StartRefreshAsync(descriptor, cancellationToken);
                    // A slow provider (an agent that takes its time to answer) gives its place to the next ones: it waits
                    // more than it loads the computer.
                    try { await refresh.WaitAsync(_options.PreparationPlaceTime, CancellationToken.None).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                finally
                {
                    slots.Release();
                }

                await refresh.ConfigureAwait(false);
            })
            .ToArray();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">Original preparation admission has closed for shutdown.</exception>
    /// <exception cref="InvalidOperationException">Retention currently owns a session and refuses new preparation.</exception>
    /// <exception cref="AgentDependencyRetentionException">A provider preparation retains unconfirmed dependencies.</exception>
    public Task RefreshProviderAsync(ModelProviderId providerId, CancellationToken cancellationToken = default)
    {
        var descriptor = GetProviderDescriptor(providerId);
        return StartRefreshAsync(descriptor, cancellationToken);
    }

    /// <summary>
    /// Prepares a provider again after something it depends on changed, such as the sign-in of its account: the state
    /// comes from a probe that started after the change. A preparation already running is not joined, since it may have
    /// read the account before the change; it ends, then a new one starts.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="cancellationToken">Stops waiting; a preparation that started goes on.</param>
    /// <returns>A task that completes when the provider was prepared again.</returns>
    /// <exception cref="KeyNotFoundException">The provider is not registered.</exception>
    /// <exception cref="ObjectDisposedException">Original preparation admission has closed for shutdown.</exception>
    /// <exception cref="InvalidOperationException">Retention currently owns a session and refuses new preparation.</exception>
    /// <exception cref="AgentDependencyRetentionException">A provider preparation retains unconfirmed dependencies.</exception>
    public async Task RefreshProviderAfterChangeAsync(ModelProviderId providerId, CancellationToken cancellationToken = default)
    {
        var key = ModelProviderId.NormalizeValue(GetProviderDescriptor(providerId).ProviderId.Value);
        Task? running;
        lock (_gate)
        {
            running = _refreshTasks.TryGetValue(key, out var task) && !task.IsCompleted ? task : null;
        }

        if (running is not null)
        {
            try
            {
                await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Its outcome is replaced by the preparation below; a retained one is reported by it.
            }
        }

        // Read again: the change may have replaced the provider (a provider enabled, settings applied).
        await StartRefreshAsync(GetProviderDescriptor(providerId), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the models of a provider again when what is known of them is older than <paramref name="maxAge"/>: an
    /// endpoint can add or drop models while CodeAlta runs. A ready provider stays ready, with its models, while they are
    /// read, and keeps them when the read fails (the log says why, and Test provider shows it); a provider that is not
    /// ready is prepared as <see cref="RefreshProviderAsync"/> does. A read already running is joined first, whatever the
    /// age: its answer is newer than the list. A provider is asked again at most once per <paramref name="maxAge"/>, also
    /// when it does not answer.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="maxAge">How old its models may be; <see cref="TimeSpan.Zero"/> reads them now.</param>
    /// <param name="cancellationToken">Stops waiting; a read that started goes on.</param>
    /// <returns>The state of the provider after the read; null when it has none.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxAge"/> is negative.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; a read that started goes on.</exception>
    /// <exception cref="KeyNotFoundException">The provider is not registered.</exception>
    /// <exception cref="ObjectDisposedException">Original preparation admission has closed for shutdown.</exception>
    /// <exception cref="InvalidOperationException">Retention currently owns a session and refuses new preparation.</exception>
    /// <exception cref="AgentDependencyRetentionException">A provider preparation retains unconfirmed dependencies.</exception>
    public async Task<ModelProviderStateSnapshot?> RevalidateProviderAsync(ModelProviderId providerId, TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAge, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = GetProviderDescriptor(providerId);
        var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
        var prepared = IsPreparedFromCurrentRegistration(key, descriptor.ProviderId);
        var state = TryGetState(key);
        if (!descriptor.IsEnabled) return state;
        var now = DateTimeOffset.UtcNow;
        bool running;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            running = _refreshTasks.TryGetValue(key, out var task) && !task.IsCompleted;
            if (!running && prepared && (_revalidations.TryGetValue(key, out var asked) && now - asked < maxAge
                || state is { Availability: ModelProviderAvailability.Ready } && now - state.ObservedAt < maxAge))
            {
                return state;
            }
        }

        // A read that runs is joined (StartRefreshAsync joins it); otherwise one starts, and only an admitted read counts as asked.
        var reading = StartRefreshAsync(descriptor, cancellationToken, quiet: prepared && state is { Availability: ModelProviderAvailability.Ready });
        if (!running)
        {
            lock (_gate) _revalidations[key] = now;
        }

        await reading.ConfigureAwait(false);
        return TryGetState(key);
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">A required preparation cannot start after admission closes.</exception>
    /// <exception cref="InvalidOperationException">Retention currently owns a session and refuses required preparation.</exception>
    /// <exception cref="AgentDependencyRetentionException">A provider preparation retains unconfirmed dependencies.</exception>
    public async Task<IReadOnlyList<AgentModelInfo>> GetModelsAsync(
        ModelProviderId providerId,
        CancellationToken cancellationToken = default)
    {
        _ = GetProviderDescriptor(providerId);
        var key = ModelProviderId.NormalizeValue(providerId.Value);
        var state = TryGetState(key);
        if (state is not null && IsPreparedFromCurrentRegistration(key, providerId))
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

    /// <summary>
    /// Sets an option of a model of a provider (<see cref="AgentModelInfo.Options"/>), which the provider sends with the
    /// turns of the model from now on, and publishes the model with its options as they are now.
    /// </summary>
    /// <param name="providerId">The provider.</param>
    /// <param name="modelId">A model of the provider.</param>
    /// <param name="optionId">An option of the model.</param>
    /// <param name="value">One of the values of the option.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The model as it is now; null when the provider offers no option or does not list the model.</returns>
    /// <exception cref="ArgumentException">A name is blank, or the option or the value is not one the model offers.</exception>
    /// <exception cref="KeyNotFoundException">The provider is not registered.</exception>
    public async Task<AgentModelInfo?> SetModelOptionAsync(ModelProviderId providerId, string modelId, string optionId, string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var descriptor = GetProviderDescriptor(providerId);
        var runtime = await _registry.GetOrCreateRuntimeAsync(descriptor.ProviderId, cancellationToken).ConfigureAwait(false);
        if (runtime is not IModelProviderModelOptions options ||
            await options.SetModelOptionAsync(modelId, optionId, value, cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return null;
        }

        if (TryGetState(ModelProviderId.NormalizeValue(descriptor.ProviderId.Value)) is { } state &&
            state.Models.Any(listed => string.Equals(listed.Id, model.Id, StringComparison.Ordinal)))
        {
            PublishState(state with
            {
                Models = [.. state.Models.Select(listed => string.Equals(listed.Id, model.Id, StringComparison.Ordinal) ? model : listed)],
                ObservedAt = DateTimeOffset.UtcNow,
            });
        }

        return model;
    }

    private Task StartRefreshAsync(ModelProviderDescriptor descriptor, CancellationToken cancellationToken, bool quiet = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        var key = ModelProviderId.NormalizeValue(descriptor.ProviderId.Value);
        Task? refreshTask = null, join = null, afterQuiet = null;
        TaskCompletionSource? completion = null;
        IDisposable? admission = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_refreshTasks.TryGetValue(key, out var existing))
            {
                // One reading of its state decides: a refresh that completes meanwhile is not taken for one that runs.
                var running = !existing.IsCompleted;
                // A quiet read keeps the old list when it fails: a refresh that must report the state of the provider
                // (Test provider, a change) does not take its outcome, it starts once the quiet one ends.
                if (running && !quiet && _quietRefreshes.Contains(key)) afterQuiet = existing;
                else if (running || existing.Exception is { } failure && AgentDependencyRetentionException.Contains(failure)) join = existing;
            }

            if (join is null && afterQuiet is null)
            {
                // Admission precedes publication and the provider factory, and belongs to the shared original
                // refresh rather than any caller's cancelable WaitAsync below.
                admission = _retentionAdmission is null ? null : _retentionAdmission.TryProtectAll()
                    ?? throw new InvalidOperationException("Provider preparation cannot start during session retirement.");
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                refreshTask = completion.Task;
                _refreshTasks[key] = refreshTask;
                if (quiet) _quietRefreshes.Add(key);
                else _quietRefreshes.Remove(key);
            }
        }

        // Outside the lock: what follows may reach the registry, whose factories may reenter DrainAsync.
        if (afterQuiet is not null) return RefreshAfterQuietAsync(descriptor.ProviderId, afterQuiet, cancellationToken);
        if (join is not null) return join.WaitAsync(cancellationToken);
        // Publish first and invoke external callbacks outside the lock. A synchronous provider callback
        // may reenter DrainAsync; its snapshot must already include this original, even before an await.
        _ = CompleteRefreshAsync(descriptor, admission, completion!, quiet);
        return refreshTask!.WaitAsync(cancellationToken);
    }

    private async Task RefreshAfterQuietAsync(ModelProviderId providerId, Task quiet, CancellationToken cancellationToken)
    {
        try
        {
            await quiet.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Its outcome is replaced by the refresh below; a retained one is reported by it.
        }

        // Read again: the provider may have been registered again (its settings applied) while the quiet read ran.
        await StartRefreshAsync(GetProviderDescriptor(providerId), cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteRefreshAsync(ModelProviderDescriptor descriptor, IDisposable? admission, TaskCompletionSource completion, bool quiet)
    {
        Task? original = null;
        try
        {
            original = RefreshProviderCoreAsync(descriptor, admission, quiet);
            await original.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception failure)
        {
            completion.TrySetException(original?.Exception is { } aggregate ? aggregate.InnerExceptions : [failure]);
        }
    }

    /// <summary>Closes refresh admission and joins every original preparation before providers are released.</summary>
    /// <remarks>Cancelable observers do not own this drainage. Retained failures stay rooted by this service.</remarks>
    /// <returns>The one original drainage task.</returns>
    /// <exception cref="AgentDependencyRetentionException">A preparation still owns unconfirmed dependencies.</exception>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            _closed = true;
            return _drainTask ??= JoinRefreshesAsync(Task.WhenAll(_refreshTasks.Values));
        }
    }

    private async Task JoinRefreshesAsync(Task original)
    {
        try { await original.ConfigureAwait(false); }
        catch (Exception failure) when (AgentDependencyRetentionException.Contains(original.Exception ?? failure))
        {
            throw new AgentDependencyRetentionException("provider initialization", "original preparation drainage",
                [original.Exception ?? failure], new { Service = this, Original = original });
        }
    }

    // `quiet`: the provider was ready; it stays ready with its models while they are read, and keeps them when the read fails.
    private async Task RefreshProviderCoreAsync(ModelProviderDescriptor descriptor, IDisposable? admission, bool quiet)
    {
        var retained = false;
        Task? original = null;
        IDisposable? preparation = null;
        IModelProviderRuntime? runtime = null;
        CancellationTokenSource? timeout = null;
        var probeTimeout = _options.DefaultProbeTimeout;
        try
        {
            // Read before the runtime: a registration replaced meanwhile only makes the next read prepare it again.
            var registration = (_registry as ModelProviderRegistry)?.GetRegistrationVersion(descriptor.ProviderId) ?? 0;
            lock (_gate) _preparedRegistrations[ModelProviderId.NormalizeValue(descriptor.ProviderId.Value)] = registration;
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

            if (!quiet)
            {
                PublishState(new ModelProviderStateSnapshot
                {
                    Descriptor = descriptor,
                    Availability = ModelProviderAvailability.Probing,
                    StatusMessage = "Detecting provider...",
                    SelectedModelId = descriptor.DefaultModelId,
                    SelectedReasoningEffort = descriptor.DefaultReasoningEffort,
                    ObservedAt = DateTimeOffset.UtcNow,
                });
            }

            // The original and retention claim are already published, so Drain includes this acquisition.
            // Never take the registry lock under _gate: factories may synchronously reenter DrainAsync.
            // Custom registries own their own mutation/disposal synchronization.
            preparation = (_registry as ModelProviderRegistry)?.ProtectPreparation(descriptor.ProviderId);
            timeout = new CancellationTokenSource(_options.DefaultProbeTimeout);
            var acquisition = _registry.GetOrCreateRuntimeAsync(descriptor.ProviderId, timeout.Token).AsTask();
            original = acquisition;
            runtime = await acquisition.ConfigureAwait(false);
            if (runtime is IModelProviderProbeTimeout { ProbeTimeout: var longer } && longer > probeTimeout)
            {
                probeTimeout = longer;
                timeout.CancelAfter(longer);
            }
            original = runtime.StartAsync(timeout.Token);
            await original.ConfigureAwait(false);
            var probing = runtime.ProbeAsync(timeout.Token);
            original = probing;
            var probe = await probing.ConfigureAwait(false);
            var availability = probe.Availability is ModelProviderAvailability.Unknown or ModelProviderAvailability.Probing
                ? ModelProviderAvailability.Ready
                : probe.Availability;
            if (quiet && availability != ModelProviderAvailability.Ready)
            {
                KeepModels(descriptor, probe.StatusMessage ?? availability.ToString());
                return;
            }

            // The models its configuration hides are kept apart: no list offers them, and the provider page shows what its rules hide.
            var (models, hidden) = descriptor.ModelVisibility.Apply(ModelOrdering.Apply(probe.Models ?? [], descriptor.ModelOrder));

            PublishState(new ModelProviderStateSnapshot
            {
                Descriptor = runtime.Descriptor,
                Availability = availability,
                StatusMessage = BuildStatusMessage(runtime.Descriptor, availability, probe.StatusMessage, models.Count),
                Models = models,
                HiddenModels = hidden,
                SelectedModelId = string.IsNullOrWhiteSpace(probe.SelectedModelId)
                    ? runtime.Descriptor.DefaultModelId
                    : probe.SelectedModelId,
                SelectedReasoningEffort = probe.SelectedReasoningEffort ?? runtime.Descriptor.DefaultReasoningEffort,
                ErrorCategory = probe.ErrorCategory,
                Capabilities = probe.Capabilities ?? [],
                ObservedAt = DateTimeOffset.UtcNow,
            });
        }
        catch (Exception failure) when (AgentDependencyRetentionException.Contains(original?.Exception ?? failure))
        {
            retained = true;
            throw new AgentDependencyRetentionException("provider initialization", "unconfirmed dependency release", [original?.Exception ?? failure],
                new RetainedPreparation(this, admission, preparation, runtime, original, timeout));
        }
        catch (OperationCanceledException)
        {
            if (quiet) KeepModels(descriptor, $"no answer after {probeTimeout}");
            else PublishFailureState(descriptor, ModelProviderAvailability.Failed, $"Provider probe timed out after {probeTimeout}.", "Timeout");
        }
        catch (Exception ex)
        {
            var (availability, message, category) = ClassifyFailure(ex);
            if (quiet) KeepModels(descriptor, message);
            else PublishFailureState(descriptor, availability, message, category);
        }
        finally
        {
            if (!retained)
            {
                timeout?.Dispose();
                preparation?.Dispose();
                admission?.Dispose();
            }
            // Keep the original outcome until replacement or shutdown. In particular a swallowed RPC
            // failure cannot drop its dependency graph, and synchronous completion cannot remove a
            // newer refresh (or leave a stale completed task masquerading as in-flight work).
        }
    }

    internal sealed record RetainedPreparation(ModelProviderInitializationService Service, IDisposable? Admission,
        IDisposable? Preparation, IModelProviderRuntime? Runtime, Task? Original, CancellationTokenSource? Cancellation);

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

    // Whether the state of a provider comes from its registration as it is now. A custom registry has no version of its
    // registrations: its states are kept until a refresh.
    private bool IsPreparedFromCurrentRegistration(string key, ModelProviderId providerId)
    {
        if (_registry is not ModelProviderRegistry registry)
        {
            return true;
        }

        // Never take the registry lock under _gate.
        var current = registry.GetRegistrationVersion(providerId);
        lock (_gate)
        {
            return _preparedRegistrations.TryGetValue(key, out var prepared) && prepared == current;
        }
    }

    private ModelProviderStateSnapshot? TryGetState(string key)
    {
        lock (_gate)
        {
            return _states.TryGetValue(key, out var state) ? state : null;
        }
    }

    // A quiet read that failed: the provider keeps the models it listed, so that an endpoint that does not answer for a
    // moment does not empty the lists of the window. The log keeps why; Test provider shows it.
    private static void KeepModels(ModelProviderDescriptor descriptor, string reason)
    {
        if (LogManager.IsInitialized)
            LogManager.GetLogger(LoggerName).Info($"The models of the provider {descriptor.ProviderId.Value} could not be read again; its list is kept: {reason}");
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
        ModelProviderStateSnapshot? previous;
        lock (_gate)
        {
            _states.TryGetValue(key, out previous);
            _states[key] = state;
        }

        if (LogManager.IsInitialized && state.Availability is ModelProviderAvailability.Failed or ModelProviderAvailability.Unsupported
            && (previous is null || previous.Availability != state.Availability || !string.Equals(previous.StatusMessage, state.StatusMessage, StringComparison.Ordinal)))
        {
            var category = state.ErrorCategory is { Length: > 0 } value ? $" ({value})" : string.Empty;
            LogManager.GetLogger(LoggerName).Warn($"The provider {state.ProviderId.Value} is {state.Availability}{category}: {state.StatusMessage ?? "no message"}");
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
