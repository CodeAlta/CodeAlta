namespace CodeAlta.Sandbox;

/// <summary>
/// The host names of a profile: a name (<c>nuget.org</c>), or <c>*.</c> and a domain for its subdomains
/// (<c>*.nuget.org</c> matches <c>api.nuget.org</c>, not <c>nuget.org</c>). A bare <c>*</c> is not a pattern.
/// </summary>
public static class SandboxHostPattern
{
    /// <summary>Gets whether a pattern is valid.</summary>
    /// <param name="pattern">The pattern.</param>
    /// <returns>True for a host name, or <c>*.</c> and a host name.</returns>
    public static bool IsValid(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var name = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;
        return name.Length is > 0 and <= 253 && Uri.CheckHostName(name) == UriHostNameType.Dns
            && name.Split('.').All(static label => label.Length is > 0 and <= 63 && label.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '-'));
    }

    /// <summary>Gets whether a host matches one of the patterns.</summary>
    /// <param name="host">The host name a program asks for.</param>
    /// <param name="patterns">The patterns of the profile.</param>
    /// <returns>True when a pattern matches, ignoring case and a final dot.</returns>
    public static bool Matches(string host, IEnumerable<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(patterns);
        var name = host.TrimEnd('.');
        if (name.Length == 0) return false;
        foreach (var pattern in patterns)
        {
            if (!IsValid(pattern)) continue;
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                if (name.Length > pattern.Length - 1 && name.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (name.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
