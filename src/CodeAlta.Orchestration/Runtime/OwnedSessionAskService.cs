using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>Bounded volatile ask ownership for explicitly opted-in commands. No renderer or durable authority.</summary>
public sealed class OwnedSessionAskService
{
    private readonly object _gate = new();
    private readonly AltaAskService _queue = new(); // Private: no external QueueChanged observers or direct mutations.
    private readonly Func<OwnedTextSendRequest, OwnedAskSubmission, OwnedSessionCommandAdmission> _send;
    private readonly Dictionary<string, Ask> _asks = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ActionWork> _actions = [];
    // One per admitted command, bounded by the command owner's configured receipt capacity.
    // Exhausting ask records must reject the tool, not prevent a later ordinary send from binding.
    private readonly List<OwnedSessionAskExecution> _executions = [];
    private bool _closed;

    internal OwnedSessionAskService(bool enabled, Func<OwnedTextSendRequest, OwnedAskSubmission, OwnedSessionCommandAdmission> send)
    { Enabled = enabled; _send = send; }

    /// <summary>Gets whether this host explicitly enabled restricted asks.</summary>
    public bool Enabled { get; }

    /// <summary>Validates scalar transport shape only, without granting response authority.</summary>
    public static bool IsValidHandle(OwnedAskHandle? handle) => OwnedSessionAskTool.Handle(handle);

    /// <summary>Validates a bounded exact session identity without performing lookup.</summary>
    public static bool IsValidSessionId(string? sessionId) => OwnedSessionAskTool.Identity(sessionId);

    internal OwnedSessionAskExecution CreateExecution(Guid operation, string session, CancellationToken token)
    {
        var execution = new OwnedSessionAskExecution(this, operation, session, token);
        lock (_gate)
        {
            if (_closed || !Enabled) execution.Close();
            else _executions.Add(execution);
        }
        return execution;
    }

    /// <summary>
    /// Queues an ask on behalf of a session's own <c>alta</c> tool, through the open send of that session.
    /// </summary>
    /// <param name="sessionId">The session that asks.</param>
    /// <param name="request">A validated ask; it is checked again against this owner's limits.</param>
    /// <param name="cancellationToken">Cancels before admission.</param>
    /// <returns>
    /// The queued ask, or null when asks are disabled, no send of the session is running, that send already
    /// asked, or capacity is exhausted.
    /// </returns>
    /// <exception cref="ArgumentException">The request has a file to review, or exceeds the owner's limits.</exception>
    public AltaAskQueueResult? QueueFromSession(string sessionId, AltaAskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OwnedSessionAskTool.Identity(sessionId)) throw new ArgumentException("Invalid session.", nameof(sessionId));
        var restricted = OwnedSessionAskTool.Restrict(request);
        OwnedSessionAskExecution[] candidates;
        lock (_gate)
        {
            if (_closed || !Enabled) return null;
            candidates = [.. _executions.Where(execution => execution.SessionId == sessionId)];
        }

        // The newest send of the session is the one whose run is open; older ones refuse.
        for (var index = candidates.Length - 1; index >= 0; index--)
            if (candidates[index].Queue(restricted, cancellationToken) is { } queued) return queued;
        return null;
    }

    internal AltaAskQueueResult? Queue(OwnedAskHandle origin, AltaAskRequest request)
    {
        lock (_gate)
        {
            if (_closed || !Enabled || _asks.Count >= 256) return null;
            // This exact implementation commits synchronously and returns Task.FromResult. The private
            // queue has no observers. No user callback or Commands call occurs under either owner gate.
            var original = _queue.QueueAsync(request, origin.SessionId, new() { Kind = "agent", SourceSessionId = origin.SessionId });
            var queued = original.GetAwaiter().GetResult();
            var snapshot = _queue.GetPending(origin.SessionId).Single(value => value.AskId == queued.AskId);
            _asks.Add(queued.AskId, new(origin with { AskId = queued.AskId }, snapshot.Request, original));
            return queued;
        }
    }

    /// <summary>Returns the pending head and latest retained disposition, never an acknowledgment.</summary>
    /// <exception cref="ArgumentException">The session identity is invalid.</exception>
    public OwnedAskPage List(string sessionId)
    {
        if (!OwnedSessionAskTool.Identity(sessionId)) throw new ArgumentException("Invalid session.", nameof(sessionId));
        lock (_gate)
        {
            var pending = _queue.GetPending(sessionId);
            var head = pending.FirstOrDefault();
            var actions = _actions.Values.Where(value => value.Request.Handle.SessionId == sessionId).ToArray();
            return new(head is null ? null : Head(head), actions.LastOrDefault()?.Disposition, pending.Count > 1 || actions.Length > 1);
        }
    }

    /// <summary>Reads separate retained evidence for an exact original action; never dispatches or retries.</summary>
    public OwnedAskDisposition? Observe(Guid actionId, OwnedAskHandle handle)
    {
        lock (_gate) return _actions.TryGetValue(actionId, out var action) && action.Request.Handle == handle ? action.Disposition : null;
    }

    /// <summary>Claims and owns one answer independently of transport-wait cancellation.</summary>
    /// <exception cref="ArgumentException">The action or answer is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation precedes action admission.</exception>
    public Task<OwnedAskDisposition> AnswerAsync(OwnedAskAction request, CancellationToken cancellationToken)
        => Admit(request, cancel: false, cancellationToken);

    /// <summary>Cancels only the exact unclaimed pending generation, never a provider run.</summary>
    /// <exception cref="ArgumentException">The action is invalid.</exception>
    /// <exception cref="OperationCanceledException">Cancellation precedes action admission.</exception>
    public Task<OwnedAskDisposition> CancelAsync(OwnedAskAction request, CancellationToken cancellationToken)
        => Admit(request, cancel: true, cancellationToken);

    private Task<OwnedAskDisposition> Admit(OwnedAskAction request, bool cancel, CancellationToken token)
    {
        if (request is null || request.ActionId == Guid.Empty || !OwnedSessionAskTool.Handle(request.Handle) || request.Answers is null)
            throw new ArgumentException("Invalid action.");
        ActionWork work;
        lock (_gate)
        {
            token.ThrowIfCancellationRequested();
            if (!_asks.TryGetValue(request.Handle.AskId, out var ask) || !SameOrigin(ask.Origin, request.Handle))
                return Task.FromResult(new OwnedAskDisposition(request.ActionId, request.Handle, "rejected"));
            var answers = cancel
                ? request.Answers.Count == 0 ? System.Array.Empty<AltaAskAnswer>() : throw new ArgumentException("Cancel has no answers.")
                : OwnedSessionAskTool.CaptureAnswers(ask.Request, request.Answers);
            var text = cancel ? "" : AltaAskAnswerMarkdownFormatter.Format(ask.Request, answers);
            if (!OwnedSessionAskTool.Text(text, 32768)) throw new ArgumentException("Formatted answer exceeds the limit.");
            if (_actions.TryGetValue(request.ActionId, out var previous))
                return previous.Cancel == cancel && previous.Request.Handle == request.Handle && previous.Text == text
                    ? previous.Work : Task.FromResult(new OwnedAskDisposition(request.ActionId, request.Handle, "conflict"));
            if (_closed || !Enabled || _actions.Count >= 256)
                return Task.FromResult(new OwnedAskDisposition(request.ActionId, request.Handle, _closed ? "closed" : !Enabled ? "disabled" : "capacity"));
            var head = _queue.Peek(request.Handle.SessionId);
            if (head?.ResponseHandle is null || head.AskId != request.Handle.AskId || head.ResponseHandle.Generation != request.Handle.ResponseGeneration)
                return Task.FromResult(new OwnedAskDisposition(request.ActionId, request.Handle, "rejected"));
            work = new(request with { Answers = answers }, cancel, text, head.ResponseHandle);
            _actions.Add(request.ActionId, work);
            work.Work = RunAsync(work); // First await is the unreleased launch gate.
        }
        work.Launch.TrySetResult();
        return work.Work;
    }

    private async Task<OwnedAskDisposition> RunAsync(ActionWork work)
    {
        await work.Launch.Task.ConfigureAwait(false);
        string status;
        string? runId = null;
        try
        {
            if (work.Cancel) status = _queue.TryCancelResponse(work.Handle).Accepted ? "cancelled" : "rejected";
            else
            {
                try { work.Response = _queue.RespondAsync(work.Handle, () => DispatchAsync(work)); }
                finally { work.DispatchLaunch.TrySetResult(); } // Retain the response before launching Commands.
                var result = await work.Response.ConfigureAwait(false);
                runId = result.DispatchResult?.RunId;
                status = !result.Claimed ? "rejected" : result.DispatchResult?.Admission switch
                {
                    SessionPromptResponseAdmission.Admitted => "admitted",
                    SessionPromptResponseAdmission.DefinitelyNotAdmittedByThisRoute => "not_admitted",
                    _ => "indeterminate",
                };
            }
        }
        catch (Exception) { status = "indeterminate"; }
        lock (_gate) return work.Disposition = new(work.Request.ActionId, work.Request.Handle, status, runId);
    }

    private async Task<SessionPromptResponseResult> DispatchAsync(ActionWork work)
    {
        await work.DispatchLaunch.Task.ConfigureAwait(false);
        // This callback is entered outside both queue and ask owner gates. Never invert Commands -> Asks.
        var context = work.Submission;
        try
        {
            var admission = _send(new(work.Request.ActionId.ToString("D"), work.Request.Handle.SessionId, work.Text), context);
            if (admission.Kind != OwnedSessionCommandAdmissionKind.Accepted || admission.Receipt is null)
                return SessionPromptResponseResult.NotAdmitted();
            work.Receipt = admission.Receipt;
            await work.Receipt.Completion.ConfigureAwait(false);
        }
        catch (Exception) { /* Retain positive return evidence even when later cleanup/publication fails. */ }
        return context.PositiveRunId is { } run ? SessionPromptResponseResult.Admitted(run) : SessionPromptResponseResult.Indeterminate();
    }

    /// <summary>Closes producers and actions before command cancellation; never waits for answer dispatch.</summary>
    public void CloseAdmission()
    {
        OwnedSessionAskExecution[] executions;
        lock (_gate) { _closed = true; executions = [.. _executions]; }
        foreach (var execution in executions) execution.Close();
    }

    /// <summary>Joins original action dispatch after command sends/control have settled; does not cancel a wait.</summary>
    /// <exception cref="InvalidOperationException">Ask admission is still open.</exception>
    public Task DrainAsync()
    {
        lock (_gate)
        {
            if (!_closed) throw new InvalidOperationException("Close ask admission before joining.");
            return Task.WhenAll(_actions.Values.Select(value => value.Work));
        }
    }

    private OwnedAskHead Head(AltaQueuedAsk head) => new(_asks[head.AskId].Origin with { ResponseGeneration = head.ResponseHandle!.Generation },
        head.Request, head.ResponseState switch { AltaAskResponseState.Pending => "pending", AltaAskResponseState.Submitting => "submitting", _ => "indeterminate" });
    private static bool SameOrigin(OwnedAskHandle original, OwnedAskHandle handle) => original == (handle with { ResponseGeneration = original.ResponseGeneration });
    private sealed record Ask(OwnedAskHandle Origin, AltaAskRequest Request, Task<AltaAskQueueResult> OriginalQueue);
    private sealed class ActionWork(OwnedAskAction request, bool cancel, string text, AltaAskResponseHandle handle)
    {
        internal OwnedAskAction Request { get; } = request;
        internal bool Cancel { get; } = cancel;
        internal string Text { get; } = text;
        internal AltaAskResponseHandle Handle { get; } = handle;
        internal OwnedAskSubmission Submission { get; } = new(request.ActionId, handle.AskId);
        internal TaskCompletionSource Launch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DispatchLaunch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<OwnedAskDisposition> Work { get; set; } = null!; // Assigned under owner gate before publication/launch.
        internal Task<AltaAskResponseResult>? Response { get; set; }
        internal OwnedSessionCommandReceipt? Receipt { get; set; }
        internal OwnedAskDisposition Disposition { get; set; } = new(request.ActionId, request.Handle, "submitting");
    }
}
