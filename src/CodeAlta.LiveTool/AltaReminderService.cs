using System.Globalization;

namespace CodeAlta.LiveTool;

internal sealed class AltaReminderService(IServiceProvider services)
{
    private static readonly TimeSpan MaximumDelayChunk = TimeSpan.FromDays(1);

    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));
    private readonly object _gate = new();
    private readonly Dictionary<string, ReminderEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

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

        var now = DateTimeOffset.UtcNow;
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
        return entry.Descriptor;
    }

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

    public bool TryDelete(string reminderId, out AltaReminderDescriptor? descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        ReminderEntry? entry;
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
                CompletedAt = DateTimeOffset.UtcNow,
            };
            entry.Descriptor = descriptor;
        }

        entry.Cancel();
        return true;
    }

    private async Task RunReminderAsync(ReminderEntry entry)
    {
        try
        {
            while (true)
            {
                DateTimeOffset dueAt;
                lock (_gate)
                {
                    if (!ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) || entry.IsCancellationRequested)
                    {
                        return;
                    }

                    dueAt = entry.Descriptor.DueAt ?? DateTimeOffset.UtcNow;
                }

                while (true)
                {
                    var delay = dueAt - DateTimeOffset.UtcNow;
                    if (delay <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await Task.Delay(delay > MaximumDelayChunk ? MaximumDelayChunk : delay, entry.CancellationToken).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    if (!ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) || entry.IsCancellationRequested)
                    {
                        return;
                    }
                }

                var delivery = await DeliverAsync(entry).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry) || entry.IsCancellationRequested)
                    {
                        return;
                    }

                    var firedCount = entry.Descriptor.FiredCount + 1;
                    var completed = firedCount >= entry.Descriptor.RepeatCount;
                    entry.Descriptor = entry.Descriptor with
                    {
                        FiredCount = firedCount,
                        LastFiredAt = DateTimeOffset.UtcNow,
                        LastExitCode = delivery.ExitCode,
                        LastError = delivery.Error,
                        LastTranscriptPreview = CreatePreview(delivery.Transcript),
                        State = completed ? AltaReminderStates.Completed : AltaReminderStates.Active,
                        DueAt = completed ? null : DateTimeOffset.UtcNow + entry.Descriptor.Duration,
                        CompletedAt = completed ? DateTimeOffset.UtcNow : null,
                    };

                    if (completed)
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (entry.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_entries.GetValueOrDefault(entry.Descriptor.ReminderId), entry))
                {
                    entry.Descriptor = entry.Descriptor with
                    {
                        State = AltaReminderStates.Completed,
                        CompletedAt = DateTimeOffset.UtcNow,
                        LastExitCode = AltaExitCodes.Failure,
                        LastError = ex.Message,
                        LastTranscriptPreview = CreatePreview(ex.ToString()),
                    };
                }
            }
        }
        finally
        {
            entry.DisposeCancellation();
        }
    }

    private async Task<AltaReminderDeliveryResult> DeliverAsync(ReminderEntry entry)
    {
        var dispatcher = _services.Get<AltaCommandDispatcher>() ?? new AltaCommandDispatcher(new AltaCommandRegistry(), _services);
        var caller = new AltaCallerIdentity
        {
            Kind = "reminder",
            SourceSessionId = entry.Descriptor.SourceSessionId,
            SourceAgentId = entry.Descriptor.SourceAgentId,
            SourceProjectId = entry.Descriptor.SourceProjectId,
            PluginRuntimeKey = entry.Descriptor.PluginRuntimeKey,
        };

        try
        {
            var result = await dispatcher.InvokeAsync(
                    ["session", "send", entry.Descriptor.TargetSessionId, "--stdin", "--queue-if-busy"],
                    entry.Content,
                    caller,
                    entry.Descriptor.Cwd,
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

    private sealed class ReminderEntry(AltaReminderDescriptor descriptor, string content)
    {
        private readonly CancellationTokenSource _cancellation = new();

        public AltaReminderDescriptor Descriptor { get; set; } = descriptor;

        public string Content { get; } = content;

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

internal sealed record AltaReminderCreateRequest
{
    public required string TargetSessionId { get; init; }

    public required string Content { get; init; }

    public required TimeSpan Duration { get; init; }

    public required int RepeatCount { get; init; }

    public string? SourceSessionId { get; init; }

    public string? SourceAgentId { get; init; }

    public string? SourceProjectId { get; init; }

    public string? PluginRuntimeKey { get; init; }

    public string? Cwd { get; init; }
}

internal sealed record AltaReminderDescriptor
{
    public required string ReminderId { get; init; }

    public required string TargetSessionId { get; init; }

    public string? SourceSessionId { get; init; }

    public string? SourceAgentId { get; init; }

    public string? SourceProjectId { get; init; }

    public string? PluginRuntimeKey { get; init; }

    public string? Cwd { get; init; }

    public required TimeSpan Duration { get; init; }

    public required int RepeatCount { get; init; }

    public required int FiredCount { get; init; }

    public required string State { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset? DueAt { get; init; }

    public DateTimeOffset? LastFiredAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public int? LastExitCode { get; init; }

    public string? LastError { get; init; }

    public string? LastTranscriptPreview { get; init; }

    public required string ContentPreview { get; init; }
}

internal static class AltaReminderStates
{
    public const string Active = "active";

    public const string Completed = "completed";

    public const string Deleted = "deleted";
}

internal sealed record AltaReminderDeliveryResult(int ExitCode, string? Error, string Transcript);
