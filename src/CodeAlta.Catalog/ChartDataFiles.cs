using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Catalog.Worktrees;

namespace CodeAlta.Catalog;

/// <summary>A data file read for a chart: its status, and its text for <c>ok</c>.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>not_found</c>; <c>outside</c> for a path that leaves the folder (<c>..</c>, a full path, a link or a junction to
/// a place outside it); <c>not_data</c> for a file that is no CSV, TSV or JSON file; <c>too_large</c> past
/// <see cref="ChartDataFiles.MaximumBytes"/>; <c>no_project</c> when there is no folder to read from; <c>failed</c>.
/// </param>
/// <param name="Text">The text of the file; null unless <c>ok</c>.</param>
public sealed record ChartDataRead(string Status, string? Text = null);

/// <summary>
/// The data files that the charts of the Markdown name (<c>"data": {"url": "out/results.csv"}</c> in a <c>vega-lite</c> block),
/// read for the window that draws them. A chart is data that a model wrote, which a prompt injection can steer: it reads only a
/// CSV, TSV or JSON file below the folder it is read in (the folder of a session, or of a document of a project), by a relative
/// path without <c>..</c>, through no link or junction that leads outside that folder, and of
/// <see cref="MaximumBytes"/> at most.
/// </summary>
public static partial class ChartDataFiles
{
    /// <summary>The largest data file read, in bytes: <c>maximumChartFileBytes</c> of the window.</summary>
    public const int MaximumBytes = 4 * 1024 * 1024;

    /// <summary>The longest path read, in UTF-16 units.</summary>
    public const int MaximumPathLength = 1024;

    // The extensions of the data files a chart reads.
    private static readonly string[] Extensions = [".csv", ".tsv", ".json", ".geojson", ".topojson"];

    /// <summary>
    /// The parts of a path as a chart may name a data file: relative, with <c>/</c> or <c>\</c>, perhaps after <c>./</c>.
    /// </summary>
    /// <param name="path">The path as the chart writes it.</param>
    /// <param name="parts">The names of its folders and of the file; empty unless the path is accepted.</param>
    /// <returns><c>ok</c>, <c>outside</c> for a path that names a place outside the folder, <c>not_data</c> for a file that is no data file, or <c>invalid_request</c>.</returns>
    public static string Parse(string? path, out string[] parts)
    {
        parts = [];
        if (path is not { Length: > 0 and <= MaximumPathLength } || path.Any(static c => char.IsControl(c) || c is '?' or '#' or '*' or '"' or '<' or '>' or '|')) return "invalid_request";
        // A drive, an address, a full path, the home folder, an alternate data stream: none is below the folder.
        if (path.Contains(':') || path[0] is '/' or '\\' or '~' || Path.IsPathRooted(path)) return "outside";
        var named = path.Replace('\\', '/').Split('/').ToList();
        while (named.Count > 1 && named[0] == ".") named.RemoveAt(0);
        if (named.Contains("..") || named.Contains(".")) return "outside";
        // Windows drops trailing dots and spaces of a name: such a part would address another name.
        if (named.Any(static part => part.Length == 0 || part != part.Trim() || part.EndsWith('.'))) return "invalid_request";
        if (!Extensions.Any(extension => named[^1].EndsWith(extension, StringComparison.OrdinalIgnoreCase))) return "not_data";
        parts = [.. named];
        return "ok";
    }

    /// <summary>Reads a data file that a chart names, below a folder.</summary>
    /// <param name="root">The folder nothing is read outside of: the folder of the session, or of the project.</param>
    /// <param name="directory">The folder below <paramref name="root"/> that a relative path starts from, with <c>/</c>; null for the root itself.</param>
    /// <param name="path">The path as the chart writes it.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The text of the file, or why it was not read.</returns>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public static async Task<ChartDataRead> ReadAsync(string? root, string? directory, string? path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return new("no_project");
        var status = Parse(path, out var parts);
        if (status != "ok") return new(status);
        string[] folders = [];
        if (!string.IsNullOrEmpty(directory))
        {
            var start = Parse(directory + "/x.csv", out var below);
            if (start != "ok") return new(start == "outside" ? "outside" : "invalid_request");
            folders = below[..^1];
        }

        try
        {
            var full = Path.GetFullPath(root);
            var resolvedRoot = Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
            var current = full;
            string[] all = [.. folders, .. parts];
            for (var index = 0; index < all.Length; index++)
            {
                current = Path.Combine(current, all[index]);
                FileSystemInfo entry = index == all.Length - 1 ? new FileInfo(current) : new DirectoryInfo(current);
                if (!entry.Exists && entry.LinkTarget is null) return new("not_found");
                // A link or a junction is followed only to a place inside the folder.
                if (entry.LinkTarget is not null)
                {
                    var target = entry.ResolveLinkTarget(returnFinalTarget: true);
                    if (target is null || !target.Exists) return new("not_found");
                    if (!GitWorktreeService.IsWithin(target.FullName, full) && !GitWorktreeService.IsWithin(target.FullName, resolvedRoot)) return new("outside");
                }
            }

            if (Directory.Exists(current)) return new("not_found");
            await using var stream = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            if (stream.Length > MaximumBytes) return new("too_large");
            // One byte more than the limit shows a file that grew past it while it was read.
            var buffer = new byte[(int)stream.Length + 1];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (count > MaximumBytes) return new("too_large");
            var bytes = buffer.AsSpan(0, count);
            // Data is text: a file with a NUL is something else named as data (the encodings that have one start with a byte order mark).
            if (bytes is not ([0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]) && bytes.Contains((byte)0)) return new("not_data");
            using var reader = new StreamReader(new MemoryStream(buffer, 0, count), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return new("ok", await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new("not_found");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return new("failed");
        }
    }

    /// <summary>
    /// The data files that the <c>vega-lite</c> (or <c>vegalite</c>) blocks of a text of Markdown name, as they write them: what
    /// a message vouches for, where a reader that is not the window (the page of the phones) names a file to read.
    /// </summary>
    /// <param name="markdown">The text.</param>
    /// <returns>The paths, in the order of the text, each once.</returns>
    public static IReadOnlyList<string> NamedIn(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return [];
        var found = new List<string>();
        try
        {
            foreach (Match block in ChartBlock().Matches(markdown))
            {
                try
                {
                    using var document = JsonDocument.Parse(block.Groups["body"].Value, new JsonDocumentOptions { MaxDepth = 256 });
                    Collect(document.RootElement, null, found);
                }
                catch (JsonException)
                {
                    // Not a spec: it names no file.
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A text too long to search names what was found in it before.
        }

        return found.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void Collect(JsonElement element, string? name, List<string> found)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Collect(item, null, found);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object) return;
        if (name == "data" && element.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String && url.GetString() is { Length: > 0 } value) found.Add(value);
        foreach (var property in element.EnumerateObject()) Collect(property.Value, property.Name, found);
    }

    // A fenced block of a chart: its fence (``` or ~~~, three or more), the language, its body, and the same fence to close it.
    // The end of a line is before a line feed only: the carriage return of a text written on Windows is taken before it.
    [GeneratedRegex(@"^[ ]{0,3}(?<fence>`{3,}|~{3,})[ \t]*vega-?lite[ \t]*\r?\n(?<body>.*?)\r?\n[ ]{0,3}\k<fence>[`~]*[ \t]*\r?$",RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ChartBlock();
}
