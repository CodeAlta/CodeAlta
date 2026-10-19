namespace CodeAlta.Agent.Cursor;

/// <summary>
/// CodeAlta's signed-in view of a Cursor provider. Sign-out writes a marker under the state root and does not
/// run <c>agent logout</c>, so the machine-wide CLI login stays in place.
/// </summary>
public static class CursorSignInView
{
    /// <summary>
    /// Returns whether CodeAlta has signed this provider out.
    /// </summary>
    /// <param name="stateRootPath">The profile state root.</param>
    /// <param name="providerKey">The provider key.</param>
    /// <returns><see langword="true"/> when a later status read must stay signed out.</returns>
    public static bool IsDismissed(string stateRootPath, string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        return File.Exists(MarkerPath(stateRootPath, providerKey));
    }

    /// <summary>
    /// Records that CodeAlta no longer treats this provider as signed in.
    /// </summary>
    /// <param name="stateRootPath">The profile state root.</param>
    /// <param name="providerKey">The provider key.</param>
    public static void Dismiss(string stateRootPath, string providerKey)
    {
        var path = MarkerPath(stateRootPath, providerKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "signed-out");
    }

    /// <summary>
    /// Clears the signed-out marker after a successful login.
    /// </summary>
    /// <param name="stateRootPath">The profile state root.</param>
    /// <param name="providerKey">The provider key.</param>
    public static void Accept(string stateRootPath, string providerKey)
    {
        var path = MarkerPath(stateRootPath, providerKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string MarkerPath(string stateRootPath, string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        var safe = new string(providerKey.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
        return Path.Combine(stateRootPath, "auth", "cursor", safe + ".signed-out");
    }
}
