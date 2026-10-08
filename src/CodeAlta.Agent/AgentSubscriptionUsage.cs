namespace CodeAlta.Agent;

/// <summary>
/// What a subscription lets its account use, as the provider reports it for the account and not for one session:
/// the limits of its plan, how much of each is used and when each starts over.
/// </summary>
/// <param name="Plan">The plan of the account as the provider names it, such as <c>pro</c> or <c>max</c>.</param>
/// <param name="Limits">The limits, the ones that matter most first.</param>
/// <param name="ObservedAt">When the provider was asked.</param>
public sealed record AgentSubscriptionUsage(string? Plan, IReadOnlyList<AgentSubscriptionLimit> Limits, DateTimeOffset ObservedAt);

/// <summary>One limit of a subscription: a window of time that starts over, or a quota of a billing period.</summary>
/// <param name="Id">
/// What the limit is, in the words of the provider: <c>five_hour</c>, <c>seven_day</c>, <c>premium_interactions</c>,
/// <c>chat</c>, <c>completions</c>, <c>extra_usage</c>, <c>credits</c>, or the identifier of a limit of the provider's own.
/// </param>
/// <param name="Name">What the limit is for when it is narrower than the plan: a model, a feature.</param>
/// <param name="UsedPercent">How much is used, from 0 to 100 and above 100 when the limit is passed.</param>
/// <param name="ResetsAt">When the limit starts over.</param>
/// <param name="WindowMinutes">How long the window is, for a limit that is a window of time.</param>
/// <param name="Used">How many units are used, when the provider counts them.</param>
/// <param name="Total">How many units the plan includes.</param>
/// <param name="Unit">What a unit is: <c>requests</c> or <c>credits</c>.</param>
/// <param name="Remaining">How many units are left, for a balance that has no total.</param>
/// <param name="Unlimited">Whether the plan sets no limit.</param>
public sealed record AgentSubscriptionLimit(
    string Id,
    string? Name = null,
    double? UsedPercent = null,
    DateTimeOffset? ResetsAt = null,
    long? WindowMinutes = null,
    double? Used = null,
    double? Total = null,
    string? Unit = null,
    bool Unlimited = false,
    double? Remaining = null);

/// <summary>The answer of a provider that was asked for the usage of its subscription.</summary>
/// <param name="Status">One of the statuses of this type.</param>
/// <param name="Usage">The usage, with the status <see cref="Ok"/>.</param>
public sealed record AgentSubscriptionUsageReading(string Status, AgentSubscriptionUsage? Usage = null)
{
    /// <summary>The provider answered.</summary>
    public const string Ok = "ok";

    /// <summary>No account is signed in.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>The account or the way it is signed in gives no usage: an API key, a plan without limits.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The usage is read by asking a tool of the provider (its CLI), and the machine does not have it.</summary>
    public const string ToolMissing = "tool_missing";

    /// <summary>The tool of the provider is there, but not signed in with a subscription, or with another account than the provider.</summary>
    public const string ToolSignedOut = "tool_signed_out";

    /// <summary>Gets the answer of an account that gives no usage.</summary>
    public static AgentSubscriptionUsageReading NotAvailable { get; } = new(Unavailable);

    /// <summary>Gets the answer of a provider whose tool is not on the machine.</summary>
    public static AgentSubscriptionUsageReading NoTool { get; } = new(ToolMissing);

    /// <summary>Gets the answer of a provider whose tool is not signed in with the account of the provider.</summary>
    public static AgentSubscriptionUsageReading ToolNotSignedIn { get; } = new(ToolSignedOut);

    /// <summary>Gets the answer of a provider without a signed-in account.</summary>
    public static AgentSubscriptionUsageReading NotSignedIn { get; } = new(SignedOut);
}
