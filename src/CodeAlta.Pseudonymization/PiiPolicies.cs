using System.Collections.Frozen;

namespace CodeAlta.Pseudonymization;

/// <summary>What is done with a kind of personal data before it is sent.</summary>
public enum PiiPolicy
{
    /// <summary>It is replaced by a pseudonym, which is put back in the answers.</summary>
    Pseudonymize,

    /// <summary>It is replaced by its kind alone (<c>[IBAN]</c>), which nothing puts back.</summary>
    Mask,

    /// <summary>It is sent as it is.</summary>
    Keep,
}

/// <summary>The policy of each kind of personal data: pseudonymized, except ordinary dates, web addresses and UUIDs, which are kept.</summary>
public sealed class PiiPolicies
{
    private readonly FrozenDictionary<string, PiiPolicy> _policies;

    private PiiPolicies(FrozenDictionary<string, PiiPolicy> policies) => _policies = policies;

    /// <summary>Gets the defaults: ordinary dates, web addresses and UUIDs, which name no one, are kept.</summary>
    public static PiiPolicies Default { get; } = new(new Dictionary<string, PiiPolicy>
    {
        [PiiEntities.Date] = PiiPolicy.Keep,
        [PiiEntities.Url] = PiiPolicy.Keep,
        [PiiEntities.Uuid] = PiiPolicy.Keep,
    }.ToFrozenDictionary(StringComparer.Ordinal));

    /// <summary>Gets the policy of a kind.</summary>
    /// <param name="entity">The kind.</param>
    /// <returns>Its policy.</returns>
    public PiiPolicy For(string entity) => _policies.TryGetValue(entity, out var policy) ? policy : PiiPolicy.Pseudonymize;

    /// <summary>Gets these policies with the policy of one kind changed.</summary>
    /// <param name="entity">The kind, one of <see cref="PiiEntities.All"/>.</param>
    /// <param name="policy">Its policy.</param>
    /// <returns>The policies.</returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is not a kind the pseudonymizer knows: a typing error must never send in clear what it meant to hide.</exception>
    public PiiPolicies With(string entity, PiiPolicy policy)
    {
        if (!PiiEntities.All.Contains(entity)) throw new ArgumentException($"'{entity}' is not a kind of personal data the pseudonymizer knows.", nameof(entity));
        var policies = new Dictionary<string, PiiPolicy>(_policies, StringComparer.Ordinal) { [entity] = policy };
        return new PiiPolicies(policies.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
