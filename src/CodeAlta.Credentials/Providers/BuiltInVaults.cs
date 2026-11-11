using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

// The password managers read through their own program beside 1Password: Bitwarden, KeePassXC, pass and the keychain of the system.
internal static class BuiltInVaults
{
    public static IEnumerable<PluginCredentialProviderContribution> All(VaultCommandRunner runner)
    {
        _ = runner;
        yield break;
    }
}
