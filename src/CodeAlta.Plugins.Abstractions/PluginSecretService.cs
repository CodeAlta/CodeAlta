namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Masks the values of secrets a plugin gives out: a value of the store a plugin resolves for a process, a server or a request
/// (a key, a variable) is told before they get it, and the host masks it from then on in what the sessions keep, show and log,
/// whatever the store keeps of it later.
/// </summary>
public interface IPluginSecretService
{
    /// <summary>Masks a value from now on, for as long as the host runs.</summary>
    /// <param name="value">The value; one too short to mask is ignored.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    void Mask(string value);

    /// <summary>
    /// Gets the values of the secrets kept in vaults that the host holds, and its confirmations of the secrets that ask before use,
    /// for a plugin that resolves <c>${secret:NAME}</c> references itself (the MCP servers); null for a host without them, where such
    /// secrets cannot be read.
    /// </summary>
    CodeAlta.Agent.Runtime.IBoundSecretSource? BoundSecrets => null;
}

/// <summary>The secret service of a host that masks nothing a plugin tells it (a host written before plugins could).</summary>
public sealed class NoopPluginSecretService : IPluginSecretService
{
    private NoopPluginSecretService()
    {
    }

    /// <summary>Gets the instance.</summary>
    public static NoopPluginSecretService Instance { get; } = new();

    /// <inheritdoc />
    public void Mask(string value) => ArgumentNullException.ThrowIfNull(value);
}
