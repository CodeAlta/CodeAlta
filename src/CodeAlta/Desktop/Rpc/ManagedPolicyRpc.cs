using CodeAlta.Agent;
using CodeAlta.Hosting;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The policy of the organization that applies to every session of this computer (<see cref="ManagedPolicy"/>), for the
/// pages that show what it locks: the permission modes it turns off, the sandbox it requires, where it comes from and
/// where to ask. The reading of the managed settings of the Copilot accounts can be started again from here.
/// </summary>
/// <remarks>Failures cross as status codes, never as the text of an exception.</remarks>
[NeoRpcService("managedPolicy", Version = 1)]
internal sealed class ManagedPolicyService
{
    private const int MaximumKeys = 32;
    private readonly ManagedPolicyState? _state;
    private readonly CopilotManagedPolicySource? _copilot;
    private readonly string? _epoch;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal ManagedPolicyService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="state">The policies of the host.</param>
    /// <param name="copilot">The reading of the Copilot accounts, or null when the host does not read them.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="epoch"/> is blank.</exception>
    internal ManagedPolicyService(ManagedPolicyState state, CopilotManagedPolicySource? copilot, string epoch)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _state = state;
        _copilot = copilot;
        _epoch = epoch;
    }

    /// <summary>Reads the policy that applies now, and where the reading of the Copilot accounts stands.</summary>
    [NeoRpcMethod("read")]
    public ManagedPolicyResponse Read(ManagedPolicyRequest request)
        => Refused(request?.ExpectedEpoch) is { } code ? Empty(code) : Project(_state!.Current, _copilot?.Status);

    /// <summary>Reads the managed settings of the Copilot accounts again, then the policy that applies.</summary>
    [NeoRpcMethod("refresh")]
    public async Task<ManagedPolicyResponse> RefreshAsync(ManagedPolicyRequest request, CancellationToken cancellationToken)
    {
        if (Refused(request?.ExpectedEpoch) is { } code) return Empty(code);
        var status = _copilot is null ? null : await _copilot.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return Project(_state!.Current, status);
    }

    internal static ManagedPolicyResponse Project(ManagedPolicy policy, CopilotManagedPolicyStatus? copilot)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return new("ok", !policy.IsEmpty, policy.Source, policy.Account, policy.BypassPermissionsDisabled, policy.AssistedPermissionsDisabled,
            policy.DefaultPermissionMode, policy.SandboxRequired, policy.RunOutsideSandboxDisallowed, policy.FreshPolicyMissing,
            Uri.TryCreate(policy.SupportUrl, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" ? url.AbsoluteUri : null,
            [.. policy.ManagedKeys.Where(static key => key.Length is > 0 and <= 128).Take(MaximumKeys)], policy.ReadAt,
            copilot is null ? null : new(copilot.State, copilot.Message, Kilobytes(copilot.Received), Kilobytes(copilot.Total), copilot.ReadAt));
    }

    // The size of a download crosses in kilobytes: a whole number the page divides.
    private static int? Kilobytes(long? bytes) => bytes is { } value && value >= 0 ? (int)Math.Min(value / 1024, int.MaxValue) : null;

    private string? Refused(string? expectedEpoch)
        => _state is null ? "unavailable" : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;

    private static ManagedPolicyResponse Empty(string status)
        => new(status, false, null, null, false, false, null, false, false, false, null, [], null, null);
}

/// <summary>Asks for the policy of the organization of the host of an epoch.</summary>
internal sealed record ManagedPolicyRequest(string? ExpectedEpoch);

/// <summary>
/// The policy of the organization that applies to every session. <c>Managed</c> is false when no policy narrows
/// anything. <c>SupportUrl</c> is an <c>http</c> or <c>https</c> page, to open in the browser. <c>Copilot</c> says where the
/// reading of the managed settings of the Copilot accounts stands: <c>none</c> (no Copilot provider), <c>installing</c>,
/// <c>reading</c>, <c>ready</c> or <c>failed</c>; null when the host does not read them.
/// </summary>
internal sealed record ManagedPolicyResponse(string Status, bool Managed, string? Source, string? Account, bool BypassPermissionsDisabled,
    bool AssistedPermissionsDisabled, string? DefaultPermissionMode, bool SandboxRequired, bool RunOutsideSandboxDisallowed, bool FreshPolicyMissing,
    string? SupportUrl, IReadOnlyList<string> ManagedKeys, DateTimeOffset? ReadAt, ManagedPolicyReading? Copilot);

/// <summary>Where the reading of the managed settings of the Copilot accounts stands, with the progress of the download of its runtime, in kilobytes.</summary>
internal sealed record ManagedPolicyReading(string State, string? Message, int? ReceivedKilobytes, int? TotalKilobytes, DateTimeOffset? ReadAt);
