using System.Text.Json;
using System.Runtime.ExceptionServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Orchestration.Runtime;

// A per-send capability. It is never stored in session options or rebound to a replacement operation.
internal sealed class OwnedSessionAskExecution : AgentRunLifecycle
{
    private readonly object _gate = new();
    private readonly OwnedSessionAskService _owner;
    private readonly Guid _operation;
    private readonly string _session;
    private readonly CancellationToken _operationToken;
    private readonly OwnedAskSubmission? _answer;
    private CancellationToken _runToken;
    private Guid _runtime;
    private long _attachment;
    private ModelProviderId _provider;
    private string? _run;
    private bool _bound;
    private bool _closed;
    private bool _committed;
    private readonly List<CancellationTokenSource> _interactiveWaits = [];
    // Producer work is entirely synchronous and bounded. Retain its original completed task before return.
    private Task<AgentToolResult>? _committedInvocation;

    internal OwnedSessionAskExecution(OwnedSessionAskService owner, Guid operation, string session, CancellationToken token,
        OwnedAskSubmission? answer = null)
    {
        _owner = owner; _operation = operation; _session = session; _operationToken = token; _answer = answer;
        Tool = OwnedSessionAskTool.Create(Invoke);
    }

    internal AgentToolDefinition Tool { get; }

    internal void Bind(Guid runtime, long attachment, ModelProviderId provider)
    {
        lock (_gate)
        {
            if (_closed || _bound || runtime == Guid.Empty || attachment is <= 0 or > 9007199254740991
                || !OwnedSessionAskTool.Identity(provider.Value)) throw new InvalidOperationException("Ask execution cannot bind.");
            _runtime = runtime; _attachment = attachment; _provider = provider; _bound = true;
        }
    }

    public override Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
    {
        lock (_gate)
        {
            if (_closed || !_bound || _run is not null || !OwnedSessionAskTool.Identity(runId.Value))
                throw new InvalidOperationException("Ask execution requires its original actual run binding.");
            _operationToken.ThrowIfCancellationRequested();
            executionToken.ThrowIfCancellationRequested();
            _runToken = executionToken;
            _run = runId.Value;
        }
        // This send's run is the one whose input is the answer: the session has it.
        _answer?.RecordRunStarted(runId);
        return Task.CompletedTask;
    }

    public override Task ClosingAsync(AgentRunId runId) { Close(); return Task.CompletedTask; }

    // Acquiring the gate joins any synchronous producer body. No answer dispatch is joined here.
    internal void Close()
    {
        CancellationTokenSource[] waits;
        lock (_gate)
        {
            _closed = true;
            waits = [.. _interactiveWaits];
        }

        foreach (var wait in waits)
        {
            try { wait.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    // Read without the gate: the owner reads it under its own gate, which Queue takes inside this one.
    internal bool IsClosed => Volatile.Read(ref _closed);

    internal AgentSendOptions Compose(AgentSendOptions options) => Compose(options, includeTool: true);

    // A session whose own tools include `alta` reaches this execution through that tool (see Queue): adding the
    // per-send tool as well would give the run two tools of one name.
    internal AgentSendOptions Compose(AgentSendOptions options, bool includeTool) => new()
    {
        Input = options.Input, AskId = options.AskId, SourceSessionId = options.SourceSessionId, Provenance = options.Provenance,
        OnPermissionRequest = options.OnPermissionRequest,
        OnUserInputRequest = options.OnUserInputRequest,
        OnConversationHeld = options.OnConversationHeld,
        OnInteractiveAsk = options.OnInteractiveAsk ?? (_owner.Enabled && !IsClosed ? AskInteractiveAsync : null),
        EnableUserInputTool = options.EnableUserInputTool,
        AdditionalTools = includeTool ? System.Array.AsReadOnly<AgentToolDefinition>([.. (options.AdditionalTools ?? []), Tool]) : options.AdditionalTools,
        RunLifecycle = new Lifecycle(this, options.RunLifecycle),
    };

    // Presents a live interactive question from a running provider (such as ACP elicitation/create) in the ask panel
    // immediately and returns the user's answer directly to the provider.
    private async Task<AgentInteractiveAskResponse> AskInteractiveAsync(AgentInteractiveAskRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        OwnedAskHandle origin;
        CancellationTokenSource linked;
        lock (_gate)
        {
            if (_closed || !_bound || _run is null || !_owner.Enabled
                || _operationToken.IsCancellationRequested || _runToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return new AgentInteractiveAskResponse(Cancelled: true, []);
            }

            origin = new OwnedAskHandle(_operation, _runtime, _attachment, _provider.Value, _session, _run, "", 0);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _operationToken, _runToken);
            _interactiveWaits.Add(linked);
        }

        try
        {
            var askRequest = ToAltaAskRequest(request);
            if (askRequest is null || askRequest.Questions.Count == 0)
            {
                return new AgentInteractiveAskResponse(Cancelled: true, []);
            }

            var answers = await _owner.AskImmediateAsync(origin, askRequest, linked.Token).ConfigureAwait(false);
            if (answers is null)
            {
                return new AgentInteractiveAskResponse(Cancelled: true, []);
            }

            return new AgentInteractiveAskResponse(
                Cancelled: false,
                [.. answers.Select(static answer => new AgentInteractiveAskAnswer(answer.QuestionIndex, answer.SelectedChoiceIndexes, answer.FreeformText))]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AgentInteractiveAskResponse(Cancelled: true, []);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new AgentInteractiveAskResponse(Cancelled: true, []);
        }
        finally
        {
            lock (_gate)
            {
                _interactiveWaits.Remove(linked);
            }

            linked.Dispose();
        }
    }

    // Projects the interactive request into an AltaAskRequest while preserving exact 1:1 question and choice indexes.
    // Returns null if any question or choice cannot be represented faithfully within CodeAlta's ask limits.
    private static AltaAskRequest? ToAltaAskRequest(AgentInteractiveAskRequest request)
    {
        if (request.Questions.Count is 0 or > 12)
        {
            return null;
        }

        // Reserve room across all text fields so OwnedSessionAskTool.Restrict's 8192-character aggregate budget never fails.
        const int MaxBudget = 8000;
        var budget = MaxBudget;
        var questions = new List<AltaAskQuestion>(request.Questions.Count);
        var intro = string.IsNullOrWhiteSpace(request.Message) ? null : CleanText(request.Message, 400);
        foreach (var source in request.Questions)
        {
            var sourceChoices = source.Choices ?? [];
            if (sourceChoices.Count > 20 || budget < 32)
            {
                return null;
            }

            var title = Take(source.Title, 120, ref budget) ?? Take($"Question {questions.Count + 1}", 120, ref budget);
            var text = Take(source.Question, 1500, ref budget) ?? title;
            if (title is null || text is null)
            {
                return null;
            }

            var rawDescription = questions.Count == 0 && intro is not null && !string.Equals(intro, text, StringComparison.OrdinalIgnoreCase)
                ? string.IsNullOrWhiteSpace(source.Description) ? intro : $"{intro}\n{source.Description}"
                : source.Description;
            var description = Take(rawDescription, 1000, ref budget);

            var choices = new List<AltaAskChoice>(sourceChoices.Count);
            for (var choiceIndex = 0; choiceIndex < sourceChoices.Count; choiceIndex++)
            {
                var choice = sourceChoices[choiceIndex];
                var choiceTitle = Take(choice.Title, 120, ref budget) ?? Take($"Option {choiceIndex + 1}", 120, ref budget);
                if (choiceTitle is null)
                {
                    return null;
                }

                var choiceDescription = Take(choice.Description, 400, ref budget);
                choices.Add(new AltaAskChoice { Title = choiceTitle, Description = choiceDescription });
            }

            AltaAskFreeform? freeform = null;
            if (source.Freeform is not null || choices.Count == 0)
            {
                freeform = new AltaAskFreeform
                {
                    Title = Take(source.Freeform?.Title, 120, ref budget),
                    Placeholder = Take(source.Freeform?.Placeholder, 500, ref budget),
                };
            }

            questions.Add(new AltaAskQuestion
            {
                Title = title,
                Question = text,
                Description = description,
                Choices = choices.AsReadOnly(),
                AllowMultiSelect = source.AllowMultiSelect && choices.Count > 0,
                Freeform = freeform,
            });
        }

        return new AltaAskRequest { Questions = questions.AsReadOnly() };

        static string? Take(string? raw, int fieldMax, ref int remaining)
        {
            if (remaining <= 0)
            {
                return null;
            }

            var cleaned = CleanText(raw, Math.Min(fieldMax, remaining));
            if (cleaned is null)
            {
                return null;
            }

            remaining -= cleaned.Length;
            return cleaned;
        }

        static string? CleanText(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
            {
                return null;
            }

            var trimmed = value.Trim();
            var builder = new System.Text.StringBuilder(Math.Min(trimmed.Length, maxLength));
            for (var i = 0; i < trimmed.Length && builder.Length < maxLength; i++)
            {
                var ch = trimmed[i];
                if (ch == '\0')
                {
                    continue;
                }

                if (char.IsSurrogate(ch))
                {
                    if (char.IsHighSurrogate(ch) && i + 1 < trimmed.Length && char.IsLowSurrogate(trimmed[i + 1]))
                    {
                        if (builder.Length + 2 > maxLength)
                        {
                            break;
                        }

                        builder.Append(ch);
                        builder.Append(trimmed[++i]);
                    }

                    continue;
                }

                builder.Append(ch);
            }

            var result = builder.ToString().Trim();
            return result.Length == 0 ? null : result;
        }
    }

    internal string SessionId => _session;

    // The same admission as the per-send tool, for an already validated request: one ask per send, only while
    // this send's run is open.
    internal AltaAskQueueResult? Queue(AltaAskRequest request, CancellationToken token)
    {
        lock (_gate)
        {
            if (_closed || !_bound || _run is null || _committed || _operationToken.IsCancellationRequested
                || _runToken.IsCancellationRequested || token.IsCancellationRequested) return null;
            var queued = _owner.Queue(new(_operation, _runtime, _attachment, _provider.Value, _session, _run, "", 0), request);
            if (queued is null) return null;
            _committed = true;
            return queued;
        }
    }

    internal static AgentRunLifecycle Combine(AgentRunLifecycle? first, AgentRunLifecycle second)
        => first is null ? second : new Combined(first, second);

    private sealed class Combined(AgentRunLifecycle first, AgentRunLifecycle second) : AgentRunLifecycle, AgentSession.IRunStartEvidence
    {
        private readonly object _gate = new();
        private ClosingPair? _closing;
        private readonly AgentSession.RunStartInvocation _firstStart = new(OwnedProviderEventForwarding.HasRetention);
        private readonly AgentSession.RunStartInvocation _secondStart = new(OwnedProviderEventForwarding.HasRetention);
        AgentSession.RunStartInvocation? AgentSession.IRunStartEvidence.FailedStart
            => _firstStart.Failure is not null ? _firstStart : _secondStart.Failure is not null ? _secondStart : null;
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            await _firstStart.RunAsync(first, runId, executionToken).ConfigureAwait(false);
            await _secondStart.RunAsync(second, runId, executionToken).ConfigureAwait(false);
        }
        public override Task ClosingAsync(AgentRunId runId)
        {
            ClosingPair closing;
            lock (_gate)
            {
                if (_closing is not null) return _closing.Completion;
                closing = _closing = new ClosingPair(() => first.ClosingAsync(runId), () => second.ClosingAsync(runId));
            }
            closing.Launch();
            return closing.Completion;
        }
    }

    private Task<AgentToolResult> Invoke(AgentToolInvocation invocation, CancellationToken token)
    {
        lock (_gate)
        {
            if (_closed || !_bound || _run is null || _committed || _operationToken.IsCancellationRequested
                || _runToken.IsCancellationRequested || token.IsCancellationRequested
                || invocation.ProviderId != _provider || invocation.SessionId != _session)
                return Task.FromResult(new AgentToolResult(false, [], "Ask producer is unavailable."));
            try
            {
                var request = OwnedSessionAskTool.ParseInvocation(invocation);
                var queued = _owner.Queue(new(_operation, _runtime, _attachment, _provider.Value, _session, _run, "", 0), request);
                if (queued is null) return Task.FromResult(new AgentToolResult(false, [], "Ask capacity or admission is closed."));
                _committed = true;
                var reply = new OwnedAskToolReply("alta.ask.queued", queued.AskId, _session, false, true);
                _committedInvocation = Task.FromResult(new AgentToolResult(true,
                    [new AgentToolResultItem.Text(JsonSerializer.Serialize(reply, OwnedAskJsonContext.Default.OwnedAskToolReply))]));
                return _committedInvocation;
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException)
            { return Task.FromResult(new AgentToolResult(false, [], "Invalid restricted ask request.")); }
        }
    }

    internal sealed class Lifecycle(AgentRunLifecycle ask, AgentRunLifecycle? previous) : AgentRunLifecycle, AgentSession.IRunStartEvidence
    {
        private readonly object _gate = new();
        private ClosingPair? _closing;
        private readonly AgentSession.RunStartInvocation _previousStart = new(OwnedProviderEventForwarding.HasRetention);
        private readonly AgentSession.RunStartInvocation _askStart = new(OwnedProviderEventForwarding.HasRetention);
        AgentSession.RunStartInvocation? AgentSession.IRunStartEvidence.FailedStart
            => _previousStart.Failure is not null ? _previousStart : _askStart.Failure is not null ? _askStart : null;
        public override async Task StartedAsync(AgentRunId runId, CancellationToken executionToken)
        {
            // AgentSession retains this original start and always invokes Closing, including start failure.
            if (previous is not null) await _previousStart.RunAsync(previous, runId, executionToken).ConfigureAwait(false);
            await _askStart.RunAsync(ask, runId, executionToken).ConfigureAwait(false);
        }
        public override Task ClosingAsync(AgentRunId runId)
        {
            ClosingPair closing;
            lock (_gate)
            {
                if (_closing is not null) return _closing.Completion;
                closing = _closing = new ClosingPair(
                    () => ask.ClosingAsync(runId),
                    () => previous is null ? Task.CompletedTask : previous.ClosingAsync(runId), firstRequired: true);
            }
            // Independent ask closure precedes a potentially noncooperative permission join.
            closing.Launch();
            return closing.Completion;
        }
    }

    internal sealed class ClosingPair
    {
        private readonly bool _firstRequired;
        internal ClosingPair(Func<Task> first, Func<Task> second, bool firstRequired = false)
        {
            _firstRequired = firstRequired;
            First = new SessionPermissionService.DeliveryStage(first);
            Second = new SessionPermissionService.DeliveryStage(second);
            Completion = CompleteAsync();
        }
        internal SessionPermissionService.DeliveryStage First { get; }
        internal SessionPermissionService.DeliveryStage Second { get; }
        internal Task Completion { get; }
        internal void Launch() { First.Launch(); Second.Launch(); }
        private async Task CompleteAsync()
        {
            var first = await First.Outcome.ConfigureAwait(false);
            var second = await Second.Outcome.ConfigureAwait(false);
            var failures = new List<Exception>();
            if (first is not null) failures.Add(First.Failure!);
            if (second is not null) failures.Add(Second.Failure!);
            if (First.Original is null || Second.Original is null
                || (_firstRequired && first is not null) || failures.Any(OwnedProviderEventForwarding.HasRetention))
                throw new AgentDependencyRetentionException("owned lifecycle", "closing", failures, this);
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1) throw new AggregateException(failures);
        }
    }
}
