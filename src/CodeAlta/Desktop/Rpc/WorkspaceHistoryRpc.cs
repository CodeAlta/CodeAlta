using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    // The kind of the error of a run that never gave its prompt to the provider: the conversation changed while it waited.
    internal const string ConversationChangedErrorKind = "ConversationChangedWhileWaiting";

    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? _readHistory;
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? _readHistoryTail;

    [NeoRpcMethod("history")]
    public Task<HistoryResponse> HistoryAsync(HistoryRequest request, CancellationToken cancellationToken) =>
        ReadHistoryAsync(request, _readHistory, cancellationToken);

    [NeoRpcMethod("historyTail")]
    public Task<HistoryResponse> HistoryTailAsync(HistoryRequest request, CancellationToken cancellationToken) =>
        ReadHistoryTailAsync(request, _readHistoryTail, cancellationToken);

    // Version 2 cursors are exclusive reverse boundaries; version 1 remains forward-only.
    internal static Task<HistoryResponse> ReadHistoryTailAsync(HistoryRequest request,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? read,
        CancellationToken cancellationToken) => ReadHistoryCoreAsync("historyTail", request, read, 2, cancellationToken);

    // Actual RPC route. Tests supply literal callbacks, never instantiate a catalog/service.
    internal static Task<HistoryResponse> ReadHistoryAsync(HistoryRequest request,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? read,
        CancellationToken cancellationToken) => ReadHistoryCoreAsync("history", request, read, 1, cancellationToken);

    private static async Task<HistoryResponse> ReadHistoryCoreAsync(string route, HistoryRequest request,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? read, int version,
        CancellationToken cancellationToken, int sourceReserve = 0)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        AgentSessionHistoryCursor? cursor;
        try
        {
            ValidateIdentity(request.SessionId, 256, required: true);
            cursor = ParseCursor(request, version);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or OverflowException)
        {
            return Failure("invalid_cursor");
        }
        if (read is null) return Failure("unconfigured");
        try
        {
            var page = await read(request.SessionId, cursor, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ProjectHistory(page, version, sourceReserve);
        }
        catch (OperationCanceledException) { throw; }
        // Every slot was held by other reads for the bounded wait: nothing was read, and asking again can succeed.
        catch (OwnedWorkspaceBusyException) { return Failure("busy"); }
        catch (AgentSessionHistoryException exception)
        {
            return Failure(route, request.SessionId, exception.Code is "missing_session" or "invalid_cursor" or "outside_root" or "history_changed"
                or "unsupported_format" or "record_too_large" or "corrupt_record" ? exception.Code : "read_failed", exception);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return Failure(route, request.SessionId, "missing_session", exception);
        }
        // Never serialize cache/provider/infrastructure exception details: the response has only the code.
        catch (Exception exception) { return Failure(route, request.SessionId, "read_failed", exception); }
    }

    // The local log keeps the exception the response omits, so a "could not be read" report can be diagnosed.
    // A journal that grows while it is read is routine during a live turn and is not logged.
    internal static void LogHistoryFailure(string route, string sessionId, string code, Exception exception)
    {
        if (code == "history_changed" || !LogManager.IsInitialized) return;
        // The history exception message is generic; its code names the actual limitation.
        var cause = exception is AgentSessionHistoryException { Code: var actual } && actual != code ? $" (history code {actual})" : "";
        LogManager.GetLogger("CodeAlta.Desktop.History").Warn(exception, $"Desktop {route} for session {sessionId} failed with {code}{cause}");
    }

    private static HistoryResponse Failure(string route, string sessionId, string code, Exception exception)
    {
        LogHistoryFailure(route, sessionId, code, exception);
        return Failure(code);
    }

    private static AgentSessionHistoryCursor? ParseCursor(HistoryRequest request, int version)
    {
        var value = request.Cursor;
        if (value is null) return null;
        if (value.Version != version || !string.Equals(value.SessionId, request.SessionId, StringComparison.Ordinal)) throw new FormatException();
        var length = Decimal(value.Length);
        var ticks = Decimal(value.LastWriteUtcTicks);
        var offset = Decimal(value.Offset);
        if (length <= 0 || offset <= 0 || offset >= length || ticks > DateTime.MaxValue.Ticks) throw new FormatException();
        return new(request.SessionId, length, ticks, offset);
    }

    private static long Decimal(string value)
    {
        if (value is null || value.Length is < 1 or > 19 || value.Any(c => c is < '0' or > '9')) throw new FormatException();
        return long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    internal static HistoryResponse ProjectHistory(AgentSessionHistoryPage page, int version = 1, int sourceReserve = 0)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Entries.Count > 100) return Failure("wire_limit");
        var rows = new List<HistoryEntry>();
        // Full response accounting: worst-case JSON UTF-16 escaping, fixed per-row/property overhead,
        // and 8 KiB reserved for response/cursor/RPC envelope. Never drop rows then advance their cursor.
        var remaining = 700 * 1024 - 8192 - sourceReserve;
        // The prompt a later prompt record of the page replaced: a page is a run of consecutive records.
        AgentSystemPromptEvent? previousPrompt = null;
        foreach (var entry in page.Entries)
        {
            var value = entry.Event;
            var type = "";
            string? kind = null, phase = null, contentId = null, activityId = null, parentId = null, interactionId = null;
            string? text = null, name = null, details = null, source = null;
            var omitted = false;
            HistoryImage[]? images = null;
            HistoryAttachment[]? attachments = null;
            string? clientRequestId = null;
            var imageCost = 0;
            HistoryPromptSummary? promptSummary = null;
            var promptCost = 0;
            switch (value)
            {
                case AgentContentDeltaEvent delta:
                    type = "contentDelta"; kind = delta.Kind.ToString(); contentId = delta.ContentId;
                    parentId = delta.ParentActivityId; text = delta.Delta; details = Json(delta.Details);
                    break;
                case AgentContentCompletedEvent completed:
                    type = "contentCompleted"; kind = completed.Kind.ToString(); contentId = completed.ContentId;
                    parentId = completed.ParentActivityId; text = completed.Content;
                    interactionId = completed.AskId; details = Json(completed.Details);
                    // A prompt another session of an agent sent names that session: the page shows whose it is.
                    if (completed.Kind == AgentContentKind.User)
                    {
                        source = completed.SourceSessionId;
                        clientRequestId = PromptImageHistory.ReadClientRequestId(completed.Details);
                    }
                    // A user message and a tool result list their images; their text and details do not name the files.
                    images = HistoryImageProjection.Project(completed, ref text, ref details, out attachments, out imageCost);
                    break;
                case AgentActivityEvent activity:
                    type = "activity"; kind = activity.Kind.ToString(); phase = activity.Phase.ToString();
                    activityId = activity.ActivityId; parentId = activity.ParentActivityId; name = activity.Name;
                    text = activity.Message; details = HistoryImageProjection.ActivityDetails(activity.Details);
                    break;
                case AgentNotesEvent notes: type = "notes"; kind = notes.Kind.ToString(); text = notes.Markdown; break;
                case AgentErrorEvent error:
                    type = "error"; text = error.Message;
                    // A provider without a Claude Code CLI: the card of the error proposes its download.
                    for (var info = error.ExceptionInfo; info is not null; info = info.InnerException)
                        if (CodeAlta.Hosting.ConfiguredClaudeCodeCli.IsNotFound(info.Type)) { kind = CodeAlta.Hosting.ConfiguredClaudeCodeCli.NotFoundErrorKind; break; }
                    // A run that never gave its prompt to the provider: the page shows the prompt as not sent.
                    if (error.ExceptionInfo?.Type == typeof(AgentConversationChangedException).FullName) kind = ConversationChangedErrorKind;
                    details = ErrorDetails(error);
                    omitted = error.ExceptionInfo?.StackTrace is not null || error.ExceptionInfo?.InnerException is not null;
                    break;
                case AgentRawEvent: type = "raw"; omitted = true; break;
                case AgentSystemPromptEvent prompt:
                    // The row says what the record is; its texts are read whole by its window (toolCalls.readPrompt).
                    type = "system_prompt"; kind = prompt.Reason; name = SystemPromptProjection.Name(prompt);
                    promptSummary = ProjectPrompt(prompt, previousPrompt, out promptCost);
                    previousPrompt = prompt;
                    break;
                case AgentSessionUpdateEvent update:
                    type = "sessionUpdate"; kind = update.Kind.ToString(); text = FormatSessionUpdate(update);
                    details = Json(update.Details);
                    // A local compaction's details would be cut mid-JSON by the row budget: send its figures and,
                    // as text, its checkpoint summary.
                    if (update.Kind == AgentSessionUpdateKind.CompactionCompleted
                        && HistoryCompactionProjection.TryProject(update.Details, out var figures, out var checkpoint))
                    {
                        details = figures;
                        if (checkpoint is not null)
                            text = $"{text}\n\n{HistoryCompactionProjection.SummaryHeading}\n\n{checkpoint}";
                    }
                    break;
                case AgentPlanSnapshotEvent plan:
                    type = "planSnapshot"; kind = plan.Snapshot.ChangeKind?.ToString(); text = FormatPlan(plan.Snapshot);
                    break;
                case AgentInteractionEvent interaction:
                    type = "interaction"; kind = interaction.Kind.ToString(); interactionId = interaction.InteractionId;
                    text = interaction.Message; details = Json(interaction.Details);
                    break;
                case AgentGenericPermissionRequest permission:
                    type = "permissionGeneric"; kind = permission.Kind; interactionId = permission.InteractionId;
                    name = "Permission request"; details = permission.Raw.GetRawText();
                    break;
                case AgentCommandPermissionRequest permission:
                    type = "permissionCommand"; kind = permission.Kind; interactionId = permission.InteractionId;
                    name = "Command permission"; text = FormatCommandPermission(permission);
                    break;
                case AgentFileChangePermissionRequest permission:
                    type = "permissionFileChange"; kind = permission.Kind; interactionId = permission.InteractionId;
                    name = "File-change permission"; text = FormatFilePermission(permission);
                    break;
                case AgentUserInputRequest input:
                    type = "userInputRequest"; kind = "UserInput"; interactionId = input.InteractionId;
                    name = "User input requested"; text = FormatUserInput(input);
                    break;
                default: return Failure("unsupported_format");
            }
            var provider = value.ProviderId.Value;
            var run = value.RunId?.Value;
            // Providers can report opaque item identities longer than the wire limit (Copilot Responses items exceed
            // 400 characters); compact them exactly like the live display so both still correlate the same item.
            contentId = Compact(contentId); activityId = Compact(activityId); parentId = Compact(parentId);
            ValidateIdentity(provider, 256, required: true);
            ValidateIdentity(value.SessionId, 256, required: true);
            var identityCost = 0;
            foreach (var id in new[] { provider, value.SessionId, run, kind, phase, contentId, activityId, parentId, interactionId, source })
            {
                ValidateIdentity(id, 256, required: false);
                identityCost += (id?.Length ?? 0) * 6;
            }
            var shortened = false;
            name = Preview(name, 256, ref shortened);
            var rowsRemaining = page.Entries.Count - rows.Count;
            var rowBudget = remaining / rowsRemaining;
            var detailsBudget = Math.Max(0, rowBudget - 1024 - identityCost - imageCost - promptCost - 6 * (name?.Length ?? 0));
            var fileCost = 0;
            var toolCost = 0;
            var tool = value is AgentActivityEvent activityValue && activityValue.Kind != AgentActivityKind.FileChange
                ? HistoryToolProjection.Project(activityValue, detailsBudget / 2, out toolCost)
                : value is AgentContentCompletedEvent output && output.Kind is AgentContentKind.ToolOutput or AgentContentKind.CommandOutput or AgentContentKind.FileChangeOutput
                    ? HistoryToolProjection.ProjectOutput(output.Content, detailsBudget / 2, out toolCost) : null;
            detailsBudget -= toolCost;
            var files = (type == "activity" && kind == "FileChange") || (type == "sessionUpdate" && kind == "DiffUpdated")
                ? HistoryFileProjection.Project(details, detailsBudget / 2, out fileCost) : null;
            detailsBudget -= fileCost;
            var detailsShortened = false;
            details = Preview(details, Math.Min(8 * 1024, detailsBudget / 18), ref detailsShortened);
            var textBudget = Math.Max(0, detailsBudget - 6 * (details?.Length ?? 0));
            text = Preview(text, Math.Min(32 * 1024, textBudget / 6), ref shortened);
            var cost = 1024 + identityCost + imageCost + promptCost + fileCost + toolCost + 6 * ((text?.Length ?? 0) + (name?.Length ?? 0) + (details?.Length ?? 0));
            if (cost > remaining) return Failure("wire_limit");
            remaining -= cost;
            rows.Add(new(entry.Offset.ToString(CultureInfo.InvariantCulture), type, provider, value.SessionId, run,
                value.Timestamp, kind, phase, contentId, activityId, parentId, interactionId, name, text, details,
                shortened, detailsShortened, omitted, files, tool, images, source, promptSummary, attachments, clientRequestId));
        }
        HistoryCursor? next = null;
        if (page.Next is { } cursor)
        {
            ValidateIdentity(cursor.SessionId, 256, required: true);
            next = new(version, cursor.SessionId, cursor.Length.ToString(CultureInfo.InvariantCulture),
                cursor.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture), cursor.Offset.ToString(CultureInfo.InvariantCulture));
        }
        return new("ok", rows.ToArray(), next, page.TailOmitted);
    }

    private static string? Preview(string? value, int limit, ref bool shortened)
    {
        if (value is null) return null;
        ValidateUnicode(value);
        if (value.Length <= limit) return value;
        shortened = true;
        if (limit == 0) return string.Empty;
        return value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
    }

    private static string? Compact(string? value) => value is null ? null : RuntimeDisplayProjection.CompactIdentifier(value);

    private static string? Json(JsonElement? value) => value?.GetRawText();

    // What the card of an error shows beyond its message: the exceptions it wraps, the kind of failure, and what the
    // provider said (its code, the HTTP status, the request id, the payload last since the row budget may cut it).
    private static string? ErrorDetails(AgentErrorEvent error)
    {
        if (error.ExceptionInfo is not { } info) return null;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("exception", $"{info.Type}: {info.Message}");
            if (error.Failure is { } failure) writer.WriteString("failure", failure.Kind.ToString());
            var depth = 0;
            for (var inner = info.InnerException; inner is not null && depth < 4; inner = inner.InnerException, depth++)
            {
                if (depth == 0) writer.WriteStartArray("causes");
                writer.WriteStringValue($"{inner.Type}: {inner.Message}");
            }
            if (depth > 0) writer.WriteEndArray();
            var data = info.CollectData();
            if (data.Count > 0)
            {
                writer.WriteStartObject("data");
                foreach (var (key, value) in data)
                    if (key != AgentErrorData.Payload) writer.WriteString(key, value);
                if (data.TryGetValue(AgentErrorData.Payload, out var payload)) writer.WriteString(AgentErrorData.Payload, payload);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // What the row of a prompt record says. The lines it changed are counted when the prompt it replaced is in
    // the same page; otherwise the row asks for them by itself, which costs a pass over the journal.
    private static HistoryPromptSummary ProjectPrompt(AgentSystemPromptEvent prompt, AgentSystemPromptEvent? previous, out int cost)
    {
        var changed = SystemPromptProjection.IsChange(prompt);
        var change = changed && previous is not null ? SystemPromptProjection.Change(previous, prompt) : null;
        var cut = false;
        var hash = Preview(prompt.EffectivePromptHash, 256, ref cut) ?? string.Empty;
        var path = Preview(prompt.AgentPromptUsage?.SourcePath, 1024, ref cut);
        cost = 512 + 6 * (hash.Length + (path?.Length ?? 0));
        return new(changed, hash, path, prompt.Statistics.TotalApproxTokens, prompt.Statistics.SystemApproxTokens, prompt.Statistics.DeveloperApproxTokens,
            prompt.ProviderPayloadSummary.AppliedToProvider, prompt.ProviderPayloadSummary.Lossy, change?.Added, change?.Removed);
    }

    private static string FormatSessionUpdate(AgentSessionUpdateEvent update)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(update.Message)) text.AppendLine(update.Message);
        if (update.Usage is { } usage)
        {
            if (text.Length > 0) text.AppendLine();
            text.Append("**Context:** ").Append(usage.CurrentTokens?.ToString(CultureInfo.InvariantCulture) ?? "unknown")
                .Append(" / ").Append(usage.TokenLimit?.ToString(CultureInfo.InvariantCulture) ?? "unknown").Append(" tokens");
            if (usage.WindowUsagePercentage is { } percentage)
                text.Append(" (").Append(percentage.ToString("0.#", CultureInfo.InvariantCulture)).Append("%)");
            text.AppendLine();
            if (usage.MessageCount is { } count) text.Append("\n**Messages in context:** ").AppendLine(count.ToString(CultureInfo.InvariantCulture));
            if (usage.PromptCacheExpiresAt is { } expiresAt) text.Append("\n**Prompt cache expires:** ").AppendLine(expiresAt.ToString("O", CultureInfo.InvariantCulture));
            if (usage.LastOperation is { } operation)
            {
                if (!string.IsNullOrWhiteSpace(operation.Model)) text.Append("\n**Model:** ").AppendLine(operation.Model);
                // The input is the whole input of the request; what the cache read and wrote is part of it.
                var input = AgentInputTokenUsage.From(operation);
                AppendMetric(text, "Input tokens", operation.InputTokens is null ? null : input?.Total);
                AppendMetric(text, "Output tokens", operation.OutputTokens);
                AppendMetric(text, "Cached input tokens", operation.CachedInputTokens ?? operation.CacheReadTokens);
                AppendMetric(text, "Cache write tokens", operation.CacheWriteTokens);
                AppendMetric(text, "Reasoning tokens", operation.ReasoningTokens);
                if (operation.Cost is { } cost)
                    text.Append("\n**Cost:** ").AppendLine(string.IsNullOrWhiteSpace(operation.CostUnit) ? cost.ToString(CultureInfo.InvariantCulture)
                        : $"{cost.ToString("0.####", CultureInfo.InvariantCulture)} {operation.CostUnit}");
                if (operation.DurationMs is { } duration) text.Append("\n**Duration:** ").Append(duration.ToString("0.##", CultureInfo.InvariantCulture)).AppendLine(" ms");
                if (!string.IsNullOrWhiteSpace(operation.ReasoningEffort)) text.Append("\n**Reasoning effort:** ").AppendLine(operation.ReasoningEffort);
            }
            text.Append("\n**Usage scope/source:** ").Append(usage.Scope).Append(" · ").Append(usage.Source);
        }
        return text.Length == 0 ? update.Kind.ToString() : text.ToString().TrimEnd();
    }

    private static string FormatPlan(AgentPlanSnapshot plan)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(plan.Explanation)) text.AppendLine(plan.Explanation).AppendLine();
        if (plan.Steps is not null)
            foreach (var step in plan.Steps)
                text.Append("- [").Append(step.Status == AgentPlanStepStatus.Completed ? 'x' : ' ').Append("] ")
                    .Append(step.Text).Append(step.Status == AgentPlanStepStatus.InProgress ? " *(in progress)*" : "").AppendLine();
        return text.Length == 0 ? "Plan removed." : text.ToString().TrimEnd();
    }

    private static string FormatCommandPermission(AgentCommandPermissionRequest permission)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(permission.Reason)) text.AppendLine(permission.Reason).AppendLine();
        if (!string.IsNullOrWhiteSpace(permission.Command)) AppendSection(text, "Command", permission.Command);
        if (!string.IsNullOrWhiteSpace(permission.WorkingDirectory)) text.Append("\n**Working directory:** `").Append(permission.WorkingDirectory).AppendLine("`");
        if (permission.Network is { } network) text.Append("\n**Network:** ").Append(network.Protocol).Append("://").AppendLine(network.Host);
        return text.ToString().TrimEnd();
    }

    private static string FormatFilePermission(AgentFileChangePermissionRequest permission)
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(permission.Reason)) text.AppendLine(permission.Reason);
        if (!string.IsNullOrWhiteSpace(permission.GrantRoot)) text.Append("\n**Requested root:** `").Append(permission.GrantRoot).AppendLine("`");
        return text.ToString().TrimEnd();
    }

    private static string FormatUserInput(AgentUserInputRequest input)
    {
        var text = new StringBuilder();
        foreach (var prompt in input.Form.Prompts)
        {
            text.Append("- **").Append(prompt.Header ?? prompt.Id).Append(":** ").AppendLine(prompt.Question);
            if (prompt.Options is not null)
                foreach (var option in prompt.Options) text.Append("  - ").Append(option.Label)
                    .Append(string.IsNullOrWhiteSpace(option.Description) ? "" : $" — {option.Description}").AppendLine();
        }
        return text.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder text, string heading, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        text.Append("\n### ").AppendLine(heading).AppendLine().AppendLine(value);
    }

    private static void AppendMetric(StringBuilder text, string label, long? value)
    {
        if (value is not null) text.Append("\n**").Append(label).Append(":** ").AppendLine(value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static HistoryResponse Failure(string code) => new(code, [], null, false);
}

internal sealed record HistoryRequest(string SessionId, HistoryCursor? Cursor,
    // The stamp of the journal a previous newest timeline page was read from (historyTimeline without cursor only):
    // while the journal still has it, the answer is `unchanged`. Absent from the other requests.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? Known = null);
internal sealed record HistoryCursor(int Version, string SessionId, string Length, string LastWriteUtcTicks, string Offset);
internal sealed record HistoryResponse(string Status, HistoryEntry[] Entries, HistoryCursor? Next, bool TailOmitted);
internal sealed record HistoryEntry(string Offset, string EventType, string ProviderId, string SessionId, string? RunId,
    DateTimeOffset Timestamp, string? Kind, string? Phase, string? ContentId, string? ActivityId, string? ParentActivityId,
    string? InteractionId, string? Name, string? Text, string? Details, bool TextTruncated, bool DetailsTruncated, bool BodyOmitted,
    HistoryFileSet? Files = null, HistoryToolSummary? Tool = null, HistoryImage[]? Images = null, string? SourceSessionId = null,
    HistoryPromptSummary? Prompt = null,
    // The files attached to a user message; absent from the rows that have none.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    HistoryAttachment[]? Attachments = null,
    // The identity of the Send of a user message, by which the page recognizes its prompt; absent when there is none.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    string? ClientRequestId = null);

/// <summary>What the row of a system prompt record says; the texts of the prompt are read by its window.</summary>
/// <param name="Changed">Whether the prompt replaced an earlier one of its session.</param>
/// <param name="Hash">The hash of the prompt.</param>
/// <param name="SourcePath">The file of the agent prompt, when the record names one.</param>
/// <param name="TotalTokens">The approximate size of the prompt, in tokens.</param>
/// <param name="SystemTokens">The approximate size of the system message, in tokens.</param>
/// <param name="DeveloperTokens">The approximate size of the developer instructions, in tokens.</param>
/// <param name="Applied">Whether the prompt was sent to the provider.</param>
/// <param name="Lossy">Whether the provider could not take the prompt as it is written.</param>
/// <param name="Added">The lines the prompt added to the one it replaced, when that one is in the same page.</param>
/// <param name="Removed">The lines the prompt removed from the one it replaced, when that one is in the same page.</param>
internal sealed record HistoryPromptSummary(bool Changed, string Hash, string? SourcePath, int TotalTokens, int SystemTokens, int DeveloperTokens,
    bool Applied, bool Lossy, int? Added, int? Removed);
