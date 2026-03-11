using System.Text.RegularExpressions;

namespace CodeAlta.Workspaces;

/// <summary>
/// Validates workspace and project slugs.
/// </summary>
public static partial class WorkspaceKeyValidator
{
    /// <summary>
    /// Validates the slug format (<c>^[a-z0-9][a-z0-9\-_.]{1,63}$</c>).
    /// </summary>
    /// <param name="key">The slug to validate.</param>
    /// <param name="paramName">The associated parameter name.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is invalid.</exception>
    public static void Validate(string? key, string? paramName = null)
    {
        if (!IsValid(key))
        {
            throw new ArgumentException(
                "Slugs must match ^[a-z0-9][a-z0-9\\-_.]{1,63}$.",
                paramName ?? nameof(key));
        }
    }

    /// <summary>
    /// Determines whether a slug is valid.
    /// </summary>
    /// <param name="key">The slug to validate.</param>
    /// <returns><see langword="true"/> when valid; otherwise <see langword="false"/>.</returns>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        return KeyRegex().IsMatch(key);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9\\-_.]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();
}
