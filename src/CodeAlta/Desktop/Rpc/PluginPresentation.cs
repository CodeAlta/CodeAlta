using System.Text.RegularExpressions;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// How the Settings page shows a source plugin, read from its package folder: the icon, the color, the version and the
/// author its <c>[Plugin]</c> attribute names, when the plugin does not run and cannot say them itself, and the picture
/// of its card, a file <c>plugin.webp</c>, <c>plugin.png</c>, <c>plugin.jpg</c> or <c>plugin.jpeg</c> beside
/// <c>plugin.cs</c>.
/// </summary>
internal static partial class PluginPresentation
{
    /// <summary>The largest picture of a card that is read.</summary>
    internal const int MaximumImageLength = 2 * 1024 * 1024;

    // The attribute is at the head of the entry file, after its usings and a comment.
    private const int MaximumSourceLength = 32 * 1024;

    private static readonly (string Extension, string Mime)[] ImageTypes =
        [(".webp", "image/webp"), (".png", "image/png"), (".jpg", "image/jpeg"), (".jpeg", "image/jpeg")];

    /// <summary>What a <c>[Plugin]</c> attribute says of how the plugin is shown; a value it does not give is null.</summary>
    internal readonly record struct Source(string? Icon, string? Color, string? Version, string? Author);

    /// <summary>The picture of a card: its file, its media type and a token that changes when the file does.</summary>
    internal readonly record struct Image(string Path, string Mime, string Token);

    /// <summary>
    /// Reads the <c>[Plugin]</c> attribute of an entry file as text: the plugin is not built for it. A file that cannot be
    /// read, or has no such attribute, gives nothing.
    /// </summary>
    /// <param name="entryFilePath">The <c>plugin.cs</c> of the package.</param>
    internal static Source ReadSource(string? entryFilePath)
    {
        if (entryFilePath is null) return default;
        string text;
        try
        {
            using var reader = new StreamReader(entryFilePath);
            var buffer = new char[MaximumSourceLength];
            text = new string(buffer, 0, reader.ReadBlock(buffer));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return default;
        }

        try
        {
            var attribute = PluginAttributePattern().Match(text);
            if (!attribute.Success) return default;
            var arguments = attribute.Groups["arguments"].Value;
            return new(Icon(Named(arguments, "Icon")), Color(Named(arguments, "Color")), Named(arguments, "Version"), Named(arguments, "Author"));
        }
        catch (Exception exception) when (exception is RegexMatchTimeoutException or ArgumentException)
        {
            // A source that does not read as an attribute says nothing.
            return default;
        }
    }

    /// <summary>The name of an icon a plugin gives, when it is one: lowercase letters, digits and dashes.</summary>
    internal static string? Icon(string? value)
        => value?.Trim() is { Length: > 0 and <= 64 } icon && IconPattern().IsMatch(icon) ? icon : null;

    /// <summary>The color a plugin gives, when it is one: <c>#rgb</c> or <c>#rrggbb</c>.</summary>
    internal static string? Color(string? value)
        => value?.Trim() is { } color && ColorPattern().IsMatch(color) ? color.ToLowerInvariant() : null;

    /// <summary>Finds the picture of the card of a package, when its folder has one that is not too large.</summary>
    /// <param name="packageDirectory">The folder of the package.</param>
    internal static Image? FindImage(string? packageDirectory)
    {
        if (packageDirectory is null) return null;
        foreach (var (extension, mime) in ImageTypes)
        {
            try
            {
                var file = new FileInfo(Path.Combine(packageDirectory, "plugin" + extension));
                if (file.Exists && file.Length is > 0 and <= MaximumImageLength)
                {
                    return new(file.FullName, mime, $"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                // A file that cannot be looked at is no picture.
            }
        }

        return null;
    }

    // The value of a named argument that is a plain string literal; a verbatim or interpolated one is left alone.
    private static string? Named(string arguments, string name)
    {
        var match = Regex.Match(arguments, $@"(?<![A-Za-z0-9_]){name}\s*=\s*""(?<value>(?:[^""\\\r\n]|\\.)*)""", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        if (!match.Success) return null;
        var value = Regex.Unescape(match.Groups["value"].Value).Trim();
        var clean = new string([.. value.Where(static character => !char.IsControl(character))]);
        return clean.Length is > 0 and <= 128 ? clean : null;
    }

    // The attribute starts its line: one that a comment or a text quotes is not it.
    [GeneratedRegex(@"^[ \t]*\[\s*Plugin\s*\((?<arguments>(?:[^""]|""(?:[^""\\\r\n]|\\.)*"")*?)\)\s*\]", RegexOptions.CultureInvariant | RegexOptions.Multiline, 500)]
    private static partial Regex PluginAttributePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IconPattern();

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();
}
