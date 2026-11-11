using System.Collections.Frozen;

namespace CodeAlta.Agent;

/// <summary>
/// What an organization requires of the sessions of this computer, as a source outside CodeAlta gives it: the managed
/// settings of a GitHub Copilot enterprise, for example. A policy only narrows what a session may do: it refuses
/// permission modes, requires the sandbox, and never allows more than the settings of the user do. It applies to every
/// session of the host, whatever its provider. <see cref="None"/> says nothing.
/// </summary>
public sealed record ManagedPolicy
{
    // The permission modes a policy knows: those of the host and of every provider (the ids of the Claude Code CLI).
    private const string Ask = "default";
    private const string AcceptEdits = "acceptEdits";
    private const string Assisted = "auto";
    private const string Bypass = "bypassPermissions";
    private static readonly FrozenSet<string> HostModes = FrozenSet.Create(StringComparer.Ordinal, Ask, AcceptEdits, Bypass);

    /// <summary>Gets the policy that says nothing.</summary>
    public static ManagedPolicy None { get; } = new();

    /// <summary>Gets what gives the policy, for people, such as <c>GitHub Copilot</c>; null for none.</summary>
    public string? Source { get; init; }

    /// <summary>Gets the account the policy was read for, for people; null when it is the computer's own.</summary>
    public string? Account { get; init; }

    /// <summary>Gets whether a session may not run in a mode that asks nothing (<c>bypassPermissions</c>).</summary>
    public bool BypassPermissionsDisabled { get; init; }

    /// <summary>
    /// Gets whether a session may not run in a mode where the provider decides by itself what it asks, Claude Code's
    /// <c>auto</c>, the nearest to the assisted mode of Copilot.
    /// </summary>
    public bool AssistedPermissionsDisabled { get; init; }

    /// <summary>Gets the mode a new session runs in when it is given none, <c>default</c>, <c>acceptEdits</c> or <c>bypassPermissions</c>; null to leave it to the settings.</summary>
    public string? DefaultPermissionMode { get; init; }

    /// <summary>Gets whether every session runs in the sandbox, where the sandbox can run on this computer.</summary>
    public bool SandboxRequired { get; init; }

    /// <summary>
    /// Gets whether, with <see cref="SandboxRequired"/>, a computer where the sandbox cannot run runs no command of any
    /// session, rather than run them without it.
    /// </summary>
    public bool SandboxUnavailableBlocks { get; init; }

    /// <summary>Gets whether no command of a sandboxed session may run outside its sandbox.</summary>
    public bool RunOutsideSandboxDisallowed { get; init; }

    /// <summary>
    /// Gets whether the source requires a policy read since the start of CodeAlta and none could be read: no session
    /// may send a prompt until one is.
    /// </summary>
    public bool FreshPolicyMissing { get; init; }

    /// <summary>Gets the page where the organization says who to ask; null for none.</summary>
    public string? SupportUrl { get; init; }

    /// <summary>Gets the names of the settings the source manages, as it names them, for people.</summary>
    public IReadOnlyList<string> ManagedKeys { get; init; } = [];

    /// <summary>Gets when the policy was read; null when it never was.</summary>
    public DateTimeOffset? ReadAt { get; init; }

    /// <summary>Gets whether the policy narrows nothing that CodeAlta enforces.</summary>
    public bool IsEmpty => !BypassPermissionsDisabled && !AssistedPermissionsDisabled && DefaultPermissionMode is null && !SandboxRequired
        && !RunOutsideSandboxDisallowed && !FreshPolicyMissing;

    /// <summary>Gets whether the policy refuses a permission mode.</summary>
    /// <param name="permissionMode">A mode, or null for none.</param>
    /// <returns>True for a mode a session may not run in.</returns>
    public bool Refuses(string? permissionMode)
        => permissionMode?.Trim() switch
        {
            Bypass => BypassPermissionsDisabled,
            Assisted => AssistedPermissionsDisabled,
            _ => false,
        };

    /// <summary>
    /// Gets the mode a session runs in under the policy: the mode itself when the policy allows it, else the widest mode
    /// below it that the policy allows and the session can be given.
    /// </summary>
    /// <param name="permissionMode">The mode, or null for none.</param>
    /// <param name="offered">The modes the session can be given, or null for those of the host (<c>default</c>, <c>acceptEdits</c>, <c>bypassPermissions</c>).</param>
    /// <returns>The mode; null when <paramref name="permissionMode"/> is null.</returns>
    public string? Narrow(string? permissionMode, IReadOnlyCollection<string>? offered = null)
    {
        if (!Refuses(permissionMode)) return permissionMode;
        offered ??= HostModes;
        foreach (var candidate in (string[])[Assisted, AcceptEdits])
        {
            if (Rank(candidate) < Rank(permissionMode) && !Refuses(candidate)
                && offered.Contains(candidate, StringComparer.Ordinal))
                return candidate;
        }

        return Ask;
    }

    // How much a mode lets a session do without asking, as the host compares modes: plan and dontAsk lowest, then the
    // mode that asks (and an unknown one), acceptEdits, auto, then bypassPermissions.
    private static int Rank(string? permissionMode)
        => permissionMode?.Trim() switch
        {
            "plan" or "dontAsk" => 0,
            AcceptEdits => 2,
            Assisted => 3,
            Bypass => 4,
            _ => 1,
        };

    /// <inheritdoc />
    public bool Equals(ManagedPolicy? other)
        => other is not null && Source == other.Source && Account == other.Account && BypassPermissionsDisabled == other.BypassPermissionsDisabled
            && AssistedPermissionsDisabled == other.AssistedPermissionsDisabled && DefaultPermissionMode == other.DefaultPermissionMode
            && SandboxRequired == other.SandboxRequired && SandboxUnavailableBlocks == other.SandboxUnavailableBlocks && RunOutsideSandboxDisallowed == other.RunOutsideSandboxDisallowed
            && FreshPolicyMissing == other.FreshPolicyMissing && SupportUrl == other.SupportUrl && ReadAt == other.ReadAt
            && ManagedKeys.SequenceEqual(other.ManagedKeys, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
        => HashCode.Combine(Source, BypassPermissionsDisabled, AssistedPermissionsDisabled, DefaultPermissionMode, SandboxRequired, RunOutsideSandboxDisallowed, FreshPolicyMissing, ReadAt);

    /// <summary>Gets why the policy refuses a permission mode, for the user or an agent; null when it does not.</summary>
    /// <param name="permissionMode">A mode, or null for none.</param>
    /// <returns>The sentence, or null.</returns>
    public string? Refusal(string? permissionMode)
        => Refuses(permissionMode) ? $"The permission mode '{permissionMode!.Trim()}' is turned off by the policy of your organization{From}." : null;

    /// <summary>Gets why no prompt may be sent while <see cref="FreshPolicyMissing"/>, for the user or an agent; null when one may.</summary>
    public string? SendRefusal => FreshPolicyMissing
        ? $"The policy of your organization{From} must be read again before a session runs, and it could not be read: check the connection, then refresh it from the page of the provider."
        : null;

    /// <summary>Gets why a command may not run outside the sandbox, for the agent; null when it may.</summary>
    public string? RunOutsideRefusal => RunOutsideSandboxDisallowed
        ? $"The policy of your organization{From} keeps every command in the sandbox: this one cannot run outside it."
        : null;

    private string From => Source is { Length: > 0 } source ? $" ({source})" : string.Empty;

    /// <summary>
    /// Combines the policies of several sources into the one that applies: each refusal and requirement of any of them,
    /// and the narrowest default mode.
    /// </summary>
    /// <param name="policies">The policies.</param>
    /// <returns>The combined policy; <see cref="None"/> when there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policies"/> is null.</exception>
    public static ManagedPolicy Combine(IEnumerable<ManagedPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        var list = policies.Where(static policy => !policy.IsEmpty).ToArray();
        if (list.Length == 0) return None;
        if (list.Length == 1) return list[0];
        string? defaultMode = null;
        foreach (var mode in list.Select(static policy => policy.DefaultPermissionMode).OfType<string>())
            defaultMode = defaultMode is null || Rank(mode) < Rank(defaultMode) ? mode : defaultMode;
        return new ManagedPolicy
        {
            Source = string.Join(", ", list.Select(static policy => policy.Source).OfType<string>().Distinct(StringComparer.Ordinal)) is { Length: > 0 } sources ? sources : null,
            Account = string.Join(", ", list.Select(static policy => policy.Account).OfType<string>().Distinct(StringComparer.Ordinal)) is { Length: > 0 } accounts ? accounts : null,
            BypassPermissionsDisabled = list.Any(static policy => policy.BypassPermissionsDisabled),
            AssistedPermissionsDisabled = list.Any(static policy => policy.AssistedPermissionsDisabled),
            DefaultPermissionMode = defaultMode,
            SandboxRequired = list.Any(static policy => policy.SandboxRequired),
            SandboxUnavailableBlocks = list.Any(static policy => policy.SandboxRequired && policy.SandboxUnavailableBlocks),
            RunOutsideSandboxDisallowed = list.Any(static policy => policy.RunOutsideSandboxDisallowed),
            FreshPolicyMissing = list.Any(static policy => policy.FreshPolicyMissing),
            SupportUrl = list.Select(static policy => policy.SupportUrl).FirstOrDefault(static url => url is not null),
            ManagedKeys = list.SelectMany(static policy => policy.ManagedKeys).Distinct(StringComparer.Ordinal).ToArray(),
            ReadAt = list.Select(static policy => policy.ReadAt).Where(static at => at is not null).Min(),
        };
    }
}

/// <summary>
/// The policies the sources of a host give it (<see cref="ManagedPolicy"/>), one per source, and the one that applies
/// to its sessions. A source sets its policy when it reads one, and removes it when it has none any more; readers take
/// <see cref="Current"/> at each decision, so a change applies to the next one.
/// </summary>
public sealed class ManagedPolicyState
{
    private readonly Lock _gate = new();
    private Dictionary<string, ManagedPolicy> _policies = new(StringComparer.Ordinal);
    private ManagedPolicy _current = ManagedPolicy.None;

    /// <summary>Raised after the policy that applies changed, on the thread of the source that changed it.</summary>
    public event Action<ManagedPolicy>? Changed;

    /// <summary>Gets the policy that applies: the combination of the policies of every source.</summary>
    public ManagedPolicy Current => Volatile.Read(ref _current);

    /// <summary>Gets the policy of each source, by the key of the source.</summary>
    public IReadOnlyDictionary<string, ManagedPolicy> Policies
    {
        get
        {
            lock (_gate) return new Dictionary<string, ManagedPolicy>(_policies, StringComparer.Ordinal);
        }
    }

    /// <summary>Sets the policy of a source; an empty policy removes it.</summary>
    /// <param name="sourceKey">The key of the source, such as the key of a provider.</param>
    /// <param name="policy">The policy.</param>
    /// <exception cref="ArgumentException"><paramref name="sourceKey"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public void Set(string sourceKey, ManagedPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        ArgumentNullException.ThrowIfNull(policy);
        Update(policies =>
        {
            if (policy.IsEmpty) return policies.Remove(sourceKey);
            if (policies.TryGetValue(sourceKey, out var held) && held == policy) return false;
            policies[sourceKey] = policy;
            return true;
        });
    }

    /// <summary>Removes the policy of a source.</summary>
    /// <param name="sourceKey">The key of the source.</param>
    /// <exception cref="ArgumentException"><paramref name="sourceKey"/> is blank.</exception>
    public void Remove(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        Update(policies => policies.Remove(sourceKey));
    }

    private void Update(Func<Dictionary<string, ManagedPolicy>, bool> change)
    {
        ManagedPolicy current;
        lock (_gate)
        {
            var next = new Dictionary<string, ManagedPolicy>(_policies, StringComparer.Ordinal);
            if (!change(next)) return;
            _policies = next;
            current = ManagedPolicy.Combine(next.Values);
            if (current == _current) return;
            Volatile.Write(ref _current, current);
        }

        Changed?.Invoke(current);
    }
}
