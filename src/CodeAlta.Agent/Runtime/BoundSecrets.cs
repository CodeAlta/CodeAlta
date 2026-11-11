namespace CodeAlta.Agent.Runtime;

/// <summary>What the source of the secrets kept in vaults answers for one secret.</summary>
/// <param name="Value">The value, when it can be given now; null otherwise.</param>
/// <param name="Problem">Why it cannot be given now, naming the secret and its vault (locked, waits for a confirmation, not found); null when <paramref name="Value"/> is set.</param>
public sealed record BoundSecretRead(string? Value, string? Problem)
{
    /// <summary>Creates the answer of a value that can be given.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public static BoundSecretRead Of(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(value, null);
    }

    /// <summary>Creates the answer of a value that cannot be given now.</summary>
    /// <param name="problem">Why, naming the secret.</param>
    /// <returns>The answer.</returns>
    /// <exception cref="ArgumentException"><paramref name="problem"/> is blank.</exception>
    public static BoundSecretRead Unavailable(string problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(problem);
        return new(null, problem);
    }
}

/// <summary>
/// The values of the secrets of this machine that are kept in vaults (1Password, Bitwarden, a command...), as the credential
/// broker of a host holds them in memory, and the confirmations of the secrets that ask before use. It never reads a vault
/// itself: it answers from memory, at once, and says why when it cannot, so that what resolves a reference never waits for a
/// vault or a person.
/// </summary>
/// <remarks>
/// A use names what will get the value, such as <c>provider anthropic</c> or <c>MCP server grafana</c>: a secret that asks
/// before use is given only for a use the user confirmed, and a use that is not confirmed is recorded so that the user can
/// confirm it.
/// </remarks>
public interface IBoundSecretSource
{
    /// <summary>Gets the value of a secret kept in a vault, for a use.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <param name="use">What will get the value, such as <c>provider anthropic</c>; null for a use nobody names.</param>
    /// <returns>The value, or why it cannot be given now.</returns>
    BoundSecretRead ReadBound(string name, string? use);

    /// <summary>Tells whether a secret of this computer that asks before use may be given for a use.</summary>
    /// <param name="name">The name of the secret.</param>
    /// <param name="use">What will get the value; null for a use nobody names.</param>
    /// <returns>Null when it may; else why not, naming the secret.</returns>
    string? AuthorizeUse(string name, string? use);
}
