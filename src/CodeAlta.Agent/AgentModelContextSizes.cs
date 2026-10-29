using System.Text.Json.Serialization;
using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Agent;

/// <summary>Verified context capacities of one exact provider/model, independent of saved preferences.</summary>
public sealed record AgentModelContextSizes
{
    /// <summary>The largest exact integer shared by the runtime and JavaScript/RPC clients.</summary>
    public const long MaximumTokens = 9_007_199_254_740_991;

    /// <summary>Creates immutable, distinct choices. Missing defaults or invalid choices leave Default only.</summary>
    /// <param name="DefaultTokens">The advertised default capacity, if known.</param>
    /// <param name="TokenCounts">The exact capacities the adapter has verified as selectable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="TokenCounts"/> is null.</exception>
    public AgentModelContextSizes(long? DefaultTokens, IReadOnlyList<long> TokenCounts)
        : this(DefaultTokens, TokenCounts, IsProviderManaged: false)
    {
    }

    /// <summary>Creates choices whose application and usage may be owned by an external provider.</summary>
    /// <param name="DefaultTokens">The advertised default capacity, or null when the provider does not report it.</param>
    /// <param name="TokenCounts">The exact verified choices.</param>
    /// <param name="IsProviderManaged">Whether the provider applies choices and reports its own effective budget. Only these choices may have an unknown default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="TokenCounts"/> is null.</exception>
    [JsonConstructor]
    public AgentModelContextSizes(long? DefaultTokens, IReadOnlyList<long> TokenCounts, bool IsProviderManaged)
    {
        ArgumentNullException.ThrowIfNull(TokenCounts);
        this.IsProviderManaged = IsProviderManaged;
        this.DefaultTokens = DefaultTokens is > 0 and <= MaximumTokens ? DefaultTokens : null;
        this.TokenCounts = (IsProviderManaged && DefaultTokens is null ||
                            this.DefaultTokens is { } defaultTokens && TokenCounts.Contains(defaultTokens)) &&
                           TokenCounts.All(static value => value is > 0 and <= MaximumTokens)
            ? Array.AsReadOnly(TokenCounts.Distinct().Order().ToArray())
            : Array.Empty<long>();
    }

    /// <summary>Gets the advertised default capacity, in the same units as <see cref="TokenCounts"/>.</summary>
    public long? DefaultTokens { get; }

    /// <summary>
    /// Gets whether these capacities are input budgets with output already reserved, rather than total context
    /// envelopes. Adapters set this from their discovery contract; the runtime must not reserve output twice.
    /// </summary>
    public bool IsInputBudget { get; init; }

    /// <summary>Gets whether the adapter applies choices itself and owns context usage and compaction. No local budget is synthesized.</summary>
    public bool IsProviderManaged { get; }

    /// <summary>Gets exact verified choices; empty means Default only.</summary>
    public IReadOnlyList<long> TokenCounts { get; }

    /// <summary>Whether a choice is verified. Null (Default) is always supported.</summary>
    public bool Supports(long? tokens) => tokens is null || tokens > 0 && TokenCounts.Contains(tokens.Value);

    /// <summary>Resolves valid session choice, valid matching preference, then advertised default; never clamps.</summary>
    public long? Resolve(long? sessionTokens, long? preferenceTokens)
        => sessionTokens is not null && Supports(sessionTokens) ? sessionTokens
            : preferenceTokens is not null && Supports(preferenceTokens) ? preferenceTokens
            : DefaultTokens;

    /// <summary>Resolves the capacity shown for Default and used by the runtime. Unsupported preferences are ignored.</summary>
    public static long? DefaultCapacity(AgentModelInfo? model, long? preferenceTokens)
        => model?.ContextSizes is { IsProviderManaged: true } managed
            ? managed.Resolve(null, preferenceTokens)
            : model?.ContextSizes?.Resolve(null, preferenceTokens) ?? AgentTokenBudgetResolver.ReadContextCapacity(model);
}

/// <summary>A context change: a null object keeps the selection; an object with null tokens explicitly selects Default.</summary>
/// <param name="Tokens">An exact advertised capacity, or null for Default.</param>
public sealed record AgentContextSelection(long? Tokens);
