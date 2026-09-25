using System.Globalization;
using System.Runtime.ExceptionServices;

namespace CodeAlta.LiveTool;

/// <summary>Outcome of a reminder content edit guarded by exact owner and revision.</summary>
public enum AltaReminderContentUpdateResult
{
    /// <summary>The edit committed.</summary>
    Updated,
    /// <summary>The exact reminder or owner was not found.</summary>
    Missing,
    /// <summary>Content changed since the snapshot was read.</summary>
    Conflict,
    /// <summary>The reminder has finished firing.</summary>
    Completed,
}

/// <summary>
/// Stores and runs in-process delayed prompt reminders for the live-tool command surface.
/// </summary>
public sealed class AltaReminderService : IAsyncDisposable
{
    private static readonly TimeSpan MaximumDelayChunk = TimeSpan.FromDays(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly IAltaReminderDelivery? _delivery;
    private readonly object _gate = new();
    private readonly Dictionary<string, ReminderEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ReminderEntry> _workers = [];
    private readonly Lazy<Task> _disposeTask;
    private bool _stopping;
    private Exception? _retiredFailure;
    private AltaReminderNotificationFailure? _lastNotificationFailure;

    /// <summary>Initializes the in-process reminder service.</summary>
    /// <param name="services">Host services used when reminders deliver prompts.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services" /> is <see langword="null" />.</exception>
    public AltaReminderService(IServiceProvider services) : this(services, TimeProvider.System)
    {
    }

    internal AltaReminderService(IServiceProvider services, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _services = services;
        _timeProvider = timeProvider;
        _disposeTask = new Lazy<Task>(DisposeCoreAsync);
    }

    /// <summary>Initializes a reminder owner with a host-owned delivery route and clock.</summary>
    /// <param name="services">Services for the default command route (not used when delivery is supplied).</param>
    /// <param name="timeProvider">Clock for scheduling.</param>
    /// <param name="delivery">Host-owned delivery route; must retain admitted work until it settles.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AltaReminderService(IServiceProvider services, TimeProvider timeProvider, IAltaReminderDelivery delivery)
        : this(services, timeProvider)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        _delivery = delivery;
    }

    /// <summary>
    /// Occurs when the active reminder set or reminder metadata changes.
    /// </summary>
    /// <remarks>
    /// This is outside-gate invalidation/requery, not ordered replay. Every subscriber captured
    /// for a pass is attempted; exceptions, including observer cancellation, become bounded feedback.
    /// Failed feedback is published after the pass, so callbacks cannot rely on seeing it immediately.
    /// Blocking or infinitely reentrant observers can still prevent progress.
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>Gets the last failed notification pass published by this service, or <see langword="null" />.</summary>
    /// <remarks>
    /// Publication order is not mutation order. Successful passes do not clear this single snapshot;
    /// later failures replace it, including after deletion. Query-based feedback is process-only,
    /// not ordered event replay. Publishing diagnostics does not raise <see cref="Changed" />.
    /// Worker disposal leaves queries available; retained reminder descriptors can be historical.
    /// </remarks>
    /// <returns>The immutable failure snapshot.</returns>
    public AltaReminderNotificationFailure? GetLastNotificationFailure()
    {
        lock (_gate)
        {
            PruneCompletedWorkersUnderGate();
            return _lastNotificationFailure;
        }
    }

    /// <summary>
    /// Creates and starts a delayed prompt reminder.
    /// </summary>
    /// <param name="request">The reminder creation request.</param>
    /// <returns>The created reminder descriptor.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">Thrown when a required string value is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when duration or repeat count is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The service has started worker disposal.</exception>
    public AltaReminderDescriptor Create(AltaReminderCreateRequest request)
        => Create(request, out _);

    /// <summary>Creates a reminder and returns observer feedback without retracting the committed creation.</summary>
    /// <param name="request">The reminder creation request.</param>
    /// <param name="notificationFailure">Receives this notification pass's failure, or <see langword="null" />.</param>
    /// <returns>The created reminder descriptor.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">Thrown when a required string value is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when duration or repeat count is not positive.</exception>
    /// <exception cref="ObjectDisposedException">The service has started worker disposal.</exception>
    public AltaReminderDescriptor Create(AltaReminderCreateRequest request, out AltaReminderNotificationFailure? notificationFailure)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TargetSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Content);
        if (request.Duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Reminder duration must be positive.");
        }

        if (request.RepeatCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Reminder repeat count must be positive.");
        }

        lock (_gate)
        {
            PruneCompletedWorkersUnderGate();
            ObjectDisposedException.ThrowIf(_stopping, this);
        }

        var now = _timeProvider.GetUtcNow();
        var descriptor = new AltaReminderDescriptor
        {
            ReminderId = "reminder-" + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture),
            TargetSessionId = request.TargetSessionId.Trim(),
            SourceSessionId = NormalizeOptional(request.SourceSessionId),
            SourceAgentId = NormalizeOptional(request.SourceAgentId),
            SourceProjectId = NormalizeOptional(request.SourceProjectId),
            PluginRuntimeKey = NormalizeOptional(request.PluginRuntimeKey),
            Cwd = NormalizeOptional(request.Cwd),
            Duration = request.Duration,
            RepeatCount = request.RepeatCount,
            FiredCount = 0,
            State = AltaReminderStates.Active,
            CreatedAt = now,
            DueAt = now + request.Duration,
            ContentPreview = CreatePreview(request.Content),
        };

        var entry = new ReminderEntry(this, descriptor, request.Content);
        NotificationContext context;
        var admitted = false;
        try
        {
            lock (_gate)
            {
                PruneCompletedWorkersUnderGate();
                ObjectDisposedException.ThrowIf(_stopping, this);

                var addedToEntries = false;
                var addedToWorkers = false;
                try
                {
                    _entries.Add(descriptor.ReminderId, entry);
                    addedToEntries = true;
                    _workers.Add(entry);
                    addedToWorkers = true;
                    context = CaptureNotification(descriptor, AltaReminderChangeKind.Created);
                    admitted = true;
                }
                catch
                {
                    if (addedToWorkers)
                    {
                        _workers.RemoveAt(_workers.Count - 1);
                    }

                    if (addedToEntries)
                    {
                        _entries.Remove(descriptor.ReminderId);
                    }

                    throw;
                }
            }
        }
        finally
        {
            if (!admitted)
            {
                entry.DisposeCancellation();
            }
        }

        StartAndPublishReminderWorker(entry.StartAndRetainOriginal, entry.Publish);
        notificationFailure = OnChanged(context);
        lock (_gate)
        {
            return entry.Descriptor;
        }
    }

    /// <summary>
    /// Lists reminders, optionally filtered by target session.
    /// </summary>
    /// <param name="targetSessionId">Target session id to filter by, or <see langword="null" /> for all sessions.</param>
    /// <param name="includeCompleted">Whether to include completed reminders.</param>
    /// <returns>Reminder descriptors ordered by due time.</returns>
    /// <remarks>Queries remain available after worker disposal; retained Active descriptors can be historical and do not imply scheduling.</remarks>
    public IReadOnlyList<AltaReminderDescriptor> List(string? targetSessionId, bool includeCompleted)
    {
        lock (_gate)
        {
            PruneCompletedWorkersUnderGate();
            return _entries.Values
                .Select(static entry => entry.Descriptor)
                .Where(descriptor => includeCompleted || string.Equals(descriptor.State, AltaReminderStates.Active, StringComparison.OrdinalIgnoreCase))
                .Where(descriptor => string.IsNullOrWhiteSpace(targetSessionId) || string.Equals(descriptor.TargetSessionId, targetSessionId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(static descriptor => descriptor.DueAt ?? DateTimeOffset.MaxValue)
                .ThenBy(static descriptor => descriptor.CreatedAt)
                .ToArray();
        }
    }

    /// <summary>
    /// Deletes an active or retained reminder by id.
    /// </summary>
    /// <remarks>
    /// Deletion prevents future firing captures. An already captured delivery may still send;
    /// deletion does not retract queued prompts or abort a run.
    /// Cancellation is requested at most once across deletion and worker disposal. A losing
    /// deletion neither retries nor waits for the winner and does not receive its later error.
    /// Synchronous deletion notifications are not drained by worker disposal.
    /// </remarks>
    /// <param name="reminderId">The reminder id.</param>
    /// <param name="descriptor">Receives the deleted descriptor when found.</param>
    /// <returns><see langword="true" /> when the reminder was found and deleted.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reminderId" /> is missing.</exception>
    /// <exception cref="ObjectDisposedException">The service has started worker disposal.</exception>
    public bool TryDelete(string reminderId, out AltaReminderDescriptor? descriptor)
        => TryDelete(reminderId, out descriptor, out _);

    /// <summary>Deletes a reminder and returns observer feedback without retracting the committed deletion.</summary>
    /// <param name="reminderId">The reminder id.</param>
    /// <param name="descriptor">Receives the deleted descriptor when found.</param>
    /// <param name="notificationFailure">Receives this notification pass's failure, or <see langword="null" />.</param>
    /// <returns>Whether the reminder was found and deleted. Missing ids do not notify observers.</returns>
    /// <remarks>
    /// An already captured delivery may still send; deletion does not retract queued work or abort a run.
    /// Cancellation is requested at most once across deletion and worker disposal. A losing
    /// deletion neither retries nor waits for the winner and does not receive its later error.
    /// Synchronous deletion notifications are not drained by worker disposal.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reminderId" /> is missing.</exception>
    /// <exception cref="ObjectDisposedException">The service has started worker disposal.</exception>
    public bool TryDelete(string reminderId, out AltaReminderDescriptor? descriptor, out AltaReminderNotificationFailure? notificationFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        notificationFailure = null;
        ReminderEntry? entry;
        NotificationContext context;
        lock (_gate)
        {
            PruneCompletedWorkersUnderGate();
            ObjectDisposedException.ThrowIf(_stopping, this);
        }
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            PruneCompletedWorkersUnderGate();
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_entries.Remove(reminderId, out entry))
            {
                descriptor = null;
                return false;
            }

            descriptor = entry.Descriptor with
            {
                State = AltaReminderStates.Deleted,
                CompletedAt = now,
            };
            entry.Descriptor = descriptor;
            context = CaptureNotification(descriptor, AltaReminderChangeKind.Deleted);
        }

        var cancellationFailure = RequestCancellation(entry);
        if (cancellationFailure is not null)
        {
            ExceptionDispatchInfo.Throw(cancellationFailure);
        }
        notificationFailure = OnChanged(context);
        return true;
    }

    /// <summary>
    /// Gets the full prompt content for a reminder.
    /// </summary>
    /// <param name="reminderId">The reminder id.</param>
    /// <param name="content">Receives the prompt content when found.</param>
    /// <returns><see langword="true" /> when the reminder was found.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reminderId" /> is missing.</exception>
    /// <remarks>Queries remain available after worker disposal; retained Active descriptors can be historical and do not imply scheduling.</remarks>
    public bool TryGetContent(string reminderId, out string? content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        lock (_gate)
        {
            PruneCompletedWorkersUnderGate();
            if (_entries.TryGetValue(reminderId, out var entry))
            {
                content = entry.Content;
                return true;
            }
        }

        content = null;
        return false;
    }

    /// <summary>Reads one reminder's descriptor, full content and edit revision atomically.</summary>
    /// <remarks>The opaque revision changes on every content edit, including a change back to identical text. A deleted reminder is missing.</remarks>
    /// <param name="reminderId">Exact reminder id.</param>
    /// <param name="descriptor">Descriptor captured with the content.</param>
    /// <param name="content">Full content captured with the descriptor.</param>
    /// <param name="revision">Opaque edit revision for a guarded update.</param>
    /// <returns>Whether the reminder exists.</returns>
    /// <exception cref="ArgumentException">The id is missing.</exception>
    public bool TryGetEditSnapshot(string reminderId, out AltaReminderDescriptor? descriptor, out string? content, out string? revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        lock (_gate)
        {
            PruneCompletedWorkersUnderGate();
            if (_entries.TryGetValue(reminderId, out var entry) &&
                string.Equals(entry.Descriptor.ReminderId, reminderId, StringComparison.Ordinal))
            {
                descriptor = entry.Descriptor;
                content = entry.Content;
                revision = entry.ContentRevision.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }
        descriptor = null;
        content = revision = null;
        return false;
    }

    /// <summary>
    /// Updates the prompt content for a scheduled reminder without changing its due time.
    /// </summary>
    /// <remarks>Updates affect firings captured after the edit, not an already captured delivery.</remarks>
    /// <param name="reminderId">The reminder id.</param>
    /// <param name="content">The replacement prompt content.</param>
    /// <param name="descriptor">Receives the updated descriptor when found.</param>
    /// <returns><see langword="true" /> when the reminder was found and updated.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reminderId" /> or <paramref name="content" /> is missing.</exception>
    /// <exception cref="ObjectDisposedException">The service has started worker disposal.</exception>
    public bool TryUpdateContent(string reminderId, string content, out AltaReminderDescriptor? descriptor)
        => TryUpdateContent(reminderId, content, out descriptor, out _);

    /// <summary>Updates reminder content and returns observer feedback without retracting the committed edit.</summary>
    /// <param name="reminderId">The reminder id.</param>
    /// <param name="content">The replacement prompt content.</param>
    /// <param name="descriptor">Receives the updated descriptor when found.</param>
    /// <param name="notificationFailure">Receives this notification pass's failure, or <see langword="null" />.</param>
    /// <returns>Whether the reminder was found and updated. Missing ids do not notify observers.</returns>
    /// <remarks>Edits do not change due time and affect only firings captured after the edit.</remarks>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reminderId" /> or <paramref name="content" /> is missing.</exception>
    /// <exception cref="ObjectDisposedException">The service has started worker disposal.</exception>
    public bool TryUpdateContent(string reminderId, string content, out AltaReminderDescriptor? descriptor, out AltaReminderNotificationFailure? notificationFailure)
        => UpdateContent(reminderId, null, null, content, false, out descriptor, out notificationFailure) == AltaReminderContentUpdateResult.Updated;

    /// <summary>Atomically compares the exact owner and edit revision before replacing an active reminder's message.</summary>
    /// <remarks>Does not change schedule, counts or due time. Only future firing captures use the edit; already captured deliveries cannot be retracted. Unlike the legacy overload, completed reminders are refused.</remarks>
    /// <param name="reminderId">Exact reminder id.</param>
    /// <param name="targetSessionId">Exact owning session id.</param>
    /// <param name="expectedRevision">Opaque revision returned by <see cref="TryGetEditSnapshot" />.</param>
    /// <param name="content">Full replacement message.</param>
    /// <param name="descriptor">Updated descriptor on success, otherwise null.</param>
    /// <param name="notificationFailure">Observer feedback after a committed edit, otherwise null.</param>
    /// <returns>The result of the atomic guarded edit.</returns>
    /// <exception cref="ArgumentException">An id, revision or content is missing.</exception>
    /// <exception cref="ObjectDisposedException">Worker disposal has started.</exception>
    public AltaReminderContentUpdateResult TryUpdateContent(string reminderId, string targetSessionId, string expectedRevision,
        string content, out AltaReminderDescriptor? descriptor, out AltaReminderNotificationFailure? notificationFailure)
        => UpdateContent(reminderId, targetSessionId, expectedRevision, content, true, out descriptor, out notificationFailure);

    private AltaReminderContentUpdateResult UpdateContent(string reminderId, string? targetSessionId, string? expectedRevision,
        string content, bool guarded, out AltaReminderDescriptor? descriptor, out AltaReminderNotificationFailure? notificationFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (guarded)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetSessionId);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        }
        notificationFailure = null;
        NotificationContext context;
        lock (_gate)
        {
            PruneCompletedWorkersUnderGate();
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (!_entries.TryGetValue(reminderId, out var entry))
            {
                descriptor = null;
                return AltaReminderContentUpdateResult.Missing;
            }

            if (guarded && (!string.Equals(entry.Descriptor.ReminderId, reminderId, StringComparison.Ordinal) ||
                !string.Equals(entry.Descriptor.TargetSessionId, targetSessionId, StringComparison.Ordinal)))
            {
                descriptor = null;
                return AltaReminderContentUpdateResult.Missing;
            }
            if (guarded && entry.Descriptor.State != AltaReminderStates.Active)
            {
                descriptor = null;
                return AltaReminderContentUpdateResult.Completed;
            }
            if (guarded && !string.Equals(entry.ContentRevision.ToString(CultureInfo.InvariantCulture), expectedRevision, StringComparison.Ordinal))
            {
                descriptor = null;
                return AltaReminderContentUpdateResult.Conflict;
            }

            var nextRevision = checked(entry.ContentRevision + 1);
            entry.Content = content;
            entry.ContentRevision = nextRevision;
            descriptor = entry.Descriptor with
            {
                ContentPreview = CreatePreview(content),
            };
            entry.Descriptor = descriptor;
            context = CaptureNotification(descriptor, AltaReminderChangeKind.ContentUpdated);
        }

        notificationFailure = OnChanged(context);
        return AltaReminderContentUpdateResult.Updated;
    }

    private async Task RunReminderAsync(ReminderEntry entry)
    {
        try
        {
            while (true)
            {
                DateTimeOffset? scheduledDueAt;
                lock (_gate)
                {
                    if (_stopping ||
                        !ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) ||
                        entry.IsCancellationRequested)
                    {
                        return;
                    }

                    scheduledDueAt = entry.Descriptor.DueAt;
                }

                var dueAt = scheduledDueAt ?? _timeProvider.GetUtcNow();
                while (true)
                {
                    var delay = dueAt - _timeProvider.GetUtcNow();
                    if (delay <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await Task.Delay(delay > MaximumDelayChunk ? MaximumDelayChunk : delay, _timeProvider, entry.CancellationToken).ConfigureAwait(false);
                }

                ReminderDeliverySnapshot snapshot;
                lock (_gate)
                {
                    if (_stopping ||
                        !ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) ||
                        entry.IsCancellationRequested)
                    {
                        return;
                    }

                    // This captures one local delivery attempt, not runtime admission. Later edits
                    // affect subsequent firings; deletion cannot retract this immutable attempt.
                    snapshot = new ReminderDeliverySnapshot(entry.Descriptor, entry.Content);
                }

                var delivery = await DeliverAsync(snapshot).ConfigureAwait(false);
                var firedAt = _timeProvider.GetUtcNow();
                var completed = false;
                NotificationContext context;
                lock (_gate)
                {
                    if (_stopping ||
                        !ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) ||
                        entry.IsCancellationRequested)
                    {
                        return;
                    }

                    var firedCount = entry.Descriptor.FiredCount + 1;
                    completed = firedCount >= entry.Descriptor.RepeatCount;
                    entry.Descriptor = entry.Descriptor with
                    {
                        FiredCount = firedCount,
                        LastFiredAt = firedAt,
                        LastExitCode = delivery.ExitCode,
                        LastError = delivery.Error,
                        LastTranscriptPreview = CreatePreview(delivery.Transcript),
                        State = completed ? AltaReminderStates.Completed : AltaReminderStates.Active,
                        DueAt = completed ? null : firedAt + entry.Descriptor.Duration,
                        CompletedAt = completed ? firedAt : null,
                    };
                    context = CaptureNotification(entry.Descriptor, AltaReminderChangeKind.Fired);
                }

                OnChanged(context);
                if (completed)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (entry.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NotificationContext? context = null;
            var completedAt = _timeProvider.GetUtcNow();
            lock (_gate)
            {
                if (ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry))
                {
                    entry.Descriptor = entry.Descriptor with
                    {
                        State = AltaReminderStates.Completed,
                        CompletedAt = completedAt,
                        LastExitCode = AltaExitCodes.Failure,
                        LastError = ex.Message,
                        LastTranscriptPreview = CreatePreview(ex.ToString()),
                    };
                    context = CaptureNotification(entry.Descriptor, AltaReminderChangeKind.Failed);
                }
            }

            if (context is not null)
            {
                OnChanged(context);
            }
        }
        finally
        {
            Task cancellationReturned;
            lock (_gate)
            {
                entry.SourceClosing = true;
                cancellationReturned = entry.CancellationIssued
                    ? entry.CancellationReturned.Task
                    : Task.CompletedTask;
            }

            await FinishReminderSourceAsync(
                cancellationReturned, entry.ReleaseSource).ConfigureAwait(false);
        }
    }

    private async Task<AltaReminderDeliveryResult> DeliverAsync(ReminderDeliverySnapshot snapshot)
    {
        if (_delivery is not null)
        {
            try { return await _delivery.DeliverAsync(snapshot.Descriptor, snapshot.Content).ConfigureAwait(false); }
            catch (Exception ex) { return new AltaReminderDeliveryResult(AltaExitCodes.Failure, ex.Message, string.Empty); }
        }
        var dispatcher = _services.Get<AltaCommandDispatcher>() ?? new AltaCommandDispatcher(new AltaCommandRegistry(), _services);
        var caller = new AltaCallerIdentity
        {
            Kind = "reminder",
            SourceSessionId = snapshot.Descriptor.SourceSessionId,
            SourceAgentId = snapshot.Descriptor.SourceAgentId,
            SourceProjectId = snapshot.Descriptor.SourceProjectId,
            PluginRuntimeKey = snapshot.Descriptor.PluginRuntimeKey,
        };

        try
        {
            var result = await dispatcher.InvokeAsync(
                    ["session", "send", snapshot.Descriptor.TargetSessionId, "--stdin", "--queue-if-busy"],
                    snapshot.Content,
                    caller,
                    snapshot.Descriptor.Cwd,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return new AltaReminderDeliveryResult(result.ExitCode, result.Error, result.Transcript);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AltaReminderDeliveryResult(AltaExitCodes.Failure, ex.Message, ex.ToString());
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CreatePreview(string value)
        => value.Length <= 160 ? value : value[..160];

    private static NotificationContext CaptureNotification(AltaReminderDescriptor descriptor, AltaReminderChangeKind kind)
        => new(descriptor.ReminderId, descriptor.TargetSessionId, kind, descriptor.FiredCount);

    private AltaReminderNotificationFailure? OnChanged(NotificationContext context)
    {
        var observers = Changed;
        if (observers is null)
        {
            return null;
        }

        const int MaximumMessages = 8;
        const int MaximumMessageLength = 512;
        List<string>? messages = null;
        var failureCount = 0;
        var truncated = false;
        foreach (EventHandler observer in observers.GetInvocationList())
        {
            try
            {
                observer(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // Observer cancellation is feedback failure, not timer/dispatch cancellation.
                failureCount++;
                if (failureCount > MaximumMessages)
                {
                    truncated = true;
                    continue;
                }

                string message;
                try
                {
                    message = ex.Message;
                }
                catch (Exception)
                {
                    message = "Observer failed; its message is unavailable.";
                }

                if (message.Length > MaximumMessageLength)
                {
                    message = message[..MaximumMessageLength];
                    truncated = true;
                }

                (messages ??= []).Add(message);
            }
        }

        if (messages is null)
        {
            return null;
        }

        var failure = new AltaReminderNotificationFailure(context.ReminderId, context.TargetSessionId,
            context.ChangeKind, context.FiredCount, failureCount, messages.ToArray(), truncated);
        lock (_gate)
        {
            // Only diagnostic publication is owned here. Never restore stale entry metadata,
            // erase a newer edit, resurrect a deletion, or recursively notify about feedback.
            _lastNotificationFailure = failure;
        }

        return failure;
    }

    /// <summary>
    /// Stops new reminder-worker admission and capture, and joins retained published workers.
    /// </summary>
    /// <remarks>
    /// Repeated calls share one disposal task, snapshot and cancellation pass, including its
    /// terminal outcome. Recursive disposal and callbacks waiting for their own disposal
    /// are unsupported and can throw or deadlock. There is no termination timeout.
    ///
    /// This disposes worker resources, not all concurrent service activity. The join includes
    /// each selected worker's returned delivery, worker-fired observer pass and finally.
    /// Pre-admission preparation, rejected local acquisition cleanup and synchronous CRUD
    /// notification passes can outlive disposal. Enclosing command/output work, posted UI
    /// closures, dialogs and detached send/provider/queue descendants are not joined.
    ///
    /// Valid mutations reject after stopping; queries remain available. Stopping itself
    /// synthesizes no descriptor changes or events, so retained Active descriptors can be
    /// historical rather than evidence of continued scheduling. Ordinary completed reminder
    /// descriptors remain retained. Only the first failure retired through opportunistic
    /// pruning is retained for disposal; this is not exhaustive historical failure replay.
    /// Completed deleted worker graphs may remain until an existing-call pruning opportunity;
    /// this is not eager reclamation or an absolute byte bound. Failed disposal can retain
    /// its finite stopped worker snapshot.
    ///
    /// Invocation/publication/snapshot allocation failures do not certify termination of
    /// unknown work. This operation does not establish full service quietness or preserve
    /// externally disposed dependencies.
    /// </remarks>
    /// <returns>The shared worker-disposal operation.</returns>
    /// <exception cref="Exception">A lone observed cleanup failure is rethrown.</exception>
    /// <exception cref="OperationCanceledException">The sole observed failure is cancellation.</exception>
    /// <exception cref="AggregateException">Multiple direct failures are reported in order.</exception>
    public ValueTask DisposeAsync() => new(_disposeTask.Value);

    private async Task DisposeCoreAsync()
    {
        ReminderWorkerJoin[] workers;
        Exception? retiredFailure;
        lock (_gate)
        {
            _stopping = true;
            workers = new ReminderWorkerJoin[_workers.Count];
            for (var index = 0; index < workers.Length; index++)
            {
                workers[index] = _workers[index].Join;
            }

            retiredFailure = _retiredFailure;
        }

        await DisposeReminderWorkersAsync(workers, retiredFailure).ConfigureAwait(false);

        lock (_gate)
        {
            _workers.Clear();
        }
    }

    internal readonly record struct ReminderWorkerJoin(
        Func<Exception?> RequestCancellation,
        Task<Task> Publication,
        Func<Exception?> ReadCancellationFailure);

    /// <summary>Snapshots supplied join inputs, requests cancellation, then joins actual originals.</summary>
    /// <remarks>
    /// Shallow-copy once before ordered validation and callbacks; concurrent mutation during
    /// the copy is not made atomic. Null originals fail. Readers cannot replace worker errors.
    /// Direct order is history, indexed cancellation/read errors, indexed publication/worker
    /// errors. No flattening or deduplication. A sole observed OCE can cancel this operation
    /// even when its input task was faulted. Pending work can prevent further joins.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The input array is null.</exception>
    /// <exception cref="ArgumentException">An indexed mandatory member is null.</exception>
    /// <exception cref="Exception">A lone observed failure is rethrown through EDI.</exception>
    /// <exception cref="AggregateException">Multiple direct failures are retained in order.</exception>
    internal static Task DisposeReminderWorkersAsync(
        ReminderWorkerJoin[] workers,
        Exception? retiredFailure)
    {
        ArgumentNullException.ThrowIfNull(workers);

        var snapshot = (ReminderWorkerJoin[])workers.Clone();
        for (var index = 0; index < snapshot.Length; index++)
        {
            if (snapshot[index].RequestCancellation is null)
            {
                throw new ArgumentException(
                    $"Worker {index} has no cancellation operation.", nameof(workers));
            }

            if (snapshot[index].Publication is null)
            {
                throw new ArgumentException(
                    $"Worker {index} has no publication task.", nameof(workers));
            }

            if (snapshot[index].ReadCancellationFailure is null)
            {
                throw new ArgumentException(
                    $"Worker {index} has no cancellation-failure reader.", nameof(workers));
            }
        }

        var cancellationFailures = new Exception?[snapshot.Length];
        var workerFailures = new Exception?[snapshot.Length];
        var failures = new List<Exception>(checked(snapshot.Length * 2 + 1));
        return CoreAsync();

        async Task CoreAsync()
        {
            for (var index = 0; index < snapshot.Length; index++)
            {
                try
                {
                    cancellationFailures[index] = snapshot[index].RequestCancellation();
                }
                catch (Exception ex)
                {
                    cancellationFailures[index] = ex;
                }
            }

            for (var index = 0; index < snapshot.Length; index++)
            {
                try
                {
                    var original = await snapshot[index].Publication.ConfigureAwait(false);
                    if (original is null)
                    {
                        throw new InvalidOperationException(
                            $"Worker {index} published a null original task.");
                    }

                    await original.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    workerFailures[index] = ex;
                }

                if (cancellationFailures[index] is null)
                {
                    try
                    {
                        cancellationFailures[index] =
                            snapshot[index].ReadCancellationFailure();
                    }
                    catch (Exception ex)
                    {
                        cancellationFailures[index] = ex;
                    }
                }
            }

            if (retiredFailure is not null)
            {
                failures.Add(retiredFailure);
            }

            foreach (var failure in cancellationFailures)
            {
                if (failure is not null)
                {
                    failures.Add(failure);
                }
            }

            foreach (var failure in workerFailures)
            {
                if (failure is not null)
                {
                    failures.Add(failure);
                }
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(failures);
            }
        }
    }

    /// <summary>Runs the original-retaining action before its publication-completion action.</summary>
    /// <remarks>
    /// Mandatory actions validate synchronously. Completion failure supersedes start failure;
    /// otherwise a start failure is rethrown through EDI. Completion failure is not publication
    /// or proof of termination. The production start action assigns the actual original.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory action is null.</exception>
    /// <exception cref="Exception">Completion failure, or the earlier start failure, escapes.</exception>
    internal static void StartAndPublishReminderWorker(
        Action startAndRetainOriginal,
        Action<Exception?> completePublication)
    {
        ArgumentNullException.ThrowIfNull(startAndRetainOriginal);
        ArgumentNullException.ThrowIfNull(completePublication);

        Exception? failure = null;
        try
        {
            startAndRetainOriginal();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        completePublication(failure);

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    /// <summary>Reports cancellation traversal return to the private completion action.</summary>
    /// <remarks>
    /// Completion failure supersedes cancellation failure and does not certify a return
    /// fence or permit release. Successful completion returns the cancellation error or null.
    /// A blocking traversal is unbounded; no ordinary throwing private registration is assumed.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory action is null.</exception>
    /// <exception cref="Exception">The completion action throws.</exception>
    internal static Exception? CancelAndComplete(
        Action cancel,
        Action<Exception?> complete)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentNullException.ThrowIfNull(complete);

        Exception? failure = null;
        try
        {
            cancel();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        complete(failure);
        return failure;
    }

    /// <summary>Releases the worker source only after a successful traversal-return fence.</summary>
    /// <exception cref="ArgumentNullException">A mandatory input is null.</exception>
    /// <exception cref="Exception">The fence or release action fails; release is not retried.</exception>
    internal static Task FinishReminderSourceAsync(
        Task cancellationReturned,
        Action releaseSource)
    {
        ArgumentNullException.ThrowIfNull(cancellationReturned);
        ArgumentNullException.ThrowIfNull(releaseSource);
        return CoreAsync();

        async Task CoreAsync()
        {
            await cancellationReturned.ConfigureAwait(false);
            releaseSource();
        }
    }

    private Exception? RequestCancellation(ReminderEntry entry)
    {
        lock (_gate)
        {
            if (entry.SourceClosing || entry.CancellationIssued)
            {
                return null;
            }

            entry.CancellationIssued = true;
        }

        return CancelAndComplete(entry.CancelSource, entry.CancellationCompleted);
    }

    private void CompleteCancellation(ReminderEntry entry, Exception? failure)
    {
        lock (_gate)
        {
            entry.CancellationFailure = failure;
        }

        entry.CancellationReturned.TrySetResult();
    }

    private Exception? ReadCancellationFailure(ReminderEntry entry)
    {
        lock (_gate)
        {
            return entry.CancellationFailure;
        }
    }

    /// <summary>Synchronously observes only an actually completed supplied original.</summary>
    /// <remarks>Pending tasks return false without GetResult; this core does not mutate history.</remarks>
    /// <exception cref="ArgumentNullException">The original is null.</exception>
    internal static bool TryObserveCompletedReminder(
        Task original,
        out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(original);
        failure = null;
        if (!original.IsCompleted)
        {
            return false;
        }

        try
        {
            original.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        return true;
    }

    private void PruneCompletedWorkersUnderGate()
    {
        if (_stopping)
        {
            return;
        }

        for (var index = 0; index < _workers.Count;)
        {
            var entry = _workers[index];
            if (!entry.Publication.Task.IsCompletedSuccessfully ||
                entry.Original is not { } original)
            {
                index++;
                continue;
            }

            if (!TryObserveCompletedReminder(original, out var workerFailure))
            {
                index++;
                continue;
            }

            _retiredFailure ??= entry.CancellationFailure ?? workerFailure;
            _workers.RemoveAt(index);
        }
    }

    private sealed record NotificationContext(string ReminderId, string TargetSessionId, AltaReminderChangeKind ChangeKind, int FiredCount);

    private sealed record ReminderDeliverySnapshot(AltaReminderDescriptor Descriptor, string Content);

    private sealed class ReminderEntry
    {
        private readonly CancellationTokenSource _cancellation;

        public ReminderEntry(
            AltaReminderService owner,
            AltaReminderDescriptor descriptor,
            string content)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentNullException.ThrowIfNull(content);

            Descriptor = descriptor;
            Content = content;
            Publication = new TaskCompletionSource<Task>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationReturned = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            StartAndRetainOriginal = () => Original = owner.RunReminderAsync(this);
            Publish = CompletePublication;
            CancelSource = Cancel;
            ReleaseSource = DisposeCancellation;
            CancellationCompleted = failure => owner.CompleteCancellation(this, failure);
            Join = new ReminderWorkerJoin(
                () => owner.RequestCancellation(this),
                Publication.Task,
                () => owner.ReadCancellationFailure(this));

            // Last acquisition: no subsequent adapter/signal allocation before return.
            _cancellation = new CancellationTokenSource();
        }

        public AltaReminderDescriptor Descriptor { get; set; }

        public string Content { get; set; }

        public long ContentRevision { get; set; }

        public Task? Original { get; set; }

        public TaskCompletionSource<Task> Publication { get; }

        public TaskCompletionSource CancellationReturned { get; }

        public bool CancellationIssued { get; set; }

        public bool SourceClosing { get; set; }

        public Exception? CancellationFailure { get; set; }

        public Action StartAndRetainOriginal { get; }

        public Action<Exception?> Publish { get; }

        public Action CancelSource { get; }

        public Action ReleaseSource { get; }

        public Action<Exception?> CancellationCompleted { get; }

        public ReminderWorkerJoin Join { get; }

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        private void CompletePublication(Exception? failure)
        {
            if (Original is { } original)
            {
                Publication.TrySetResult(original);
            }
            else
            {
                Publication.TrySetException(failure ?? new InvalidOperationException(
                    "Reminder invocation returned without an original task."));
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void DisposeCancellation() => _cancellation.Dispose();
    }
}

/// <summary>
/// Request to create an in-process delayed prompt reminder.
/// </summary>
public sealed record AltaReminderCreateRequest
{
    /// <summary>Gets the session that receives the prompt when the reminder fires.</summary>
    public required string TargetSessionId { get; init; }

    /// <summary>Gets the prompt content to send when the reminder fires.</summary>
    public required string Content { get; init; }

    /// <summary>Gets the delay between creation/repeats and delivery.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets the total number of deliveries before completion.</summary>
    public required int RepeatCount { get; init; }

    /// <summary>Gets the source session id associated with the request, when any.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets the source agent id associated with the request, when any.</summary>
    public string? SourceAgentId { get; init; }

    /// <summary>Gets the source project id associated with the request, when any.</summary>
    public string? SourceProjectId { get; init; }

    /// <summary>Gets the plugin runtime key associated with the request, when any.</summary>
    public string? PluginRuntimeKey { get; init; }

    /// <summary>Gets the working directory to use when delivering the reminder, when any.</summary>
    public string? Cwd { get; init; }
}

/// <summary>
/// Describes a delayed prompt reminder managed by the in-process host.
/// </summary>
public sealed record AltaReminderDescriptor
{
    /// <summary>Gets the unique reminder id.</summary>
    public required string ReminderId { get; init; }

    /// <summary>Gets the session that receives the prompt when the reminder fires.</summary>
    public required string TargetSessionId { get; init; }

    /// <summary>Gets the source session id associated with the reminder, when any.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Gets the source agent id associated with the reminder, when any.</summary>
    public string? SourceAgentId { get; init; }

    /// <summary>Gets the source project id associated with the reminder, when any.</summary>
    public string? SourceProjectId { get; init; }

    /// <summary>Gets the plugin runtime key associated with the reminder, when any.</summary>
    public string? PluginRuntimeKey { get; init; }

    /// <summary>Gets the working directory used when delivering the reminder, when any.</summary>
    public string? Cwd { get; init; }

    /// <summary>Gets the delay between creation/repeats and delivery.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Gets the total number of deliveries before completion.</summary>
    public required int RepeatCount { get; init; }

    /// <summary>Gets how many times this reminder has fired.</summary>
    public required int FiredCount { get; init; }

    /// <summary>Gets the reminder state.</summary>
    public required string State { get; init; }

    /// <summary>Gets when the reminder was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets when the next delivery is due, or <see langword="null" /> when complete.</summary>
    public required DateTimeOffset? DueAt { get; init; }

    /// <summary>Gets when the reminder last fired, when any.</summary>
    public DateTimeOffset? LastFiredAt { get; init; }

    /// <summary>Gets when the reminder completed or was deleted, when any.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Gets the most recent delivery exit code, when any.</summary>
    public int? LastExitCode { get; init; }

    /// <summary>Gets the most recent delivery error, when any.</summary>
    public string? LastError { get; init; }

    /// <summary>Gets a preview of the most recent delivery transcript, when any.</summary>
    public string? LastTranscriptPreview { get; init; }

    /// <summary>Gets a preview of the prompt content that will be delivered.</summary>
    public required string ContentPreview { get; init; }
}

/// <summary>
/// Well-known reminder state names.
/// </summary>
public static class AltaReminderStates
{
    /// <summary>The reminder is scheduled and may fire in the future.</summary>
    public const string Active = "active";

    /// <summary>The reminder finished all requested deliveries.</summary>
    public const string Completed = "completed";

    /// <summary>The reminder was deleted before completion.</summary>
    public const string Deleted = "deleted";
}

/// <summary>Outcome of one host-owned reminder delivery attempt; success means submission, not transcript completion.</summary>
/// <param name="ExitCode">Zero for accepted delivery; nonzero for failure.</param>
/// <param name="Error">Optional bounded failure description.</param>
/// <param name="Transcript">Optional delivery transcript.</param>
public sealed record AltaReminderDeliveryResult(int ExitCode, string? Error, string Transcript);

/// <summary>Host-owned route for delivering reminder text to its exact target session.</summary>
public interface IAltaReminderDelivery
{
    /// <summary>Submits a captured firing without retrying uncertain admission.</summary>
    /// <param name="reminder">Immutable identity and schedule for this firing.</param>
    /// <param name="content">Original prompt text.</param>
    /// <returns>Outcome after the owned submission settles.</returns>
    Task<AltaReminderDeliveryResult> DeliverAsync(AltaReminderDescriptor reminder, string content);
}
