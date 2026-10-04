using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("sessionAsks", Version = 1)]
internal sealed class SessionAsksService(OwnedSessionAskService owner, string epoch)
{
    internal const int MaximumResponseBytes = 192 * 1024;
    private readonly string _epoch = CanonicalEpoch(epoch) ? epoch : throw new ArgumentException("Canonical host epoch required.", nameof(epoch));
    private int _closed;
    internal void CloseAdmission() => Interlocked.Exchange(ref _closed, 1);

    [NeoRpcMethod("list")]
    public Task<SessionAsksPage> ListAsync(SessionAsksRequest request, CancellationToken cancellationToken)
    {
        var error = Check(request?.ExpectedHostEpoch);
        var validSession = request is not null && OwnedSessionAskService.IsValidSessionId(request.SessionId);
        if (!validSession) error = "invalid_request";
        if (error is not null) return Task.FromResult(new SessionAsksPage(error, _epoch, validSession ? request!.SessionId : null, null, null, false));
        cancellationToken.ThrowIfCancellationRequested();
        var page = owner.List(request!.SessionId);
        // One <=8192-unit request (plus a file path of at most 1000 units), at most 12 questions/240
        // choices, two scalar envelopes and one scalar disposition. Generated-serializer
        // escaping/framing is independently tested.
        return Task.FromResult(new SessionAsksPage("ok", _epoch, request.SessionId, ToWire(page.Head), ToWire(page.Latest), page.HasMore));
    }

    [NeoRpcMethod("answer")]
    public Task<SessionAskResult> AnswerAsync(SessionAskActionRequest request, CancellationToken cancellationToken)
        => ActAsync(request, false, cancellationToken);

    [NeoRpcMethod("cancel")]
    public Task<SessionAskResult> CancelAsync(SessionAskActionRequest request, CancellationToken cancellationToken)
        => ActAsync(request, true, cancellationToken);

    [NeoRpcMethod("observe")]
    public Task<SessionAskResult> ObserveAsync(SessionAskObservationRequest request, CancellationToken cancellationToken)
    {
        var error = Check(request?.ExpectedHostEpoch);
        if (request is null || request.ActionId == Guid.Empty || !TryHandle(request.Handle, out var handle))
            return Task.FromResult(new SessionAskResult("invalid_request", _epoch, null));
        if (error is not null) return Task.FromResult(new SessionAskResult(error, _epoch, null));
        cancellationToken.ThrowIfCancellationRequested();
        var value = owner.Observe(request.ActionId, handle);
        return Task.FromResult(new SessionAskResult(value is null ? "not_found" : "ok", _epoch, ToWire(value)));
    }

    private async Task<SessionAskResult> ActAsync(SessionAskActionRequest request, bool cancel, CancellationToken token)
    {
        var error = Check(request?.ExpectedHostEpoch);
        if (request?.Action is not { } action || !TryHandle(action.Handle, out var handle))
            return new("invalid_request", _epoch, null);
        if (error is not null) return new(error, _epoch, null);
        token.ThrowIfCancellationRequested();
        try
        {
            // Owner retains the original before return. Only this transport wait is cancellable.
            // Decimal parsing supplies only scalar context. The owner still recovers its exact queue handle.
            var domain = new OwnedAskAction(action.ActionId, handle, action.Answers, action.FileReview);
            var original = cancel ? owner.CancelAsync(domain, token) : owner.AnswerAsync(domain, token);
            var result = await original.WaitAsync(token).ConfigureAwait(false);
            return new("ok", _epoch, ToWire(result));
        }
        catch (ArgumentException) { return new("invalid_request", _epoch, null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return new("uncertain", _epoch, null); }
    }

    private string? Check(string? expected) => expected != _epoch ? "stale_epoch" : Volatile.Read(ref _closed) != 0 ? "closed" : !owner.Enabled ? "disabled" : null;
    private static bool CanonicalEpoch(string value) => Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty && parsed.ToString("D") == value;

    internal static SessionAskHandle ToWire(OwnedAskHandle handle) => new(handle.OperationId, handle.RuntimeInstanceId,
        handle.AttachmentGeneration.ToString(CultureInfo.InvariantCulture), handle.ProviderId, handle.SessionId, handle.RunId,
        handle.AskId, handle.ResponseGeneration.ToString(CultureInfo.InvariantCulture));

    internal static SessionAskHead? ToWire(OwnedAskHead? head)
        => head is null ? null : new(ToWire(head.Handle), head.Request, head.State);

    internal static SessionAskDisposition? ToWire(OwnedAskDisposition? disposition)
        => disposition is null ? null : new(disposition.ActionId, ToWire(disposition.Handle), disposition.Status, disposition.RunId);

    internal static bool TryHandle(SessionAskHandle? value, [NotNullWhen(true)] out OwnedAskHandle? handle)
    {
        handle = null;
        if (value is null || !Generation(value.AttachmentGeneration, 1, 9007199254740991, out var attachment)
            || !Generation(value.ResponseGeneration, 0, 256, out var response)) return false;
        var parsed = new OwnedAskHandle(value.OperationId, value.RuntimeInstanceId, attachment, value.ProviderId,
            value.SessionId, value.RunId, value.AskId, response);
        if (!OwnedSessionAskService.IsValidHandle(parsed)) return false;
        handle = parsed;
        return true;
    }

    private static bool Generation(string? value, long minimum, long maximum, out long generation)
    {
        generation = 0;
        return value is { Length: > 0 and <= 16 }
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out generation)
            && generation >= minimum && generation <= maximum
            && generation.ToString(CultureInfo.InvariantCulture) == value;
    }
}

// Desktop wire DTOs deliberately do not expose domain Int64 properties to the RPC generator.
internal sealed record SessionAskHandle(Guid OperationId, Guid RuntimeInstanceId, string AttachmentGeneration,
    string ProviderId, string SessionId, string RunId, string AskId, string ResponseGeneration);
internal sealed record SessionAskHead(SessionAskHandle Handle, AltaAskRequest Request, string State);
internal sealed record SessionAskDisposition(Guid ActionId, SessionAskHandle Handle, string Status, string? RunId);
/// <summary>An answer or a cancel of the pending ask. <paramref name="FileReview"/> is for an ask with a file to review.</summary>
internal sealed record SessionAskAction(Guid ActionId, SessionAskHandle Handle, IReadOnlyList<AltaAskAnswer> Answers, AltaAskFileReview? FileReview = null);
internal sealed record SessionAsksRequest(string ExpectedHostEpoch, string SessionId);
internal sealed record SessionAsksPage(string Status, string HostEpoch, string? SessionId, SessionAskHead? Head, SessionAskDisposition? Latest, bool HasMore);
internal sealed record SessionAskActionRequest(string ExpectedHostEpoch, SessionAskAction Action);
internal sealed record SessionAskObservationRequest(string ExpectedHostEpoch, Guid ActionId, SessionAskHandle Handle);
internal sealed record SessionAskResult(string Status, string HostEpoch, SessionAskDisposition? Disposition);
