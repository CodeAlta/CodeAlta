namespace CodeAlta.Catalog.Secrets;

/// <summary>
/// Protects the values of a <see cref="LocalSecretStore"/> with a key only the user of this machine holds: DPAPI on
/// Windows, a key kept in the Keychain on macOS or in libsecret on Linux, or a key in a file only the user can read.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Gets the name of the protection, written in the store so that it is opened the same way later.</summary>
    /// <remarks>One of <see cref="SecretProtectionKinds"/>.</remarks>
    string Kind { get; }

    /// <summary>Protects a value.</summary>
    /// <param name="value">The bytes of the value.</param>
    /// <returns>The protected bytes.</returns>
    /// <exception cref="SecretStoreUnavailableException">The protected store of the machine cannot be used.</exception>
    byte[] Protect(ReadOnlySpan<byte> value);

    /// <summary>Gives back a value from its protected bytes.</summary>
    /// <param name="protectedValue">The protected bytes.</param>
    /// <returns>The bytes of the value.</returns>
    /// <exception cref="SecretStoreUnavailableException">The protected store of the machine cannot be used, or the bytes were protected by another key.</exception>
    byte[] Unprotect(ReadOnlySpan<byte> protectedValue);
}

/// <summary>The names of the protections of a <see cref="LocalSecretStore"/>.</summary>
public static class SecretProtectionKinds
{
    /// <summary>The Data Protection API of Windows, for the current user.</summary>
    public const string Dpapi = "dpapi";

    /// <summary>A key kept in the login Keychain of macOS.</summary>
    public const string Keychain = "keychain";

    /// <summary>A key kept in the Secret Service of the desktop session on Linux (libsecret).</summary>
    public const string Libsecret = "libsecret";

    /// <summary>A key in a file only the user can read, where the machine has no protected store.</summary>
    public const string File = "file";
}

/// <summary>Thrown when the protected store of the machine cannot protect or give back a value.</summary>
public sealed class SecretStoreUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SecretStoreUnavailableException"/> class.</summary>
    /// <param name="message">What failed.</param>
    public SecretStoreUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SecretStoreUnavailableException"/> class.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The cause.</param>
    public SecretStoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
