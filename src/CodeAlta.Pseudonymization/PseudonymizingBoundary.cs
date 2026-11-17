using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Pseudonymization;

/// <summary>What a boundary is set up with, beside its table.</summary>
public sealed record PseudonymizerOptions
{
    /// <summary>
    /// Gets whether the arguments of a tool get the real values back: true for the tools of the machine (files, shell,
    /// <c>alta</c>), false for those that would send them to a third party (a web page, a remote service), which get the
    /// pseudonyms. True for every tool by default.
    /// </summary>
    public Func<string, bool> ToolReceivesRealValues { get; init; } = static _ => true;

    /// <summary>Gets values hidden wherever they are, whatever finds them or not: the names of a file of the user.</summary>
    public IReadOnlyList<(string Entity, string Value)> AlwaysHide { get; init; } = [];

    /// <summary>Gets values never hidden: what a recognizer took for personal data by mistake.</summary>
    public IReadOnlySet<string> NeverHide { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the policy of each kind.</summary>
    public PiiPolicies Policies { get; init; } = PiiPolicies.Default;
}

/// <summary>What one request sent: the count of each kind it hid.</summary>
/// <param name="Hidden">The count of the values hidden, by kind.</param>
/// <param name="UnknownTokens">The pseudonyms the provider wrote that the table does not have.</param>
public sealed record PseudonymizationReport(IReadOnlyDictionary<string, int> Hidden, IReadOnlyList<string> UnknownTokens);

/// <summary>
/// The boundary of one confidential session: what it sends is analyzed and its personal data replaced by the pseudonyms
/// of its table; what the provider answers gets the real values back. A text sent once goes the same way each time after
/// (<see cref="PseudonymCache"/>). What cannot be analyzed is not sent.
/// </summary>
public sealed partial class PseudonymizingBoundary : IAgentProviderBoundary
{
    /// <summary>What the model of a confidential session is told about the pseudonyms.</summary>
    public const string Instruction =
        "<codealta-context>Pseudonymization: on. Values written as [KIND_NNN], such as [PERSONNE_001] or [IBAN_002], are pseudonyms of personal data the user keeps from you. " +
        "Copy each one exactly as it is, with its brackets, capitals, underscore and digits, wherever you refer to it, also in the arguments of tool calls and in the files you write: " +
        "the tools of this machine receive the real value. Never translate one, never invent one, never try to guess the value it stands for.</codealta-context>";

    // The longest a pseudonym can be: what could still begin one at the end of a part of a stream is held back up to that.
    private const int MaximumTokenLength = 32;

    private readonly PiiAnalyzer _analyzer;
    private readonly PseudonymTable _table;
    private readonly PseudonymCache _cache;
    private readonly PseudonymizerOptions _options;

    /// <summary>Creates the boundary.</summary>
    /// <param name="analyzer">What finds the personal data.</param>
    /// <param name="table">The pseudonyms of the session.</param>
    /// <param name="cache">What each text of the session was sent as.</param>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PseudonymizingBoundary(PiiAnalyzer analyzer, PseudonymTable table, PseudonymCache cache, PseudonymizerOptions options)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        _analyzer = analyzer;
        _table = table;
        _cache = cache;
        _options = options;
    }

    /// <summary>Gets the pseudonyms of the session.</summary>
    public PseudonymTable Table => _table;

    /// <summary>Gets what each text of the session was sent as.</summary>
    public PseudonymCache Cache => _cache;

    /// <summary>Raised after each request is prepared, with what it hid.</summary>
    public event Action<PseudonymizationReport>? RequestPrepared;

    /// <summary>Raised after the answer of a request is restored, with the pseudonyms it did not know.</summary>
    public event Action<PseudonymizationReport>? ResponseRestored;

    /// <inheritdoc />
    public async ValueTask<AgentTurnRequest> PrepareRequestAsync(AgentTurnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pass = new HidingPass(this);
        // First every text the cache does not have, analyzed at once: a model works in batches.
        Visit(request, pass.Collect);
        await pass.AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        var sent = request with
        {
            SystemMessage = pass.Hide(request.SystemMessage),
            DeveloperInstructions = pass.Hide(request.DeveloperInstructions),
            SessionInstructions = string.IsNullOrWhiteSpace(request.SessionInstructions)
                ? Instruction
                : string.Concat(pass.Hide(request.SessionInstructions), "\n\n", Instruction),
            Conversation = [.. request.Conversation.Select(message => message with { Parts = [.. message.Parts.Select(pass.HidePart)] })],
        };
        RequestPrepared?.Invoke(new PseudonymizationReport(pass.Hidden, []));
        return sent;
    }

    /// <inheritdoc />
    public IAgentBoundaryTextStream CreateResponseStream(AgentContentKind kind, string contentId) => new RestoringStream(this);

    /// <inheritdoc />
    public AgentTurnResponse RestoreResponse(AgentTurnRequest request, AgentTurnResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        var unknown = new List<string>();
        Dictionary<string, string>? unrunnable = response.UnrunnableToolCalls is { Count: > 0 } given ? new(given, StringComparer.Ordinal) : null;
        var parts = new List<AgentMessagePart>(response.AssistantMessage.Parts.Count);
        foreach (var part in response.AssistantMessage.Parts)
        {
            switch (part)
            {
                case AgentMessagePart.Text text:
                    parts.Add(new AgentMessagePart.Text(RestoreRemembered(text.Value, unknown)));
                    break;
                case AgentMessagePart.Reasoning reasoning:
                    parts.Add(reasoning with
                    {
                        Value = reasoning.Value is null ? null : RestoreRemembered(reasoning.Value, unknown),
                        SummaryParts = reasoning.SummaryParts is null ? null : [.. reasoning.SummaryParts.Select(summary => RestoreRemembered(summary, unknown))],
                    });
                    break;
                case AgentMessagePart.ToolCall call:
                    var callUnknown = new List<string>();
                    var real = _options.ToolReceivesRealValues(call.Name);
                    var arguments = real ? MapStrings(call.Arguments, value => Restore(value, callUnknown)) : call.Arguments;
                    if (real)
                    {
                        _cache.Set(arguments.GetRawText(), call.Arguments.GetRawText());
                        if (callUnknown.Count > 0)
                        {
                            (unrunnable ??= new(StringComparer.Ordinal))[call.CallId] =
                                $"The arguments name {string.Join(", ", callUnknown.Distinct(StringComparer.Ordinal))}, which is no pseudonym of this session: copy the pseudonyms exactly as they were given.";
                        }
                    }

                    unknown.AddRange(callUnknown);
                    parts.Add(call with { Arguments = arguments });
                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }

        var restored = response with
        {
            AssistantMessage = response.AssistantMessage with { Parts = parts },
            UnrunnableToolCalls = unrunnable,
            Summary = response.Summary is null ? null : Restore(response.Summary, unknown),
            Title = response.Title is null ? null : Restore(response.Title, unknown),
            ProviderActivities = response.ProviderActivities is null ? null
                : [.. response.ProviderActivities.Select(activity => activity with { Message = activity.Message is null ? null : Restore(activity.Message, unknown) })],
        };
        ResponseRestored?.Invoke(new PseudonymizationReport(new Dictionary<string, int>(), [.. unknown.Distinct(StringComparer.Ordinal)]));
        return restored;
    }

    /// <summary>Pseudonymizes one text as a request would, on this machine: nothing is sent.</summary>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Stops the analysis.</param>
    /// <returns>The text as the provider would read it.</returns>
    /// <exception cref="AgentProviderBoundaryException">The text could not be analyzed.</exception>
    public async ValueTask<string> PseudonymizeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        var pass = new HidingPass(this);
        pass.Collect(text);
        await pass.AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        return pass.Hide(text) ?? text;
    }

    /// <summary>Puts the real values back in a text the provider wrote.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The text with the real values.</returns>
    public string Restore(string text) => Restore(text, null);

    // A text of the answer: what it is with the real values is sent back as the provider wrote it.
    private string RestoreRemembered(string text, List<string> unknown)
    {
        var restored = Restore(text, unknown);
        _cache.Set(restored, text);
        return restored;
    }

    private string Restore(string text, List<string>? unknown)
    {
        if (string.IsNullOrEmpty(text)) return text;
        StringBuilder? builder = null;
        var copied = 0;
        foreach (Match match in PseudonymTable.TolerantToken().Matches(text))
        {
            var canonical = _table.Canonical(match.Value);
            if (canonical is null || !_table.TryGetValue(canonical, out var value))
            {
                if (unknown is not null && PseudonymTable.StrictToken().IsMatch(match.Value) && match.Value.StartsWith('[')) unknown.Add(match.Value);
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, match.Index - copied).Append(value);
            copied = match.Index + match.Length;
        }

        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }

    private static void Visit(AgentTurnRequest request, Action<string?> visit)
    {
        visit(request.SystemMessage);
        visit(request.DeveloperInstructions);
        visit(request.SessionInstructions);
        foreach (var message in request.Conversation)
        {
            foreach (var part in message.Parts)
            {
                switch (part)
                {
                    case AgentMessagePart.Text text:
                        visit(text.Value);
                        break;
                    case AgentMessagePart.Reasoning reasoning:
                        visit(reasoning.Value);
                        foreach (var summary in reasoning.SummaryParts ?? []) visit(summary);
                        break;
                    case AgentMessagePart.ToolCall call:
                        VisitStrings(call.Arguments, visit);
                        break;
                    case AgentMessagePart.ToolResult result:
                        visit(result.Result.Error);
                        foreach (var item in result.Result.Items)
                        {
                            if (item is AgentToolResultItem.Text text) visit(text.Value);
                        }

                        break;
                }
            }
        }
    }

    private static void VisitStrings(JsonElement element, Action<string?> visit)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                visit(element.GetString());
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) VisitStrings(property.Value, visit);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) VisitStrings(item, visit);
                break;
        }
    }

    // Rewrites the string values of a JSON element, its property names and other values as they are.
    private static JsonElement MapStrings(JsonElement element, Func<string, string> map)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            Write(element, writer, map);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();

        static void Write(JsonElement element, Utf8JsonWriter writer, Func<string, string> map)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    writer.WriteStringValue(map(element.GetString()!));
                    break;
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        Write(property.Value, writer, map);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) Write(item, writer, map);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }

    // One preparation: the texts it analyzes, then what each becomes.
    private sealed class HidingPass(PseudonymizingBoundary owner)
    {
        private readonly List<string> _pending = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<PiiSpan>> _analyzed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _hidden = new(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, int> Hidden => _hidden;

        public void Collect(string? text)
        {
            if (string.IsNullOrEmpty(text) || owner._cache.TryGet(text, out _) || !_seen.Add(text)) return;
            _pending.Add(text);
        }

        public async ValueTask AnalyzeAsync(CancellationToken cancellationToken)
        {
            if (_pending.Count == 0) return;
            IReadOnlyList<IReadOnlyList<PiiSpan>> results;
            try
            {
                results = await owner._analyzer.AnalyzeAsync(_pending, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not AgentProviderBoundaryException and not OutOfMemoryException)
            {
                throw new AgentProviderBoundaryException("The personal data of this request could not be analyzed: nothing was sent.", exception);
            }

            for (var index = 0; index < _pending.Count; index++) _analyzed[_pending[index]] = results[index];
        }

        public string? Hide(string? text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (owner._cache.TryGet(text, out var sent)) return sent;
            if (!_analyzed.TryGetValue(text, out var spans))
            {
                // Every text was collected before: one that was not would go out unanalyzed.
                throw new AgentProviderBoundaryException("A text of this request was not analyzed: nothing was sent.");
            }

            var hidden = Apply(text, spans);
            owner._cache.Set(text, hidden);
            return hidden;
        }

        public AgentMessagePart HidePart(AgentMessagePart part) => part switch
        {
            AgentMessagePart.Text text => new AgentMessagePart.Text(Hide(text.Value)!),
            AgentMessagePart.Reasoning reasoning => reasoning with
            {
                Value = Hide(reasoning.Value),
                SummaryParts = reasoning.SummaryParts is null ? null : [.. reasoning.SummaryParts.Select(summary => Hide(summary)!)],
            },
            AgentMessagePart.ToolCall call => call with { Arguments = HideArguments(call.Arguments) },
            AgentMessagePart.ToolResult result => result with
            {
                Result = result.Result with
                {
                    Error = Hide(result.Result.Error),
                    Items = [.. result.Result.Items.Select(item => item is AgentToolResultItem.Text text ? new AgentToolResultItem.Text(Hide(text.Value)!) : item)],
                },
            },
            _ => part,
        };

        private JsonElement HideArguments(JsonElement arguments)
        {
            var raw = arguments.GetRawText();
            if (owner._cache.TryGet(raw, out var sent))
            {
                using var document = JsonDocument.Parse(sent);
                return document.RootElement.Clone();
            }

            var hidden = MapStrings(arguments, value => Hide(value)!);
            owner._cache.Set(raw, hidden.GetRawText());
            return hidden;
        }

        // The analysis, the values the session already hid and the values the user always hides, resolved together, then
        // replaced by what their policy says.
        private string Apply(string text, IReadOnlyList<PiiSpan> analyzed)
        {
            var candidates = new List<PiiSpan>(analyzed);
            foreach (var (entity, value) in owner._table.KnownValues()) AddOccurrences(text, entity, value, StringComparison.Ordinal, candidates);
            foreach (var (entity, value) in owner._options.AlwaysHide) AddOccurrences(text, entity, value, StringComparison.OrdinalIgnoreCase, candidates);
            var spans = PiiAnalyzer.Resolve(text, candidates);
            if (spans.Count == 0) return text;
            var builder = new StringBuilder(text.Length);
            var copied = 0;
            foreach (var span in spans)
            {
                var value = text.Substring(span.Start, span.Length);
                if (owner._options.NeverHide.Contains(value)) continue;
                var policy = owner._options.Policies.For(span.Entity);
                if (policy == PiiPolicy.Keep) continue;
                var replacement = policy == PiiPolicy.Mask ? $"[{span.Entity}]" : owner._table.TokenFor(span.Entity, value);
                builder.Append(text, copied, span.Start - copied).Append(replacement);
                copied = span.End;
                _hidden[span.Entity] = _hidden.TryGetValue(span.Entity, out var count) ? count + 1 : 1;
            }

            return builder.Append(text, copied, text.Length - copied).ToString();
        }

        // A value is found whole: not inside a longer word or number.
        private static void AddOccurrences(string text, string entity, string value, StringComparison comparison, List<PiiSpan> candidates)
        {
            if (value.Length < 3) return;
            for (var index = text.IndexOf(value, comparison); index >= 0; index = text.IndexOf(value, index + value.Length, comparison))
            {
                var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
                var after = index + value.Length == text.Length || !char.IsLetterOrDigit(text[index + value.Length]);
                if (before && after) candidates.Add(new PiiSpan(entity, index, value.Length, 1, "known"));
                if (index + value.Length >= text.Length) break;
            }
        }
    }

    // Restores a streamed text: the end of a part that could begin a pseudonym waits for the next part.
    private sealed partial class RestoringStream(PseudonymizingBoundary owner) : IAgentBoundaryTextStream
    {
        private string _held = string.Empty;

        public string Write(string part)
        {
            ArgumentNullException.ThrowIfNull(part);
            var text = _held.Length == 0 ? part : string.Concat(_held, part);
            var start = text.LastIndexOf('[');
            if (start >= 0 && text.Length - start < MaximumTokenLength && TokenStart().IsMatch(text.AsSpan(start)))
            {
                _held = text[start..];
                return owner.Restore(text[..start]);
            }

            _held = string.Empty;
            return owner.Restore(text);
        }

        public string Flush()
        {
            var text = _held;
            _held = string.Empty;
            return owner.Restore(text);
        }

        // What may still become a pseudonym: a bracket, a kind, a separator and digits, not closed yet.
        [GeneratedRegex(@"^\[[A-Za-zÀ-ÖØ-öø-ÿ_]{0,25}(?:[ _-]\d{0,4})?$", RegexOptions.CultureInvariant)]
        private static partial Regex TokenStart();
    }
}
