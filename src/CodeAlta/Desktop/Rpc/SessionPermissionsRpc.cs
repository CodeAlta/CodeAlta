using System.Globalization;
using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// No trusted ListAsync/ResolveAsync fallback: renderer authority is always the owned mailbox binding.
[NeoRpcService("sessionPermissions", Version = 1)]
internal sealed class SessionPermissionsService
{
    internal const int MaximumResponseBytes = 192 * 1024;
    private readonly Func<string, CancellationToken, ValueTask<SessionOwnedPermissionPage>> _list;
    private readonly Func<SessionOwnedPermissionHandle, AgentPermissionDecisionKind, CancellationToken, ValueTask<bool>> _resolve;
    private readonly string _epoch;
    // Read at every call: the user turns the review on and off while the host runs.
    private readonly Func<bool> _enabled;

    internal SessionPermissionsService(SessionPermissionService permissions, string epoch, Func<bool> enabled)
        : this(permissions.ListOwnedCommandsAsync, permissions.ResolveOwnedCommandAsync, epoch, enabled) { }

    // Mandatory inert transport-test adapters; never constructs a host, provider or native surface.
    internal SessionPermissionsService(Func<string, CancellationToken, ValueTask<SessionOwnedPermissionPage>> list,
        Func<SessionOwnedPermissionHandle, AgentPermissionDecisionKind, CancellationToken, ValueTask<bool>> resolve,
        string epoch, Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(enabled);
        if (!GuidValue(epoch, out _)) throw new ArgumentException("A canonical host epoch is required.", nameof(epoch));
        _list = list;
        _resolve = resolve;
        _epoch = epoch;
        _enabled = enabled;
    }

    [NeoRpcMethod("list")]
    public async Task<SessionPermissionsPage> ListAsync(SessionPermissionsRequest request, CancellationToken cancellationToken)
    {
        SessionPermissionsPage Error(string status) => new(status, _epoch, request?.SessionId, [], false);
        if (request is null || !Identity(request.SessionId)) return new("invalid_request", _epoch, null, [], false);
        if (request.ExpectedHostEpoch != _epoch) return Error("stale_epoch");
        if (!_enabled()) return Error("disabled");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var page = await _list(request.SessionId, cancellationToken).ConfigureAwait(false);
            if (page?.Entries is null || page.Entries.Count > 4) return Error("wire_limit");
            var entries = new List<SessionPermissionCommand>(page.Entries.Count);
            var attempts = new HashSet<Guid>();
            foreach (var entry in page.Entries)
            {
                // Each kind is held to its own complete shape: what the other kind carries must be absent, so a
                // half-filled request is refused rather than shown with a blank command or a blank folder.
                var shaped = entry?.Request is { } value && value.Kind switch
                {
                    "commandExecution" => Text(value.Command, 4096, true) && Text(value.WorkingDirectory, 1024, true) && value.GrantRoot is null,
                    "fileChange" => Text(value.GrantRoot, 1024, true) && value.Command is null && value.WorkingDirectory is null,
                    _ => false,
                };
                if (entry?.Handle is not { } handle || entry.Request is not { } summary || handle.Attempt is null
                    || handle.Attempt != summary.Handle || handle.Attempt.SessionId != request.SessionId
                    || !shaped || !Identity(summary.ProviderId.Value) || !Text(summary.Reason, 1024, false)
                    || !attempts.Add(handle.Attempt.AttemptId)) return Error("wire_limit");
                var wireHandle = new SessionPermissionCommandHandle(handle.OperationId.ToString("D"), handle.RuntimeInstanceId.ToString("D"),
                    handle.AttachmentGeneration.ToString(CultureInfo.InvariantCulture), handle.Attempt.SessionId,
                    handle.Attempt.RunId, handle.Attempt.InteractionId, handle.Attempt.AttemptId.ToString("D"));
                if (!TryHandle(wireHandle, out _)) return Error("wire_limit");
                entries.Add(new(wireHandle, summary.ProviderId.Value, summary.Kind, summary.Command, summary.WorkingDirectory, summary.GrantRoot, summary.Reason)
                    { Shortened = summary.Shortened });
            }
            // Four complete commands, each <=6,144 text + 512 identity UTF-16 units. Worst-case six-byte
            // JSON escaping plus GUIDs/decimal identities/keys and 4 KiB framing fit in 192 KiB. Never truncate.
            return new("ok", _epoch, request.SessionId, entries.ToArray(), page.HasMore);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Error("read_failed"); }
    }

    [NeoRpcMethod("resolve")]
    public async Task<SessionPermissionResolution> ResolveAsync(SessionPermissionResolveRequest request, CancellationToken cancellationToken)
    {
        if (request is null || !TryHandle(request.Handle, out var handle)
            || request.Decision is not ("allow_once" or "deny" or "cancel")) return new("invalid_request", _epoch, null);
        if (request.ExpectedHostEpoch != _epoch) return new("stale_epoch", _epoch, request.Handle);
        if (!_enabled()) return new("disabled", _epoch, request.Handle);
        cancellationToken.ThrowIfCancellationRequested();
        var decision = request.Decision switch
        {
            "allow_once" => AgentPermissionDecisionKind.AllowOnce,
            "deny" => AgentPermissionDecisionKind.Deny,
            _ => AgentPermissionDecisionKind.Cancel,
        };
        try
        {
            var accepted = await _resolve(handle!, decision, cancellationToken).ConfigureAwait(false);
            return new(accepted ? "resolved" : "rejected", _epoch, request.Handle);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // Failure is not proof of non-commit. No automatic retry and no raw exception content.
        catch (Exception) { return new("uncertain", _epoch, request.Handle); }
    }

    private static bool TryHandle(SessionPermissionCommandHandle? value, out SessionOwnedPermissionHandle? handle)
    {
        handle = null;
        if (value is null || !GuidValue(value.OperationId, out var operation) || !GuidValue(value.RuntimeInstanceId, out var runtime)
            || !GuidValue(value.AttemptId, out var attempt) || !Identity(value.SessionId) || !Identity(value.InteractionId)
            || (value.RunId is not null && !Identity(value.RunId))
            || !long.TryParse(value.AttachmentGeneration, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation <= 0 || generation.ToString(CultureInfo.InvariantCulture) != value.AttachmentGeneration) return false;
        handle = new(operation, runtime, generation, new(value.SessionId, value.RunId, value.InteractionId, attempt));
        return true;
    }

    private static bool GuidValue(string? value, out Guid guid) => Guid.TryParseExact(value, "D", out guid)
        && guid != Guid.Empty && guid.ToString("D") == value;
    private static bool Identity(string? value) => value is not null && Text(value, 128, true) && value == value.Trim() && !value.Any(char.IsControl);
    private static bool Text(string? value, int limit, bool required)
    {
        if (value is null) return !required;
        if (value.Length > limit || (required && string.IsNullOrWhiteSpace(value))) return false;
        for (var i = 0; i < value.Length; i++)
            if (value[i] == '\0' || (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i == value.Length || !char.IsLowSurrogate(value[i])))) return false;
        return true;
    }
}

internal sealed record SessionPermissionsRequest(string ExpectedHostEpoch, string SessionId);
internal sealed record SessionPermissionCommandHandle(string OperationId, string RuntimeInstanceId, string AttachmentGeneration,
    string SessionId, string? RunId, string InteractionId, string AttemptId);
/// <summary>
/// One pending permission of a session. <paramref name="Kind"/> says which shape it has: a
/// <c>commandExecution</c> carries <paramref name="Command"/> and <paramref name="WorkingDirectory"/> and no
/// <paramref name="GrantRoot"/>; a <c>fileChange</c> carries only <paramref name="GrantRoot"/>.
/// </summary>
internal sealed record SessionPermissionCommand(SessionPermissionCommandHandle Handle, string ProviderId, string Kind,
    string? Command, string? WorkingDirectory, string? GrantRoot, string? Reason)
{
    /// <summary>True when the command or the reason is cut: what is allowed is longer than what is shown.</summary>
    public bool Shortened { get; init; }
}
internal sealed record SessionPermissionsPage(string Status, string HostEpoch, string? SessionId, SessionPermissionCommand[] Entries, bool HasMore);
internal sealed record SessionPermissionResolveRequest(string ExpectedHostEpoch, SessionPermissionCommandHandle Handle, string Decision);
internal sealed record SessionPermissionResolution(string Status, string HostEpoch, SessionPermissionCommandHandle? Handle);
