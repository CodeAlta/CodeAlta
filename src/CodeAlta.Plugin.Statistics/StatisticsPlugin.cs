using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Ansi;
using XenoAtom.CommandLine;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.Markdown;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Styling;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.Plugin.Statistics;

/// <summary>
/// Built-in plugin that projects per-turn and session statistics as transient timeline cards.
/// </summary>
[Plugin("statistics", DisplayName = "Statistics", Description = "Projects transient per-turn and session statistics from normalized agent events.")]
public sealed class StatisticsPlugin : PluginBase
{
    private const string ProjectionName = "statistics";
    private const string RenderTarget = "codealta.statistics.turn.v1";
    private static readonly ConcurrentDictionary<string, AsyncTurnStatisticsProjection> s_turnProjections = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public override IEnumerable<PluginThreadEventProjectionContribution> GetThreadEventProjections()
    {
        yield return new PluginThreadEventProjectionContribution
        {
            Name = ProjectionName,
            ProjectAsync = ProjectAsync,
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution
        {
            Path = "statistics estimate",
            Description = "Estimate text bytes and tokens using CodeAlta's current statistics approximation.",
            Policy = new PluginAltaCommandPolicy
            {
                RequiresInProcessRuntime = false,
                SupportsCatalogOnlyContext = true,
            },
            CreateCommandNode = CreateAltaStatisticsCommand,
        };
    }

    private static Command CreateAltaStatisticsCommand(PluginAltaCommandContext context)
    {
        var command = new Command("statistics", "Statistics plugin commands.")
        {
            new CommandUsage(),
            new HelpOption(),
        };
        command.Add(CreateEstimateCommand(context));
        return command;
    }

    private static Command CreateEstimateCommand(PluginAltaCommandContext context)
    {
        string? text = null;
        var command = new Command("estimate", "Estimate UTF-8 bytes and approximate tokens for text.")
        {
            new CommandUsage(),
            new HelpOption(),
        };
        command.Add("<text>", "Text to estimate.", value => text = value);
        command.Add((_, _) =>
        {
            var value = text ?? string.Empty;
            var bytes = ByteCount(value);
            WriteAltaRecord(context.Stdout, new
            {
                type = "alta.statistics.estimate",
                version = 1,
                correlationId = context.CorrelationId,
                pluginRuntimeKey = context.Plugin.RuntimeKey,
                bytes,
                formattedBytes = FormatBytes(bytes),
                estimatedTokens = EstimateTokensFromCharacters(value.Length),
                characterCount = value.Length,
            });
            return new ValueTask<int>(0);
        });
        return command;
    }

    private static ValueTask<IReadOnlyList<PluginDerivedThreadEvent>> ProjectAsync(
        PluginThreadEventProjectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Events.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<PluginDerivedThreadEvent>>([]);
        }

        var turns = PendingTurnBuilder.BuildTurns(context.Events).ToArray();
        if (turns.Length == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<PluginDerivedThreadEvent>>([]);
        }

        var projected = new List<PluginDerivedThreadEvent>(turns.Length);
        foreach (var turn in turns.Where(static item => item.IsComplete))
        {
            var state = GetOrCreateProjectionState(context.ThreadId, turn);
            projected.Add(new PluginDerivedThreadEvent
            {
                EventId = $"statistics:{EscapeEventId(context.ThreadId)}:{EscapeEventId(turn.Key)}",
                Timestamp = turn.Timestamp,
                Markdown = state.Markdown,
                DetailSections = state.DetailSections,
                DynamicContent = state,
                RenderTarget = RenderTarget,
                Payload = state.Payload,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<PluginDerivedThreadEvent>>(projected);
    }

    private static AsyncTurnStatisticsProjection GetOrCreateProjectionState(string threadId, PendingTurn turn)
    {
        var cacheKey = FormattableString.Invariant($"{threadId}:{turn.Key}:{turn.Fingerprint}");
        return s_turnProjections.GetOrAdd(cacheKey, _ =>
        {
            var state = new AsyncTurnStatisticsProjection(turn);
            state.Start();
            return state;
        });
    }

    /// <summary>
    /// Estimates token count using CodeAlta's current approximation rule.
    /// </summary>
    /// <param name="characterCount">The character count.</param>
    /// <returns>The estimated token count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="characterCount"/> is negative.</exception>
    public static long EstimateTokensFromCharacters(long characterCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(characterCount);
        return (characterCount + 3) / 4;
    }

    /// <summary>
    /// Formats a byte count with compact binary units.
    /// </summary>
    /// <param name="bytes">The byte count.</param>
    /// <returns>The formatted byte count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="bytes"/> is negative.</exception>
    public static string FormatBytes(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        if (bytes < 1024)
        {
            return FormattableString.Invariant($"{bytes} B");
        }

        var value = bytes / 1024d;
        if (value < 1024d)
        {
            return FormattableString.Invariant($"{value:0.#} KB");
        }

        value /= 1024d;
        return FormattableString.Invariant($"{value:0.#} MB");
    }

    /// <summary>
    /// Formats a duration for compact timeline display.
    /// </summary>
    /// <param name="duration">The duration.</param>
    /// <returns>The formatted duration.</returns>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalSeconds < 1
            ? FormattableString.Invariant($"{duration.TotalMilliseconds:0} ms")
            : duration.TotalMinutes < 1
                ? FormattableString.Invariant($"{duration.TotalSeconds:0.0}s")
                : duration.TotalHours < 1
                    ? FormattableString.Invariant($"{(int)duration.TotalMinutes}m {duration.Seconds}s")
                    : FormattableString.Invariant($"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s");
    }

    private static string EscapeEventId(string value)
        => string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : string.Create(value.Length, value, static (span, source) =>
            {
                for (var index = 0; index < source.Length; index++)
                {
                    var ch = source[index];
                    span[index] = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_';
                }
            });

    private static void WriteAltaRecord(TextWriter writer, object record)
    {
        writer.Write(JsonSerializer.Serialize(record, JsonSerializerOptions.Web));
        writer.WriteLine();
    }

    private static long ByteCount(string? text)
        => string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);

    private static bool IsTerminalPhase(AgentActivityPhase phase)
        => phase is AgentActivityPhase.Completed or AgentActivityPhase.Failed or AgentActivityPhase.Canceled;

    private static bool IsToolActivity(AgentActivityKind kind)
        => kind is not AgentActivityKind.Turn and not AgentActivityKind.Compaction;

    private static bool IsToolOutputKind(AgentContentKind kind)
        => kind is AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput or AgentContentKind.ToolOutput;

    private static string ToolBucketName(AgentActivityKind kind, string? name)
    {
        if (kind == AgentActivityKind.CommandExecution ||
            Contains(name, "shell") ||
            Contains(name, "command") ||
            Contains(name, "bash") ||
            Contains(name, "pwsh") ||
            Contains(name, "powershell"))
        {
            return "shell";
        }

        var normalizedName = string.IsNullOrWhiteSpace(name) ? kind.ToString() : name.Trim();
        return FormattableString.Invariant($"{kind}:{normalizedName}");

        static bool Contains(string? text, string value)
            => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    private sealed class TurnStatisticsBuilder
    {
        private readonly Dictionary<string, TurnBuilder> _turns = new(StringComparer.Ordinal);
        private TurnBuilder? _fallbackTurn;
        private int _fallbackOrdinal;

        public static IReadOnlyList<TurnStatistics> BuildTurns(IReadOnlyList<AgentEvent> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            var builder = new TurnStatisticsBuilder();
            foreach (var @event in events.OrderBy(static item => item.Timestamp))
            {
                builder.Add(@event);
            }

            return builder._turns.Values
                .Select(static item => item.Build())
                .OrderBy(static item => item.StartedAt ?? item.FirstEventAt ?? DateTimeOffset.MinValue)
                .ToArray();
        }

        public static TurnStatistics BuildTurn(string key, string sessionId, string? runId, IReadOnlyList<AgentEvent> events)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
            ArgumentNullException.ThrowIfNull(events);

            var turn = new TurnBuilder(key, sessionId, runId);
            foreach (var @event in events.OrderBy(static item => item.Timestamp))
            {
                turn.Add(@event);
            }

            return turn.Build();
        }

        private void Add(AgentEvent @event)
        {
            var turn = GetTurn(@event);
            turn.Add(@event);
        }

        private TurnBuilder GetTurn(AgentEvent @event)
        {
            if (@event.RunId is { } runId)
            {
                var key = "run-" + runId.Value;
                return GetOrCreate(key, @event.SessionId, runId.Value);
            }

            if (_fallbackTurn is null || StartsFallbackTurn(@event, _fallbackTurn))
            {
                var key = FormattableString.Invariant($"session-{@event.SessionId}-turn-{++_fallbackOrdinal}");
                _fallbackTurn = GetOrCreate(key, @event.SessionId, null);
            }

            return _fallbackTurn;
        }

        private TurnBuilder GetOrCreate(string key, string sessionId, string? runId)
        {
            if (_turns.TryGetValue(key, out var turn))
            {
                return turn;
            }

            turn = new TurnBuilder(key, sessionId, runId);
            _turns.Add(key, turn);
            return turn;
        }

        private static bool StartsFallbackTurn(AgentEvent @event, TurnBuilder current)
            => @event is AgentActivityEvent { Kind: AgentActivityKind.Turn, Phase: AgentActivityPhase.Started } && current.HasEvents;
    }

    private sealed class PendingTurnBuilder
    {
        private readonly Dictionary<string, PendingTurnAccumulator> _turns = new(StringComparer.Ordinal);
        private PendingTurnAccumulator? _fallbackTurn;
        private int _fallbackOrdinal;

        public static IReadOnlyList<PendingTurn> BuildTurns(IReadOnlyList<AgentEvent> events)
        {
            ArgumentNullException.ThrowIfNull(events);
            var builder = new PendingTurnBuilder();
            foreach (var @event in events.OrderBy(static item => item.Timestamp))
            {
                builder.Add(@event);
            }

            return builder._turns.Values
                .Select(static item => item.Build())
                .OrderBy(static item => item.Timestamp)
                .ToArray();
        }

        private void Add(AgentEvent @event)
        {
            var turn = GetTurn(@event);
            turn.Add(@event);
        }

        private PendingTurnAccumulator GetTurn(AgentEvent @event)
        {
            if (@event.RunId is { } runId)
            {
                var key = "run-" + runId.Value;
                return GetOrCreate(key, @event.SessionId, runId.Value);
            }

            if (_fallbackTurn is null || StartsFallbackTurn(@event, _fallbackTurn))
            {
                var key = FormattableString.Invariant($"session-{@event.SessionId}-turn-{++_fallbackOrdinal}");
                _fallbackTurn = GetOrCreate(key, @event.SessionId, null);
            }

            return _fallbackTurn;
        }

        private PendingTurnAccumulator GetOrCreate(string key, string sessionId, string? runId)
        {
            if (_turns.TryGetValue(key, out var turn))
            {
                return turn;
            }

            turn = new PendingTurnAccumulator(key, sessionId, runId);
            _turns.Add(key, turn);
            return turn;
        }

        private static bool StartsFallbackTurn(AgentEvent @event, PendingTurnAccumulator current)
            => @event is AgentActivityEvent { Kind: AgentActivityKind.Turn, Phase: AgentActivityPhase.Started } && current.HasEvents;
    }

    private sealed class PendingTurnAccumulator(string key, string sessionId, string? runId)
    {
        private readonly List<AgentEvent> _events = [];
        private HashCode _fingerprint = new();
        private DateTimeOffset? _startedAt;
        private DateTimeOffset? _endedAt;
        private DateTimeOffset? _firstEventAt;
        private DateTimeOffset? _lastEventAt;

        public bool HasEvents => _events.Count > 0;

        public void Add(AgentEvent @event)
        {
            _events.Add(@event);
            _firstEventAt = Min(_firstEventAt, @event.Timestamp);
            _lastEventAt = Max(_lastEventAt, @event.Timestamp);
            AddFingerprint(@event);

            switch (@event)
            {
                case AgentActivityEvent { Kind: AgentActivityKind.Turn, Phase: AgentActivityPhase.Started } activity:
                    _startedAt = Min(_startedAt, activity.Timestamp);
                    break;
                case AgentActivityEvent { Kind: AgentActivityKind.Turn } activity when IsTerminalPhase(activity.Phase):
                    _endedAt = Max(_endedAt, activity.Timestamp);
                    break;
                case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown or AgentSessionUpdateKind.TaskCompleted } update:
                    _endedAt = Max(_endedAt, update.Timestamp);
                    break;
                case AgentErrorEvent error:
                    _endedAt = Max(_endedAt, error.Timestamp);
                    break;
            }
        }

        public PendingTurn Build()
            => new(
                key,
                sessionId,
                runId,
                _endedAt is not null,
                _endedAt ?? _lastEventAt ?? _startedAt ?? _firstEventAt ?? DateTimeOffset.UtcNow,
                _fingerprint.ToHashCode().ToString("x8", CultureInfo.InvariantCulture),
                _events.ToArray());

        private void AddFingerprint(AgentEvent @event)
        {
            _fingerprint.Add(@event.GetType().FullName, StringComparer.Ordinal);
            _fingerprint.Add(@event.Timestamp.UtcTicks);
            _fingerprint.Add(@event.RunId?.Value, StringComparer.Ordinal);
            switch (@event)
            {
                case AgentContentDeltaEvent delta:
                    _fingerprint.Add(delta.Kind);
                    _fingerprint.Add(delta.ContentId, StringComparer.Ordinal);
                    _fingerprint.Add(delta.ParentActivityId, StringComparer.Ordinal);
                    _fingerprint.Add(delta.Delta, StringComparer.Ordinal);
                    break;
                case AgentContentCompletedEvent completed:
                    _fingerprint.Add(completed.Kind);
                    _fingerprint.Add(completed.ContentId, StringComparer.Ordinal);
                    _fingerprint.Add(completed.ParentActivityId, StringComparer.Ordinal);
                    _fingerprint.Add(completed.Content, StringComparer.Ordinal);
                    break;
                case AgentActivityEvent activity:
                    _fingerprint.Add(activity.Kind);
                    _fingerprint.Add(activity.Phase);
                    _fingerprint.Add(activity.ActivityId, StringComparer.Ordinal);
                    _fingerprint.Add(activity.ParentActivityId, StringComparer.Ordinal);
                    _fingerprint.Add(activity.Name, StringComparer.Ordinal);
                    _fingerprint.Add(activity.Message, StringComparer.Ordinal);
                    _fingerprint.Add(activity.Details?.GetRawText(), StringComparer.Ordinal);
                    break;
                case AgentSessionUpdateEvent update:
                    _fingerprint.Add(update.Kind);
                    _fingerprint.Add(update.Message, StringComparer.Ordinal);
                    _fingerprint.Add(update.Details?.GetRawText(), StringComparer.Ordinal);
                    break;
            }
        }

        private static DateTimeOffset? Min(DateTimeOffset? left, DateTimeOffset right)
            => left is null || right < left.Value ? right : left;

        private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset right)
            => left is null || right > left.Value ? right : left;
    }

    private sealed record PendingTurn(
        string Key,
        string SessionId,
        string? RunId,
        bool IsComplete,
        DateTimeOffset Timestamp,
        string Fingerprint,
        IReadOnlyList<AgentEvent> Events);

    private sealed class AsyncTurnStatisticsProjection : PluginDynamicDerivedThreadEventContent
    {
        private readonly object _gate = new();
        private readonly PendingTurn _turn;
        private string _markdown;
        private IReadOnlyList<PluginDerivedThreadEventDetailSection> _detailSections = [];
        private PluginThreadEventVisualFactory? _visualFactory;
        private object? _payload;
        private bool _started;

        public AsyncTurnStatisticsProjection(PendingTurn turn)
        {
            _turn = turn;
            _markdown = "**Turn statistics** · computing...";
            _payload = new
            {
                turn.Key,
                turn.SessionId,
                turn.RunId,
                Status = "computing",
            };
            _detailSections =
            [
                new PluginDerivedThreadEventDetailSection
                {
                    Header = "Detailed statistics",
                    Markdown = "Computing detailed statistics...",
                },
            ];
            _visualFactory = _ => StatisticsVisualRenderer.RenderComputingTurnCard();
        }

        public override string Markdown
        {
            get
            {
                lock (_gate)
                {
                    return _markdown;
                }
            }
        }

        public override IReadOnlyList<PluginDerivedThreadEventDetailSection> DetailSections
        {
            get
            {
                lock (_gate)
                {
                    return _detailSections;
                }
            }
        }

        public override PluginThreadEventVisualFactory? VisualFactory
        {
            get
            {
                lock (_gate)
                {
                    return _visualFactory;
                }
            }
        }

        public object? Payload
        {
            get
            {
                lock (_gate)
                {
                    return _payload;
                }
            }
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
            }

            _ = Task.Run(Compute);
        }

        private void Compute()
        {
            try
            {
                var turn = TurnStatisticsBuilder.BuildTurn(_turn.Key, _turn.SessionId, _turn.RunId, _turn.Events);
                UpdateCompleted(turn);
            }
            catch (Exception ex)
            {
                UpdateFailed(ex);
            }
        }

        private void UpdateCompleted(TurnStatistics turn)
        {
            lock (_gate)
            {
                _markdown = StatisticsMarkdownRenderer.RenderTurnSummary(turn);
                _detailSections =
                [
                    new PluginDerivedThreadEventDetailSection
                    {
                        Header = "Detailed statistics",
                        Markdown = StatisticsMarkdownRenderer.RenderTurnDetails(turn),
                        VisualFactory = _ => StatisticsVisualRenderer.RenderTurnDetails(turn),
                    },
                ];
                _visualFactory = _ => StatisticsVisualRenderer.RenderTurnCard(turn);
                _payload = new
                {
                    turn.Key,
                    turn.SessionId,
                    turn.RunId,
                    turn.Duration,
                    ToolCalls = turn.Tools.Count,
                    turn.ReportedInputTokens,
                    turn.ReportedOutputTokens,
                    turn.EstimatedInputTokens,
                    turn.EstimatedOutputTokens,
                    ToolInputCharacters = turn.ToolInput.Characters,
                    GeneratedOutputCharacters = turn.GeneratedOutput.Characters,
                    turn.CompactionCount,
                    turn.CompactionDuration,
                    Status = "ready",
                };
            }

            NotifyChanged();
        }

        private void UpdateFailed(Exception ex)
        {
            lock (_gate)
            {
                _markdown = $"**Turn statistics** · failed: {ex.Message}";
                _detailSections = [];
                _visualFactory = null;
                _payload = new
                {
                    _turn.Key,
                    _turn.SessionId,
                    _turn.RunId,
                    Status = "failed",
                    Error = ex.Message,
                };
            }

            NotifyChanged();
        }
    }

    private sealed class TurnBuilder(string key, string sessionId, string? runId)
    {
        private readonly Dictionary<string, ContentBuilder> _content = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolCallBuilder> _tools = new(StringComparer.Ordinal);
        private readonly List<DateTimeOffset> _modelOutputTimestamps = [];
        private readonly List<DateTimeOffset> _assistantDeltaTimestamps = [];
        private readonly List<DateTimeOffset> _reasoningDeltaTimestamps = [];
        private readonly List<DateTimeOffset> _generatedOutputDeltaTimestamps = [];
        private readonly CompactionStatisticsBuilder _compactions = new();
        private readonly StringBuilder _planSnapshotText = new();
        private AgentOperationUsageSnapshot? _lastOperationUsage;
        private AgentSystemPromptStatistics? _systemPromptStatistics;

        public bool HasEvents { get; private set; }

        public void Add(AgentEvent @event)
        {
            HasEvents = true;
            FirstEventAt = Min(FirstEventAt, @event.Timestamp);
            LastEventAt = Max(LastEventAt, @event.Timestamp);
            BackendId = @event.BackendId.Value;

            switch (@event)
            {
                case AgentContentDeltaEvent delta:
                    AddDelta(delta);
                    break;
                case AgentContentCompletedEvent completed:
                    AddCompleted(completed);
                    break;
                case AgentActivityEvent activity:
                    AddActivity(activity);
                    break;
                case AgentSystemPromptEvent systemPrompt:
                    _systemPromptStatistics = systemPrompt.Statistics;
                    MessageCounts.SystemPrompt++;
                    break;
                case AgentSessionUpdateEvent update:
                    if (update.Usage?.LastOperation is { } usage)
                    {
                        _lastOperationUsage = usage;
                    }

                    _compactions.Add(update);

                    if (update.Kind is AgentSessionUpdateKind.Idle or AgentSessionUpdateKind.Shutdown or AgentSessionUpdateKind.TaskCompleted)
                    {
                        IsComplete = true;
                        EndedAt = update.Timestamp;
                    }

                    MessageCounts.SessionUpdate++;
                    break;
                case AgentPlanSnapshotEvent plan:
                    MessageCounts.Plan++;
                    AppendPlanSnapshot(plan.Snapshot);
                    _modelOutputTimestamps.Add(plan.Timestamp);
                    break;
                case AgentErrorEvent:
                    MessageCounts.Error++;
                    IsComplete = true;
                    EndedAt = @event.Timestamp;
                    break;
            }
        }

        public string Key { get; } = key;

        public string SessionId { get; } = sessionId;

        public string? RunId { get; } = runId;

        public string? BackendId { get; private set; }

        public DateTimeOffset? FirstEventAt { get; private set; }

        public DateTimeOffset? LastEventAt { get; private set; }

        public DateTimeOffset? StartedAt { get; private set; }

        public DateTimeOffset? EndedAt { get; private set; }

        public DateTimeOffset? FirstAssistantAt { get; private set; }

        public DateTimeOffset? FirstReasoningAt { get; private set; }

        public bool IsComplete { get; private set; }

        public MessageCounts MessageCounts { get; } = new();

        public TurnStatistics Build()
        {
            var contents = _content.Values.Select(static item => item.Build()).ToArray();
            var tools = _tools.Values
                .Select(item => item.Build(ContentStats.For(contents.Where(content => IsOwnedToolOutput(content, item.ActivityId)))))
                .OrderBy(static item => item.StartedAt ?? item.EndedAt ?? DateTimeOffset.MinValue)
                .ToArray();
            var assistant = ContentStats.For(contents, AgentContentKind.Assistant);
            var reasoning = ContentStats.For(contents, AgentContentKind.Reasoning);
            var reasoningSummary = ContentStats.For(contents, AgentContentKind.ReasoningSummary);
            var plan = ContentStats.Sum(ContentStats.For(contents, AgentContentKind.Plan, AgentContentKind.Notice), ContentStats.ForText(_planSnapshotText.ToString()));
            var prompt = ContentStats.For(contents, AgentContentKind.User);
            var toolInput = ContentStats.Sum(tools.Select(static item => item.Input));
            var generatedReasoningSummary = reasoning.Characters > 0 || reasoning.Bytes > 0 || reasoning.EstimatedTokens > 0
                ? ContentStats.Empty
                : reasoningSummary;
            var unownedToolOutput = ContentStats.For(contents.Where(content =>
                IsToolOutputKind(content.Kind) &&
                !_tools.ContainsKey(content.ContentId) &&
                (string.IsNullOrWhiteSpace(content.ParentActivityId) || !_tools.ContainsKey(content.ParentActivityId))));
            var toolOutput = ContentStats.Sum(tools.Select(static item => item.Output), unownedToolOutput);
            var generatedOutput = ContentStats.Sum(assistant, reasoning, generatedReasoningSummary, plan, toolInput);
            var outputChars = generatedOutput.Characters;
            var estimatedInputTokens = EstimateTokensFromCharacters(prompt.Characters + (_systemPromptStatistics?.SystemChars ?? 0) + (_systemPromptStatistics?.DeveloperChars ?? 0));
            var estimatedOutputTokens = EstimateTokensFromCharacters(outputChars);
            var compactions = _compactions.Build();
            var toolIntervals = tools
                .Where(static item => item.StartedAt is not null && item.EndedAt is not null && item.EndedAt >= item.StartedAt)
                .Select(static item => new TimeInterval(item.StartedAt!.Value, item.EndedAt!.Value))
                .ToArray();
            var outputIntervals = BuildOutputIntervals(_modelOutputTimestamps);
            var duration = ResolveDuration();
            var toolSpan = MergeIntervals(toolIntervals);
            var modelOutputSpan = MergeIntervals(outputIntervals);
            var thinkingTime = duration - toolSpan - modelOutputSpan;
            if (thinkingTime < TimeSpan.Zero)
            {
                thinkingTime = TimeSpan.Zero;
            }

            return new TurnStatistics(
                Key,
                SessionId,
                RunId,
                BackendId,
                FirstEventAt,
                LastEventAt,
                StartedAt,
                EndedAt,
                IsComplete,
                duration,
                FirstAssistantAt - StartedAt,
                FirstReasoningAt - StartedAt,
                thinkingTime,
                LongestGap(BuildInterestingTimestamps()),
                toolSpan,
                tools.Sum(static item => item.Duration?.Ticks ?? 0) is var ticks ? TimeSpan.FromTicks(ticks) : TimeSpan.Zero,
                ResolveStreamDuration(_assistantDeltaTimestamps),
                ResolveStreamDuration(_reasoningDeltaTimestamps),
                ResolveStreamDuration(_generatedOutputDeltaTimestamps),
                prompt,
                assistant,
                reasoning,
                reasoningSummary,
                plan,
                toolInput,
                toolOutput,
                generatedOutput,
                compactions.Count,
                compactions.Duration,
                MessageCounts.Clone(),
                tools,
                BuildBuckets(tools),
                _lastOperationUsage?.InputTokens,
                _lastOperationUsage?.OutputTokens,
                _lastOperationUsage?.CachedInputTokens,
                _lastOperationUsage?.CacheReadTokens,
                _lastOperationUsage?.CacheWriteTokens,
                _lastOperationUsage?.ReasoningTokens,
                _lastOperationUsage?.Model,
                _lastOperationUsage?.DurationMs,
                _systemPromptStatistics?.SystemApproxTokens,
                _systemPromptStatistics?.DeveloperApproxTokens,
                estimatedInputTokens,
                estimatedOutputTokens);
        }

        private void AddDelta(AgentContentDeltaEvent delta)
        {
            var content = GetContent(delta.Kind, delta.ContentId, delta.ParentActivityId);
            content.AddDelta(delta.Delta);
            AddContentMessageCount(delta.Kind);
            if (IsModelGeneratedContent(delta.Kind))
            {
                _modelOutputTimestamps.Add(delta.Timestamp);
                _generatedOutputDeltaTimestamps.Add(delta.Timestamp);
                if (delta.Kind == AgentContentKind.Assistant)
                {
                    FirstAssistantAt = Min(FirstAssistantAt, delta.Timestamp);
                    _assistantDeltaTimestamps.Add(delta.Timestamp);
                }
                else if (delta.Kind == AgentContentKind.Reasoning)
                {
                    FirstReasoningAt = Min(FirstReasoningAt, delta.Timestamp);
                    _reasoningDeltaTimestamps.Add(delta.Timestamp);
                }
            }
        }

        private void AddCompleted(AgentContentCompletedEvent completed)
        {
            var content = GetContent(completed.Kind, completed.ContentId, completed.ParentActivityId);
            content.SetCompleted(completed.Content);
            AddContentMessageCount(completed.Kind);
            if (IsModelGeneratedContent(completed.Kind))
            {
                _modelOutputTimestamps.Add(completed.Timestamp);
                if (completed.Kind == AgentContentKind.Assistant)
                {
                    FirstAssistantAt = Min(FirstAssistantAt, completed.Timestamp);
                }
                else if (completed.Kind == AgentContentKind.Reasoning)
                {
                    FirstReasoningAt = Min(FirstReasoningAt, completed.Timestamp);
                }
            }
        }

        private static bool IsModelGeneratedContent(AgentContentKind kind)
            => kind is AgentContentKind.Assistant or AgentContentKind.Reasoning or AgentContentKind.ReasoningSummary or AgentContentKind.Plan or AgentContentKind.Notice;

        private static bool IsOwnedToolOutput(ContentStatistics content, string activityId)
            => IsToolOutputKind(content.Kind) &&
               (string.Equals(content.ParentActivityId, activityId, StringComparison.Ordinal) ||
                string.Equals(content.ContentId, activityId, StringComparison.Ordinal));

        private void AddActivity(AgentActivityEvent activity)
        {
            if (activity.Kind == AgentActivityKind.Turn)
            {
                if (activity.Phase == AgentActivityPhase.Started)
                {
                    StartedAt = Min(StartedAt, activity.Timestamp);
                }
                else if (IsTerminalPhase(activity.Phase))
                {
                    EndedAt = Max(EndedAt, activity.Timestamp);
                    IsComplete = true;
                }

                return;
            }

            if (activity.Kind == AgentActivityKind.Compaction)
            {
                _compactions.Add(activity);
                return;
            }

            if (IsToolActivity(activity.Kind))
            {
                if (GetTool(activity).Add(activity))
                {
                    _modelOutputTimestamps.Add(activity.Timestamp);
                }
            }
        }

        private void AppendPlanSnapshot(AgentPlanSnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Explanation))
            {
                AppendPlanText(snapshot.Explanation);
            }

            if (snapshot.Steps is not null)
            {
                foreach (var step in snapshot.Steps)
                {
                    AppendPlanText(step.Text);
                }
            }
        }

        private void AppendPlanText(string text)
        {
            if (_planSnapshotText.Length > 0)
            {
                _planSnapshotText.AppendLine();
            }

            _planSnapshotText.Append(text);
        }

        private ContentBuilder GetContent(AgentContentKind kind, string contentId, string? parentActivityId)
        {
            var key = FormattableString.Invariant($"{kind}:{contentId}");
            if (!_content.TryGetValue(key, out var content))
            {
                content = new ContentBuilder(kind, contentId, parentActivityId);
                _content.Add(key, content);
            }

            return content;
        }

        private ToolCallBuilder GetTool(AgentActivityEvent activity)
        {
            if (!_tools.TryGetValue(activity.ActivityId, out var tool))
            {
                tool = new ToolCallBuilder(activity.ActivityId, activity.Kind, activity.Name);
                _tools.Add(activity.ActivityId, tool);
            }

            return tool;
        }

        private void AddContentMessageCount(AgentContentKind kind)
        {
            switch (kind)
            {
                case AgentContentKind.User:
                    MessageCounts.User++;
                    break;
                case AgentContentKind.Assistant:
                    MessageCounts.Assistant++;
                    break;
                case AgentContentKind.Reasoning:
                    MessageCounts.Reasoning++;
                    break;
                case AgentContentKind.ReasoningSummary:
                    MessageCounts.ReasoningSummary++;
                    break;
                case AgentContentKind.Plan:
                    MessageCounts.Plan++;
                    break;
                case AgentContentKind.Notice:
                    MessageCounts.Notice++;
                    break;
                default:
                    if (IsToolOutputKind(kind))
                    {
                        MessageCounts.ToolOutput++;
                    }

                    break;
            }
        }

        private TimeSpan ResolveDuration()
        {
            var start = StartedAt ?? FirstEventAt;
            var end = EndedAt ?? LastEventAt;
            return start is not null && end is not null && end >= start ? end.Value - start.Value : TimeSpan.Zero;
        }

        private IReadOnlyList<DateTimeOffset> BuildInterestingTimestamps()
        {
            var values = new List<DateTimeOffset>();
            if (StartedAt is { } started)
            {
                values.Add(started);
            }

            values.AddRange(_modelOutputTimestamps);
            values.AddRange(_tools.Values.SelectMany(static tool => tool.Timestamps));
            if (EndedAt is { } ended)
            {
                values.Add(ended);
            }

            return values.Order().ToArray();
        }

        private static IReadOnlyList<TimeInterval> BuildOutputIntervals(IReadOnlyList<DateTimeOffset> timestamps)
        {
            if (timestamps.Count == 0)
            {
                return [];
            }

            return timestamps
                .Order()
                .Select(static timestamp => new TimeInterval(timestamp, timestamp.AddMilliseconds(250)))
                .ToArray();
        }

        private static TimeSpan MergeIntervals(IReadOnlyList<TimeInterval> intervals)
        {
            if (intervals.Count == 0)
            {
                return TimeSpan.Zero;
            }

            var ordered = intervals.OrderBy(static item => item.Start).ToArray();
            var total = TimeSpan.Zero;
            var start = ordered[0].Start;
            var end = ordered[0].End;
            foreach (var interval in ordered.Skip(1))
            {
                if (interval.Start <= end)
                {
                    if (interval.End > end)
                    {
                        end = interval.End;
                    }
                }
                else
                {
                    total += end - start;
                    start = interval.Start;
                    end = interval.End;
                }
            }

            return total + (end - start);
        }

        private static TimeSpan LongestGap(IReadOnlyList<DateTimeOffset> timestamps)
        {
            var longest = TimeSpan.Zero;
            for (var index = 1; index < timestamps.Count; index++)
            {
                var gap = timestamps[index] - timestamps[index - 1];
                if (gap > longest)
                {
                    longest = gap;
                }
            }

            return longest;
        }

        private static TimeSpan? ResolveStreamDuration(IReadOnlyList<DateTimeOffset> timestamps)
        {
            if (timestamps.Count < 2)
            {
                return null;
            }

            var ordered = timestamps.Order().ToArray();
            var duration = ordered[^1] - ordered[0];
            return duration > TimeSpan.Zero ? duration : null;
        }

        private static IReadOnlyList<ToolBucketStatistics> BuildBuckets(IReadOnlyList<ToolCallStatistics> tools)
            => tools
                .GroupBy(static tool => tool.Bucket, StringComparer.OrdinalIgnoreCase)
                .Select(static group => new ToolBucketStatistics(
                    group.Key,
                    group.Count(),
                    group.Count(static item => item.Failed),
                    group.Count(static item => item.Canceled),
                    TimeSpan.FromTicks(group.Sum(static item => item.Duration?.Ticks ?? 0)),
                    group.Sum(static item => item.Input.Characters),
                    group.Sum(static item => item.Output.Characters)))
                .OrderByDescending(static item => item.CallCount)
                .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static DateTimeOffset? Min(DateTimeOffset? left, DateTimeOffset right)
            => left is null || right < left.Value ? right : left;

        private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset right)
            => left is null || right > left.Value ? right : left;
    }

    private sealed class ContentBuilder(AgentContentKind kind, string contentId, string? parentActivityId)
    {
        private readonly StringBuilder _deltas = new();
        private string? _completed;

        public void AddDelta(string delta)
            => _deltas.Append(delta);

        public void SetCompleted(string completed)
            => _completed = completed;

        public ContentStatistics Build()
        {
            var text = _completed ?? _deltas.ToString();
            return new ContentStatistics(kind, contentId, parentActivityId, text.Length, ByteCount(text), EstimateTokensFromCharacters(text.Length));
        }
    }

    private sealed class ToolCallBuilder(string activityId, AgentActivityKind kind, string? name)
    {
        private DateTimeOffset? _startedAt;
        private DateTimeOffset? _endedAt;
        private AgentActivityPhase _lastPhase;
        private readonly List<string> _inputParts = [];
        private readonly List<string> _outputParts = [];
        private readonly HashSet<string> _seenInputParts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seenOutputParts = new(StringComparer.Ordinal);

        public string ActivityId { get; } = activityId;

        public IReadOnlyList<DateTimeOffset> Timestamps => _timestamps;

        private readonly List<DateTimeOffset> _timestamps = [];

        public bool Add(AgentActivityEvent activity)
        {
            _timestamps.Add(activity.Timestamp);
            _lastPhase = activity.Phase;
            if (activity.Phase is AgentActivityPhase.Requested or AgentActivityPhase.Started)
            {
                _startedAt = _startedAt is null || activity.Timestamp < _startedAt.Value ? activity.Timestamp : _startedAt;
            }

            if (IsTerminalPhase(activity.Phase))
            {
                _endedAt = _endedAt is null || activity.Timestamp > _endedAt.Value ? activity.Timestamp : _endedAt;
                _startedAt ??= activity.Timestamp;
            }

            var input = ResolveToolInput(activity);
            if (!string.IsNullOrWhiteSpace(input) && _seenInputParts.Add(input))
            {
                _inputParts.Add(input);
            }

            var output = ResolveToolOutput(activity);
            if (!string.IsNullOrWhiteSpace(output) && _seenOutputParts.Add(output))
            {
                _outputParts.Add(output);
            }

            return !string.IsNullOrWhiteSpace(input);
        }

        public ToolCallStatistics Build(ContentStats contentOutput)
        {
            var input = ContentStats.ForText(string.Join(Environment.NewLine + Environment.NewLine, _inputParts));
            var activityOutput = ContentStats.ForText(string.Join(Environment.NewLine + Environment.NewLine, _outputParts));
            var output = contentOutput.Characters > 0 || contentOutput.Bytes > 0 ? contentOutput : activityOutput;
            return new ToolCallStatistics(
                ActivityId,
                kind,
                string.IsNullOrWhiteSpace(name) ? kind.ToString() : name.Trim(),
                ToolBucketName(kind, name),
                _startedAt,
                _endedAt,
                _startedAt is not null && _endedAt is not null && _endedAt >= _startedAt ? _endedAt - _startedAt : null,
                Failed: _lastPhase == AgentActivityPhase.Failed,
                Canceled: _lastPhase == AgentActivityPhase.Canceled,
                Input: input,
                Output: output);
        }

        private static string? ResolveToolInput(AgentActivityEvent activity)
        {
            var parts = new List<string>();
            if (activity.Details is { ValueKind: JsonValueKind.Object } details)
            {
                AppendString(parts, details, "command");
                AppendString(parts, details, "toolName");
                AppendString(parts, details, "mcpToolName");
                AppendString(parts, details, "tool");
                AppendString(parts, details, "name");
                AppendRaw(parts, details, "arguments");
                AppendRaw(parts, details, "input");
                AppendString(parts, details, "cwd", static value => $"cwd: {value}");
                AppendString(parts, details, "query");
                AppendString(parts, details, "prompt");
                AppendString(parts, details, "path");
                AppendString(parts, details, "server", static value => $"server: {value}");
                AppendString(parts, details, "mcpServerName", static value => $"server: {value}");
                AppendString(parts, details, "agentDescription");
            }

            if (parts.Count == 0)
            {
                if (activity.Kind is AgentActivityKind.WebSearch or AgentActivityKind.ImageGeneration && !string.IsNullOrWhiteSpace(activity.Message))
                {
                    parts.Add(activity.Message);
                }
                else if (activity.Kind == AgentActivityKind.CommandExecution && !string.IsNullOrWhiteSpace(activity.Name))
                {
                    parts.Add(activity.Name);
                }
            }

            return parts.Count == 0
                ? null
                : string.Join(Environment.NewLine + Environment.NewLine, parts.Where(static item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal));
        }

        private static string? ResolveToolOutput(AgentActivityEvent activity)
        {
            if (activity.Details is { ValueKind: JsonValueKind.Object } details)
            {
                if (TryGetString(details, "aggregatedOutput", out var aggregatedOutput))
                {
                    return aggregatedOutput;
                }

                if (TryResolveNestedString(details, out var nestedOutput, "result", "content") ||
                    TryResolveNestedString(details, out nestedOutput, "error", "message") ||
                    TryResolveNestedString(details, out nestedOutput, "output", "body") ||
                    TryResolveNestedString(details, out nestedOutput, "result", "detailedContent"))
                {
                    return nestedOutput;
                }
            }

            return activity.Kind == AgentActivityKind.CommandExecution || IsTerminalPhase(activity.Phase)
                ? activity.Message
                : null;
        }

        private static void AppendRaw(List<string> parts, JsonElement details, string propertyName)
        {
            if (details.TryGetProperty(propertyName, out var value))
            {
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(text);
                }
            }
        }

        private static void AppendString(List<string> parts, JsonElement details, string propertyName, Func<string, string>? formatter = null)
        {
            if (TryGetString(details, propertyName, out var value))
            {
                parts.Add(formatter?.Invoke(value) ?? value);
            }
        }

        private static bool TryGetString(JsonElement details, string propertyName, out string value)
        {
            value = string.Empty;
            if (!details.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            value = property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.GetRawText();
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool TryResolveNestedString(JsonElement root, out string? value, params string[] path)
        {
            value = null;
            var current = root;
            foreach (var segment in path)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return false;
                }
            }

            value = current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
            return !string.IsNullOrWhiteSpace(value);
        }
    }

    private sealed class CompactionStatisticsBuilder
    {
        private readonly Dictionary<string, DateTimeOffset> _activityStarts = new(StringComparer.Ordinal);
        private int _activityCount;
        private TimeSpan _activityDuration;
        private DateTimeOffset? _sessionStartedAt;
        private int _sessionCount;
        private TimeSpan _sessionDuration;

        public void Add(AgentActivityEvent activity)
        {
            if (activity.Kind != AgentActivityKind.Compaction)
            {
                return;
            }

            if (activity.Phase is AgentActivityPhase.Requested or AgentActivityPhase.Started)
            {
                _activityStarts[activity.ActivityId] = activity.Timestamp;
                return;
            }

            if (!IsTerminalPhase(activity.Phase))
            {
                return;
            }

            _activityCount++;
            if (_activityStarts.Remove(activity.ActivityId, out var startedAt) && activity.Timestamp >= startedAt)
            {
                _activityDuration += activity.Timestamp - startedAt;
            }
        }

        public void Add(AgentSessionUpdateEvent update)
        {
            if (update.Kind == AgentSessionUpdateKind.CompactionStarted)
            {
                _sessionStartedAt = update.Timestamp;
            }
            else if (update.Kind == AgentSessionUpdateKind.CompactionCompleted)
            {
                _sessionCount++;
                if (_sessionStartedAt is { } startedAt && update.Timestamp >= startedAt)
                {
                    _sessionDuration += update.Timestamp - startedAt;
                }

                _sessionStartedAt = null;
            }
        }

        public CompactionStatistics Build()
            => _sessionCount > 0
                ? new CompactionStatistics(_sessionCount, _sessionDuration)
                : new CompactionStatistics(_activityCount, _activityDuration);
    }

    private sealed record CompactionStatistics(int Count, TimeSpan Duration);

    private sealed record TimeInterval(DateTimeOffset Start, DateTimeOffset End);

    private sealed record ContentStatistics(AgentContentKind Kind, string ContentId, string? ParentActivityId, long Characters, long Bytes, long EstimatedTokens);

    private sealed record ContentStats(long Characters, long Bytes, long EstimatedTokens)
    {
        public static ContentStats Empty { get; } = new(0, 0, 0);

        public static ContentStats For(IReadOnlyList<ContentStatistics> contents, params AgentContentKind[] kinds)
            => For((IEnumerable<ContentStatistics>)contents, kinds);

        public static ContentStats For(IEnumerable<ContentStatistics> contents, params AgentContentKind[] kinds)
        {
            var kindSet = kinds.ToHashSet();
            var matching = contents.Where(item => kindSet.Contains(item.Kind)).ToArray();
            return new ContentStats(
                matching.Sum(static item => item.Characters),
                matching.Sum(static item => item.Bytes),
                matching.Sum(static item => item.EstimatedTokens));
        }

        public static ContentStats For(IEnumerable<ContentStatistics> contents)
        {
            var values = contents.ToArray();
            return new ContentStats(
                values.Sum(static item => item.Characters),
                values.Sum(static item => item.Bytes),
                values.Sum(static item => item.EstimatedTokens));
        }

        public static ContentStats ForText(string? text)
        {
            var characters = text?.Length ?? 0;
            return new ContentStats(characters, ByteCount(text), EstimateTokensFromCharacters(characters));
        }

        public static ContentStats Sum(params ContentStats[] stats)
            => Sum((IEnumerable<ContentStats>)stats);

        public static ContentStats Sum(IEnumerable<ContentStats> stats, params ContentStats[] additional)
            => Sum(stats.Concat(additional));

        public static ContentStats Sum(IEnumerable<ContentStats> stats)
        {
            var values = stats.ToArray();
            return new ContentStats(
                values.Sum(static item => item.Characters),
                values.Sum(static item => item.Bytes),
                values.Sum(static item => item.EstimatedTokens));
        }
    }

    private sealed class MessageCounts
    {
        public int User { get; set; }

        public int Assistant { get; set; }

        public int Reasoning { get; set; }

        public int ReasoningSummary { get; set; }

        public int Plan { get; set; }

        public int Notice { get; set; }

        public int ToolOutput { get; set; }

        public int Error { get; set; }

        public int SystemPrompt { get; set; }

        public int SessionUpdate { get; set; }

        public MessageCounts Clone()
            => (MessageCounts)MemberwiseClone();
    }

    private sealed record ToolCallStatistics(
        string ActivityId,
        AgentActivityKind Kind,
        string Name,
        string Bucket,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        TimeSpan? Duration,
        bool Failed,
        bool Canceled,
        ContentStats Input,
        ContentStats Output);

    private sealed record ToolBucketStatistics(
        string Name,
        int CallCount,
        int FailedCount,
        int CanceledCount,
        TimeSpan TotalDuration,
        long InputCharacters,
        long OutputCharacters);

    private sealed record TurnStatistics(
        string Key,
        string SessionId,
        string? RunId,
        string? BackendId,
        DateTimeOffset? FirstEventAt,
        DateTimeOffset? LastEventAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        bool IsComplete,
        TimeSpan Duration,
        TimeSpan? FirstAssistantLatency,
        TimeSpan? FirstReasoningLatency,
        TimeSpan ThinkingTime,
        TimeSpan LongestGap,
        TimeSpan ToolSpan,
        TimeSpan TotalToolTime,
        TimeSpan? AssistantOutputDuration,
        TimeSpan? ReasoningOutputDuration,
        TimeSpan? GeneratedOutputDuration,
        ContentStats Prompt,
        ContentStats Assistant,
        ContentStats Reasoning,
        ContentStats ReasoningSummary,
        ContentStats Plan,
        ContentStats ToolInput,
        ContentStats ToolOutput,
        ContentStats GeneratedOutput,
        int CompactionCount,
        TimeSpan CompactionDuration,
        MessageCounts MessageCounts,
        IReadOnlyList<ToolCallStatistics> Tools,
        IReadOnlyList<ToolBucketStatistics> Buckets,
        long? ReportedInputTokens,
        long? ReportedOutputTokens,
        long? ReportedCachedInputTokens,
        long? ReportedCacheReadTokens,
        long? ReportedCacheWriteTokens,
        long? ReportedReasoningTokens,
        string? ReportedModel,
        double? ReportedDurationMs,
        int? SystemPromptTokens,
        int? DeveloperPromptTokens,
        long EstimatedInputTokens,
        long EstimatedOutputTokens)
    {
        public long DisplayInputTokens => ReportedInputTokens ?? EstimatedInputTokens;

        public long DisplayOutputTokens => EstimatedOutputTokens;

        public bool HasReportedUsage => ReportedInputTokens is not null || ReportedOutputTokens is not null || ReportedReasoningTokens is not null;

        public double? AssistantTokensPerSecond => Rate(Assistant.EstimatedTokens, AssistantOutputDuration);

        public double? ReasoningTokensPerSecond => Rate(Reasoning.EstimatedTokens, ReasoningOutputDuration);

        public double? GeneratedOutputTokensPerSecond => Rate(
            Math.Max(0, GeneratedOutput.EstimatedTokens - ToolInput.EstimatedTokens),
            GeneratedOutputDuration);

        private static double? Rate(long tokens, TimeSpan? duration)
            => tokens <= 0 || duration is null || duration.Value.TotalSeconds <= 0 ? null : tokens / duration.Value.TotalSeconds;
    }

    private sealed record SessionStatistics(
        int TurnCount,
        TimeSpan TotalDuration,
        TimeSpan TotalThinkingTime,
        TimeSpan TotalToolTime,
        long TotalInputTokens,
        long TotalOutputTokens,
        long TotalAssistantCharacters,
        long TotalReasoningCharacters,
        long TotalToolOutputCharacters)
    {
        public static SessionStatistics FromTurns(IReadOnlyList<TurnStatistics> turns)
            => new(
                turns.Count(static item => item.IsComplete),
                TimeSpan.FromTicks(turns.Sum(static item => item.Duration.Ticks)),
                TimeSpan.FromTicks(turns.Sum(static item => item.ThinkingTime.Ticks)),
                TimeSpan.FromTicks(turns.Sum(static item => item.TotalToolTime.Ticks)),
                turns.Sum(static item => item.DisplayInputTokens),
                turns.Sum(static item => item.DisplayOutputTokens),
                turns.Sum(static item => item.Assistant.Characters),
                turns.Sum(static item => item.Reasoning.Characters),
                turns.Sum(static item => item.ToolOutput.Characters));
    }

    private static class StatisticsVisualRenderer
    {
        public static Visual RenderComputingTurnCard()
            => new Collapsible(
                CreateHeaderMarkup("[bold]Turn statistics[/] · computing..."),
                new Markup("[dim]Computing detailed statistics...[/]") { Wrap = false })
            {
                IsExpanded = false,
            };

        public static Visual RenderTurnCard(TurnStatistics turn)
            => new Collapsible(
                CreateHeaderMarkup(StatisticsMarkdownRenderer.RenderTurnSummaryMarkup(turn)),
                RenderTurnDetails(turn))
            {
                IsExpanded = false,
            };

        public static Visual RenderTurnDetails(TurnStatistics turn)
        {
            var tables = new List<Visual>
            {
                CreateTable(StatisticsMarkdownRenderer.RenderMetricTable(turn)),
            };

            var usageTable = StatisticsMarkdownRenderer.RenderUsageTable(turn);
            if (!string.IsNullOrWhiteSpace(usageTable))
            {
                tables.Add(CreateTable(usageTable));
            }

            var toolBucketTable = StatisticsMarkdownRenderer.RenderToolBucketTable(turn);
            if (!string.IsNullOrWhiteSpace(toolBucketTable))
            {
                tables.Add(CreateTable(toolBucketTable));
            }

            return new WrapHStack(tables.ToArray())
                .Spacing(1)
                .RunSpacing(1)
                .MeasureMode(WrapMeasureMode.ConstrainToRun)
                .HorizontalAlignment(Align.Stretch);
        }

        private static Markup CreateHeaderMarkup(string markup)
            => new(markup)
            {
                Wrap = false,
                HorizontalAlignment = Align.Stretch,
                VerticalAlignment = Align.Start,
            };

        private static Visual CreateTable(string markdown)
            => new MarkdownControl(markdown.Trim())
            {
                HorizontalAlignment = Align.Start,
                VerticalAlignment = Align.Start,
                Options = MarkdownRenderOptions.Default with
                {
                    TableStyle = TableStyle.Minimal,
                    WrapCodeBlocks = true,
                    MaxCodeBlockHeight = 14,
                },
            };
    }

    private static class StatisticsMarkdownRenderer
    {
        private const string TurnStatisticsTitle = "Turn statistics";

        public static string RenderTurnSummary(TurnStatistics turn)
        {
            var builder = new StringBuilder();
            builder.Append("**")
                .Append(TurnStatisticsTitle)
                .Append("**")
                .Append(RenderTurnSummarySuffix(turn));
            return builder.ToString();
        }

        public static string RenderTurnSummaryMarkup(TurnStatistics turn)
            => $"[bold]{TurnStatisticsTitle}[/]{AnsiMarkup.Escape(RenderTurnSummarySuffix(turn))}";

        private static string RenderTurnSummarySuffix(TurnStatistics turn)
        {
            var inputTokenSource = turn.ReportedInputTokens is not null ? "reported" : "estimated ≈ chars/4";
            var builder = new StringBuilder();
            builder.Append(" · ")
                .Append(FormatDuration(turn.Duration))
                .Append(" · tokens ")
                .Append(FormatNumber(turn.DisplayInputTokens))
                .Append(" in (")
                .Append(inputTokenSource)
                .Append(") / ≈")
                .Append(FormatNumber(turn.DisplayOutputTokens))
                .Append(" out (estimated generated) · tools ")
                .Append(turn.Tools.Count)
                .Append(" calls / ")
                .Append(FormatDuration(turn.TotalToolTime));
            if (turn.ReportedOutputTokens is not null)
            {
                builder.Append(" · provider out ")
                    .Append(FormatNumber(turn.ReportedOutputTokens.Value));
            }

            if (turn.CompactionCount > 0)
            {
                builder.Append(" · compactions ")
                    .Append(turn.CompactionCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" / ")
                    .Append(FormatDuration(turn.CompactionDuration));
            }

            return builder.ToString();
        }

        public static string RenderTurnDetails(TurnStatistics turn)
        {
            var builder = new StringBuilder();
            builder.Append(RenderMetricTable(turn));

            var usageTable = RenderUsageTable(turn);
            if (!string.IsNullOrWhiteSpace(usageTable))
            {
                builder.AppendLine().AppendLine().Append(usageTable);
            }

            var toolBucketTable = RenderToolBucketTable(turn);
            if (!string.IsNullOrWhiteSpace(toolBucketTable))
            {
                builder.AppendLine().AppendLine().Append(toolBucketTable);
            }

            return builder.ToString();
        }

        public static string RenderMetricTable(TurnStatistics turn)
        {
            var builder = new StringBuilder();
            builder.AppendLine("| Metric | Value |")
                .AppendLine("| --- | ---: |")
                .Append("| Prompt | ").Append(FormatSize(turn.Prompt)).AppendLine(" |")
                .Append("| Assistant | ").Append(FormatSize(turn.Assistant)).AppendLine(" |")
                .Append("| Reasoning | ").Append(FormatSize(turn.Reasoning)).AppendLine(" |")
                .Append("| Reasoning summary | ").Append(FormatSize(turn.ReasoningSummary)).AppendLine(" |")
                .Append("| Plan/notice | ").Append(FormatSize(turn.Plan)).AppendLine(" |")
                .Append("| Tool input (model generated) | ").Append(FormatSize(turn.ToolInput)).AppendLine(" |")
                .Append("| Generated output | ").Append(FormatSize(turn.GeneratedOutput)).AppendLine(" |")
                .Append("| Tool output | ").Append(FormatSize(turn.ToolOutput)).AppendLine(" |")
                .Append("| Compactions | ").Append(turn.CompactionCount.ToString(CultureInfo.InvariantCulture)).Append(" / ").Append(FormatDuration(turn.CompactionDuration)).AppendLine(" |")
                .Append("| First assistant token | ").Append(FormatOptionalDuration(turn.FirstAssistantLatency)).AppendLine(" |")
                .Append("| First reasoning token | ").Append(FormatOptionalDuration(turn.FirstReasoningLatency)).AppendLine(" |")
                .Append("| Thinking time | ").Append(FormatDuration(turn.ThinkingTime)).AppendLine(" |")
                .Append("| Longest gap | ").Append(FormatDuration(turn.LongestGap)).AppendLine(" |")
                .Append("| Assistant speed | ").Append(FormatRate(turn.AssistantTokensPerSecond)).AppendLine(" |")
                .Append("| Reasoning speed | ").Append(FormatRate(turn.ReasoningTokensPerSecond)).AppendLine(" |")
                .Append("| Generated output speed | ").Append(FormatRate(turn.GeneratedOutputTokensPerSecond)).AppendLine(" |");

            return builder.ToString();
        }

        public static string RenderUsageTable(TurnStatistics turn)
        {
            if (!turn.HasReportedUsage && turn.SystemPromptTokens is null && turn.DeveloperPromptTokens is null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine("| Usage | Tokens |").AppendLine("| --- | ---: |");
            AppendTokenLine(builder, "Input (provider reported)", turn.ReportedInputTokens);
            AppendTokenLine(builder, "Cached input (provider reported)", turn.ReportedCachedInputTokens);
            AppendTokenLine(builder, "Cache read (provider reported)", turn.ReportedCacheReadTokens);
            AppendTokenLine(builder, "Cache write (provider reported)", turn.ReportedCacheWriteTokens);
            AppendTokenLine(builder, "Output (provider reported)", turn.ReportedOutputTokens);
            AppendTokenLine(builder, "Reasoning (provider reported)", turn.ReportedReasoningTokens);
            AppendTokenLine(builder, "System prompt", turn.SystemPromptTokens);
            AppendTokenLine(builder, "Developer prompt", turn.DeveloperPromptTokens);
            return builder.ToString();
        }

        public static string RenderToolBucketTable(TurnStatistics turn)
        {
            if (turn.Buckets.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.AppendLine("| Tool bucket | Calls | Fail | Cancel | Duration | Input | Output |")
                .AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var bucket in turn.Buckets)
            {
                builder.Append("| ").Append(EscapeMarkdown(bucket.Name))
                    .Append(" | ").Append(bucket.CallCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ").Append(bucket.FailedCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ").Append(bucket.CanceledCount.ToString(CultureInfo.InvariantCulture))
                    .Append(" | ").Append(FormatDuration(bucket.TotalDuration))
                    .Append(" | ").Append(FormatNumber(bucket.InputCharacters)).Append(" chars")
                    .Append(" | ").Append(FormatNumber(bucket.OutputCharacters)).Append(" chars |")
                    .AppendLine();
            }

            return builder.ToString();
        }

        private static void AppendTokenLine(StringBuilder builder, string label, long? value)
        {
            if (value is not null)
            {
                builder.Append("| ").Append(label).Append(" | ").Append(FormatNumber(value.Value)).AppendLine(" |");
            }
        }

        private static string FormatSize(ContentStats stats)
            => FormattableString.Invariant($"{FormatNumber(stats.Characters)} chars / {FormatBytes(stats.Bytes)} / ≈{FormatNumber(stats.EstimatedTokens)} tokens");

        private static string FormatOptionalDuration(TimeSpan? duration)
            => duration is null ? "n/a" : FormatDuration(duration.Value);

        private static string FormatRate(double? rate)
            => rate is null || rate <= 0 ? "n/a" : FormattableString.Invariant($"{rate:0.#} tokens/s");

        private static string FormatNumber(long value)
            => value.ToString("N0", CultureInfo.InvariantCulture);

        private static string EscapeMarkdown(string text)
            => text.Replace("|", "\\|", StringComparison.Ordinal);
    }
}
