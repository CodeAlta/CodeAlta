using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("sessionUserInput", Version = 1)]
internal sealed class SessionUserInputService
{
    internal const int MaximumPageBytes = 256 * 1024, MaximumResolveBytes = 96 * 1024;
    private readonly Func<string, ValueTask<SessionOwnedUserInputPage>> _list;
    private readonly Func<SessionOwnedUserInputHandle, IReadOnlyList<SessionOwnedUserInputAnswer>, ValueTask<bool>> _resolve;
    private readonly Func<SessionOwnedUserInputHandle, ValueTask<bool>> _cancel;
    private readonly string _epoch;
    private readonly bool _enabled;

    internal SessionUserInputService(SessionPermissionService owner, string epoch, bool enabled)
        : this(id => owner.ListOwnedUserInputsAsync(id, default), (handle, answers) => owner.ResolveOwnedUserInputAsync(handle, answers, default),
            handle => owner.CancelOwnedUserInputAsync(handle, default), epoch, enabled) { }

    internal SessionUserInputService(Func<string, ValueTask<SessionOwnedUserInputPage>> list,
        Func<SessionOwnedUserInputHandle, IReadOnlyList<SessionOwnedUserInputAnswer>, ValueTask<bool>> resolve,
        Func<SessionOwnedUserInputHandle, ValueTask<bool>> cancel, string epoch, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(list); ArgumentNullException.ThrowIfNull(resolve); ArgumentNullException.ThrowIfNull(cancel);
        if (!GuidValue(epoch, out _)) throw new ArgumentException("Canonical epoch required.", nameof(epoch));
        _list = list; _resolve = resolve; _cancel = cancel; _epoch = epoch; _enabled = enabled;
    }

    [NeoRpcMethod("list")]
    public async Task<UserInputPage> ListAsync(UserInputListRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !Identity(request.SessionId) || !GuidValue(request.ExpectedHostEpoch, out _)) return new("invalid_request", _epoch, null, [], false);
        UserInputPage Error(string status) => new(status, _epoch, request.SessionId, [], false);
        if (request.ExpectedHostEpoch != _epoch) return Error("stale_epoch");
        if (!_enabled) return Error("disabled");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var page = await _list(request.SessionId).ConfigureAwait(false);
            if (page?.Entries is not { } entries) return Error("wire_limit");
            var count = entries.Count;
            if (count is < 0 or > 4) return Error("wire_limit");
            var result = new UserInputEntry[count]; var ids = new HashSet<Guid>();
            for (var i = 0; i < result.Length; i++)
            {
                var entry = entries[i];
                if (entry?.Handle is not { } handle || handle.SessionId != request.SessionId || !Identity(entry.ProviderId)
                    || !ids.Add(handle.AttemptId)) return Error("wire_limit");
                var wire = Wire(handle); var form = OwnedUserInputValidation.Snapshot(entry.Form);
                if (!TryHandle(wire, out _) || form is null) return Error("wire_limit");
                result[i] = new(wire, entry.ProviderId, form.Prompts.Select(p => new UserInputPrompt(p.Id, p.Question, p.Header,
                    p.Options!.Select(o => new UserInputOption(o.Label, o.Description)).ToArray(), p.AllowFreeform)).ToArray());
            }
            return new("ok", _epoch, request.SessionId, result, page.HasMore);
        }
        catch (Exception) { return Error("read_failed"); }
    }

    [NeoRpcMethod("resolve")]
    public Task<UserInputResult> ResolveAsync(UserInputResolveRequest request, CancellationToken cancellationToken)
        => DecideAsync(request?.ExpectedHostEpoch, request?.Handle, request?.Answers, false, cancellationToken);

    [NeoRpcMethod("cancel")]
    public Task<UserInputResult> CancelAsync(UserInputCancelRequest request, CancellationToken cancellationToken)
        => DecideAsync(request?.ExpectedHostEpoch, request?.Handle, null, true, cancellationToken);

    private async Task<UserInputResult> DecideAsync(string? epoch, UserInputHandle? wire, UserInputAnswer[]? answers, bool cancel, CancellationToken token)
    {
        if (!GuidValue(epoch, out _) || !TryHandle(wire, out var handle) || (!cancel && !ValidAnswers(answers))) return new("invalid_request", _epoch, null);
        if (epoch != _epoch) return new("stale_epoch", _epoch, wire);
        if (!_enabled) return new("disabled", _epoch, wire);
        token.ThrowIfCancellationRequested();
        // Admission samples transport cancellation only before launch. Never abandon an owner decision or
        // reinterpret a post-admission exception as non-commit. This is not provider/tool/history success.
        try
        {
            var accepted = cancel ? await _cancel(handle!).ConfigureAwait(false)
                : await _resolve(handle!, Array.AsReadOnly(answers!.Select(a => new SessionOwnedUserInputAnswer(a.PromptId, a.Value)).ToArray())).ConfigureAwait(false);
            return new(accepted ? (cancel ? "cancelled" : "resolved") : "rejected", _epoch, wire);
        }
        catch (Exception) { return new("uncertain", _epoch, wire); }
    }

    internal static bool ValidAnswers(UserInputAnswer[]? answers)
    {
        if (answers is not { Length: >= 1 and <= 8 }) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal); var total = 0;
        foreach (var a in answers)
        {
            if (a is null || !Identity(a.PromptId) || !ids.Add(a.PromptId) || !OwnedUserInputValidation.Text(a.Value, 2048)) return false;
            total += a.Value.Length; if (total > 8192) return false;
        }
        return true;
    }

    internal static bool TryHandle(UserInputHandle? value, out SessionOwnedUserInputHandle? handle)
    {
        handle = null;
        if (value is null || !GuidValue(value.OperationId, out var operation) || !GuidValue(value.RuntimeInstanceId, out var runtime)
            || !GuidValue(value.AttemptId, out var attempt) || !Identity(value.SessionId) || !Identity(value.InteractionId)
            || (value.RunId is not null && !Identity(value.RunId)) || value.AttachmentGeneration is not { Length: >= 1 and <= 16 }
            || !long.TryParse(value.AttachmentGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation is <= 0 or > 9007199254740991 || generation.ToString(CultureInfo.InvariantCulture) != value.AttachmentGeneration) return false;
        handle = new(operation, runtime, generation, value.SessionId, value.RunId, value.InteractionId, attempt); return true;
    }
    internal static UserInputHandle Wire(SessionOwnedUserInputHandle value) => new(value.OperationId.ToString("D"), value.RuntimeInstanceId.ToString("D"),
        value.AttachmentGeneration.ToString(CultureInfo.InvariantCulture), value.SessionId, value.RunId, value.InteractionId, value.AttemptId.ToString("D"));
    private static bool GuidValue(string? value, out Guid guid) => Guid.TryParseExact(value, "D", out guid) && guid != Guid.Empty && guid.ToString("D") == value;
    private static bool Identity(string? value) => OwnedUserInputValidation.Text(value, 128, identity: true);
}

internal sealed record UserInputListRequest(string ExpectedHostEpoch, string SessionId);
internal sealed record UserInputHandle(string OperationId, string RuntimeInstanceId, string AttachmentGeneration, string SessionId, string? RunId, string InteractionId, string AttemptId);
internal sealed record UserInputOption(string Label, string? Description);
internal sealed record UserInputPrompt(string Id, string Question, string? Header, UserInputOption[] Options, bool AllowFreeform);
internal sealed record UserInputEntry(UserInputHandle Handle, string ProviderId, UserInputPrompt[] Prompts);
internal sealed record UserInputPage(string Status, string HostEpoch, string? SessionId, UserInputEntry[] Entries, bool HasMore);
internal sealed record UserInputAnswer(string PromptId, string Value);
internal sealed record UserInputResolveRequest(string ExpectedHostEpoch, UserInputHandle Handle, UserInputAnswer[] Answers);
internal sealed record UserInputCancelRequest(string ExpectedHostEpoch, UserInputHandle Handle);
internal sealed record UserInputResult(string Status, string HostEpoch, UserInputHandle? Handle);
