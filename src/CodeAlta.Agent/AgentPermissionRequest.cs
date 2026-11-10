using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Agent;

/// <summary>
/// Shared base type for a permission request originating from a provider.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="InteractionId">Stable interaction identifier.</param>
/// <param name="Kind">The shared permission-request kind.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(AgentGenericPermissionRequest), "generic")]
[JsonDerivedType(typeof(AgentCommandPermissionRequest), "command")]
[JsonDerivedType(typeof(AgentFileChangePermissionRequest), "fileChange")]
[JsonDerivedType(typeof(AgentSkillPermissionRequest), "skill")]
[JsonDerivedType(typeof(AgentBrowserPermissionRequest), "browser")]
public abstract record AgentPermissionRequest(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string InteractionId,
    string Kind)
    : AgentEvent(ProviderId, SessionId, Timestamp, RunId);

/// <summary>
/// Generic permission request for providers that do not expose a richer typed prompt.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="InteractionId">Stable interaction identifier.</param>
/// <param name="Kind">The provider-defined request kind.</param>
/// <param name="Raw">The raw request payload.</param>
public sealed record AgentGenericPermissionRequest(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string InteractionId,
    string Kind,
    JsonElement Raw)
    : AgentPermissionRequest(ProviderId, SessionId, Timestamp, RunId, InteractionId, Kind);

/// <summary>
/// Permission request for command execution.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="InteractionId">Stable interaction identifier.</param>
/// <param name="ApprovalId">Optional provider-specific approval identifier.</param>
/// <param name="Command">Optional command text.</param>
/// <param name="WorkingDirectory">Optional working directory.</param>
/// <param name="Actions">Optional parsed command actions.</param>
/// <param name="Reason">Optional explanatory reason.</param>
/// <param name="Network">Optional network access request details.</param>
/// <param name="ProposedExecPolicyAmendment">Optional proposed exec policy amendment.</param>
/// <param name="ProposedNetworkPolicyAmendments">Optional proposed network policy amendments.</param>
public sealed record AgentCommandPermissionRequest(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string InteractionId,
    string? ApprovalId,
    string? Command,
    string? WorkingDirectory,
    IReadOnlyList<AgentCommandPreviewAction>? Actions,
    string? Reason,
    AgentNetworkAccessRequest? Network,
    IReadOnlyList<string>? ProposedExecPolicyAmendment,
    IReadOnlyList<AgentNetworkPolicyAmendment>? ProposedNetworkPolicyAmendments)
    : AgentPermissionRequest(ProviderId, SessionId, Timestamp, RunId, InteractionId, Kind: "commandExecution")
{
    /// <summary>
    /// Gets a value indicating whether a sandboxed session asks to run the command outside its sandbox, on this
    /// computer with the rights of the user; <see cref="AgentCommandPermissionRequest.Reason"/> says why. No permission
    /// mode answers such a request for the user, and a host that has no user refuses it.
    /// </summary>
    public bool OutsideSandbox { get; init; }
}

/// <summary>
/// Permission request for file changes.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="InteractionId">Stable interaction identifier.</param>
/// <param name="GrantRoot">Optional root path to grant for the session.</param>
/// <param name="Reason">Optional explanatory reason.</param>
public sealed record AgentFileChangePermissionRequest(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string InteractionId,
    string? GrantRoot,
    string? Reason)
    : AgentPermissionRequest(ProviderId, SessionId, Timestamp, RunId, InteractionId, Kind: "fileChange");

/// <summary>
/// Permission request to load a skill an MCP server publishes: its instructions come from that server and may ask the
/// agent to run commands. It is asked in every permission mode: no mode answers it for the user.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="InteractionId">Stable interaction identifier.</param>
/// <param name="Server">The key of the MCP server that publishes the skill.</param>
/// <param name="SkillName">The name of the skill.</param>
/// <param name="Uri">The URI of the <c>SKILL.md</c> of the skill on that server.</param>
/// <param name="FileCount">The number of files of the skill; 0 when its server generates them.</param>
/// <param name="TotalBytes">The number of bytes of its files; 0 when its server generates them.</param>
/// <param name="Dynamic">Whether its server generates its files, which can then not be checked nor approved for good.</param>
public sealed record AgentSkillPermissionRequest(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string InteractionId,
    string Server,
    string SkillName,
    string Uri,
    int FileCount,
    long TotalBytes,
    bool Dynamic)
    : AgentPermissionRequest(ProviderId, SessionId, Timestamp, RunId, InteractionId, Kind: AgentSkillPermissionRequest.SkillKind)
{
    /// <summary>The kind of the request.</summary>
    public const string SkillKind = "skill";
}

/// <summary>
/// Permission request to drive the user's own browser, with their sign-ins: for a session (<paramref name="Site"/> null),
/// or for a site the session loads there. It is asked in every permission mode: no mode answers it for the user.
/// </summary>
/// <param name="ProviderId">The model provider identifier.</param>
/// <param name="SessionId">The session identifier.</param>
/// <param name="Timestamp">The event timestamp.</param>
/// <param name="RunId">Optional run identifier.</param>
/// <param name="InteractionId">Stable interaction identifier.</param>
/// <param name="Browser">The name of the browser, such as <c>Google Chrome</c>.</param>
/// <param name="Profile">The profile it would drive, such as <c>Person 1</c>.</param>
/// <param name="Site">The site the session would act on, such as <c>github.com</c>; null for the use of the browser itself.</param>
public sealed record AgentBrowserPermissionRequest(
    ModelProviderId ProviderId,
    string SessionId,
    DateTimeOffset Timestamp,
    AgentRunId? RunId,
    string InteractionId,
    string Browser,
    string Profile,
    string? Site)
    : AgentPermissionRequest(ProviderId, SessionId, Timestamp, RunId, InteractionId, Kind: AgentBrowserPermissionRequest.BrowserKind)
{
    /// <summary>The kind of the request.</summary>
    public const string BrowserKind = "browser";
}

/// <summary>
/// Identifies the kind of a parsed command action.
/// </summary>
public enum AgentCommandPreviewKind
{
    /// <summary>
    /// A read-oriented command action.
    /// </summary>
    Read,

    /// <summary>
    /// A list-files command action.
    /// </summary>
    ListFiles,

    /// <summary>
    /// A search command action.
    /// </summary>
    Search,

    /// <summary>
    /// An unknown command action.
    /// </summary>
    Unknown,
}

/// <summary>
/// Parsed command action for UI preview.
/// </summary>
/// <param name="Kind">The action kind.</param>
/// <param name="Command">The original command fragment.</param>
/// <param name="Path">Optional filesystem path.</param>
/// <param name="Query">Optional search query.</param>
/// <param name="Name">Optional display name.</param>
public sealed record AgentCommandPreviewAction(
    AgentCommandPreviewKind Kind,
    string Command,
    string? Path = null,
    string? Query = null,
    string? Name = null);

/// <summary>
/// Network access request details.
/// </summary>
/// <param name="Host">The target host.</param>
/// <param name="Protocol">The target protocol.</param>
public sealed record AgentNetworkAccessRequest(
    string Host,
    string Protocol);

/// <summary>
/// Network policy amendment action.
/// </summary>
public enum AgentNetworkPolicyAction
{
    /// <summary>
    /// Allow network access.
    /// </summary>
    Allow,

    /// <summary>
    /// Deny network access.
    /// </summary>
    Deny,
}

/// <summary>
/// Proposed network policy amendment.
/// </summary>
/// <param name="Action">The amendment action.</param>
/// <param name="Host">The target host.</param>
public sealed record AgentNetworkPolicyAmendment(
    AgentNetworkPolicyAction Action,
    string Host);

/// <summary>
/// Represents the decision for a permission request.
/// </summary>
/// <param name="Kind">The decision kind.</param>
/// <param name="ExecPolicyAmendment">Optional exec policy amendment.</param>
/// <param name="NetworkPolicyAmendment">Optional network policy amendment.</param>
public sealed record AgentPermissionDecision(
    AgentPermissionDecisionKind Kind,
    IReadOnlyList<string>? ExecPolicyAmendment = null,
    AgentNetworkPolicyAmendment? NetworkPolicyAmendment = null)
{
    /// <summary>Gets who made the decision; <see cref="AgentPermissionDecisionSource.Unspecified"/> when nothing says so.</summary>
    public AgentPermissionDecisionSource Source { get; init; }

    /// <summary>
    /// Gets why a rule of CodeAlta, or a run without a user, made the decision: a short clause such as
    /// <c>the permission mode of this session does not approve it</c>, or null.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>Creates the denial of the user, who answered the request.</summary>
    /// <returns>The decision.</returns>
    public static AgentPermissionDecision DeniedByUser() => new(AgentPermissionDecisionKind.Deny) { Source = AgentPermissionDecisionSource.User };

    /// <summary>Creates the denial of a rule or a permission mode of CodeAlta, which nobody was asked about.</summary>
    /// <param name="reason">Why the rule denies the request: a short clause.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or blank.</exception>
    public static AgentPermissionDecision DeniedByPolicy(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(AgentPermissionDecisionKind.Deny) { Source = AgentPermissionDecisionSource.Policy, Reason = reason };
    }

    /// <summary>Creates the denial of a request that waits for the user, where no user can be asked.</summary>
    /// <param name="reason">Why no user can be asked: a short clause.</param>
    /// <returns>The decision.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is null or blank.</exception>
    public static AgentPermissionDecision DeniedUnattended(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(AgentPermissionDecisionKind.Deny) { Source = AgentPermissionDecisionSource.Unattended, Reason = reason };
    }

    /// <summary>
    /// Gets the sentence that tells an agent who denied its action and why, such as
    /// <c>The user denied this action in CodeAlta.</c>
    /// </summary>
    /// <returns>The sentence, ending with a period.</returns>
    public string DescribeDenial()
    {
        var reason = Reason?.Trim().TrimEnd('.');
        return Source switch
        {
            AgentPermissionDecisionSource.User => "The user denied this action in CodeAlta.",
            AgentPermissionDecisionSource.Policy => string.IsNullOrEmpty(reason)
                ? "CodeAlta's permission rules denied this action."
                : $"CodeAlta's permission rules denied this action: {reason}.",
            AgentPermissionDecisionSource.Unattended => string.IsNullOrEmpty(reason)
                ? "CodeAlta denied this action without asking the user: no user can be asked here."
                : $"CodeAlta denied this action without asking the user: {reason}.",
            _ => "This action was denied in CodeAlta.",
        };
    }
}

/// <summary>
/// Who made a permission decision.
/// </summary>
public enum AgentPermissionDecisionSource
{
    /// <summary>
    /// Nothing says who decided.
    /// </summary>
    Unspecified,

    /// <summary>
    /// The user answered the request.
    /// </summary>
    User,

    /// <summary>
    /// A rule or a permission mode of CodeAlta answered the request, without asking anybody.
    /// </summary>
    Policy,

    /// <summary>
    /// The request waits for the user, and no user can be asked: a run without a user, a session that is not open.
    /// </summary>
    Unattended,
}

/// <summary>
/// The kind of permission decision.
/// </summary>
public enum AgentPermissionDecisionKind
{
    /// <summary>
    /// Allow the action once.
    /// </summary>
    AllowOnce,

    /// <summary>
    /// Allow the action for the remainder of the session.
    /// </summary>
    AllowForSession,

    /// <summary>
    /// Deny the action.
    /// </summary>
    Deny,

    /// <summary>
    /// Cancel the request.
    /// </summary>
    Cancel,

    /// <summary>
    /// Allow the action from now on, also in other sessions: the one who asked keeps the approval. Only a request that
    /// offers it (<see cref="AgentSkillPermissionRequest"/> of a skill whose files can be checked, or
    /// <see cref="AgentBrowserPermissionRequest"/> of a site) is answered so.
    /// </summary>
    AllowAlways,
}

/// <summary>
/// Permission request handler delegate.
/// </summary>
/// <param name="request">The permission request.</param>
/// <param name="cancellationToken">A token to cancel the operation.</param>
public delegate Task<AgentPermissionDecision> AgentPermissionRequestHandler(
    AgentPermissionRequest request,
    CancellationToken cancellationToken);
