using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

internal sealed partial class WorkspaceService
{
    private readonly Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? _readHistory;

    [NeoRpcMethod("history")]
    public Task<HistoryResponse> HistoryAsync(HistoryRequest request, CancellationToken cancellationToken) =>
        ReadHistoryAsync(request, _readHistory, cancellationToken);

    // Actual RPC route. Tests supply literal callbacks, never instantiate a catalog/service.
    internal static async Task<HistoryResponse> ReadHistoryAsync(HistoryRequest request,
        Func<string, AgentSessionHistoryCursor?, CancellationToken, Task<AgentSessionHistoryPage>>? read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        AgentSessionHistoryCursor? cursor;
        try
        {
            ValidateIdentity(request.SessionId, 256, required: true);
            cursor = ParseCursor(request);
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
            return ProjectHistory(page);
        }
        catch (OperationCanceledException) { throw; }
        catch (AgentSessionHistoryException exception)
        {
            return Failure(exception.Code is "missing_session" or "invalid_cursor" or "outside_root" or "history_changed"
                or "unsupported_format" or "record_too_large" or "corrupt_record" ? exception.Code : "read_failed");
        }
        catch (FileNotFoundException) { return Failure("missing_session"); }
        catch (DirectoryNotFoundException) { return Failure("missing_session"); }
        catch (Exception) { return Failure("read_failed"); } // Never serialize cache/provider/infrastructure exception details.
    }

    private static AgentSessionHistoryCursor? ParseCursor(HistoryRequest request)
    {
        var value = request.Cursor;
        if (value is null) return null;
        if (value.Version != 1 || !string.Equals(value.SessionId, request.SessionId, StringComparison.Ordinal)) throw new FormatException();
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

    internal static HistoryResponse ProjectHistory(AgentSessionHistoryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Entries.Count > 100) return Failure("wire_limit");
        var rows = new List<HistoryEntry>();
        // Full response accounting: worst-case JSON UTF-16 escaping, fixed per-row/property overhead,
        // and 8 KiB reserved for response/cursor/RPC envelope. Never drop rows then advance their cursor.
        var remaining = 700 * 1024 - 8192;
        foreach (var entry in page.Entries)
        {
            var value = entry.Event;
            var type = "";
            string? kind = null, phase = null, contentId = null, activityId = null, parentId = null, interactionId = null;
            string? text = null, name = null, details = null;
            var omitted = false;
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
                    break;
                case AgentActivityEvent activity:
                    type = "activity"; kind = activity.Kind.ToString(); phase = activity.Phase.ToString();
                    activityId = activity.ActivityId; parentId = activity.ParentActivityId; name = activity.Name;
                    text = activity.Message; details = Json(activity.Details);
                    break;
                case AgentNotesEvent notes: type = "notes"; kind = notes.Kind.ToString(); text = notes.Markdown; break;
                case AgentErrorEvent error:
                    type = "error"; text = error.Message;
                    details = error.ExceptionInfo is null ? null : $"{error.ExceptionInfo.Type}: {error.ExceptionInfo.Message}";
                    omitted = error.ExceptionInfo?.StackTrace is not null || error.ExceptionInfo?.InnerException is not null;
                    break;
                case AgentRawEvent: type = "raw"; omitted = true; break;
                case AgentSystemPromptEvent prompt:
                    type = "system_prompt"; kind = prompt.Reason; name = prompt.AgentPromptUsage?.DisplayName ?? prompt.AgentPromptId;
                    text = FormatPrompt(prompt); details = Json(prompt.Manifest);
                    break;
                case AgentSessionUpdateEvent update:
                    type = "sessionUpdate"; kind = update.Kind.ToString(); text = FormatSessionUpdate(update);
                    details = Json(update.Details);
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
            ValidateIdentity(provider, 256, required: true);
            ValidateIdentity(value.SessionId, 256, required: true);
            var identityCost = 0;
            foreach (var id in new[] { provider, value.SessionId, run, kind, phase, contentId, activityId, parentId, interactionId })
            {
                ValidateIdentity(id, 256, required: false);
                identityCost += (id?.Length ?? 0) * 6;
            }
            var shortened = false;
            name = Preview(name, 256, ref shortened);
            var rowsRemaining = page.Entries.Count - rows.Count;
            var rowBudget = remaining / rowsRemaining;
            var detailsBudget = Math.Max(0, rowBudget - 1024 - identityCost - 6 * (name?.Length ?? 0));
            var detailsShortened = false;
            details = Preview(details, Math.Min(8 * 1024, detailsBudget / 18), ref detailsShortened);
            var textBudget = Math.Max(0, detailsBudget - 6 * (details?.Length ?? 0));
            text = Preview(text, Math.Min(32 * 1024, textBudget / 6), ref shortened);
            var cost = 1024 + identityCost + 6 * ((text?.Length ?? 0) + (name?.Length ?? 0) + (details?.Length ?? 0));
            if (cost > remaining) return Failure("wire_limit");
            remaining -= cost;
            rows.Add(new(entry.Offset.ToString(CultureInfo.InvariantCulture), type, provider, value.SessionId, run,
                value.Timestamp, kind, phase, contentId, activityId, parentId, interactionId, name, text, details,
                shortened, detailsShortened, omitted));
        }
        HistoryCursor? next = null;
        if (page.Next is { } cursor)
        {
            ValidateIdentity(cursor.SessionId, 256, required: true);
            next = new(1, cursor.SessionId, cursor.Length.ToString(CultureInfo.InvariantCulture),
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

    private static string? Json(JsonElement? value) => value?.GetRawText();

    private static string FormatPrompt(AgentSystemPromptEvent prompt)
    {
        var text = new StringBuilder();
        text.Append("**Reason:** ").AppendLine(prompt.Reason);
        text.Append("\n**Effective hash:** `").Append(prompt.EffectivePromptHash).AppendLine("`");
        if (prompt.AgentPromptUsage is { } usage)
        {
            text.Append("\n**Agent prompt:** ").Append(usage.DisplayName ?? usage.PromptName);
            if (!string.IsNullOrWhiteSpace(usage.SourcePath)) text.Append(" (`").Append(usage.SourcePath).Append("`)");
            text.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(prompt.AgentPromptId)) text.Append("\n**Agent prompt:** ").AppendLine(prompt.AgentPromptId);
        text.Append("\n**Provider mapping:** ").Append(prompt.ProviderPayloadSummary.ChannelMapping)
            .Append(prompt.ProviderPayloadSummary.AppliedToProvider ? " · applied" : " · not applied")
            .AppendLine(prompt.ProviderPayloadSummary.Lossy ? " · lossy" : "");
        text.Append("\n**Approximate tokens:** ").Append(prompt.Statistics.TotalApproxTokens)
            .Append(" total (").Append(prompt.Statistics.SystemApproxTokens).Append(" system, ")
            .Append(prompt.Statistics.DeveloperApproxTokens).AppendLine(" developer)");
        text.Append("\n**Change:** ").AppendLine(prompt.Change.Kind);
        AppendList(text, "Added", prompt.Change.AddedParts);
        AppendList(text, "Changed", prompt.Change.ChangedParts);
        AppendList(text, "Removed", prompt.Change.RemovedParts);
        AppendSection(text, "System message", prompt.SystemMessage);
        AppendSection(text, "Developer instructions", prompt.DeveloperInstructions);
        return text.ToString().TrimEnd();
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
            if (usage.LastOperation is { } operation)
            {
                if (!string.IsNullOrWhiteSpace(operation.Model)) text.Append("\n**Model:** ").AppendLine(operation.Model);
                AppendMetric(text, "Input tokens", operation.InputTokens);
                AppendMetric(text, "Output tokens", operation.OutputTokens);
                AppendMetric(text, "Cached input tokens", operation.CachedInputTokens);
                AppendMetric(text, "Reasoning tokens", operation.ReasoningTokens);
                if (operation.Cost is { } cost) text.Append("\n**Cost:** ").AppendLine(cost.ToString(CultureInfo.InvariantCulture));
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

    private static void AppendList(StringBuilder text, string label, IReadOnlyList<string> values)
    {
        if (values.Count > 0) text.Append("\n**").Append(label).Append(":** ").AppendLine(string.Join(", ", values));
    }

    private static void AppendMetric(StringBuilder text, string label, long? value)
    {
        if (value is not null) text.Append("\n**").Append(label).Append(":** ").AppendLine(value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static HistoryResponse Failure(string code) => new(code, [], null, false);
}

internal sealed record HistoryRequest(string SessionId, HistoryCursor? Cursor);
internal sealed record HistoryCursor(int Version, string SessionId, string Length, string LastWriteUtcTicks, string Offset);
internal sealed record HistoryResponse(string Status, HistoryEntry[] Entries, HistoryCursor? Next, bool TailOmitted);
internal sealed record HistoryEntry(string Offset, string EventType, string ProviderId, string SessionId, string? RunId,
    DateTimeOffset Timestamp, string? Kind, string? Phase, string? ContentId, string? ActivityId, string? ParentActivityId,
    string? InteractionId, string? Name, string? Text, string? Details, bool TextTruncated, bool DetailsTruncated, bool BodyOmitted);
