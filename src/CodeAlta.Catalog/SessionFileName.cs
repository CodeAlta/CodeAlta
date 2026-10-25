namespace CodeAlta.Catalog;

/// <summary>
/// Checks that a session id can name a file of its own in a flat folder (a lease, a marker) on every platform: no path
/// characters, no name Windows reserves, nothing a file system trims.
/// </summary>
internal static class SessionFileName
{
    /// <summary>The longest session id that names a file.</summary>
    internal const int MaximumLength = 128;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>Gets whether a session id can name a file.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <returns><see langword="true"/> when it can.</returns>
    internal static bool IsValid(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > MaximumLength) return false;
        if (sessionId[0] is ' ' or '.' || sessionId[^1] is ' ' or '.') return false;
        foreach (var c in sessionId)
        {
            if (char.IsControl(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        }

        var stem = sessionId.Split('.')[0];
        return !ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Throws when a session id cannot name a file.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="parameterName">The name of the parameter that holds it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sessionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> cannot name a file.</exception>
    internal static void Validate(string? sessionId, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(sessionId, parameterName);
        if (!IsValid(sessionId))
        {
            throw new ArgumentException(
                $"'{sessionId}' is not a session id that can name a file: 1 to {MaximumLength} characters, without path characters.",
                parameterName);
        }
    }
}
