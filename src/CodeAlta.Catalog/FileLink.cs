using System.Globalization;
using System.Text.RegularExpressions;

namespace CodeAlta.Catalog;

/// <summary>A link to a file of the disk, as the text of a message or of a document writes its target.</summary>
/// <remarks>
/// <para>
/// The target is a path, relative to the folder the link is read in or full, or a <c>file:</c> address. A place
/// in the file follows it: <c>#L10</c>, <c>#L10C5</c> and <c>#L10-L20</c> as a fragment, or <c>:10</c>,
/// <c>:10:5</c> and <c>:10-20</c> after the path. A range goes to its start, and <see cref="EndLine"/> holds its end.
/// </para>
/// <para>
/// Reading a target never touches the disk. A target that names another computer (a UNC path, a device path, a
/// <c>file:</c> address with a host) is no link to a file: looking at such a path would already send the
/// credentials of the user to that computer. Neither is a target with another scheme. Every reader of a link
/// goes through here, so that this rule has one place: what an answer writes is not to be trusted.
/// </para>
/// </remarks>
/// <param name="Path">The path, with the separators of the system: full, relative, or starting with <c>~</c> for the home folder.</param>
/// <param name="Line">The 1-based line to go to; null for none.</param>
/// <param name="Column">The 1-based column on that line; null for none.</param>
/// <param name="IsAddress">Whether the target was written as a <c>file:</c> address.</param>
public readonly partial record struct FileLink(string Path, int? Line, int? Column, bool IsAddress)
{
    /// <summary>The longest target that is read.</summary>
    public const int MaximumLength = 2048;

    /// <summary>
    /// The fragment of the target when it names no place in the file: the heading of a document, as a link of
    /// Markdown writes it (<c>#getting-started</c>). Null for none.
    /// </summary>
    public string? Anchor { get; init; }

    /// <summary>The 1-based last line of the range the target names (<c>#L10-L20</c>); null for none, or for a range that ends before it starts.</summary>
    public int? EndLine { get; init; }

    /// <summary>Reads the target of a link.</summary>
    /// <param name="target">The target, as the link writes it.</param>
    /// <param name="link">The link; its default when the target is none.</param>
    /// <returns>False for anything that is not a link to a file of this computer.</returns>
    public static bool TryParse(string? target, out FileLink link)
    {
        link = default;
        if (target is not { Length: > 0 and <= MaximumLength } || target != target.Trim() || target.Any(char.IsControl)) return false;
        return target.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? TryParseAddress(target, out link) : TryParsePath(target, out link);
    }

    /// <summary>The full path this link names. It may look at the disk for a path that starts at the root of a drive.</summary>
    /// <param name="start">The folder a relative path starts from; null for none.</param>
    /// <param name="home">The home folder of the user, which a path starting with <c>~</c> names; empty for none.</param>
    /// <returns>The full path; null when the link has none, such as a relative path without a folder to start from.</returns>
    public string? Locate(string? start, string home)
    {
        ArgumentNullException.ThrowIfNull(home);
        var path = Path;
        try
        {
            if (path == "~" || path.StartsWith("~" + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return home.Length == 0 ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(home, path.Length > 2 ? path[2..] : string.Empty));
            if (System.IO.Path.IsPathFullyQualified(path))
            {
                var full = System.IO.Path.GetFullPath(path);
                // "/src/file" is also written for a file of the project, as a site writes a path from its root: never as an address.
                if (IsAddress || start is null || !path.StartsWith(System.IO.Path.DirectorySeparatorChar) || System.IO.Path.Exists(full)) return full;
            }
            else if (!path.StartsWith(System.IO.Path.DirectorySeparatorChar) && System.IO.Path.IsPathRooted(path))
            {
                return null; // "C:file": a path that depends on the current folder of a drive.
            }

            return start is null || !System.IO.Path.IsPathFullyQualified(start) ? null
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(start, path.TrimStart(System.IO.Path.DirectorySeparatorChar)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryParseAddress(string target, out FileLink link)
    {
        link = default;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc) return false;
        if (uri.Host.Length > 0 && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
        var path = uri.LocalPath;
        if (path.Any(char.IsControl) || !System.IO.Path.IsPathFullyQualified(path) || NamesAnotherComputer(path)) return false;
        var (line, column, endLine) = uri.Fragment.Length > 1 ? Place(uri.Fragment.AsSpan(1)) : (null, null, null);
        var anchor = line is null && uri.Fragment.Length > 1 ? HeadingAnchor(Uri.UnescapeDataString(uri.Fragment[1..])) : null;
        if (line is null) (path, line, column, endLine) = PlaceAfter(path);
        link = new(path.Replace('/', System.IO.Path.DirectorySeparatorChar), line, column, IsAddress: true) { Anchor = anchor, EndLine = endLine };
        return true;
    }

    private static bool TryParsePath(string target, out FileLink link)
    {
        link = default;
        var fragment = target.IndexOf('#');
        // Markdown writes a space, a backslash and a letter outside ASCII of a target as an escape.
        var path = Uri.UnescapeDataString(fragment < 0 ? target : target[..fragment]).Replace('\\', '/');
        if (path.Length == 0 || path.Any(char.IsControl) || NamesAnotherComputer(path)) return false;
        var (line, column, endLine) = fragment >= 0 ? Place(target.AsSpan(fragment + 1)) : (null, null, null);
        var anchor = line is null && fragment >= 0 ? HeadingAnchor(target[(fragment + 1)..]) : null;
        if (line is null) (path, line, column, endLine) = PlaceAfter(path);
        // "/C:/folder/file", as an address without its scheme writes a path of Windows.
        if (OperatingSystem.IsWindows() && path.Length >= 3 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':') path = path[1..];
        var colon = path.IndexOf(':');
        if (colon >= 0)
        {
            // A drive is the one colon of a path; any other is a scheme, or a stream of a file.
            var drive = OperatingSystem.IsWindows() && colon == 1 && char.IsAsciiLetter(path[0]) && (path.Length == 2 || path[2] == '/')
                && path.IndexOf(':', 2) < 0;
            var name = !OperatingSystem.IsWindows() && path.AsSpan(0, colon).Contains('/');
            if (!drive && !name) return false;
        }

        if (path.Length == 0) return false;
        link = new(path.Replace('/', System.IO.Path.DirectorySeparatorChar), line, column, IsAddress: false) { Anchor = anchor, EndLine = endLine };
        return true;
    }

    // The address of a heading: letters, digits, dashes and underscores. Anything else names no heading.
    private static string? HeadingAnchor(string fragment) => HeadingName().IsMatch(fragment) ? fragment : null;

    // "//server/share", "//?/C:/", "//./device" and "/??/UNC/server/share": the first and the last name another computer, the
    // others leave the rules of a path. .NET takes "\??\" for a full path, which it hands to the system as it is written.
    private static bool NamesAnotherComputer(string path)
    {
        if (path.Length < 2 || path[0] is not ('/' or '\\')) return false;
        return path[1] is '/' or '\\' || (path.Length >= 4 && path[1] == '?' && path[2] == '?' && path[3] is '/' or '\\');
    }

    // The place a fragment names: "L10", "L10C5", "L10-L20", or the same without the letter.
    private static (int? Line, int? Column, int? EndLine) Place(ReadOnlySpan<char> fragment)
        => FragmentPlace().Match(fragment.ToString()) is { Success: true } match ? Numbers(match) : (null, null, null);

    // The place written after a path: ":10", ":10:5", ":10-20".
    private static (string Path, int? Line, int? Column, int? EndLine) PlaceAfter(string path)
    {
        if (PathPlace().Match(path) is not { Success: true } match || match.Index == 0) return (path, null, null, null);
        var (line, column, endLine) = Numbers(match);
        return (path[..match.Index], line, column, endLine);
    }

    private static (int? Line, int? Column, int? EndLine) Numbers(Match match)
    {
        var line = int.Parse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        if (line < 1) return (null, null, null);
        int? column = match.Groups[2].Success ? int.Parse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null;
        int? endLine = match.Groups[3].Success ? int.Parse(match.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture) : null;
        return (line, column is < 1 ? null : column, endLine >= line ? endLine : null);
    }

    [GeneratedRegex(@"^L?(\d{1,7})(?:[C:](\d{1,7}))?(?:-L?(\d{1,7})(?:[C:]\d{1,7})?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FragmentPlace();

    [GeneratedRegex(@":(\d{1,7})(?::(\d{1,7}))?(?:-(\d{1,7})(?::\d{1,7})?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PathPlace();

    [GeneratedRegex(@"^[\p{L}\p{N}_-]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingName();
}
