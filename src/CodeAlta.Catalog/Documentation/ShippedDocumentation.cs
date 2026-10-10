using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SharpYaml;
using SharpYaml.Model;

namespace CodeAlta.Catalog.Documentation;

/// <summary>One entry of the navigation of the user guide, as its <c>menu.yml</c> lists it.</summary>
/// <param name="Path">The page of the entry: its path below the folder of the guide, with forward slashes.</param>
/// <param name="Title">The title of the entry, as text.</param>
/// <param name="Icon">The name of the icon the menu gives the entry (the <c>bi-</c> class of its title, without the prefix); null for none.</param>
/// <param name="Children">The entries of the folder of the page, when the menu says that it has its own.</param>
public sealed record ShippedDocumentationMenuItem(string Path, string Title, string? Icon, IReadOnlyList<ShippedDocumentationMenuItem> Children);

/// <summary>A page of the user guide and its title.</summary>
/// <param name="Path">The path of the page below the folder of the guide, with forward slashes.</param>
/// <param name="Title">The title of the page.</param>
public sealed record ShippedDocumentationPageInfo(string Path, string Title);

/// <summary>A picture of a page: a file of the pictures of the guide, or a drawing the page holds.</summary>
/// <param name="Image">The name of the file among the pictures of the guide; null for a drawing.</param>
/// <param name="Svg">The SVG of a drawing, as the page writes it; null for a file.</param>
/// <param name="Alt">What the picture shows, as text.</param>
/// <param name="Caption">The caption, as the inline HTML of the page; null for none.</param>
public sealed record ShippedDocumentationFigure(string? Image, string? Svg, string Alt, string? Caption)
{
    /// <summary>Gets the width of the picture of a file, in pixels, when its header says it: a reader keeps its place before the file is read.</summary>
    public int? Width { get; init; }

    /// <summary>Gets the height of the picture of a file, in pixels, when its header says it.</summary>
    public int? Height { get; init; }
}

/// <summary>One part of a page: a text of Markdown, or a picture.</summary>
/// <param name="Markdown">The Markdown of a text; null for a picture.</param>
/// <param name="Figure">The picture; null for a text.</param>
public sealed record ShippedDocumentationBlock(string? Markdown, ShippedDocumentationFigure? Figure);

/// <summary>
/// A page of the user guide, read for an application: the templates of the site are replaced by what they stand
/// for, the pictures are parts of their own, and every link to another page names it from the folder of the guide.
/// </summary>
/// <param name="Path">The path of the page below the folder of the guide, with forward slashes.</param>
/// <param name="Title">The title of the page.</param>
/// <param name="Blocks">The parts of the page, in their order.</param>
public sealed record ShippedDocumentationPage(string Path, string Title, IReadOnlyList<ShippedDocumentationBlock> Blocks)
{
    /// <summary>
    /// Gets the page as one text of Markdown: a picture is an image whose address is its file below the folder of
    /// the guide, followed by its caption.
    /// </summary>
    /// <returns>The Markdown, which ends with a line break.</returns>
    public string ToMarkdown()
    {
        var builder = new StringBuilder();
        foreach (var block in Blocks)
        {
            if (builder.Length > 0) builder.Append('\n');
            if (block.Figure is { } figure)
            {
                var alt = figure.Alt.Replace("[", "(", StringComparison.Ordinal).Replace("]", ")", StringComparison.Ordinal);
                builder.Append(figure.Image is { } image ? $"![{alt}]({ShippedDocumentation.ImageFolder}/{image})" : $"*{alt}*").Append('\n');
                if (figure.Caption is { Length: > 0 } caption) builder.Append('\n').Append(caption).Append('\n');
            }
            else if (block.Markdown is { } markdown)
            {
                builder.Append(markdown).Append('\n');
            }
        }

        return builder.ToString();
    }
}

/// <summary>A picture of the user guide, read from its file.</summary>
/// <param name="Name">The name of the file.</param>
/// <param name="MediaType">The media type its extension names.</param>
/// <param name="Content">The bytes of the file.</param>
public sealed record ShippedDocumentationImage(string Name, string MediaType, ReadOnlyMemory<byte> Content);

/// <summary>A place of the user guide where a text was found.</summary>
/// <param name="Path">The page.</param>
/// <param name="Title">The title of the page.</param>
/// <param name="Heading">The heading the place is under, as text; null for the start of the page.</param>
/// <param name="Text">The line that has the text, without its markup, shortened around the text.</param>
public sealed record ShippedDocumentationSearchHit(string Path, string Title, string? Heading, string Text);

/// <summary>
/// The user guide that ships with an application: the pages of the site (<c>*.md</c>), their navigation
/// (<c>menu.yml</c>, one for the guide and one for each folder the menu says has its own) and their pictures
/// (<c>img</c>).
/// </summary>
/// <remarks>
/// <para>
/// The pages and the pictures are listed once, skipping links of the file system (symbolic links, junctions),
/// and a caller names one of them by its path
/// below the folder or by its file name: a name that is not in the list is answered with nothing, whatever it is
/// (a full path, a path with <c>..</c>, an address).
/// </para>
/// <para>
/// Every file read rechecks the file and its ancestors through the guide root, refusing observed links or
/// missing directories. These checks are not atomic with opening the file: concurrent filesystem replacement
/// can still race them. The parents of the supplied guide root are trusted.
/// </para>
/// <para>
/// A page is the Markdown of the site, which the site runs templates over. <see cref="ReadPage"/> replaces the
/// templates the pages use (the screenshots, the marks of the comparison tables, the addresses of the site) so
/// that an application and an agent read plain Markdown.
/// </para>
/// </remarks>
public sealed partial class ShippedDocumentation
{
    /// <summary>The name of the file that lists the navigation of a folder.</summary>
    public const string MenuFileName = "menu.yml";

    /// <summary>The page the guide starts with.</summary>
    public const string HomePage = "readme.md";

    /// <summary>The folder of the pictures, below the folder of the guide.</summary>
    public const string ImageFolder = "img";

    /// <summary>The largest page that is read, in bytes.</summary>
    public const int MaximumPageBytes = 1024 * 1024;

    /// <summary>The largest picture that is read, in bytes.</summary>
    public const int MaximumImageBytes = 8 * 1024 * 1024;

    /// <summary>The longest path of a page, and the longest name of a picture.</summary>
    public const int MaximumPathLength = 200;

    private const int MaximumMenuBytes = 256 * 1024;
    private const int MaximumPages = 512;
    private const int MaximumImages = 1024;
    private const int MaximumDepth = 4;
    private const int MaximumMenuDepth = 3;
    private const int MaximumTitleLength = 200;
    private const int MaximumSearchHits = 40;
    private const int MaximumSearchHitsOfPage = 6;

    private readonly Lock _gate = new();
    private Index? _index;

    /// <summary>Creates the guide of a folder. Nothing is read until something is asked.</summary>
    /// <param name="root">The folder of the guide.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is null, blank or no path.</exception>
    public ShippedDocumentation(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
    }

    /// <summary>Gets the full path of the folder of the guide.</summary>
    public string Root { get; }

    /// <summary>Gets a value indicating whether the folder holds a guide: at least one page.</summary>
    public bool Available => GetIndex().Pages.Count > 0;

    /// <summary>Gets the page the guide opens with: <see cref="HomePage"/> when it is there, otherwise the first entry of the menu.</summary>
    public string? Home
    {
        get
        {
            var index = GetIndex();
            return index.Pages.TryGetValue(HomePage, out var home) ? home.Key : index.Menu.Count > 0 ? index.Menu[0].Path : null;
        }
    }

    /// <summary>
    /// Gets the navigation of the guide, read from its <c>menu.yml</c>. A guide without a menu, or whose menu
    /// names no page it has, lists its pages by name, the first page first.
    /// </summary>
    /// <returns>The entries, in the order of the menu.</returns>
    public IReadOnlyList<ShippedDocumentationMenuItem> GetMenu() => GetIndex().Menu;

    /// <summary>Lists every page of the guide, whether the menu names it or not, by path.</summary>
    /// <returns>The pages and their titles.</returns>
    public IReadOnlyList<ShippedDocumentationPageInfo> ListPages() => GetIndex().List;

    /// <summary>Finds the page a path names.</summary>
    /// <param name="path">A path below the folder of the guide, with forward slashes.</param>
    /// <returns>The path as the guide writes it; null when the guide has no such page.</returns>
    public string? ResolvePage(string? path)
        => NormalizePath(path) is { } key && GetIndex().Pages.TryGetValue(key, out var page) ? page.Key : null;

    /// <summary>Gets the full path of the file of a page, for a reader that is given the folder of the guide.</summary>
    /// <param name="path">A path below the folder of the guide, with forward slashes.</param>
    /// <returns>The full path; null when the guide has no such page.</returns>
    public string? GetPageFile(string? path)
        => NormalizePath(path) is { } key && GetIndex().Pages.TryGetValue(key, out var page) ? page.FullPath : null;

    /// <summary>Finds the page a file of the disk is, when it is one of the guide.</summary>
    /// <param name="fullPath">The full path of a file.</param>
    /// <returns>The path of the page below the folder of the guide; null when the file is no page of the guide.</returns>
    public string? FindPage(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return null;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(fullPath)) return null;
            var full = System.IO.Path.GetFullPath(fullPath);
            foreach (var page in GetIndex().Pages.Values)
            {
                if (string.Equals(page.FullPath, full, PathComparison)) return page.Key;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }

        return null;
    }

    /// <summary>Reads a page.</summary>
    /// <param name="path">A path below the folder of the guide, with forward slashes.</param>
    /// <returns>The page; null when the guide has no such page, or when its file cannot be read.</returns>
    public ShippedDocumentationPage? ReadPage(string? path)
    {
        var index = GetIndex();
        if (NormalizePath(path) is not { } key || !index.Pages.TryGetValue(key, out var page)) return null;
        if (ReadBounded(page.FullPath, MaximumPageBytes) is not { } bytes) return null;
        var (_, body) = ShippedDocumentationMarkup.SplitFrontMatter(DecodeText(bytes));
        var blocks = ShippedDocumentationMarkup.Expand(body, page.Key,
            candidate => NormalizePath(candidate) is { } normalized && index.Pages.TryGetValue(normalized, out var found) ? found.Key : null,
            name => index.Images.ContainsKey(name));
        // The size of each picture, so that a reader keeps its place before the file is read.
        return new(page.Key, page.Title, [.. blocks.Select(block => block.Figure is { Image: { } name } figure && index.Images.TryGetValue(name, out var image) && image.Size is { } size
            ? new ShippedDocumentationBlock(null, figure with { Width = size.Width, Height = size.Height })
            : block)]);
    }

    /// <summary>Reads a picture of the guide.</summary>
    /// <param name="name">The name of a file of the pictures of the guide.</param>
    /// <returns>The picture; null when the guide has no such picture, or when its file cannot be read.</returns>
    public ShippedDocumentationImage? ReadImage(string? name)
    {
        if (name is null || !IsSegment(name) || !GetIndex().Images.TryGetValue(name, out var image)) return null;
        return ReadBounded(image.FullPath, MaximumImageBytes) is { } bytes ? new(image.Name, image.MediaType, bytes) : null;
    }

    /// <summary>Finds a text in the pages of the guide, whatever its case.</summary>
    /// <param name="text">The text: two characters at least.</param>
    /// <returns>The places, in the order of the pages; none for a text that is too short.</returns>
    public IReadOnlyList<ShippedDocumentationSearchHit> Search(string? text)
    {
        var query = text?.Trim();
        if (query is not { Length: >= 2 and <= 100 }) return [];
        var hits = new List<ShippedDocumentationSearchHit>();
        foreach (var info in OrderedPages())
        {
            if (hits.Count >= MaximumSearchHits) break;
            if (ReadPage(info.Path) is not { } page) continue;
            ShippedDocumentationMarkup.Search(page, query, MaximumSearchHitsOfPage, MaximumSearchHits - hits.Count, hits);
        }

        return hits;
    }

    /// <summary>Whether a text is the path of a page as a caller may write it: names separated by forward slashes, nothing else.</summary>
    /// <param name="path">The text.</param>
    /// <returns>The path; null for anything that is no such path (a full path, a path with <c>.</c> or <c>..</c>, a backslash, an address).</returns>
    public static string? NormalizePath(string? path)
    {
        if (path is not { Length: > 0 and <= MaximumPathLength }) return null;
        var start = 0;
        while (true)
        {
            var slash = path.IndexOf('/', start);
            var segment = slash < 0 ? path.AsSpan(start) : path.AsSpan(start, slash - start);
            if (!IsSegment(segment)) return null;
            if (slash < 0) return path;
            start = slash + 1;
        }
    }

    // The pages in the order a reader meets them: the ones of the menu first, then the others.
    private IEnumerable<ShippedDocumentationPageInfo> OrderedPages()
    {
        var index = GetIndex();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ShippedDocumentationPageInfo>();
        void Add(IReadOnlyList<ShippedDocumentationMenuItem> items)
        {
            foreach (var item in items)
            {
                if (seen.Add(item.Path) && index.Pages.TryGetValue(item.Path, out var page)) ordered.Add(new(page.Key, page.Title));
                Add(item.Children);
            }
        }

        Add(index.Menu);
        ordered.AddRange(index.List.Where(page => seen.Add(page.Path)));
        return ordered;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // A name of a file or of a folder of the guide: letters, digits, '.', '_' and '-', starting with a letter or a digit.
    private static bool IsSegment(ReadOnlySpan<char> name)
    {
        if (name.Length is 0 or > 100 || !char.IsAsciiLetterOrDigit(name[0]) || name[^1] == '.') return false;
        foreach (var character in name)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) return false;
        }

        return true;
    }

    private Index GetIndex()
    {
        lock (_gate)
        {
            if (_index is { } known) return known;
            var index = BuildIndex();
            // A folder that is not there yet is looked at again.
            if (index.Pages.Count > 0) _index = index;
            return index;
        }
    }

    private Index BuildIndex()
    {
        var pages = new Dictionary<string, PageEntry>(StringComparer.OrdinalIgnoreCase);
        var menus = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var images = new Dictionary<string, ImageEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = new DirectoryInfo(Root);
            if (root.Exists && !IsLink(root))
            {
                Collect(root, string.Empty, 0, pages, menus);
                var pictures = new DirectoryInfo(System.IO.Path.Combine(Root, ImageFolder));
                if (pictures.Exists && !IsLink(pictures))
                {
                    foreach (var file in pictures.EnumerateFiles().OrderBy(static file => file.Name, StringComparer.Ordinal))
                    {
                        if (images.Count >= MaximumImages) break;
                        if (IsLink(file) || !IsSegment(file.Name) || file.Length > MaximumImageBytes || MediaType(file.Extension) is not { } mediaType) continue;
                        images.TryAdd(file.Name, new(file.Name, file.FullName, mediaType, ReadSize(file.FullName)));
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // What could be listed is the guide.
        }

        var titled = new Dictionary<string, PageEntry>(pages.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages.Values) titled[page.Key] = page with { Title = ReadTitle(page) };
        var list = titled.Values.OrderBy(static page => page.Key, StringComparer.OrdinalIgnoreCase).Select(static page => new ShippedDocumentationPageInfo(page.Key, page.Title)).ToArray();
        var menu = ReadMenu(string.Empty, null, titled, menus, 0);
        if (menu.Count == 0)
        {
            menu = [.. list.OrderBy(static page => string.Equals(page.Path, HomePage, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(static page => page.Path.Count(static character => character == '/'))
                .ThenBy(static page => page.Path, StringComparer.OrdinalIgnoreCase)
                .Select(static page => new ShippedDocumentationMenuItem(page.Path, page.Title, null, []))];
        }

        return new(titled.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), images.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), menu, list);
    }

    // The pages and the menus of a folder and of the folders in it. A link of the file system is never followed, and never listed.
    private static void Collect(DirectoryInfo directory, string prefix, int depth, Dictionary<string, PageEntry> pages, Dictionary<string, string> menus)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos().OrderBy(static entry => entry.Name, StringComparer.Ordinal))
        {
            if (IsLink(entry) || !IsSegment(entry.Name)) continue;
            if (entry is DirectoryInfo child)
            {
                // The pictures are no pages.
                if (depth + 1 < MaximumDepth && !(prefix.Length == 0 && string.Equals(child.Name, ImageFolder, StringComparison.OrdinalIgnoreCase)))
                    Collect(child, prefix + child.Name + "/", depth + 1, pages, menus);
            }
            else if (entry is FileInfo file)
            {
                var key = prefix + file.Name;
                if (key.Length > MaximumPathLength) continue;
                if (string.Equals(file.Name, MenuFileName, StringComparison.OrdinalIgnoreCase) && file.Length <= MaximumMenuBytes) menus.TryAdd(prefix, file.FullName);
                else if (string.Equals(file.Extension, ".md", StringComparison.OrdinalIgnoreCase) && file.Length <= MaximumPageBytes && pages.Count < MaximumPages)
                    pages.TryAdd(key, new(key, file.FullName, string.Empty));
            }
        }
    }

    private static bool IsLink(FileSystemInfo entry) => (entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null;

    private static string? MediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".webp" => "image/webp",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => null,
    };

    // The title of a page: the one of its front matter, else its first heading, else its name.
    private string ReadTitle(PageEntry page)
    {
        if (ReadBounded(page.FullPath, MaximumPageBytes) is { } bytes)
        {
            var (title, body) = ShippedDocumentationMarkup.SplitFrontMatter(DecodeText(bytes));
            title ??= ShippedDocumentationMarkup.FirstHeading(body);
            if (PlainText(title) is { Length: > 0 } text) return text;
        }

        return System.IO.Path.GetFileNameWithoutExtension(page.Key);
    }

    // The entries of the menu of a folder. `folder: true` on an entry says that the folder of its page has a menu of its own.
    private IReadOnlyList<ShippedDocumentationMenuItem> ReadMenu(string prefix, string? parent, Dictionary<string, PageEntry> pages, Dictionary<string, string> menus, int depth)
    {
        if (!menus.TryGetValue(prefix, out var file) || ReadBounded(file, MaximumMenuBytes) is not { } bytes) return [];
        YamlStream stream;
        try
        {
            stream = YamlStream.Load(new StringReader(DecodeText(bytes)));
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException or ArgumentException or FormatException)
        {
            return [];
        }

        if (stream.Count == 0 || stream[0].Contents is not YamlMapping root) return [];
        var items = new List<ShippedDocumentationMenuItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var menu in root)
        {
            if (menu.Value is not YamlSequence entries) continue;
            foreach (var element in entries)
            {
                if (element is not YamlMapping entry || Scalar(entry, "path") is not { } path) continue;
                // A path of the menu starts from the folder of the menu, and stays in the guide.
                if (NormalizePath(prefix + path.Replace('\\', '/')) is not { } key || !pages.TryGetValue(key, out var page)) continue;
                if (string.Equals(page.Key, parent, StringComparison.OrdinalIgnoreCase) || !seen.Add(page.Key)) continue;
                var title = Scalar(entry, "title");
                IReadOnlyList<ShippedDocumentationMenuItem> children = [];
                var slash = page.Key.LastIndexOf('/');
                var folder = slash < 0 ? string.Empty : page.Key[..(slash + 1)];
                if (depth + 1 < MaximumMenuDepth && string.Equals(Scalar(entry, "folder"), "true", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(folder, prefix, StringComparison.OrdinalIgnoreCase))
                {
                    children = ReadMenu(folder, page.Key, pages, menus, depth + 1);
                }

                items.Add(new(page.Key, PlainText(title) is { Length: > 0 } text ? text : page.Title, IconOf(title), children));
            }
        }

        return items;
    }

    private static string? Scalar(YamlMapping mapping, string name)
    {
        foreach (var entry in mapping)
        {
            if (entry.Key is YamlValue key && key.Value == name) return (entry.Value as YamlValue)?.Value;
        }

        return null;
    }

    // The title of the menu is the HTML of the site: an icon, then the words.
    private static string? IconOf(string? title)
        => title is not null && MenuIcon().Match(title) is { Success: true } match ? match.Groups[1].Value : null;

    internal static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = Whitespace().Replace(WebUtility.HtmlDecode(Tag().Replace(html, string.Empty)), " ").Trim();
        return text.Length <= MaximumTitleLength ? text : text[..MaximumTitleLength];
    }

    private static string DecodeText(ReadOnlyMemory<byte> bytes)
    {
        var span = bytes.Span;
        if (span is [0xEF, 0xBB, 0xBF, ..]) span = span[3..];
        return Encoding.UTF8.GetString(span);
    }

    // Recheck the canonical file path and every ancestor, including the approved root, on each read.
    // Cached entries and leaf attributes do not reveal an ancestor replaced by a link. This is not atomic with open.
    private bool IsFileInGuide(string fullPath)
    {
        if (!fullPath.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, PathComparison)) return false;
        var file = new FileInfo(fullPath);
        if (!file.Exists || IsLink(file)) return false;
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || IsLink(directory)) return false;
            if (string.Equals(directory.FullName, Root, PathComparison)) return true;
        }

        return false;
    }

    // The bounded bytes of a listed file, after point-in-time path/link validation.
    private ReadOnlyMemory<byte>? ReadBounded(string fullPath, int maximum)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(fullPath);
            if (!IsFileInGuide(full)) return null;
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (stream.Length > maximum) return null;
            var buffer = new byte[(int)stream.Length];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // The size a picture declares in its header: PNG, GIF and the three forms of WebP. Null for anything else.
    private (int Width, int Height)? ReadSize(string fullPath)
    {
        Span<byte> header = stackalloc byte[32];
        int read;
        try
        {
            if (!IsFileInGuide(fullPath)) return null;
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.None);
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }

        return ImageSize(header[..read]);
    }

    /// <summary>Reads the size of a picture from the start of its file.</summary>
    internal static (int Width, int Height)? ImageSize(ReadOnlySpan<byte> header)
    {
        (int Width, int Height)? size = null;
        if (header.Length >= 24 && header[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            size = (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[16..]), System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header[20..]));
        }
        else if (header.Length >= 10 && header[..4].SequenceEqual("GIF8"u8))
        {
            size = (header[6] | header[7] << 8, header[8] | header[9] << 8);
        }
        else if (header.Length >= 30 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            var chunk = header[12..16];
            if (chunk.SequenceEqual("VP8X"u8))
            {
                size = ((header[24] | header[25] << 8 | header[26] << 16) + 1, (header[27] | header[28] << 8 | header[29] << 16) + 1);
            }
            else if (chunk.SequenceEqual("VP8L"u8) && header[20] == 0x2F)
            {
                var bits = header[21] | header[22] << 8 | header[23] << 16 | header[24] << 24;
                size = ((bits & 0x3FFF) + 1, (bits >> 14 & 0x3FFF) + 1);
            }
            else if (chunk.SequenceEqual("VP8 "u8) && header[23] == 0x9D && header[24] == 0x01 && header[25] == 0x2A)
            {
                size = ((header[26] | header[27] << 8) & 0x3FFF, (header[28] | header[29] << 8) & 0x3FFF);
            }
        }

        return size is { Width: > 0 and <= 65535, Height: > 0 and <= 65535 } ? size : null;
    }

    [GeneratedRegex(@"\bbi-([a-z0-9]+(?:-[a-z0-9]+)*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex MenuIcon();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private sealed record PageEntry(string Key, string FullPath, string Title);

    private sealed record ImageEntry(string Name, string FullPath, string MediaType, (int Width, int Height)? Size);

    private sealed record Index(FrozenDictionary<string, PageEntry> Pages, FrozenDictionary<string, ImageEntry> Images,
        IReadOnlyList<ShippedDocumentationMenuItem> Menu, IReadOnlyList<ShippedDocumentationPageInfo> List);
}
