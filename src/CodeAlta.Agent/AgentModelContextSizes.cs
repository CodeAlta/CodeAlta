using CodeAlta.Agent.Runtime.Compaction;

namespace CodeAlta.Agent;

/// <summary>Verified context capacities of one exact provider/model, independent of saved preferences.</summary>
public sealed record AgentModelContextSizes
{
    /// <summary>The largest exact integer shared by the runtime and JavaScript/RPC clients.</summary>
    public const long MaximumTokens = 9_007_199_254_740_991;

    /// <summary>Creates immutable, distinct choices. Missing defaults or invalid choices leave Default only.</summary>
    /// <param name="DefaultTokens">The advertised default total capacity, if known.</param>
    /// <param name="TokenCounts">The exact capacities the adapter has verified as selectable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="TokenCounts"/> is null.</exception>
    public AgentModelContextSizes(long? DefaultTokens, IReadOnlyList<long> TokenCounts)
    {
        ArgumentNullException.ThrowIfNull(TokenCounts);
        this.DefaultTokens = DefaultTokens is > 0 and <= MaximumTokens ? DefaultTokens : null;
        this.TokenCounts = this.DefaultTokens is { } defaultTokens &&
                           TokenCounts.Contains(defaultTokens) && TokenCounts.All(static value => value is > 0 and <= MaximumTokens)
            ? Array.AsReadOnly(TokenCounts.Distinct().Order().ToArray())
            : Array.Empty<long>();
    }

    /// <summary>Gets the advertised default total capacity, not its usable input budget.</summary>
    public long? DefaultTokens { get; }

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
        => model?.ContextSizes?.Resolve(null, preferenceTokens) ?? AgentTokenBudgetResolver.ReadContextCapacity(model);
}

/// <summary>A context change: a null object keeps the selection; an object with null tokens explicitly selects Default.</summary>
/// <param name="Tokens">An exact advertised capacity, or null for Default.</param>
public sealed record AgentContextSelection(long? Tokens);
