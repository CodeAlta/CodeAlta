using CodeAlta.Credentials.Providers;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials;

/// <summary>
/// The types of credential providers a host knows: the built-in ones (1Password, Bitwarden, KeePassXC, a command, the secret
/// managers...) and those of the running plugins of the user. A plugin type never shadows a built-in one.
/// </summary>
public sealed class CredentialProviderTypes
{
    private readonly IReadOnlyList<PluginCredentialProviderContribution> _builtIn;
    private readonly Func<IReadOnlyList<PluginCredentialProviderContribution>>? _plugins;

    /// <summary>Initializes a new instance of the <see cref="CredentialProviderTypes"/> class.</summary>
    /// <param name="runner">What runs the programs of the vaults.</param>
    /// <param name="plugins">Gives the types the running plugins of the user bring; null for none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runner"/> is null.</exception>
    public CredentialProviderTypes(VaultCommandRunner runner, Func<IReadOnlyList<PluginCredentialProviderContribution>>? plugins = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _plugins = plugins;
        _builtIn = [.. BuiltIn(runner)];
    }

    /// <summary>Gets the built-in types, in the order the window offers them.</summary>
    public IReadOnlyList<PluginCredentialProviderContribution> BuiltIn() => _builtIn;

    /// <summary>Lists every type: the built-in ones, then those of the plugins.</summary>
    /// <returns>The types.</returns>
    public IReadOnlyList<PluginCredentialProviderContribution> List()
    {
        var plugins = Plugins();
        return [.. _builtIn, .. plugins.Where(plugin => _builtIn.All(builtIn => !string.Equals(builtIn.Type, plugin.Type, StringComparison.Ordinal)))];
    }

    /// <summary>Finds a type.</summary>
    /// <param name="type">The type, as <c>[credentials.&lt;id&gt;]</c> writes it.</param>
    /// <returns>The type, or null when no built-in type nor running plugin has it.</returns>
    public PluginCredentialProviderContribution? Find(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        return _builtIn.FirstOrDefault(builtIn => string.Equals(builtIn.Type, type, StringComparison.Ordinal))
            ?? Plugins().FirstOrDefault(plugin => string.Equals(plugin.Type, type, StringComparison.Ordinal));
    }

    /// <summary>Gets whether a type is built in.</summary>
    /// <param name="type">The type.</param>
    /// <returns>True for a built-in type.</returns>
    public bool IsBuiltIn(string? type) => _builtIn.Any(builtIn => string.Equals(builtIn.Type, type, StringComparison.Ordinal));

    private IReadOnlyList<PluginCredentialProviderContribution> Plugins()
    {
        if (_plugins is null) return [];
        try
        {
            return [.. _plugins().Where(static plugin => plugin.Validate() is null)];
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return [];
        }
    }

    private static IEnumerable<PluginCredentialProviderContribution> BuiltIn(VaultCommandRunner runner)
    {
        yield return OnePasswordProvider.Contribution(runner);
        foreach (var vault in BuiltInVaults.All(runner)) yield return vault;
        yield return CommandProvider.Contribution(runner);
        foreach (var preset in CommandPresets.All(runner)) yield return preset;
    }
}
