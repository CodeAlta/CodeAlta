using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics.Journal;

namespace CodeAlta.Plugin.Statistics.Facts;

/// <summary>
/// Turns the records of one session into additive facts. It counts what happened in the records it is given and nothing else,
/// and keeps in <see cref="SessionFactsState"/> what a later call needs, so that reading a journal in two halves gives
/// exactly the facts of reading it at once.
/// </summary>
/// <remarks>
/// <para>The definitions are those of the statistics proposal: a run goes from the first record that carries it to its
/// <c>Idle</c> or <c>error</c>; requests and tokens come from the <c>UsageUpdated</c> records only; a tool call is counted
/// when it starts and measured when it ends; a prompt belongs to the sender its provenance names.</para>
/// <para>Not thread-safe.</para>
/// </remarks>
internal sealed class SessionFactsReducer : IJournalRecordSink
{
    private const long MaxToolDurationMs = 7L * 24 * 60 * 60 * 1000;
    private static readonly TimeSpan ProvenanceWindow = TimeSpan.FromSeconds(5);

    /// <summary>The longest time between two records of a run that still counts as time the run was active.</summary>
    internal static readonly TimeSpan MaxRunGap = TimeSpan.FromDays(7);

    private readonly string _sessionId;
    private readonly SessionFactsState _state;
    private readonly HashSet<ulong> _seenPrompts;
    private readonly HashSet<string> _dirtyRuns = new(StringComparer.Ordinal);
    private FactBatch _batch;
    private bool _recordsInBatch;
    private readonly HashSet<string> _interrupted = new(StringComparer.Ordinal);
    private AgentOperationUsageSnapshot? _lastOperation;

    /// <summary>Initializes a reducer.</summary>
    /// <param name="sessionId">The session whose records are reduced.</param>
    /// <param name="state">The state the previous catch-up left; null to start from the beginning of a journal.</param>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    public SessionFactsReducer(string sessionId, SessionFactsState? state = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _sessionId = sessionId;
        _state = state ?? new SessionFactsState();
        _seenPrompts = [.. _state.SeenPrompts];
        _batch = new FactBatch(sessionId);
    }

    /// <summary>Gets the state to carry to the next call.</summary>
    public SessionFactsState State => _state;

    /// <summary>Gets the counters of the oddities the reducer met, for the tests and the harness; they are not facts and are not kept.</summary>
    public ReducerDiagnostics Diagnostics { get; } = new();

    /// <summary>Gets or sets a value indicating whether an open run is closed as interrupted when another run starts.</summary>
    public bool InterruptRunWhenAnotherStarts { get; set; } = true;

    /// <inheritdoc />
    public bool IsPromptSeen(ulong idHash) => _seenPrompts.Contains(idHash);

    /// <summary>Takes the facts added since the last call, and starts a new batch.</summary>
    /// <returns>The batch: the additions, the runs that changed and the session as it is.</returns>
    public FactBatch TakeBatch()
    {
        foreach (var runId in _dirtyRuns)
        {
            var open = FindOpenRun(runId);
            if (open is not null)
            {
                _batch.Runs[runId] = BuildRunRow(open, RunOutcome.Running, open.AccountedTo);
            }
        }

        _dirtyRuns.Clear();
        if (_recordsInBatch && _state.FirstRecord is not null)
        {
            _batch.Session = BuildSessionRow();
        }

        _recordsInBatch = false;

        var taken = _batch;
        _batch = new FactBatch(_sessionId);
        return taken;
    }

    /// <summary>
    /// Closes every run that has not ended as interrupted, at its last record. The caller decides when a session will not
    /// write any more: nothing here does it, because a run that is going is not an interrupted one.
    /// </summary>
    /// <remarks>
    /// The caller can only guess, from how long the session has been quiet: a run that waits for an answer of the user, or for a
    /// long command, writes nothing either. The runs are remembered as they were (<see cref="SessionFactsState.SettledRuns"/>), and
    /// a later record of one opens it again and takes back what its closing counted.
    /// </remarks>
    public void InterruptOpenRuns()
    {
        foreach (var run in _state.OpenRuns.ToArray())
        {
            var settled = new SettledRunState { Run = run, End = run.AccountedTo, Provider = CurrentProvider(), Model = CurrentModel(), Effort = _state.Effort };
            CloseRun(run, RunOutcome.Interrupted, run.AccountedTo);
            _state.SettledRuns.Add(settled);
            if (_state.SettledRuns.Count > SessionFactsState.MaxSettledRuns)
            {
                _state.SettledRuns.RemoveAt(0);
            }
        }
    }

    /// <inheritdoc />
    public void OnRecord(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Timestamp is not { } timestamp)
        {
            return;
        }

        _recordsInBatch = true;
        if (_state.FirstRecord is not { } first || timestamp < first)
        {
            _state.FirstRecord = timestamp;
        }

        if (_state.LastRecord is not { } last || timestamp > last)
        {
            _state.LastRecord = timestamp;
        }

        var modelApplied = false;
        var run = string.IsNullOrEmpty(record.RunId) ? null : TouchRun(record.RunId, timestamp, record as ModelChangedRecord, ref modelApplied);
        switch (record)
        {
            case HeaderRecord header:
                OnHeader(header, timestamp);
                break;
            case StateRecord state:
                OnState(state);
                break;
            case ModelChangedRecord changed:
                if (!modelApplied)
                {
                    OnModelChanged(changed);
                }

                break;
            case UserContentRecord user:
                OnUserContent(user, timestamp, run);
                break;
            case ContentRecord content:
                OnContent(content, timestamp, run);
                break;
            case UsageRecord usage:
                OnUsage(usage, timestamp, run);
                break;
            case ToolRecord tool:
                OnTool(tool, timestamp, run);
                break;
            case CompactionRecord compaction:
                OnCompaction(compaction, timestamp, run);
                break;
            case SystemPromptRecord prompt:
                OnSystemPrompt(prompt, timestamp);
                break;
            case RunEndRecord end:
                OnRunEnd(end, timestamp, run);
                break;
        }

        if (!string.IsNullOrEmpty(record.Provider))
        {
            _state.Provider = StatisticsProviders.Fold(record.Provider);
        }
    }

    // ----- runs -----

    // Opens the run of a record or counts the time of the one that is going. A change of model that starts a run is in force from the
    // start of that run, and one that comes during a run only from its own time.
    private OpenRunState? TouchRun(string runId, DateTimeOffset timestamp, ModelChangedRecord? change, ref bool changeApplied)
    {
        var run = FindOpenRun(runId);
        if (run is not null)
        {
            Account(run, timestamp);
            _dirtyRuns.Add(runId);
            return run;
        }

        if (IsClosed(runId))
        {
            if (_state.SettledRuns.Find(candidate => string.Equals(candidate.Run.RunId, runId, StringComparison.Ordinal)) is { } settled)
            {
                // Closed because its session had been quiet for long, and it goes on: it is the same run, and it was not interrupted.
                run = Reopen(settled);
                Account(run, timestamp);
                _dirtyRuns.Add(runId);
                return run;
            }

            Diagnostics.RecordsAfterRunEnd++;
            if (_interrupted.Contains(runId))
            {
                Diagnostics.RecordsAfterInterruption++;
            }

            return null;
        }

        if (InterruptRunWhenAnotherStarts && _state.OpenRuns.Count > 0)
        {
            // A session runs one turn at a time: a run that has no end when the next one starts was interrupted.
            foreach (var previous in _state.OpenRuns.ToArray())
            {
                Diagnostics.RunsInterruptedByNextRun++;
                _interrupted.Add(previous.RunId);
                CloseRun(previous, RunOutcome.Interrupted, previous.AccountedTo);
            }
        }

        if (change is not null)
        {
            OnModelChanged(change);
            changeApplied = true;
        }

        // A session runs one turn at a time: once another run starts, a run that was closed for a quiet session did end there.
        _state.SettledRuns.Clear();
        run = new OpenRunState
        {
            RunId = runId,
            Start = timestamp,
            AccountedTo = timestamp,
            Provider = CurrentProvider(),
            Model = CurrentModel(),
            Effort = _state.Effort,
            PermissionMode = _state.PermissionMode,
        };
        _state.OpenRuns.Add(run);
        _dirtyRuns.Add(runId);
        _batch.ActivityFor(new ActivityKey(QuarterHour.Of(timestamp), run.Provider, run.Model, run.Effort)).RunsStarted++;
        if (run.PermissionMode.Length > 0)
        {
            _batch.CountDetail(new DetailKey(QuarterHour.Of(timestamp), DetailList.PermissionMode, run.PermissionMode));
        }

        return run;
    }

    private OpenRunState? FindOpenRun(string runId)
    {
        var runs = _state.OpenRuns;
        for (var index = 0; index < runs.Count; index++)
        {
            if (string.Equals(runs[index].RunId, runId, StringComparison.Ordinal))
            {
                return runs[index];
            }
        }

        return null;
    }

    private bool IsClosed(string runId)
    {
        var closed = _state.ClosedRuns;
        for (var index = 0; index < closed.Count; index++)
        {
            if (string.Equals(closed[index], runId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Counts the time of a run up to a record, cut at the limits of quarter hours, for the model in force. The time of a
    // record that is earlier than the latest one counted adds nothing: only the latest time of a run matters.
    private void Account(OpenRunState run, DateTimeOffset to)
    {
        if (to <= run.AccountedTo)
        {
            return;
        }

        if (to - run.AccountedTo > MaxRunGap)
        {
            // No run stays a week without a record: the time is one of a damaged line or of a clock that was wrong. It is not time
            // the run was active, and it is not walked through one quarter hour at a time.
            run.AccountedTo = to;
            return;
        }

        var provider = CurrentProvider();
        var model = CurrentModel();
        var effort = _state.Effort;
        var cursor = run.AccountedTo;
        while (cursor < to)
        {
            var quarter = QuarterHour.Of(cursor);
            var pieceEnd = quarter.End < to ? quarter.End : to;
            var total = run.RemainderTicks + (pieceEnd - cursor).Ticks;
            var milliseconds = total / TimeSpan.TicksPerMillisecond;
            run.RemainderTicks = total % TimeSpan.TicksPerMillisecond;
            if (milliseconds != 0)
            {
                _batch.ActivityFor(new ActivityKey(quarter, provider, model, effort)).ActiveMs += milliseconds;
            }

            cursor = pieceEnd;
        }

        run.AccountedTo = to;
    }

    private void CloseRun(OpenRunState run, RunOutcome outcome, DateTimeOffset endTimestamp)
    {
        var end = endTimestamp > run.AccountedTo ? endTimestamp : run.AccountedTo;
        Account(run, end);
        var provider = CurrentProvider();
        var model = CurrentModel();
        var outcomeKey = new ActivityKey(QuarterHour.Of(end), provider, model, _state.Effort);
        var measures = _batch.ActivityFor(outcomeKey);
        switch (outcome)
        {
            case RunOutcome.Completed:
                measures.RunsCompleted++;
                break;
            case RunOutcome.Failed:
                measures.RunsFailed++;
                break;
            default:
                measures.RunsInterrupted++;
                break;
        }

        var startQuarter = QuarterHour.Of(run.Start);
        var durationMs = ObserveRun(run, end, +1);
        _batch.Offer(startQuarter, ExtremeMeasure.LongestRunMs, string.Empty, durationMs, run.RunId, end);
        _batch.Offer(startQuarter, ExtremeMeasure.MostToolCallsInRun, string.Empty, run.ToolCalls, run.RunId, end);
        _batch.Runs[run.RunId] = BuildRunRow(run, outcome, end);
        _state.OpenRuns.Remove(run);
        _dirtyRuns.Remove(run.RunId);
        _state.ClosedRuns.Add(run.RunId);
        if (_state.ClosedRuns.Count > SessionFactsState.MaxClosedRuns)
        {
            var forgotten = _state.ClosedRuns[0];
            _state.ClosedRuns.RemoveAt(0);
            _state.SettledRuns.RemoveAll(settled => string.Equals(settled.Run.RunId, forgotten, StringComparison.Ordinal));
        }
    }

    // Puts (+1) a run that ends in the distributions of the runs, or takes it out (-1) when the run is opened again.
    private long ObserveRun(OpenRunState run, DateTimeOffset end, int sign)
    {
        var startQuarter = QuarterHour.Of(run.Start);
        var durationMs = Math.Max(0, (long)Math.Round((end - run.Start).TotalMilliseconds));
        ObserveSigned(startQuarter, HistogramMeasure.RunDurationMs, string.Empty, durationMs, sign);
        ObserveSigned(startQuarter, HistogramMeasure.RunToolCalls, string.Empty, run.ToolCalls, sign);
        if (run.CostUsd > 0)
        {
            ObserveSigned(startQuarter, HistogramMeasure.RunCostMicro, CostUnits.Usd, (long)Math.Round(run.CostUsd * 1_000_000), sign);
        }

        if (run.CostCredits > 0)
        {
            ObserveSigned(startQuarter, HistogramMeasure.RunCostMicro, CostUnits.Credits, (long)Math.Round(run.CostCredits * 1_000_000), sign);
        }

        return durationMs;
    }

    // Opens again a run that was closed as interrupted for a session that went quiet: the interruption and the place of the run in
    // the distributions are taken back, and the run goes on as it was. The largest values it gave stay: it can only give larger ones.
    private OpenRunState Reopen(SettledRunState settled)
    {
        var run = settled.Run;
        _state.SettledRuns.Remove(settled);
        _state.ClosedRuns.Remove(run.RunId);
        _batch.ActivityFor(new ActivityKey(QuarterHour.Of(settled.End), settled.Provider, settled.Model, settled.Effort)).RunsInterrupted--;
        ObserveRun(run, settled.End, -1);
        _state.OpenRuns.Add(run);
        Diagnostics.RunsReopened++;
        return run;
    }

    private RunRow BuildRunRow(OpenRunState run, RunOutcome outcome, DateTimeOffset end)
        => new()
        {
            SessionId = _sessionId,
            RunId = run.RunId,
            Start = run.Start,
            End = end,
            Outcome = outcome,
            Sender = run.Sender,
            PromptKind = run.PromptKind,
            PromptChars = run.PromptChars,
            PromptWords = run.PromptWords,
            Requests = run.Requests,
            ToolCalls = run.ToolCalls,
            ToolFailures = run.ToolFailures,
            InputTokens = run.InputTokens,
            OutputTokens = run.OutputTokens,
            Compactions = run.Compactions,
            AnswerChars = run.AnswerChars,
            AnswerWords = run.AnswerWords,
            CostUsd = run.CostUsd,
            CostCredits = run.CostCredits,
            Provider = run.Provider,
            Model = run.Model,
            Effort = run.Effort,
            PermissionMode = run.PermissionMode,
        };

    private SessionRow BuildSessionRow()
        => new()
        {
            SessionId = _sessionId,
            ProjectRef = _state.ProjectRef,
            SessionKind = _state.SessionKind,
            ParentSessionId = _state.ParentSessionId,
            CreatedByKind = _state.CreatedByKind,
            CreatedBySessionId = _state.CreatedBySessionId,
            AutomationId = _state.AutomationId,
            Title = _state.Title,
            Provider = _state.InitialProvider,
            PermissionMode = _state.PermissionMode.Length == 0 ? null : _state.PermissionMode,
            FirstRecord = _state.FirstRecord,
            LastRecord = _state.LastRecord,
            HasHeader = _state.HasHeader,
        };

    // ----- the identity and the choices of the session -----

    private void OnHeader(HeaderRecord header, DateTimeOffset timestamp)
    {
        _state.HasHeader = true;
        _state.SessionKind = header.SessionKind ?? _state.SessionKind;
        _state.ProjectRef = header.ProjectRef ?? _state.ProjectRef;
        _state.ParentSessionId = header.ParentSessionId ?? _state.ParentSessionId;
        ApplyCreatedBy(header.CreatedBy);
        _state.Title = string.IsNullOrWhiteSpace(header.Title) ? _state.Title : header.Title;
        _state.InitialProvider = StatisticsProviders.Fold(header.ProviderKey ?? header.Provider);
        if (_state.Provider.Length == 0)
        {
            _state.Provider = _state.InitialProvider;
        }

        CountOrigin(header.CreatedAt ?? timestamp);
    }

    private void CountOrigin(DateTimeOffset at)
    {
        if (_state.OriginCounted)
        {
            return;
        }

        _state.OriginCounted = true;
        var origin = string.IsNullOrEmpty(_state.CreatedByKind)
            ? (string.IsNullOrEmpty(_state.ParentSessionId) ? "root" : "child")
            : _state.CreatedByKind;
        _batch.CountDetail(new DetailKey(QuarterHour.Of(at), DetailList.SessionOrigin, origin));
    }

    private void ApplyCreatedBy(JournalActor? actor)
    {
        if (actor is null)
        {
            return;
        }

        _state.CreatedByKind = actor.Kind ?? _state.CreatedByKind;
        _state.CreatedBySessionId = actor.SourceSessionId ?? _state.CreatedBySessionId;
        _state.AutomationId = actor.AutomationId ?? _state.AutomationId;
    }

    private void OnState(StateRecord record)
    {
        if (!string.IsNullOrEmpty(record.PermissionMode))
        {
            _state.PermissionMode = record.PermissionMode;
        }

        _state.ParentSessionId ??= record.ParentSessionId;
        if (_state.CreatedByKind is null)
        {
            ApplyCreatedBy(record.CreatedBy);
        }

        // The state says what the session selected; the model is known from the first change of model, and from the
        // requests. Until then the selection is the best that is known.
        if (_state.Model.Length == 0 && !string.IsNullOrEmpty(record.ModelId))
        {
            _state.Model = ResolveAlias(record.ModelId);
            _state.Effort = NormalizeEffort(record.ReasoningEffort) ?? _state.Effort;
            if (_state.AgentPrompt.Length == 0 && !string.IsNullOrEmpty(record.AgentPromptId))
            {
                _state.AgentPrompt = record.AgentPromptId;
            }

            if (!string.IsNullOrEmpty(record.ProviderKey))
            {
                _state.Provider = StatisticsProviders.Fold(record.ProviderKey);
            }
        }

        foreach (var entry in record.Provenance)
        {
            RememberSeen(entry.IdHash);
            ApplyProvenance(entry);
        }
    }

    private void RememberSeen(ulong hash)
    {
        if (_seenPrompts.Add(hash))
        {
            _state.SeenPrompts.Add(hash);
            if (_state.SeenPrompts.Count > SessionFactsState.MaxSeenPrompts)
            {
                _seenPrompts.Remove(_state.SeenPrompts[0]);
                _state.SeenPrompts.RemoveAt(0);
            }
        }
    }

    private void OnModelChanged(ModelChangedRecord record)
    {
        if (!string.IsNullOrEmpty(record.ProviderKey))
        {
            _state.Provider = StatisticsProviders.Fold(record.ProviderKey);
        }

        if (!string.IsNullOrEmpty(record.ModelId))
        {
            _state.Model = ResolveAlias(record.ModelId);
        }

        _state.Effort = NormalizeEffort(record.ReasoningEffort) ?? _state.Effort;
        if (!string.IsNullOrEmpty(record.AgentPromptId))
        {
            _state.AgentPrompt = record.AgentPromptId;
        }
    }

    private string ResolveAlias(string model) => _state.ModelAliases.TryGetValue(model, out var reported) ? reported : model;

    private string CurrentProvider() => _state.Provider.Length == 0 ? StatisticsProviders.Unknown : _state.Provider;

    private string CurrentModel() => _state.Model.Length == 0 ? "unknown" : _state.Model;

    private static string? NormalizeEffort(string? effort)
    {
        if (effort is null)
        {
            return null;
        }

        var trimmed = effort.Trim();
        return trimmed.Length == 0 || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : trimmed.ToLowerInvariant();
    }

    // ----- prompts -----

    private void OnUserContent(UserContentRecord record, DateTimeOffset timestamp, OpenRunState? run)
    {
        var entry = TakeUnmatchedProvenance(timestamp, record.RunId);
        if (entry is not null)
        {
            Diagnostics.ProvenanceMatchedEarly++;
        }

        var detectedSteer = run is { HasPrompt: true };
        var prompt = new RecentPromptState
        {
            At = timestamp,
            RunId = record.RunId,
            IsAnswer = record.IsAnswer,
            HasSourceSession = !string.IsNullOrEmpty(record.SourceSessionId),
            DetectedSteer = detectedSteer,
            StartsRun = run is { HasPrompt: false },
            Chars = record.Chars,
            Words = record.Words,
            Files = record.Files,
            Directories = record.Directories,
            Images = record.Images,
            Skills = record.Skills,
        };
        var (sender, kind) = Classify(prompt, entry);
        prompt.Sender = sender;
        prompt.Kind = kind;
        prompt.Bound = entry is not null;
        Count(prompt, +1);

        if (run is not null && !run.HasPrompt)
        {
            run.HasPrompt = true;
            run.Sender = sender;
            run.PromptKind = kind;
            run.PromptChars = record.Chars;
            run.PromptWords = record.Words;
        }

        _state.RecentPrompts.Add(prompt);
        if (_state.RecentPrompts.Count > SessionFactsState.MaxRecentPrompts)
        {
            _state.RecentPrompts.RemoveAt(0);
        }
    }

    private static (PromptSender Sender, PromptKind Kind) Classify(RecentPromptState prompt, PendingProvenanceState? entry)
    {
        var sender = entry is not null
            ? SenderOf(entry.ActorKind, prompt.HasSourceSession || !string.IsNullOrEmpty(entry.ActorSessionId))
            : prompt.HasSourceSession ? PromptSender.Agent : PromptSender.You;
        PromptKind kind;
        if (prompt.IsAnswer)
        {
            kind = PromptKind.Answer;
        }
        else if (prompt.DetectedSteer || string.Equals(entry?.DispatchKind, "steer", StringComparison.OrdinalIgnoreCase))
        {
            kind = PromptKind.Steer;
        }
        else
        {
            kind = entry?.Queued == true ? PromptKind.Queued : PromptKind.NewTurn;
        }

        return (sender, kind);
    }

    private static PromptSender SenderOf(string? actorKind, bool hasSession)
    {
        if (string.IsNullOrEmpty(actorKind) || actorKind.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            return hasSession ? PromptSender.Agent : PromptSender.You;
        }

        return actorKind.ToLowerInvariant() switch
        {
            "agent" => PromptSender.Agent,
            "reminder" => PromptSender.Reminder,
            "automation" => PromptSender.Automation,
            _ => PromptSender.Other,
        };
    }

    // Adds (+1) or takes away (-1) what a prompt counted for its sender and its kind.
    private void Count(RecentPromptState prompt, int sign)
    {
        var quarter = QuarterHour.Of(prompt.At);
        var measures = new ContentMeasures
        {
            Count = sign,
            Chars = sign * prompt.Chars,
            Words = sign * prompt.Words,
            Files = sign * prompt.Files,
            Directories = sign * prompt.Directories,
            Images = sign * prompt.Images,
            Skills = sign * prompt.Skills,
        };
        _batch.ContentFor(new ContentKey(quarter, ContentKind.Prompt, prompt.Sender, prompt.Kind)).Add(measures);
        if (prompt.Sender == PromptSender.You)
        {
            ObserveSigned(quarter, HistogramMeasure.PromptChars, string.Empty, prompt.Chars, sign);
            ObserveSigned(quarter, HistogramMeasure.PromptWords, string.Empty, prompt.Words, sign);
        }

        if (prompt.StartsRun)
        {
            _batch.CountDetail(new DetailKey(quarter, DetailList.RunOrigin, SenderName(prompt.Sender)), sign);
        }
    }

    private void ObserveSigned(QuarterHour quarter, HistogramMeasure measure, string subject, long value, int sign)
    {
        var key = new HistogramKey(quarter, measure, subject, HistogramSteps.StepOf(value));
        _batch.Histograms.TryGetValue(key, out var current);
        _batch.Histograms[key] = current + sign;
    }

    private static string SenderName(PromptSender sender) => sender.ToString().ToLowerInvariant();

    private PendingProvenanceState? TakeUnmatchedProvenance(DateTimeOffset at, string? runId)
    {
        PendingProvenanceState? best = null;
        var bestDistance = TimeSpan.MaxValue;
        foreach (var entry in _state.UnmatchedProvenance)
        {
            var distance = entry.CreatedAt is { } created ? (created - at).Duration() : TimeSpan.MaxValue;
            var byRun = runId is not null && string.Equals(entry.RunId, runId, StringComparison.Ordinal);
            if (!byRun && distance > ProvenanceWindow)
            {
                continue;
            }

            if (byRun)
            {
                distance = TimeSpan.Zero;
            }

            if (distance < bestDistance)
            {
                best = entry;
                bestDistance = distance;
            }
        }

        if (best is not null)
        {
            _state.UnmatchedProvenance.Remove(best);
        }

        return best;
    }

    private void ApplyProvenance(PromptProvenanceEntry source)
    {
        Diagnostics.ProvenanceEntries++;
        var entry = new PendingProvenanceState
        {
            IdHash = source.IdHash,
            DispatchKind = source.DispatchKind,
            RunId = source.RunId,
            Queued = source.Queued,
            ActorKind = source.SubmittedBy?.Kind,
            ActorSessionId = source.SubmittedBy?.SourceSessionId,
            CreatedAt = source.CreatedAt,
        };

        RecentPromptState? best = null;
        var bestDistance = TimeSpan.MaxValue;
        foreach (var prompt in _state.RecentPrompts)
        {
            if (prompt.Bound)
            {
                continue;
            }

            var byRun = entry.RunId is not null && string.Equals(prompt.RunId, entry.RunId, StringComparison.Ordinal);
            var distance = entry.CreatedAt is { } created ? (created - prompt.At).Duration() : TimeSpan.MaxValue;
            if (!byRun && distance > ProvenanceWindow)
            {
                continue;
            }

            if (byRun)
            {
                distance = TimeSpan.Zero;
            }

            if (best is null || distance < bestDistance || (distance == bestDistance && prompt.At > best.At))
            {
                best = prompt;
                bestDistance = distance;
            }
        }

        if (best is null)
        {
            _state.UnmatchedProvenance.Add(entry);
            if (_state.UnmatchedProvenance.Count > SessionFactsState.MaxUnmatchedProvenance)
            {
                _state.UnmatchedProvenance.RemoveAt(0);
            }

            return;
        }

        best.Bound = true;
        Diagnostics.ProvenanceMatchedLate++;
        var (sender, kind) = Classify(best, entry);
        if (sender == best.Sender && kind == best.Kind)
        {
            return;
        }

        Diagnostics.ProvenanceCorrections++;

        // The prompt was counted before its provenance was written: move what it counted to the right sender and kind.
        Count(best, -1);
        best.Sender = sender;
        best.Kind = kind;
        Count(best, +1);
        if (best.StartsRun && best.RunId is not null)
        {
            var run = FindOpenRun(best.RunId);
            if (run is not null)
            {
                run.Sender = sender;
                run.PromptKind = kind;
                _dirtyRuns.Add(run.RunId);
            }
        }
    }

    // ----- contents -----

    private void OnContent(ContentRecord record, DateTimeOffset timestamp, OpenRunState? run)
    {
        var kind = record.Channel switch
        {
            ContentChannel.Assistant => ContentKind.Answer,
            ContentChannel.Reasoning => ContentKind.Reasoning,
            _ => ContentKind.ReasoningSummary,
        };
        var measures = _batch.ContentFor(new ContentKey(QuarterHour.Of(timestamp), kind, PromptSender.None, PromptKind.None));
        measures.Count++;
        measures.Chars += record.Chars;
        measures.Words += record.Words;
        if (run is not null && record.Channel == ContentChannel.Assistant)
        {
            run.AnswerChars += record.Chars;
            run.AnswerWords += record.Words;
        }
    }

    private void OnSystemPrompt(SystemPromptRecord record, DateTimeOffset timestamp)
    {
        var measures = _batch.ContentFor(new ContentKey(QuarterHour.Of(timestamp), ContentKind.Instructions, PromptSender.None, PromptKind.None));
        measures.Count++;
        measures.Chars += record.SystemChars + record.DeveloperChars;
        measures.ApproxTokens += record.ApproxTokens;
        if (!string.IsNullOrEmpty(record.AgentPromptId))
        {
            _state.AgentPrompt = record.AgentPromptId;
        }
    }

    // ----- requests -----

    private void OnUsage(UsageRecord record, DateTimeOffset timestamp, OpenRunState? run)
    {
        var op = record.Operation;
        if (op is null)
        {
            return;
        }

        var hasTokens = op.InputTokens is not null || op.OutputTokens is not null || op.CachedInputTokens is not null
            || op.CacheReadTokens is not null || op.CacheWriteTokens is not null || op.ReasoningTokens is not null;
        var hasCost = op.Cost is { } cost && double.IsFinite(cost) && cost >= 0;
        if (!hasTokens && !hasCost && op.DurationMs is null)
        {
            return;
        }

        var quarter = QuarterHour.Of(timestamp);
        var provider = StatisticsProviders.Fold(record.Provider ?? _state.Provider);
        if (op.Equals(_lastOperation))
        {
            Diagnostics.RepeatedOperations++;
        }

        _lastOperation = op;
        if (!string.IsNullOrWhiteSpace(op.Model))
        {
            if (_state.Model.Length > 0 && !string.Equals(_state.Model, op.Model, StringComparison.Ordinal))
            {
                Diagnostics.ModelRenamedByUsage++;
                if (_state.ModelAliases.Count < 64)
                {
                    _state.ModelAliases[_state.Model] = op.Model;
                }
            }

            // The model the provider says it used, not the alias that was chosen.
            _state.Model = op.Model;
        }

        var model = CurrentModel();
        var effort = NormalizeEffort(op.ReasoningEffort) ?? _state.Effort;
        var purpose = string.Equals(op.Initiator, "compaction", StringComparison.OrdinalIgnoreCase) ? UsagePurpose.Compaction : UsagePurpose.Turn;

        // A provider that reports the cost and the duration of a whole turn repeats them: the same pair twice in a run is one. A
        // cost without a duration is the cost of its request (the credits of Copilot), and two requests may well cost the same.
        var repeated = run is not null && op.Cost is not null && op.DurationMs is not null && run.LastCost == op.Cost && run.LastCostDuration == op.DurationMs;
        var measures = _batch.UsageFor(new UsageKey(quarter, provider, model, effort, _state.AgentPrompt, purpose));
        if (hasTokens)
        {
            var split = AgentInputTokenUsage.From(op);
            measures.Requests++;
            measures.InputTokens += split?.Total ?? 0;
            measures.FreshInputTokens += split?.Uncached ?? 0;
            measures.CacheReadTokens += split?.CacheRead ?? 0;
            measures.CacheWriteTokens += split?.CacheWrite ?? 0;
            measures.OutputTokens += Math.Max(0, op.OutputTokens ?? 0);
            measures.ReasoningTokens += Math.Max(0, op.ReasoningTokens ?? 0);
            _batch.Observe(quarter, HistogramMeasure.RequestInputTokens, model, split?.Total ?? 0);
            _batch.Observe(quarter, HistogramMeasure.RequestOutputTokens, model, Math.Max(0, op.OutputTokens ?? 0));
            _batch.Offer(quarter, ExtremeMeasure.LargestRequestInputTokens, model, split?.Total ?? 0, run?.RunId, timestamp);
            if (run is not null && purpose == UsagePurpose.Turn)
            {
                run.Requests++;
                run.InputTokens += split?.Total ?? 0;
                run.OutputTokens += Math.Max(0, op.OutputTokens ?? 0);
            }
        }

        if (!repeated && op.DurationMs is { } duration && double.IsFinite(duration) && duration > 0)
        {
            measures.ProviderDurationMs += (long)duration;
        }

        if (record.WindowTokens is { } windowTokens && record.WindowLimit is { } windowLimit && windowLimit > 0 && windowTokens >= 0)
        {
            var fill = Math.Min((long)((double)windowTokens * 1_000_000 / windowLimit), 10_000_000L);
            measures.ContextSamples++;
            measures.ContextTokensSum += windowTokens;
            measures.ContextLimitSum += windowLimit;
            measures.ContextFillPpmSum += fill;
            measures.ContextFillPpmMax = Math.Max(measures.ContextFillPpmMax, fill);
            _batch.Offer(quarter, ExtremeMeasure.HighestContextFillPpm, model, fill, run?.RunId, timestamp);
        }

        if (hasCost && !repeated)
        {
            var amount = op.Cost!.Value;
            var unit = CostUnits.Of(op.CostUnit);
            var costMeasures = _batch.CostFor(new CostKey(quarter, provider, model, unit));
            costMeasures.Total += amount;
            costMeasures.Records++;
            if (run is not null)
            {
                if (unit == CostUnits.Usd)
                {
                    run.CostUsd += amount;
                }
                else if (unit == CostUnits.Credits)
                {
                    run.CostCredits += amount;
                }
            }
        }

        if (run is not null && op.Cost is not null)
        {
            run.LastCost = op.Cost;
            run.LastCostDuration = op.DurationMs;
        }
    }

    // ----- tool calls -----

    private void OnTool(ToolRecord record, DateTimeOffset timestamp, OpenRunState? run)
    {
        var kind = StatisticsToolBuckets.KindOf(record.ActivityKind, record.Name);
        var tool = StatisticsToolBuckets.Bucket(record.ActivityKind, record.Name);
        var provider = StatisticsProviders.Fold(record.Provider ?? _state.Provider);
        var quarter = QuarterHour.Of(timestamp);
        var measures = _batch.ToolFor(new ToolKey(quarter, provider, kind, tool));
        var activityId = record.ActivityId ?? string.Empty;

        if (record.Phase == ToolPhase.Started)
        {
            CountCall(record, measures, kind, quarter, run);
            if (activityId.Length > 0)
            {
                _state.OpenTools.RemoveAll(open => open.ActivityId == activityId);
                _state.OpenTools.Add(new OpenToolState { ActivityId = activityId, Start = timestamp });
                if (_state.OpenTools.Count > SessionFactsState.MaxOpenTools)
                {
                    _state.OpenTools.RemoveAt(0);
                }
            }

            return;
        }

        OpenToolState? started = null;
        if (activityId.Length > 0)
        {
            started = _state.OpenTools.Find(open => open.ActivityId == activityId);
            if (started is not null)
            {
                _state.OpenTools.Remove(started);
            }
        }

        if (started is null)
        {
            // An end whose start is not in the journal: the call still happened.
            CountCall(record, measures, kind, quarter, run);
        }

        switch (record.Phase)
        {
            case ToolPhase.Failed:
                measures.Failures++;
                if (run is not null)
                {
                    run.ToolFailures++;
                }

                break;
            case ToolPhase.Canceled:
                measures.Canceled++;
                break;
        }

        measures.BytesOut += record.ResultBytes;
        measures.FilesRead += record.FilesRead;
        if (record.ModifiedExtensions is { Count: > 0 } extensions)
        {
            measures.FilesChanged += extensions.Count;
            foreach (var extension in extensions)
            {
                _batch.CountDetail(new DetailKey(quarter, DetailList.ChangedFileExtension, extension));
            }
        }

        if (record.Phase == ToolPhase.Completed)
        {
            measures.LinesAdded += record.LinesAdded;
            measures.LinesRemoved += record.LinesRemoved;
        }

        if (started is not null)
        {
            var durationMs = Math.Clamp((long)Math.Round((timestamp - started.Start).TotalMilliseconds), 0, MaxToolDurationMs);
            measures.DurationCount++;
            measures.DurationMsTotal += durationMs;
            measures.DurationMsMax = Math.Max(measures.DurationMsMax, durationMs);
            _batch.Observe(quarter, HistogramMeasure.ToolDurationMs, tool, durationMs);
            _batch.Offer(quarter, ExtremeMeasure.LongestToolMs, tool, durationMs, run?.RunId, timestamp);
        }
    }

    private void CountCall(ToolRecord record, ToolMeasures measures, ToolKind kind, QuarterHour quarter, OpenRunState? run)
    {
        measures.Calls++;
        measures.BytesIn += record.ArgumentBytes;
        if (run is not null)
        {
            run.ToolCalls++;
        }

        switch (kind)
        {
            case ToolKind.Shell when record.ShellProgram is { Length: > 0 } program:
                _batch.CountDetail(new DetailKey(quarter, DetailList.ShellProgram, program));
                break;
            case ToolKind.Alta when record.AltaCommand is { Length: > 0 } command:
                _batch.CountDetail(new DetailKey(quarter, DetailList.AltaCommand, command));
                break;
            case ToolKind.Skill when record.SkillName is { Length: > 0 } skill:
                _batch.CountDetail(new DetailKey(quarter, DetailList.Skill, skill));
                break;
        }
    }

    // ----- compactions, run ends -----

    private void OnCompaction(CompactionRecord record, DateTimeOffset timestamp, OpenRunState? run)
    {
        var quarter = QuarterHour.Of(timestamp);
        var measures = _batch.ActivityFor(new ActivityKey(quarter, CurrentProvider(), CurrentModel(), _state.Effort));
        measures.Compactions++;
        measures.CompactionTokensBefore += Math.Max(0, record.TokensBefore ?? 0);
        measures.CompactionTokensAfter += Math.Max(0, record.TokensAfter ?? 0);
        _batch.CountDetail(new DetailKey(quarter, DetailList.CompactionTrigger, string.IsNullOrEmpty(record.Trigger) ? "unknown" : record.Trigger));
        if (run is not null)
        {
            run.Compactions++;
        }
    }

    private void OnRunEnd(RunEndRecord record, DateTimeOffset timestamp, OpenRunState? run)
    {
        if (record.End == RunEndKind.Error)
        {
            _batch.ActivityFor(new ActivityKey(QuarterHour.Of(timestamp), CurrentProvider(), CurrentModel(), _state.Effort)).Errors++;
        }

        // A record that carries no run ends the run that is going, when there is only one.
        if (run is null && string.IsNullOrEmpty(record.RunId) && _state.OpenRuns.Count == 1)
        {
            run = _state.OpenRuns[0];
        }

        if (run is not null)
        {
            CloseRun(run, record.End == RunEndKind.Error ? RunOutcome.Failed : RunOutcome.Completed, timestamp);
        }
    }
}

/// <summary>The units a cost is given in.</summary>
internal static class CostUnits
{
    /// <summary>US dollars, the unit of a cost that names none.</summary>
    public const string Usd = "usd";

    /// <summary>The AI credits of Copilot.</summary>
    public const string Credits = "AI credits";

    /// <summary>Gets the unit of a cost.</summary>
    /// <param name="unit">The unit the provider gave; null for none.</param>
    /// <returns>The unit to count under.</returns>
    public static string Of(string? unit)
        => string.IsNullOrWhiteSpace(unit) ? Usd : unit.Equals(Credits, StringComparison.OrdinalIgnoreCase) ? Credits : unit.Trim();
}

/// <summary>Counters of what the reducer met that is not a fact: they tell how the definitions fit real journals.</summary>
internal sealed class ReducerDiagnostics
{
    /// <summary>Gets or sets the records that carried a run that had already ended.</summary>
    public long RecordsAfterRunEnd { get; set; }

    /// <summary>Gets or sets the runs that were closed as interrupted because another run started.</summary>
    public long RunsInterruptedByNextRun { get; set; }

    /// <summary>Gets or sets the provenance entries read.</summary>
    public long ProvenanceEntries { get; set; }

    /// <summary>Gets or sets the entries that matched a prompt that was counted already.</summary>
    public long ProvenanceMatchedLate { get; set; }

    /// <summary>Gets or sets the entries that matched a prompt that came after them.</summary>
    public long ProvenanceMatchedEarly { get; set; }

    /// <summary>Gets or sets the entries that changed how a prompt was counted.</summary>
    public long ProvenanceCorrections { get; set; }

    /// <summary>Gets or sets the requests whose model differs from the model in force: the model that was chosen is an alias or another version.</summary>
    public long ModelRenamedByUsage { get; set; }

    /// <summary>Gets or sets the requests whose operation is equal in every value to the one of the request before.</summary>
    public long RepeatedOperations { get; set; }

    /// <summary>Gets or sets the records of runs that were closed as interrupted because another run started.</summary>
    public long RecordsAfterInterruption { get; set; }

    /// <summary>Gets or sets the runs that were closed for a session that went quiet and were opened again by a later record.</summary>
    public long RunsReopened { get; set; }
}
