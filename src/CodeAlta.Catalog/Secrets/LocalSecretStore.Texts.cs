using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Catalog.Secrets;

// Texts another file of the machine keeps that must not be in clear (a prompt that waits, which may hold a value): protected
// as the values of the store are, by the protection the store has or would have for its first value.
public sealed partial class LocalSecretStore
{
    /// <summary>Protects a text with the protection of this store, for a file of the machine that must not keep it in clear.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The protected text, in base 64, which <see cref="UnprotectText"/> gives back on this machine.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="SecretStoreUnavailableException">The protected store of the machine cannot be used.</exception>
    /// <exception cref="InvalidDataException">The file of the store is damaged.</exception>
    public string ProtectText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var protector = TextProtector();
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            return Convert.ToBase64String(protector.Protect(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Gives back a text <see cref="ProtectText"/> protected.</summary>
    /// <param name="protectedText">The protected text, in base 64.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="protectedText"/> is null.</exception>
    /// <exception cref="SecretStoreUnavailableException">The protected store of the machine cannot be used, or the text was protected by another key.</exception>
    /// <exception cref="FormatException"><paramref name="protectedText"/> is not base 64.</exception>
    /// <exception cref="InvalidDataException">The file of the store is damaged.</exception>
    public string UnprotectText(string protectedText)
    {
        ArgumentNullException.ThrowIfNull(protectedText);
        var protector = TextProtector();
        var bytes = protector.Unprotect(Convert.FromBase64String(protectedText));
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // The protection of the store, read without marking the store as protected: a store without a value stays one.
    private ISecretProtector TextProtector()
    {
        lock (_gate)
        {
            var document = Read().Document;
            return ProtectorFor(new StoreDocument { Protection = document.Protection, Secrets = document.Secrets });
        }
    }
}
