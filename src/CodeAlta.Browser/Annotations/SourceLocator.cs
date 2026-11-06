using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Annotations;

/// <summary>What a source candidate is drawn from.</summary>
internal enum SourceKind
{
    /// <summary>The file of the page itself, a file CodeAlta serves.</summary>
    Page,

    /// <summary>The script that created the element, as the browser recorded it.</summary>
    Created,

    /// <summary>A style rule that applies to the element.</summary>
    Style,

    /// <summary>A file where the text, the id or the classes of the element are written.</summary>
    Text,
}

/// <summary>A place of the project an annotated element likely comes from.</summary>
/// <param name="Path">The file, relative to the project with <c>/</c> when it is inside it, else absolute.</param>
/// <param name="Line">The line, from 1.</param>
/// <param name="Kind">What the place is drawn from.</param>
/// <param name="Reason">Why, in a few words: what the page names in it (a selector, a function) is in a code span of Markdown.</param>
/// <param name="Certain">Whether the browser said so, rather than a search found it.</param>
internal sealed record SourceCandidate(string Path, int Line, SourceKind Kind, string Reason, bool Certain);

/// <summary>What the source of an annotated element is looked for with.</summary>
internal sealed record SourceQuery
{
    /// <summary>Gets the address of the page.</summary>
    public required Uri PageUrl { get; init; }

    /// <summary>Gets the folder of the project of the session.</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>Gets the folder the server of the page runs in or serves, or null.</summary>
    public string? ServerRoot { get; init; }

    /// <summary>Gets the file of an address CodeAlta serves itself, or null for others.</summary>
    public Func<Uri, string?>? LocalFile { get; init; }

    /// <summary>Gets the element as the page reports it.</summary>
    public required AnnotatedElement Element { get; init; }

    /// <summary>Gets what the browser says of it.</summary>
    public ElementEvidence Evidence { get; init; } = ElementEvidence.None;

    /// <summary>Gets the addresses the page loaded: its scripts, stylesheets and pictures.</summary>
    public IReadOnlyList<string> ResourceUrls { get; init; } = [];
}

/// <summary>
/// Finds where an annotated element of a page of the project is written, from what the browser tells: the file CodeAlta
/// serves, the script that created the node and the stylesheets of its rules (through their source maps), and a search
/// of its text, id and classes in the files of the project, first among those the page loaded. No plugin of a build is
/// needed: what a candidate is drawn from is said with it.
/// </summary>
internal sealed partial class SourceLocator
{
    private const int MaximumFileBytes = 1024 * 1024;
    // A bundle and its source map can be much larger than a file the project writes.
    private const int MaximumFetchedBytes = 8 * MaximumFileBytes;
    private const int MaximumRedirections = 5;
    private const int MaximumFiles = 20000;
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(4);

    private static readonly FrozenSet<string> SkippedFolders = new[]
    {
        "node_modules", ".git", "bin", "obj", "dist", "build", "out", ".next", ".nuxt", ".svelte-kit", ".output", "target", "vendor", ".venv", "venv",
        "coverage", ".alta", ".idea", ".vs", ".vscode", "__pycache__", ".turbo", ".cache", ".parcel-cache", ".angular",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> MarkupExtensions = new[]
    {
        ".html", ".htm", ".cshtml", ".razor", ".vue", ".svelte", ".astro", ".jsx", ".tsx", ".js", ".ts", ".mjs", ".cjs", ".php", ".erb", ".hbs", ".handlebars",
        ".liquid", ".njk", ".twig", ".jinja", ".j2", ".rs", ".go", ".templ", ".py", ".rb", ".ex", ".heex", ".eex", ".md", ".mdx", ".xml", ".json", ".yml", ".yaml", ".resx",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> StyleExtensions = new[]
    {
        ".css", ".scss", ".sass", ".less", ".styl", ".pcss", ".vue", ".svelte", ".astro", ".html", ".htm", ".cshtml", ".razor",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly Func<Uri, CancellationToken, Task<string?>> _fetch;
    private readonly Func<CancellationToken, ValueTask<BrowserFiles>> _files;

    /// <summary>Creates a locator for one session.</summary>
    /// <param name="fetch">Reads the text of an address of the page's server (a script, a stylesheet, a source map), or gives null.</param>
    /// <param name="files">The folders of the session, as its tools read files: the only files the locator reads.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public SourceLocator(Func<Uri, CancellationToken, Task<string?>> fetch, Func<CancellationToken, ValueTask<BrowserFiles>> files)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(files);
        _fetch = fetch;
        _files = files;
    }

    /// <summary>Creates the client <see cref="LoopbackFetch"/> takes: it follows no redirection itself and goes through no proxy.</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }, disposeHandler: true);

    /// <summary>
    /// Reads the text of an address of the loopback with a client, or gives null; other addresses and the reserved ports
    /// are never read, also when an address redirects to them.
    /// </summary>
    /// <param name="client">A client that follows no redirection itself, as <see cref="CreateClient"/> makes it.</param>
    /// <param name="reservedPorts">The ports of the loopback that are never reached: those of CodeAlta itself.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static Func<Uri, CancellationToken, Task<string?>> LoopbackFetch(HttpClient client, Func<IReadOnlyList<int>> reservedPorts)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(reservedPorts);
        return async (uri, cancellationToken) =>
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                // Each address is checked before it is asked, the ones redirections lead to too: the page's server could
                // otherwise send the locator to a port of CodeAlta that the browser itself never reaches.
                var current = uri;
                for (var hop = 0; hop <= MaximumRedirections; hop++)
                {
                    if (current.Scheme is not ("http" or "https") || !current.IsLoopback || reservedPorts().Contains(current.Port)) return null;
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    // A client that followed a redirection itself answered for an address that was not checked.
                    if (response.RequestMessage?.RequestUri != current) return null;
                    if (IsRedirection(response.StatusCode))
                    {
                        if (response.Headers.Location is not { } location || !Uri.TryCreate(current, location, out var next)) return null;
                        current = next;
                        continue;
                    }

                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumFetchedBytes) return null;
                    return await ReadAtMostAsync(response.Content, MaximumFetchedBytes, timeout.Token).ConfigureAwait(false);
                }

                return null;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
            {
                return null;
            }
        };
    }

    /// <summary>The file of a <c>/@fs/</c> address of Vite, without <c>/@fs</c>: <c>/home/me/x.js</c>, or <c>/C:/me/x.js</c> on Windows, whose drive comes first.</summary>
    internal static string FileSystemPath(string path)
        => path is ['/', var drive, ':', '/', ..] && char.IsAsciiLetter(drive) && OperatingSystem.IsWindows() ? path[1..] : path;

    private static bool IsRedirection(System.Net.HttpStatusCode status)
        => status is System.Net.HttpStatusCode.MovedPermanently or System.Net.HttpStatusCode.Found or System.Net.HttpStatusCode.SeeOther
            or System.Net.HttpStatusCode.TemporaryRedirect or System.Net.HttpStatusCode.PermanentRedirect;

    // The text of an answer, or null when it is longer than the limit. An answer that does not say its length can be
    // endless: it is read only up to the limit, never whole before it is measured.
    private static async Task<string?> ReadAtMostAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        using var bytes = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (bytes.Length + read > limit) return null;
                    bytes.Write(buffer, 0, read);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        bytes.Position = 0;
        using var reader = new StreamReader(bytes, EncodingOf(content), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    // The encoding the answer names, else UTF-8, which scripts, stylesheets and source maps are written in.
    private static Encoding EncodingOf(HttpContent content)
    {
        if (content.Headers.ContentType?.CharSet is not { Length: > 0 } charset) return Encoding.UTF8;
        try { return Encoding.GetEncoding(charset.Trim('"')); }
        catch (ArgumentException) { return Encoding.UTF8; }
    }

    /// <summary>The places of the project the element likely comes from, the surest first.</summary>
    public async Task<IReadOnlyList<SourceCandidate>> LocateAsync(SourceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var files = await _files(cancellationToken).ConfigureAwait(false);
        var run = new Run(this, query, new FileGate(files), cancellationToken);
        var candidates = new List<SourceCandidate>();

        // The file of the page, when CodeAlta serves it: the element is written there, in the line of its tag.
        var pageFile = run.FileOf(query.PageUrl);
        if (pageFile is not null && IsMarkup(pageFile) && run.Read(pageFile) is { } pageText && Best(pageText, query.Element, requireTag: true) is { } inPage)
        {
            candidates.Add(new SourceCandidate(run.Show(pageFile), inPage.Line, SourceKind.Page, "the tag of the element in the page's file", inPage.Score >= 8));
        }

        // The script that created the node, when it is code of the project itself: the innermost frame. When a library
        // created it (the renderer of React, Vue or Svelte), an outer frame of the project is only where the app was
        // mounted, which says nothing of the element.
        if (query.Evidence.Creation.FirstOrDefault(static frame => !frame.Url.StartsWith("codealta", StringComparison.OrdinalIgnoreCase)) is { } innermost
            && await run.ResolveAsync(innermost.Url, innermost.Line, innermost.Column, null).ConfigureAwait(false) is { } creator
            && run.IsProjectFile(creator.File) && !IsDependency(creator.File))
        {
            // The page names its functions and its rules: they stay code in the Markdown of the prompt.
            var name = innermost.Function.Length == 0 ? "an anonymous function" : AnnotationMarkdown.Code(innermost.Function + "()");
            candidates.Add(new SourceCandidate(run.Show(creator.File), creator.Line, SourceKind.Created, $"the browser recorded {name} creating the element ({creator.How})", true));
        }

        // The rules of its styles, at most three files.
        var styled = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in query.Evidence.Rules)
        {
            if (styled.Count == 3) break;
            if (!IsTelling(rule.Selector)) continue;
            var place = await run.StyleAsync(rule, pageFile).ConfigureAwait(false);
            if (place is not { } found || !run.IsProjectFile(found.File) || IsDependency(found.File) || !styled.Add(found.File + ":" + found.Line)) continue;
            candidates.Add(new SourceCandidate(run.Show(found.File), found.Line, SourceKind.Style, $"the rule {AnnotationMarkdown.Code(Clip(rule.Selector, 80))} ({found.How})", found.Certain));
        }

        // Nothing sure says where the element is written: its text, id and classes are looked for.
        if (!candidates.Any(static candidate => candidate.Kind is SourceKind.Page or SourceKind.Created && candidate.Certain))
        {
            foreach (var hit in run.Search())
            {
                if (candidates.Any(candidate => candidate.Path == run.Show(hit.File) && Math.Abs(candidate.Line - hit.Line) <= 2)) continue;
                candidates.Add(new SourceCandidate(run.Show(hit.File), hit.Line, SourceKind.Text, hit.Reason, false));
            }
        }

        return candidates;
    }

    // -------------------------------------------------------------------------------------------------------------
    // One search
    // -------------------------------------------------------------------------------------------------------------

    private sealed class Run(SourceLocator owner, SourceQuery query, FileGate gate, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, Task<string?>> _texts = new(StringComparer.Ordinal);
        private readonly string _project = Real(query.ProjectRoot);
        private readonly string? _server = query.ServerRoot is { Length: > 0 } server ? Real(server) : null;

        private IEnumerable<string> Roots => _server is null || _server == _project ? [_project] : [_server, _project];

        public bool IsProjectFile(string file) => Roots.Any(root => BrowserFiles.IsWithin(file, root));

        public string Show(string file)
            => BrowserFiles.IsWithin(file, _project) ? Path.GetRelativePath(_project, file).Replace('\\', '/') : file;

        /// <summary>The file of the project an address shows as it is, or null.</summary>
        public string? FileOf(Uri uri)
        {
            if (uri.IsFile) return Existing(uri.LocalPath);
            if (query.LocalFile?.Invoke(uri) is { } served) return Existing(served);
            if (uri.Scheme is not ("http" or "https")) return null;
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (path.StartsWith("/@fs/", StringComparison.Ordinal)) return Existing(FileSystemPath(path["/@fs".Length..]));
            return InRoots(path.TrimStart('/'));
        }

        // A path of an address in the folders of the server: as it is, in the folder of public files that servers commonly
        // serve from, and without the fingerprint a build adds to a name (site.b9sayid5wm.css for site.css).
        private string? InRoots(string relative)
        {
            if (relative.Length == 0) return null;
            var plain = Fingerprint().Replace(relative, string.Empty);
            foreach (var root in Roots)
            {
                foreach (var folder in PublicFolders)
                {
                    foreach (var name in plain == relative ? [relative] : new[] { relative, plain })
                    {
                        string full;
                        try { full = Path.GetFullPath(Path.Combine(root, folder, name)); }
                        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                        if (BrowserFiles.IsWithin(full, root) && gate.CanRead(full) && File.Exists(full)) return full;
                    }
                }
            }

            return null;
        }

        // A file that exists and that the session may read: a path outside its folders is never touched.
        private string? Existing(string path)
        {
            try { return gate.CanRead(path) && File.Exists(path) ? Real(path) : null; }
            catch (Exception exception) when (exception is ArgumentException or IOException) { return null; }
        }

        /// <summary>The text of a file the session may read, or null.</summary>
        public string? Read(string file) => gate.Read(file);

        // The text of an address: the file CodeAlta serves, a file of this computer, or what the server of the page answers.
        private Task<string?> TextAsync(Uri uri)
        {
            var key = uri.GetLeftPart(UriPartial.Query);
            if (!_texts.TryGetValue(key, out var text)) _texts[key] = text = ReadAsync(uri);
            return text;
        }

        private async Task<string?> ReadAsync(Uri uri)
        {
            var file = uri.IsFile ? uri.LocalPath : query.LocalFile?.Invoke(uri);
            if (file is not null) return Read(file);
            return await owner._fetch(uri, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Where a place of a script or a stylesheet of the page is written: through its source map, else the file of its address.</summary>
        public async Task<(string File, int Line, string How)?> ResolveAsync(string url, int line, int column, string? mapReference)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            if (mapReference is null && uri.Scheme is "http" or "https" or "file" && await TextAsync(uri).ConfigureAwait(false) is { } text) mapReference = MapReference(text);
            if (mapReference is not null && line >= 0 && await MapAsync(mapReference, uri).ConfigureAwait(false) is { } loaded
                && loaded.Map.Find(line, column) is { } original && SourceFile(original.Source, loaded.Base) is { } source)
            {
                return (source, original.Line + 1, "through the source map");
            }

            return FileOf(uri) is { } direct && line >= 0 ? (direct, line + 1, "at its address") : null;
        }

        private async Task<(SourceMap Map, Uri Base)?> MapAsync(string reference, Uri owner)
        {
            if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = reference.IndexOf(',', StringComparison.Ordinal);
                if (comma < 0) return null;
                var header = reference[..comma];
                var data = reference[(comma + 1)..];
                string json;
                try
                {
                    json = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                        ? Encoding.UTF8.GetString(Convert.FromBase64String(data))
                        : Uri.UnescapeDataString(data);
                }
                catch (FormatException)
                {
                    return null;
                }

                return SourceMap.Parse(json) is { } inline ? (inline, owner) : null;
            }

            if (!Uri.TryCreate(owner, reference, out var address)) return null;
            return await TextAsync(address).ConfigureAwait(false) is { } text && SourceMap.Parse(text) is { } map ? (map, address) : null;
        }

        // The file of a source of a map: a path of this computer, an address of the page's server, or a path of webpack.
        private string? SourceFile(string source, Uri mapUri)
        {
            if (source.Length == 0) return null;
            var webpack = WebpackPrefix().Match(source);
            if (webpack.Success) return InRoots(source[webpack.Length..].TrimStart('.', '/'));
            if (source.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(source, UriKind.Absolute, out var fileUri)) return Existing(fileUri.LocalPath);
            if (Path.IsPathRooted(source) && Existing(source) is { } absolute) return absolute;
            if (Uri.TryCreate(mapUri, source, out var resolved) && FileOf(resolved) is { } served) return served;
            return InRoots(source.TrimStart('.', '/'));
        }

        /// <summary>Where a style rule of the element is written.</summary>
        public async Task<(string File, int Line, string How, bool Certain)?> StyleAsync(StyleRuleEvidence rule, string? pageFile)
        {
            // A style element a dev server added: it often names the file it came from in an attribute of it, and its text
            // is that file, so the line of the rule is the line of the file when the selector is there.
            foreach (var (_, value) in rule.OwnerAttributes)
            {
                if (value.Length < 2 || !(value.StartsWith('/') || value.Contains(":\\", StringComparison.Ordinal))) continue;
                var named = Existing(value.Split('?')[0]) ?? InRoots(value.Split('?')[0].TrimStart('/'));
                if (named is null) continue;
                if (Read(named) is not { } namedText) continue;
                if (rule.Line >= 0 && LineHas(namedText, rule.Line + 1, rule.Selector)) return (named, rule.Line + 1, "in the file the style element names", true);
                if (FindSelector(named, namedText, rule.Selector) is { } inNamed) return (named, inNamed, "in the file the style element names", true);
            }

            if (rule.Inline)
            {
                if (rule.SourceMapUrl is { Length: > 0 } inlineMap && rule.Line >= 0
                    && await ResolveAsync(query.PageUrl.ToString(), rule.Line, rule.Column, inlineMap).ConfigureAwait(false) is { } mapped && mapped.How.Contains("map", StringComparison.Ordinal))
                {
                    return (mapped.File, mapped.Line, mapped.How, true);
                }

                if (pageFile is not null && rule.Line >= 0) return (pageFile, rule.SheetLine + rule.Line + 1, "in a style element of the page's file", true);
            }
            else if (rule.SheetUrl is { Length: > 0 } sheet && rule.Line >= 0 && await ResolveAsync(sheet, rule.Line, rule.Column, rule.SourceMapUrl is { Length: > 0 } map ? map : null).ConfigureAwait(false) is { } place)
            {
                return (place.File, place.Line, place.How, true);
            }

            // Nothing tells the file: the selector is looked for where the styles of the project are written. A stylesheet
            // of another site (a CDN) is no file of the project.
            if (!rule.Inline && rule.SheetUrl is { Length: > 0 } other && Uri.TryCreate(other, UriKind.Absolute, out var sheetUri)
                && !string.Equals(sheetUri.Authority, query.PageUrl.Authority, StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var file in Files(StyleExtensions))
            {
                if (Read(file) is { } text && FindSelector(file, text, rule.Selector) is { } line) return (file, line, "where the selector is written", false);
            }

            return null;
        }

        // The files the page loaded first, then the others of the project, with the extensions asked for.
        private IEnumerable<string> Files(FrozenSet<string> extensions)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in query.ResourceUrls)
            {
                if (Uri.TryCreate(resource, UriKind.Absolute, out var uri) && FileOf(uri) is { } file && extensions.Contains(Path.GetExtension(file)) && seen.Add(file)) yield return file;
            }

            var watch = Stopwatch.StartNew();
            var count = 0;
            foreach (var root in Roots)
            {
                foreach (var file in Enumerate(root, gate))
                {
                    if (watch.Elapsed > SearchBudget || ++count > MaximumFiles) yield break;
                    if (extensions.Contains(Path.GetExtension(file)) && seen.Add(file)) yield return file;
                }
            }
        }

        public IReadOnlyList<(string File, int Line, string Reason)> Search()
        {
            var element = query.Element;
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var resource in query.ResourceUrls)
            {
                if (Uri.TryCreate(resource, UriKind.Absolute, out var uri) && FileOf(uri) is { } file) loaded.Add(file);
            }

            var hits = new List<(string File, int Line, int Score, string Reason)>();
            foreach (var file in Files(MarkupExtensions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Read(file) is not { } text || Best(text, element, requireTag: false) is not { } best || best.Score < 5) continue;
                hits.Add((file, best.Line, best.Score + (loaded.Contains(file) ? 2 : 0), best.Reason));
            }

            return [.. hits.OrderByDescending(static hit => hit.Score).ThenBy(static hit => hit.File, StringComparer.Ordinal).Take(3).Select(static hit => (hit.File, hit.Line, hit.Reason))];
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    // Files and lines
    // -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether one search may read a file or a folder: as <see cref="BrowserFiles.CanRead"/> decides it for the tools of the
    /// session (inside its folders, not denied by its sandbox, no file of secrets), with the folders of the session resolved
    /// once. A path is resolved at each check, never from a folder resolved before: a folder of the project can become a
    /// link while a search runs.
    /// </summary>
    internal sealed class FileGate(BrowserFiles files)
    {
        private readonly string[] _roots = [.. files.ReadRoots.Select(Real)];
        private readonly string[] _denied = [.. files.Denied.Select(Real)];

        /// <summary>Whether the session may read a file or a folder, as its path is now.</summary>
        public bool CanRead(string path)
        {
            string full;
            try { full = Path.GetFullPath(path); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

            // Outside the folders, the path is never touched: a UNC path would make the system reach its server.
            if (!files.ReadRoots.Any(root => BrowserFiles.IsWithin(full, root)) && !_roots.Any(root => BrowserFiles.IsWithin(full, root))) return false;
            string real;
            try { real = BrowserFiles.RealPath(full); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
            return Allows(real) && !BrowserFiles.IsSecret(full);
        }

        /// <summary>The text of a file the session may read, checked as it was opened, or null.</summary>
        public string? Read(string path) => CanRead(path) ? ReadChecked(path) : null;

        /// <summary>
        /// Reads a file <see cref="CanRead"/> accepted, then checks what was opened: a file or a folder of its path replaced
        /// by a link between the check and the opening could lead out of the folders, into a denied one or to a file of
        /// secrets. Where the system does not tell the path of an opened file, the path is resolved again, which leaves a
        /// smaller window. The limit of size is kept on what was opened, while it is read: the file can grow meanwhile.
        /// </summary>
        internal string? ReadChecked(string path)
        {
            try
            {
                // A dev server or an editor may be writing the file, or replace it, while it is read.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
                var opened = BrowserFileHandles.PathOf(stream.SafeFileHandle) ?? BrowserFiles.RealPath(path);
                if (!Allows(opened)) return null;
                using var bytes = new MemoryStream();
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int read;
                    while ((read = stream.Read(buffer)) > 0)
                    {
                        if (bytes.Length + read > MaximumFileBytes) return null;
                        bytes.Write(buffer, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                bytes.Position = 0;
                using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        // A path as the system resolves it that the session may read.
        private bool Allows(string real)
            => _roots.Any(root => BrowserFiles.IsWithin(real, root)) && !_denied.Any(entry => BrowserFiles.IsWithin(real, entry)) && !BrowserFiles.IsSecret(real);
    }

    private static string Real(string path)
    {
        try { return BrowserFiles.RealPath(path); }
        catch (IOException) { return Path.GetFullPath(path); }
    }

    // The files under a folder, but none in a folder the session may not read: one its sandbox denies, or a link that leads
    // out of its folders.
    private static IEnumerable<string> Enumerate(string root, FileGate gate)
    {
        var pending = new Stack<string>();
        if (gate.CanRead(root)) pending.Push(root);
        while (pending.Count != 0)
        {
            var folder = pending.Pop();
            string[] files, folders;
            try
            {
                files = Directory.GetFiles(folder);
                folders = Directory.GetDirectories(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var file in files) yield return file;
            Array.Sort(folders, StringComparer.Ordinal);
            for (var index = folders.Length - 1; index >= 0; index--)
            {
                var name = Path.GetFileName(folders[index]);
                if (!SkippedFolders.Contains(name) && !name.StartsWith('.') && gate.CanRead(folders[index])) pending.Push(folders[index]);
            }
        }
    }

    private static bool IsMarkup(string file) => MarkupExtensions.Contains(Path.GetExtension(file));

    private static readonly ImmutableArray<string> PublicFolders = ["", "wwwroot", "public", "static"];

    private static readonly FrozenSet<string> StylesheetExtensions = new[]
    { ".css", ".scss", ".sass", ".less", ".styl", ".pcss" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // A selector that says something of the element: not one that matches every element, nor a nested part alone.
    private static bool IsTelling(string selector)
        => selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(static part => part.Length != 0 && part[0] is not ('*' or ':' or '&'));

    // Whether a line of the text of a file, from 1, holds the last simple part of a selector.
    private static bool LineHas(string text, int line, string selector)
    {
        var lines = text.Split('\n');
        if (line < 1 || line > lines.Length) return false;
        var last = SimpleSelector().Matches(selector.Split(',')[0]).LastOrDefault()?.Value ?? selector.Split(',')[0].Trim();
        return last.Length != 0 && lines[line - 1].Contains(last, StringComparison.Ordinal);
    }

    // The lines of a file where styles are written: all of a stylesheet, those inside style elements of another file.
    private static bool[] StyleLines(string file, string[] lines)
    {
        var inside = new bool[lines.Length];
        var all = StylesheetExtensions.Contains(Path.GetExtension(file));
        var open = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (!all && line.Contains("<style", StringComparison.OrdinalIgnoreCase)) open = true;
            inside[index] = all || open;
            if (!all && line.Contains("</style", StringComparison.OrdinalIgnoreCase)) open = false;
        }

        return inside;
    }

    private static bool IsDependency(string file)
    {
        var path = file.Replace('\\', '/');
        return path.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) || path.Contains("/.vite/", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/vendor/", StringComparison.OrdinalIgnoreCase) || path.Contains("/wwwroot/lib/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? MapReference(string text)
    {
        var match = SourceMappingUrl().Matches(text).LastOrDefault();
        return match is null ? null : match.Groups[1].Value;
    }

    // The line of a file where a selector is written, from 1: the whole selector, else its last class or id.
    private static int? FindSelector(string file, string text, string selector)
    {
        var lines = text.Split('\n');
        var styles = StyleLines(file, lines);
        foreach (var part in selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part[0] is '*' or ':' or '&') continue;
            // The selector written alone before its block, else as one of a list.
            for (var pass = 0; pass < 2; pass++)
            {
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!styles[index]) continue;
                    var at = lines[index].IndexOf(part, StringComparison.Ordinal);
                    if (at < 0 || (at > 0 && (char.IsLetterOrDigit(lines[index][at - 1]) || lines[index][at - 1] is '-' or '_' or '.' or '#'))) continue;
                    var rest = lines[index][(at + part.Length)..];
                    if (pass == 0 ? BlockStart().IsMatch(rest) : SelectorEnd().IsMatch(rest)) return index + 1;
                }
            }
        }

        var last = SimpleSelector().Matches(selector).LastOrDefault()?.Value;
        if (last is null) return null;
        var pattern = new Regex(Regex.Escape(last) + @"(?![\w-])", RegexOptions.CultureInvariant);
        for (var index = 0; index < lines.Length; index++)
        {
            if (styles[index] && pattern.IsMatch(lines[index])) return index + 1;
        }

        return null;
    }

    // The line of the text of a file that best looks like where the element is written, from 1, with its score; null when none does.
    private static (int Line, int Score, string Reason)? Best(string text, AnnotatedElement element, bool requireTag)
    {
        var id = element.Id.Length >= 2 ? element.Id : null;
        var words = TextOf(element);
        var classes = element.Classes.Where(static name => name.Length >= 3).Take(8).ToArray();
        if (id is null && words is null && classes.Length == 0) return null;
        // When the whole text is not in the file, the longest start of it of two words or more that is.
        string? prefix = null;
        if (words is not null && !text.Contains(words, StringComparison.Ordinal))
        {
            var parts = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var count = parts.Length - 1; count >= 2 && prefix is null; count--)
            {
                var start = string.Join(' ', parts.Take(count));
                if (start.Length >= 6 && text.Contains(start, StringComparison.Ordinal)) prefix = start;
            }
        }

        if ((id is null || !text.Contains(id, StringComparison.Ordinal)) && (words is null || !text.Contains(words, StringComparison.Ordinal)) && prefix is null
            && !classes.Any(name => text.Contains(name, StringComparison.Ordinal))) return null;

        var lines = text.Split('\n');
        var tag = "<" + element.Tag;
        (int Line, int Score, string Reason)? best = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var hasTag = element.Tag.Length != 0 && lines[index].Contains(tag, StringComparison.OrdinalIgnoreCase)
                && (lines[index].Length == lines[index].IndexOf(tag, StringComparison.OrdinalIgnoreCase) + tag.Length
                    || !char.IsLetterOrDigit(lines[index][lines[index].IndexOf(tag, StringComparison.OrdinalIgnoreCase) + tag.Length]));
            if (requireTag && !hasTag) continue;
            // A tag can spread its attributes and its text over the lines after it, until a line starts another tag:
            // the elements an element holds are not its own.
            var window = string.Join('\n', lines.Skip(index).Take(hasTag ? 1 + lines.Skip(index + 1).Take(5).TakeWhile(static line => !StartsTag(line)).Count() : 1));
            var score = 0;
            var reasons = new List<string>();
            if (id is not null && (window.Contains("\"" + id + "\"", StringComparison.Ordinal) || window.Contains("'" + id + "'", StringComparison.Ordinal)))
            {
                score += 6;
                reasons.Add("id");
            }

            if (words is not null && window.Contains(words, StringComparison.Ordinal))
            {
                score += 5;
                reasons.Add("text");
            }
            else if (prefix is not null && window.Contains(prefix, StringComparison.Ordinal))
            {
                // A text the code completes (Count is {count}): its fixed start is written there.
                score += 4;
                reasons.Add("start of the text");
            }

            var found = classes.Count(name => Regex.IsMatch(window, @"(?<![\w-])" + Regex.Escape(name) + @"(?![\w-])", RegexOptions.CultureInvariant));
            if (found != 0)
            {
                score += Math.Min(found, 4) * 2;
                reasons.Add(found == 1 ? "class" : "classes");
            }

            if (hasTag && score != 0) score += 1;
            if (score > 0 && (best is null || score > best.Value.Score)) best = (index + 1, score, $"the {string.Join(" and ", reasons)} of the element written there");
        }

        return best;
    }

    // Whether a line of markup starts with a tag that opens or closes an element.
    private static bool StartsTag(string line)
    {
        var text = line.AsSpan().TrimStart();
        return text.Length > 1 && text[0] == '<' && (char.IsLetter(text[1]) || text[1] == '/');
    }

    // The text of the element that is worth looking for: its first line, if it is a few words, not a number.
    private static string? TextOf(AnnotatedElement element)
    {
        var text = (element.Text.Length != 0 ? element.Text : element.Name).Split('\n')[0].Trim().TrimEnd('…');
        if (text.Length is < 3 or > 80 || text.All(static character => char.IsDigit(character) || char.IsPunctuation(character) || char.IsWhiteSpace(character))) return null;
        return text;
    }

    private static string Clip(string text, int limit) => text.Length > limit ? text[..limit] + "…" : text;

    [GeneratedRegex(@"[#@]\s*sourceMappingURL=([^\s'""*]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SourceMappingUrl();

    [GeneratedRegex(@"^webpack://[^/]*/", RegexOptions.CultureInvariant)]
    private static partial Regex WebpackPrefix();

    [GeneratedRegex(@"^\s*([{,]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex SelectorEnd();

    [GeneratedRegex(@"[.#][\w-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleSelector();

    [GeneratedRegex(@"^\s*(\{|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"\.[0-9a-z]{8,12}(?=\.(css|js|mjs)$)", RegexOptions.CultureInvariant)]
    private static partial Regex Fingerprint();
}