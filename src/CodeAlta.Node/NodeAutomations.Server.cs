using CodeAlta.Automations;
using CodeAlta.Fleet.Protocol;

namespace CodeAlta.Node;

// The automations a server gave this node: the due times claimed with the server before their runs start, and their
// runs reported to it as facts (never the prompt, the answer or what the trigger was about).
internal sealed partial class NodeAutomations
{
    private const int RunsReported = 20;
    private const int RunsPerReport = 100;
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReportDelay = TimeSpan.FromMilliseconds(500);

    // What was last reported of each run, per server link: a run is sent again only when it changed.
    private readonly Dictionary<string, Dictionary<string, string>> _reported = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reports = new(1, 1);
    private readonly CancellationTokenSource _reportsStop = new();
    private int _reportQueued;
    private Task _reportTask = Task.CompletedTask;

    /// <summary>
    /// Gets or sets what claims a due time with the server of a link (<see cref="FleetAutomationMethods.Claim"/>); its
    /// answer is null while that server is away, and the node then runs the time it claimed itself. Null claims with none.
    /// </summary>
    internal Func<string, FleetAutomationClaimRequest, CancellationToken, Task<FleetAutomationClaimResult?>>? ServerClaim { get; set; }

    /// <summary>Gets or sets what reports runs to the server of a link (<see cref="FleetAutomationMethods.Runs"/>), and says whether it received them; null reports nothing.</summary>
    internal Func<string, FleetAutomationRunFacts, CancellationToken, Task<bool>>? ServerReport { get; set; }

    /// <summary>
    /// Confirms with its server a due time this node claimed for an automation the server gave it: refused when a run took
    /// that time already, on this machine or another one the automation was on before.
    /// </summary>
    /// <returns>Why the run does not start; null when it starts (the server agreed, or did not answer).</returns>
    internal async ValueTask<string?> ConfirmWithServerAsync(string automationId, string occurrence, string runId, CancellationToken cancellationToken)
    {
        if (AssignmentOf(automationId) is not { Via: { } link } assignment || ServerClaim is not { } claim) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ClaimTimeout);
        FleetAutomationClaimResult? result;
        try
        {
            result = await claim(link, new FleetAutomationClaimRequest(automationId, assignment.Number, occurrence, runId,
                _options.TimeProvider.GetUtcNow() + AutomationClaimStore.LeaseDuration), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = null;
        }

        if (result is null)
        {
            // The schedule fires while its server is away: the claim of this node holds, and the run is reported later.
            Log($"The server did not answer the claim of {occurrence} of the automation {automationId}: this machine runs it.");
            return null;
        }

        return result.Granted ? null : result.Message ?? "This due time was already taken by another run.";
    }

    /// <summary>Reports to the server of a link the runs of the automations it gave this node: all of the latest ones, or only those that changed.</summary>
    /// <param name="linkId">The server link.</param>
    /// <param name="all">Whether to send them all again (the connection is new: the server may have missed some).</param>
    /// <param name="cancellationToken">Cancels the report.</param>
    /// <returns>A task that completes once they were sent or could not be.</returns>
    internal async Task ReportRunsAsync(string linkId, bool all, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(linkId);
        if (ServerReport is not { } report) return;
        await _reports.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] ids;
            Dictionary<string, long> numbers;
            lock (_gate)
            {
                var given = _assignments.Where(pair => pair.Value.Via == linkId).ToArray();
                ids = [.. given.Select(static pair => pair.Key)];
                numbers = given.ToDictionary(static pair => pair.Key, static pair => pair.Value.Number, StringComparer.Ordinal);
            }

            if (!_reported.TryGetValue(linkId, out var reported) || all) _reported[linkId] = reported = new(StringComparer.Ordinal);
            var changed = new List<(FleetAutomationRunFact Fact, string Key)>();
            foreach (var id in ids)
            {
                foreach (var run in Engine.Runs(id, RunsReported))
                {
                    var fact = new FleetAutomationRunFact(run.Id, run.AutomationId, numbers[id], run.Trigger, run.Status, run.StartedAt, run.EndedAt)
                    {
                        SessionId = run.SessionId,
                        // Why a run failed or was skipped, never what it answered nor what its trigger was about.
                        Message = run.Status is AutomationRun.Failed or AutomationRun.Skipped ? run.Message : null,
                    };
                    var key = $"{fact.Status}|{fact.EndedAt?.ToUnixTimeMilliseconds()}|{fact.SessionId}|{fact.Number}";
                    if (!reported.TryGetValue(run.Id, out var known) || known != key) changed.Add((fact, key));
                }
            }

            foreach (var batch in changed.Chunk(RunsPerReport))
            {
                if (!await report(linkId, new FleetAutomationRunFacts([.. batch.Select(static item => item.Fact)]), cancellationToken).ConfigureAwait(false)) return;
                foreach (var (fact, key) in batch) reported[fact.RunId] = key;
            }

            // Forgets what no longer is among the latest runs.
            var latest = changed.Select(static item => item.Fact.RunId).Concat(ids.SelectMany(id => Engine.Runs(id, RunsReported).Select(static run => run.Id))).ToHashSet(StringComparer.Ordinal);
            foreach (var runId in reported.Keys.Where(runId => !latest.Contains(runId)).ToArray()) reported.Remove(runId);
        }
        finally
        {
            _reports.Release();
        }
    }

    // The runs changed: they are reported in a moment, gathered.
    private void PokeReports()
    {
        if (ServerReport is null || Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _reportQueued, 1) != 0) return;
        var token = _reportsStop.Token;
        _reportTask = Task.Run(async () =>
        {
            try
            {
                // Real time, not the clock of the schedules: the runs are gathered for a moment, whatever the schedules see.
                await Task.Delay(ReportDelay, token).ConfigureAwait(false);
                Volatile.Write(ref _reportQueued, 0);
                string[] links;
                lock (_gate) links = [.. _assignments.Values.Select(static assignment => assignment.Via).OfType<string>().Distinct(StringComparer.Ordinal)];
                foreach (var link in links) await ReportRunsAsync(link, all: false, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Volatile.Write(ref _reportQueued, 0);
                Log($"The runs of the automations could not be reported to their server: {exception.Message}");
            }
        }, CancellationToken.None);
    }

    private async Task StopReportsAsync()
    {
        await _reportsStop.CancelAsync().ConfigureAwait(false);
        try { await _reportTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _reportsStop.Dispose();
    }
}

/// <summary>The claims of a node: kept in its file, and confirmed with the server of an automation that server gave.</summary>
/// <param name="local">The claims of this node.</param>
/// <param name="automations">The automations of the node, which know where each came from.</param>
internal sealed class NodeAutomationClaims(AutomationClaimStore local, NodeAutomations automations) : IAutomationClaims
{
    /// <inheritdoc />
    public bool TryClaim(string automationId, string occurrence, string runId, DateTimeOffset now) => local.TryClaim(automationId, occurrence, runId, now);

    /// <inheritdoc />
    public void Renew(DateTimeOffset now) => local.Renew(now);

    /// <inheritdoc />
    public void Release(string runId, DateTimeOffset now) => local.Release(runId, now);

    /// <inheritdoc />
    public ValueTask<string?> ConfirmAsync(string automationId, string occurrence, string runId, CancellationToken cancellationToken)
        => automations.ConfirmWithServerAsync(automationId, occurrence, runId, cancellationToken);
}
