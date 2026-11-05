using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Fleet.Protocol;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Jobs;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Fleet;

/// <summary>An operation a remote host admitted: its session, and the operation the node gave it once it answered.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="NodeOperation">The operation of the node; null when the node did not admit it.</param>
internal sealed record RemoteOperation(string SessionId, Task<Guid?> NodeOperation);

internal sealed partial class RemoteSessionHost
{
    /// <summary>How many receipts the host keeps, as the command owner of this process does.</summary>
    public const int KeptReceipts = 256;

    // The code of a command the machine could not be asked, or did not answer.
    internal const string MachineOfflineCode = "machine_offline";

    private static readonly TimeSpan OperationWait = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, OwnedSessionCommandReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _receiptOrder = new();
    // The identity of this host's view of the runtime of the node, which the handles of asks and requests carry.
    private readonly Guid _instance = Guid.NewGuid();
    private readonly ConcurrentDictionary<string, OwnedAskPage> _asks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _askReads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, OwnedAskDisposition> _askOutcomes = new();

    /// <inheritdoc />
    public int ReceiptCapacity => KeptReceipts;

    /// <inheritdoc />
    /// <remarks>
    /// Admitted here at once, then sent to the node with the client request id as its retry key: the receipt completes
    /// with what the node answers, failed with <see cref="MachineOfflineCode"/> when it could not be asked. A send while
    /// the session works is queued on the node.
    /// </remarks>
    public OwnedSessionCommandAdmission AdmitSend(OwnedTextSendRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Admit(request.ClientRequestId, OwnedSessionCommandKind.Send, request.SessionId, null, (client, operation) => SendAsync(client, request, operation));
    }

    /// <inheritdoc />
    public OwnedSessionCommandAdmission AdmitAbort(OwnedAbortRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_operations.TryGetValue(request.TargetOperationId, out var target)) return new(OwnedSessionCommandAdmissionKind.UnknownTarget);
        return Admit(request.ClientRequestId, OwnedSessionCommandKind.Abort, target.SessionId, request.TargetOperationId, async (client, operation) =>
        {
            operation.TrySetResult(null);
            var node = await target.NodeOperation.WaitAsync(OperationWait).ConfigureAwait(false);
            if (node is null) return new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Failed, null, "unknown_target");
            return ToResult(await client.AbortAsync(new FleetAbortParams(target.SessionId, node), request.ClientRequestId).ConfigureAwait(false));
        });
    }

    /// <inheritdoc />
    public OwnedSessionCommandAdmission AdmitSteer(OwnedTextSteerRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Admit(request.ClientRequestId, OwnedSessionCommandKind.Steer, request.SessionId, null, async (client, operation) =>
        {
            var result = await client.SteerAsync(new FleetSteerParams(request.SessionId, request.Text, request.ExpectedRuntimeInstanceId,
                request.ExpectedAttachmentGeneration, request.ExpectedRunId), request.ClientRequestId).ConfigureAwait(false);
            operation.TrySetResult(result.OperationId);
            return ToResult(result);
        });
    }

    /// <inheritdoc />
    /// <remarks>A remote session is compacted by its machine; the page cannot ask it yet.</remarks>
    public OwnedSessionCommandAdmission AdmitCompact(OwnedCompactRequest request, CancellationToken cancellationToken)
        => new(OwnedSessionCommandAdmissionKind.UnknownTarget);

    /// <inheritdoc />
    public OwnedSessionCommandAdmission AdmitAbortRun(OwnedAbortRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Admit(request.ClientRequestId, OwnedSessionCommandKind.AbortRun, request.SessionId, null, async (client, operation) =>
        {
            var result = await client.AbortAsync(new FleetAbortParams(request.SessionId, null, request.ExpectedRuntimeInstanceId,
                request.ExpectedAttachmentGeneration, request.ExpectedRunId), request.ClientRequestId).ConfigureAwait(false);
            operation.TrySetResult(result.OperationId);
            return ToResult(result);
        });
    }

    // One admission: a retry of the same key is the same receipt, another command with that key a conflict, and an
    // offline machine admits nothing.
    private OwnedSessionCommandAdmission Admit(string clientRequestId, OwnedSessionCommandKind kind, string sessionId, Guid? target,
        Func<FleetNodeClient, TaskCompletionSource<Guid?>, Task<OwnedSessionCommandResult>> forward)
    {
        if (string.IsNullOrWhiteSpace(clientRequestId) || string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("A command names its retry key and its session.");
        if (_receipts.TryGetValue(clientRequestId, out var existing))
        {
            return existing.Kind == kind && string.Equals(existing.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && existing.TargetOperationId == target
                ? new(OwnedSessionCommandAdmissionKind.Replay, existing)
                : new(OwnedSessionCommandAdmissionKind.Conflict);
        }

        FleetNodeClient client;
        try { client = RequireClient(); }
        catch (ObjectDisposedException) { return new(OwnedSessionCommandAdmissionKind.Closed); }
        var operation = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receipt = OwnedSessionCommandReceipt.CreateForwarded(clientRequestId, kind, sessionId, target, ForwardAsync());
        if (!_receipts.TryAdd(clientRequestId, receipt))
            return _receipts.TryGetValue(clientRequestId, out var raced) ? new(OwnedSessionCommandAdmissionKind.Replay, raced) : new(OwnedSessionCommandAdmissionKind.Conflict);
        _receiptOrder.Enqueue(clientRequestId);
        _operations[receipt.OperationId] = new RemoteOperation(sessionId, operation.Task);
        _operationOrder.Enqueue(receipt.OperationId);
        Trim();
        return new(OwnedSessionCommandAdmissionKind.Accepted, receipt);

        async Task<OwnedSessionCommandResult> ForwardAsync()
        {
            try
            {
                var result = await forward(client, operation).ConfigureAwait(false);
                RequestRefresh();
                return result;
            }
            catch (FleetException exception)
            {
                return new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Failed, null, exception.Error.Code) { Message = exception.Error.Message };
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException or OperationCanceledException)
            {
                return new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Failed, null, MachineOfflineCode);
            }
            finally
            {
                operation.TrySetResult(null);
            }
        }
    }

    private void Trim()
    {
        while (_receiptOrder.Count > KeptReceipts && _receiptOrder.TryDequeue(out var key)) _receipts.TryRemove(key, out _);
        while (_operationOrder.Count > KeptReceipts && _operationOrder.TryDequeue(out var id)) _operations.TryRemove(id, out _);
    }

    private async Task<OwnedSessionCommandResult> SendAsync(FleetNodeClient client, OwnedTextSendRequest request, TaskCompletionSource<Guid?> operation)
    {
        // Pictures and attached files are on this machine: the node is not given them, and the send says so.
        if (request.Images is { Count: > 0 } || request.Attachments is { Count: > 0 })
            return new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Failed, null, "attachments_unsupported");
        // The model of a remote session is the one its machine gives it: the selection of the composer here names the
        // choices this host offered (SelectionChoices), which are the session's own, so nothing is changed on the node.
        var result = await client.SendAsync(request.SessionId, request.Text, null, request.ClientRequestId).ConfigureAwait(false);
        if (result.Status == "busy")
        {
            // The session works: the prompt waits on the node for the end of the turn, as a send does here.
            await client.QueueAsync(request.SessionId, request.Text, "queue-" + request.ClientRequestId).ConfigureAwait(false);
            return new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Completed, null, "queued");
        }

        operation.TrySetResult(result.OperationId);
        return ToResult(result);
    }

    // The admission of the node, as a result: an admitted command is a completed submission, a refusal a failure with
    // the status of the node as its code.
    internal static OwnedSessionCommandResult ToResult(FleetCommandResult result)
    {
        AgentRunId? run = result.RunId is { Length: > 0 } id ? new AgentRunId(id) : null;
        // A prompt held by a budget of the machine is sent once its owner continues it anyway: not a failure of the send.
        if (result.Status == FleetBudgetCommandStatuses.Held) return new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Completed, run, FleetBudgetCommandStatuses.Held);
        if (result.Status is not ("accepted" or "replay")) return new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Failed, run, result.Code ?? result.Status);
        return result.Outcome switch
        {
            "Cancelled" => new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Cancelled, run, result.Code),
            "Failed" => new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Failed, run, result.Code),
            _ => new OwnedSessionCommandResult(OwnedSessionCommandOutcome.Completed, run, result.Code),
        };
    }

    /// <summary>
    /// The choices the composer of a remote session offers: the provider, the agent prompt and the model the machine
    /// listed for it, and nothing else, since a send keeps them. The provider and the model are named as the machine
    /// names them: a provider of the same key on this computer may be another account, with another name.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The choices; null when the machine does not list the session.</returns>
    public OwnedSelectionChoices? SelectionChoices(string sessionId)
    {
        var listing = Listing;
        var session = listing.Sessions.FirstOrDefault(value => string.Equals(value.Id, sessionId, StringComparison.OrdinalIgnoreCase));
        if (session is null) return null;
        var provider = session.ProviderKey ?? "default";
        var described = listing.Info?.Providers.FirstOrDefault(value => string.Equals(value.Key, provider, StringComparison.Ordinal))
            ?? (session.ProviderKey is null ? listing.Info?.Providers.FirstOrDefault(static value => value.IsDefault) : null);
        var model = session.ModelId is { Length: > 0 } id
            ? new OwnedModelChoice(id, described?.Models.FirstOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal))?.Name ?? id, [])
            : null;
        return new OwnedSelectionChoices(new OwnedSessionSelection(provider, "default", model?.Id, null), [new OwnedPromptChoice("default", "Default")],
            model is null ? [] : [model])
        {
            HostProviderName = described?.Name ?? (session.ProviderKey is null ? null : provider),
        };
    }

    /// <inheritdoc />
    public bool AsksEnabled => true;

    /// <inheritdoc />
    /// <remarks>
    /// The page asks often and this call does not wait: it gives what the node said last, and asks the node again for
    /// the next call.
    /// </remarks>
    public OwnedAskPage ListAsks(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _askReads.GetOrAdd(sessionId, id => Task.Run(async () =>
        {
            try { await ReadAsksAsync(id).ConfigureAwait(false); }
            finally { _askReads.TryRemove(id, out _); }
        }));
        return _asks.TryGetValue(sessionId, out var page) ? page : new OwnedAskPage(null, null, false);
    }

    private async Task ReadAsksAsync(string sessionId)
    {
        FleetAskList list;
        try { list = await RequireClient().ListAsksAsync(sessionId, _stop.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is FleetException or IOException or ObjectDisposedException or TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            return;
        }

        var previous = _asks.TryGetValue(sessionId, out var known) ? known.Latest : null;
        _asks[sessionId] = new OwnedAskPage(list.Ask is { } ask ? new OwnedAskHead(AskHandle(sessionId, ask), ToRequest(ask), ask.State) : null, previous, list.HasMore);
    }

    private OwnedAskHandle AskHandle(string sessionId, FleetAsk ask)
        => new(StableId(MachineId + "/" + sessionId + "/" + ask.AskId), _instance, 1, "fleet", sessionId, ask.RunId, ask.AskId, 0);

    private static AltaAskRequest ToRequest(FleetAsk ask) => new()
    {
        Questions = [.. ask.Questions.Select(static question => new AltaAskQuestion
        {
            Title = question.Title, Question = question.Question, Description = question.Description,
            Choices = [.. question.Choices.Select(static choice => new AltaAskChoice { Title = choice.Title, Description = choice.Description })],
            Freeform = question.AllowsText ? new AltaAskFreeform() : null,
        })],
    };

    private static Guid StableId(string text)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return new Guid(hash[..16]);
    }

    /// <inheritdoc />
    public OwnedAskDisposition? ObserveAsk(Guid actionId, OwnedAskHandle handle)
        => _askOutcomes.TryGetValue(actionId, out var outcome) && outcome.Handle == handle ? outcome : null;

    /// <inheritdoc />
    public Task<OwnedAskDisposition> AnswerAskAsync(OwnedAskAction action, CancellationToken cancellationToken) => ActOnAskAsync(action, cancel: false, cancellationToken);

    /// <inheritdoc />
    public Task<OwnedAskDisposition> CancelAskAsync(OwnedAskAction action, CancellationToken cancellationToken) => ActOnAskAsync(action, cancel: true, cancellationToken);

    private async Task<OwnedAskDisposition> ActOnAskAsync(OwnedAskAction action, bool cancel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_askOutcomes.TryGetValue(action.ActionId, out var known)) return known;
        var handle = action.Handle;
        var answers = cancel ? [] : action.Answers.Select(static answer => new FleetAskAnswer(answer.QuestionIndex, [.. answer.SelectedChoiceIndexes], answer.FreeformText)).ToArray();
        string status;
        string? run = null;
        try
        {
            var result = await RequireClient().AnswerAskAsync(new FleetAskAnswerParams(handle.SessionId, handle.AskId, answers, cancel), action.ActionId.ToString("N"),
                cancellationToken).ConfigureAwait(false);
            (status, run) = (result.Status, result.RunId);
        }
        catch (FleetException exception)
        {
            status = exception.Error.Code;
        }
        catch (ObjectDisposedException)
        {
            status = MachineOfflineCode;
        }

        var disposition = new OwnedAskDisposition(action.ActionId, handle, status, run);
        _askOutcomes[action.ActionId] = disposition;
        if (_asks.TryGetValue(handle.SessionId, out var page)) _asks[handle.SessionId] = page with { Head = null, Latest = disposition };
        _ = ListAsks(handle.SessionId);
        return disposition;
    }

    /// <inheritdoc />
    public async ValueTask<SessionOwnedPermissionPage> ListPermissionsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var list = await RequireClient().ListPermissionsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new SessionOwnedPermissionPage([.. list.Requests.Select(request =>
        {
            var attempt = new SessionPermissionHandle(sessionId, request.RunId, request.Id.ToString("N"), request.Id);
            return new SessionOwnedPermissionSnapshot(new SessionOwnedPermissionHandle(request.Id, _instance, 1, attempt),
                new SessionPermissionSnapshot(attempt, new ModelProviderId("fleet"), request.Timestamp, request.Kind, request.Command, request.WorkingDirectory,
                    request.Reason, request.GrantRoot));
        })], list.HasMore);
    }

    /// <inheritdoc />
    public async ValueTask<bool> ResolvePermissionAsync(SessionOwnedPermissionHandle handle, AgentPermissionDecisionKind decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var answer = decision switch
        {
            AgentPermissionDecisionKind.AllowOnce => "allow_once",
            AgentPermissionDecisionKind.AllowAlways => "allow_always",
            AgentPermissionDecisionKind.Deny => "deny",
            _ => "cancel",
        };
        var result = await RequireClient().AnswerPermissionAsync(handle.Attempt.SessionId, handle.Attempt.AttemptId, answer, cancellationToken).ConfigureAwait(false);
        return result.Status == "resolved";
    }

    /// <inheritdoc />
    /// <remarks>The questions of a provider are answered on the machine of the session for now.</remarks>
    public ValueTask<SessionOwnedUserInputPage> ListUserInputsAsync(string sessionId, CancellationToken cancellationToken)
        => ValueTask.FromResult(new SessionOwnedUserInputPage([], false));

    /// <inheritdoc />
    public ValueTask<bool> ResolveUserInputAsync(SessionOwnedUserInputHandle handle, IReadOnlyList<SessionOwnedUserInputAnswer> answers, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    /// <inheritdoc />
    public ValueTask<bool> CancelUserInputAsync(SessionOwnedUserInputHandle handle, CancellationToken cancellationToken) => ValueTask.FromResult(false);

    /// <inheritdoc />
    /// <remarks>The background jobs of a remote session are not listed across machines yet.</remarks>
    public SessionJob? FindJob(string sessionId, string jobId) => null;

    /// <inheritdoc />
    public IAsyncEnumerable<RuntimeToolOutputUpdate> ObserveJobOutputAsync(string sessionId, string jobId, CancellationToken cancellationToken)
        => AsyncEnumerable.Empty<RuntimeToolOutputUpdate>();

    /// <inheritdoc />
    public Task<string> StopBackgroundTaskAsync(string sessionId, string taskId, CancellationToken cancellationToken) => Task.FromResult("unavailable");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: a session of another machine is created with <see cref="CreateRemoteSessionAsync"/>.</exception>
    public OwnedPermissionModeChoices GetNewSessionPermissionModes(string providerKey)
        => throw new NotSupportedException("The permission modes of a new session are the ones of its machine.");

    /// <inheritdoc />
    public Task<OwnedDraftPrompts?> GetDraftPromptsAsync(OwnedProjectReferenceScope? scope, string? spaceId, CancellationToken cancellationToken)
        => Task.FromResult<OwnedDraftPrompts?>(null);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: the descriptors of this catalog mean nothing on another machine; see <see cref="CreateRemoteSessionAsync"/>.</exception>
    public Task<SessionViewDescriptor> CreateSessionAsync(ProjectDescriptor? project, ModelProviderDescriptor provider, string? title, string? worktreeDirectory,
        string? spaceId, string? permissionMode, AgentSessionOptions? options)
        => Task.FromException<SessionViewDescriptor>(new NotSupportedException("A session of another machine is created with the project and provider of that machine."));

    /// <summary>Creates a session on the machine, with the project and the provider it knows, and its first prompt when one is given.</summary>
    /// <param name="parameters">The project, space, title, prompt and model, as the machine names them.</param>
    /// <param name="retryKey">The key of the creation: the same key again is the same session.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The new session.</returns>
    /// <exception cref="ObjectDisposedException">The machine is not online.</exception>
    /// <exception cref="FleetException">The machine refused the creation (<see cref="FleetNodeErrorCodes.Unavailable"/>, a permission).</exception>
    public async Task<FleetCreateSessionResult> CreateRemoteSessionAsync(FleetCreateSessionParams parameters, string retryKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var created = await RequireClient().CreateSessionAsync(parameters, retryKey, cancellationToken).ConfigureAwait(false);
        RequestRefresh();
        return created;
    }
}
