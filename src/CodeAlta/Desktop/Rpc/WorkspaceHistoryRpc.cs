using System.Globalization;
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
            string? kind = null, phase = null, contentId = null, activityId = null, parentId = null, text = null, name = null;
            var omitted = false;
            switch (value)
            {
                case AgentContentDeltaEvent delta:
                    type = "contentDelta"; kind = delta.Kind.ToString(); contentId = delta.ContentId;
                    parentId = delta.ParentActivityId; text = delta.Delta; omitted = delta.Details is not null;
                    break;
                case AgentContentCompletedEvent completed:
                    type = "contentCompleted"; kind = completed.Kind.ToString(); contentId = completed.ContentId;
                    parentId = completed.ParentActivityId; text = completed.Content;
                    omitted = completed.Details is not null || completed.AskId is not null;
                    break;
                case AgentActivityEvent activity:
                    type = "activity"; kind = activity.Kind.ToString(); phase = activity.Phase.ToString();
                    activityId = activity.ActivityId; parentId = activity.ParentActivityId; name = activity.Name;
                    text = activity.Message; omitted = activity.Details is not null;
                    break;
                case AgentNotesEvent notes: type = "notes"; kind = notes.Kind.ToString(); text = notes.Markdown; break;
                case AgentErrorEvent error: type = "error"; text = error.Message; omitted = error.ExceptionInfo is not null; break;
                case AgentRawEvent: type = "raw"; omitted = true; break;
                case AgentSystemPromptEvent: type = "system_prompt"; omitted = true; break;
                case AgentSessionUpdateEvent: type = "sessionUpdate"; omitted = true; break;
                case AgentPlanSnapshotEvent: type = "planSnapshot"; omitted = true; break;
                case AgentInteractionEvent: type = "interaction"; omitted = true; break;
                case AgentGenericPermissionRequest: type = "permissionGeneric"; omitted = true; break;
                case AgentCommandPermissionRequest: type = "permissionCommand"; omitted = true; break;
                case AgentFileChangePermissionRequest: type = "permissionFileChange"; omitted = true; break;
                case AgentUserInputRequest: type = "userInputRequest"; omitted = true; break;
                default: return Failure("unsupported_format");
            }
            var provider = value.ProviderId.Value;
            var run = value.RunId?.Value;
            ValidateIdentity(provider, 256, required: true);
            ValidateIdentity(value.SessionId, 256, required: true);
            var identityCost = 0;
            foreach (var id in new[] { provider, value.SessionId, run, kind, phase, contentId, activityId, parentId })
            {
                ValidateIdentity(id, 256, required: false);
                identityCost += (id?.Length ?? 0) * 6;
            }
            var shortened = false;
            name = Preview(name, 256, ref shortened);
            text = Preview(text, 512, ref shortened);
            var cost = 1024 + identityCost + 6 * ((text?.Length ?? 0) + (name?.Length ?? 0));
            if (cost > remaining) return Failure("wire_limit");
            remaining -= cost;
            rows.Add(new(entry.Offset.ToString(CultureInfo.InvariantCulture), type, provider, value.SessionId, run,
                value.Timestamp, kind, phase, contentId, activityId, parentId, name, text, shortened, omitted));
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
        return value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
    }

    private static HistoryResponse Failure(string code) => new(code, [], null, false);
}

internal sealed record HistoryRequest(string SessionId, HistoryCursor? Cursor);
internal sealed record HistoryCursor(int Version, string SessionId, string Length, string LastWriteUtcTicks, string Offset);
internal sealed record HistoryResponse(string Status, HistoryEntry[] Entries, HistoryCursor? Next, bool TailOmitted);
internal sealed record HistoryEntry(string Offset, string EventType, string ProviderId, string SessionId, string? RunId,
    DateTimeOffset Timestamp, string? Kind, string? Phase, string? ContentId, string? ActivityId, string? ParentActivityId,
    string? Name, string? Text, bool TextTruncated, bool BodyOmitted);
