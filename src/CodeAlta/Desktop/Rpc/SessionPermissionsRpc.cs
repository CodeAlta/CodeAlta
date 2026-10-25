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

    /// <summary>
    /// Gets what reads the <c>SKILL.md</c> of a skill of an MCP server a session asks to load (session, server, URI),
    /// for the user to read before answering; null where the host loads no skill of a server.
    /// </summary>
    internal Func<string, string, string, CancellationToken, Task<(string? Text, bool Verified, string? Error)>>? ReadSkill { get; init; }

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
                    "commandExecution" => Text(value.Command, 4096, true) && Text(value.WorkingDirectory, 1024, true) && value.GrantRoot is null && value.Skill is null,
                    "fileChange" => Text(value.GrantRoot, 1024, true) && value.Command is null && value.WorkingDirectory is null && value.Skill is null,
                    "skill" => value.Skill is { } skill && Identity(skill.Server) && Identity(skill.Name) && Text(skill.Uri, 1024, true)
                        && skill.FileCount >= 0 && skill.TotalBytes is >= 0 and <= int.MaxValue && value.Command is null && value.WorkingDirectory is null && value.GrantRoot is null,
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
                {
                    Shortened = summary.Shortened,
                    OutsideSandbox = summary.OutsideSandbox,
                    Skill = summary.Skill is { } skillSummary
                        ? new(skillSummary.Server, skillSummary.Name, skillSummary.Uri, skillSummary.FileCount, (int)skillSummary.TotalBytes, skillSummary.Dynamic)
                        : null,
                });
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
            || request.Decision is not ("allow_once" or "allow_always" or "deny" or "cancel")) return new("invalid_request", _epoch, null);
        if (request.ExpectedHostEpoch != _epoch) return new("stale_epoch", _epoch, request.Handle);
        if (!_enabled()) return new("disabled", _epoch, request.Handle);
        cancellationToken.ThrowIfCancellationRequested();
        var decision = request.Decision switch
        {
            "allow_once" => AgentPermissionDecisionKind.AllowOnce,
            // Only the load of a skill whose files can be checked accepts it: the owner refuses it for anything else.
            "allow_always" => AgentPermissionDecisionKind.AllowAlways,
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

    /// <summary>
    /// Reads the <c>SKILL.md</c> of the skill a request that waits asks to load, checked against its entry: only for a
    /// request of kind <c>skill</c> of the session, which names the server and the URI. Nothing is loaded or approved.
    /// </summary>
    [NeoRpcMethod("skillContent", TimeoutMilliseconds = 120_000)]
    public async Task<SessionPermissionSkillContent> SkillContentAsync(SessionPermissionSkillContentRequest request, CancellationToken cancellationToken)
    {
        SessionPermissionSkillContent Error(string status, string? message = null) => new(status, _epoch, null, false, message);
        if (request is null || !TryHandle(request.Handle, out var handle)) return Error("invalid_request");
        if (request.ExpectedHostEpoch != _epoch) return Error("stale_epoch");
        if (ReadSkill is not { } read) return Error("unavailable");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var page = await _list(handle!.Attempt.SessionId, cancellationToken).ConfigureAwait(false);
            var skill = page?.Entries?.FirstOrDefault(entry => entry?.Handle == handle && entry.Request?.Kind == "skill")?.Request.Skill;
            if (skill is null) return Error("not_found");
            var (text, verified, error) = await read(handle.Attempt.SessionId, skill.Server, skill.Uri, cancellationToken).ConfigureAwait(false);
            if (text is null) return Error("read_failed", error is null ? null : Bound(new string([.. error.Where(static character => !char.IsControl(character))]), 512));
            return new("ok", _epoch, text.Length > MaximumSkillChars ? text[..(char.IsHighSurrogate(text[MaximumSkillChars - 1]) ? MaximumSkillChars - 1 : MaximumSkillChars)] : text, verified, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Error("read_failed"); }
    }

    // The most characters of a `SKILL.md` the card shows.
    private const int MaximumSkillChars = 64 * 1024;

    private static string Bound(string value, int limit) => value.Length <= limit ? value : value[..limit];

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
/// <paramref name="GrantRoot"/>; a <c>fileChange</c> carries only <paramref name="GrantRoot"/>; a <c>skill</c> carries only
/// <see cref="Skill"/>, the skill of an MCP server it asks to load.
/// </summary>
internal sealed record SessionPermissionCommand(SessionPermissionCommandHandle Handle, string ProviderId, string Kind,
    string? Command, string? WorkingDirectory, string? GrantRoot, string? Reason)
{
    /// <summary>True when the command or the reason is cut: what is allowed is longer than what is shown.</summary>
    public bool Shortened { get; init; }

    /// <summary>True when a sandboxed session asks to run the command outside its sandbox, with the rights of the user.</summary>
    public bool OutsideSandbox { get; init; }

    /// <summary>The skill of an MCP server a <c>skill</c> request asks to load; null for another kind.</summary>
    public SessionPermissionSkillEntry? Skill { get; init; }
}
/// <summary>The skill of an MCP server a permission asks to load: <paramref name="Dynamic"/> ones are allowed once only.</summary>
internal sealed record SessionPermissionSkillEntry(string Server, string Name, string Uri, int FileCount, int TotalBytes, bool Dynamic);
internal sealed record SessionPermissionsPage(string Status, string HostEpoch, string? SessionId, SessionPermissionCommand[] Entries, bool HasMore);
internal sealed record SessionPermissionResolveRequest(string ExpectedHostEpoch, SessionPermissionCommandHandle Handle, string Decision);
internal sealed record SessionPermissionResolution(string Status, string HostEpoch, SessionPermissionCommandHandle? Handle);
internal sealed record SessionPermissionSkillContentRequest(string ExpectedHostEpoch, SessionPermissionCommandHandle Handle);
/// <summary>The <c>SKILL.md</c> of the skill a request asks to load, cut at 64 Ki characters; <c>Verified</c> when checked against its entry.</summary>
internal sealed record SessionPermissionSkillContent(string Status, string HostEpoch, string? Content, bool Verified, string? Message);
