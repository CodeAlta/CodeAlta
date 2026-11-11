using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

// The password managers read through their own program beside 1Password: Bitwarden, KeePassXC, pass and the keychain of the system.
internal static class BuiltInVaults
{
    public static IEnumerable<PluginCredentialProviderContribution> All(VaultCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        yield return BitwardenProvider.Contribution(runner);
        yield return BitwardenSecretsProvider.Contribution(runner);
        yield return KeePassXcProvider.Contribution(runner);
        yield return PassProvider.Contribution(runner);
        yield return KeychainProvider.Contribution(runner);
    }
}
