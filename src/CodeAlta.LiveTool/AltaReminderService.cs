using System.Globalization;

namespace CodeAlta.LiveTool;

/// <summary>
/// Stores and runs in-process delayed prompt reminders for the live-tool command surface.
/// </summary>
public sealed class AltaReminderService
{
    private static readonly TimeSpan MaximumDelayChunk = TimeSpan.FromDays(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Dictionary<string, ReminderEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

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
    }

    /// <summary>
    /// Occurs when the active reminder set or reminder metadata changes.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Creates and starts a delayed prompt reminder.
    /// </summary>
    /// <param name="request">The reminder creation request.</param>
    /// <returns>The created reminder descriptor.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentException">Thrown when a required string value is missing.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when duration or repeat count is not positive.</exception>
    public AltaReminderDescriptor Create(AltaReminderCreateRequest request)
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

        var entry = new ReminderEntry(descriptor, request.Content);
        lock (_gate)
        {
            _entries.Add(descriptor.ReminderId, entry);
        }

        _ = RunReminderAsync(entry);
        OnChanged();
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
    public IReadOnlyList<AltaReminderDescriptor> List(string? targetSessionId, bool includeCompleted)
    {
        lock (_gate)
        {
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
    /// </remarks>
    /// <param name="reminderId">The reminder id.</param>
    /// <param name="descriptor">Receives the deleted descriptor when found.</param>
    /// <returns><see langword="true" /> when the reminder was found and deleted.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reminderId" /> is missing.</exception>
    public bool TryDelete(string reminderId, out AltaReminderDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        ReminderEntry? entry;
        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
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
        }

        entry.Cancel();
        OnChanged();
        return true;
    }

    /// <summary>
    /// Gets the full prompt content for a reminder.
    /// </summary>
    /// <param name="reminderId">The reminder id.</param>
    /// <param name="content">Receives the prompt content when found.</param>
    /// <returns><see langword="true" /> when the reminder was found.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="reminderId" /> is missing.</exception>
    public bool TryGetContent(string reminderId, out string? content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        lock (_gate)
        {
            if (_entries.TryGetValue(reminderId, out var entry))
            {
                content = entry.Content;
                return true;
            }
        }

        content = null;
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
    public bool TryUpdateContent(string reminderId, string content, out AltaReminderDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        lock (_gate)
        {
            if (!_entries.TryGetValue(reminderId, out var entry))
            {
                descriptor = null;
                return false;
            }

            entry.Content = content;
            descriptor = entry.Descriptor with
            {
                ContentPreview = CreatePreview(content),
            };
            entry.Descriptor = descriptor;
        }

        OnChanged();
        return true;
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
                    if (!ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) || entry.IsCancellationRequested)
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
                    if (!ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) || entry.IsCancellationRequested)
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
                lock (_gate)
                {
                    if (!ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) || entry.IsCancellationRequested)
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
                }

                OnChanged();
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
            var changed = false;
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
                    changed = true;
                }
            }

            if (changed)
            {
                OnChanged();
            }
        }
        finally
        {
            entry.DisposeCancellation();
        }
    }

    private async Task<AltaReminderDeliveryResult> DeliverAsync(ReminderDeliverySnapshot snapshot)
    {
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

    private void OnChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    private sealed record ReminderDeliverySnapshot(AltaReminderDescriptor Descriptor, string Content);

    private sealed class ReminderEntry(AltaReminderDescriptor descriptor, string content)
    {
        private readonly CancellationTokenSource _cancellation = new();

        public AltaReminderDescriptor Descriptor { get; set; } = descriptor;

        public string Content { get; set; } = content;

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

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

internal sealed record AltaReminderDeliveryResult(int ExitCode, string? Error, string Transcript);
