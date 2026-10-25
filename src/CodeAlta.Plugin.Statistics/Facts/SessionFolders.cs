using System.Security.Cryptography;
using System.Text;

namespace CodeAlta.Plugin.Statistics.Facts;

/// <summary>
/// The key the statistics keep of the folder of a session in the place of the folder, which they never keep. A session whose
/// recorded project the catalog does not have counts under the project of the catalog in that folder, as CodeAlta lists it
/// (<c>ProjectCatalog.ResolveSessionProject</c>): the key of the folder of the session is compared with the keys of the folders of
/// the projects. Two folders have the same key when their paths are the same text, as that rule compares them.
/// </summary>
internal static class SessionFolders
{
    /// <summary>Gets the key of a folder.</summary>
    /// <param name="path">The path of the folder.</param>
    /// <returns>The key; null for no folder.</returns>
    public static string? Key(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(path), hash);
        return Convert.ToHexStringLower(hash[..16]);
    }
}
