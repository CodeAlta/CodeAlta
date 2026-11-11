using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Hosting;

// What the builder of the providers of a registry reads their secrets with: the masking of the host, told each value before a
// provider gets it, and the secrets kept in vaults the host holds, given for the use of each provider.
internal sealed record ProviderSecretAccess(Action<string>? Issued, IBoundSecretSource? Bound)
{
    public static ProviderSecretAccess Of(ModelProviderRegistry registry) => new(registry.SecretIssued, registry.BoundSecrets);

    // tellIssued: whether the resolver tells the masking itself; the key of a provider is told once normalized, by the builder.
    public static SecretReferenceResolver Resolver(ProviderSecretAccess? access, string stateRootPath, string providerKey, bool tellIssued = false)
        => SecretReferenceResolver.ForMachine(stateRootPath, tellIssued ? access?.Issued : null, access?.Bound, $"provider {providerKey}");
}
