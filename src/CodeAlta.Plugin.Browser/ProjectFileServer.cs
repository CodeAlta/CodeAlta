using System.Collections.Frozen;
using System.Net;
using System.Security.Cryptography;
using CodeAlta.Browser.Launch;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Plugin.Browser;

/// <summary>
/// Serves the files of the folders of projects to the browser of their sessions, on the loopback and read only, so
/// that a page such as a design written as one HTML file opens with its scripts, styles and pictures, and CodeAlta knows
/// the file of each address. A folder is reached under a random token; nothing outside it, no file of secrets or of
/// version control, and no other method than GET and HEAD is served.
/// </summary>
internal sealed class ProjectFileServer : IAsyncDisposable
{
    private static readonly FrozenDictionary<string, string> MediaTypes = new Dictionary<string, string>
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8", [".css"] = "text/css; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8", [".json"] = "application/json; charset=utf-8",
        [".map"] = "application/json; charset=utf-8", [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif", [".ico"] = "image/x-icon", [".woff"] = "font/woff", [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf", [".otf"] = "font/otf", [".txt"] = "text/plain; charset=utf-8", [".md"] = "text/plain; charset=utf-8", [".xml"] = "application/xml",
        [".wasm"] = "application/wasm", [".mp4"] = "video/mp4", [".webm"] = "video/webm", [".mp3"] = "audio/mpeg", [".pdf"] = "application/pdf",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> VersionControl = new[] { ".git", ".hg", ".svn" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _roots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<CancellationToken, Task<string>>> _pages = new(StringComparer.Ordinal);

    // The first segment of the addresses of the pages CodeAlta makes, which no token of a folder is.
    private const string PagesSegment = "_codealta";
    private HttpListener? _listener;
    private Task? _loop;
    private int _port;
    private int _disposed;

    /// <summary>Gets the origin of the server once it runs, as <c>http://127.0.0.1:port</c>; null before.</summary>
    public string? Origin
    {
        get
        {
            lock (_gate) return _listener is null ? null : $"http://127.0.0.1:{_port}";
        }
    }

    /// <summary>The address of a file of a folder, starting the server when it does not run yet.</summary>
    /// <exception cref="ArgumentException">The file is not inside the folder.</exception>
    /// <exception cref="HttpListenerException">The server could not start.</exception>
    public Uri Address(string root, string file)
    {
        var realRoot = BrowserFiles.RealPath(root);
        var realFile = BrowserFiles.RealPath(file);
        if (!BrowserFiles.IsWithin(realFile, realRoot)) throw new ArgumentException($"{file} is not inside {root}.", nameof(file));
        string token;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!_tokens.TryGetValue(realRoot, out token!))
            {
                token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                _tokens[realRoot] = token;
                _roots[token] = realRoot;
            }

            StartLocked();
        }

        var relative = Path.GetRelativePath(realRoot, realFile).Replace('\\', '/');
        var path = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        return new Uri($"http://127.0.0.1:{_port}/{token}/{path}");
    }

    /// <summary>
    /// The address of a page CodeAlta makes, such as the start page of the browser of a session, made again at each
    /// request; the server starts when it does not run yet.
    /// </summary>
    /// <exception cref="HttpListenerException">The server could not start.</exception>
    public Uri PageAddress(Func<CancellationToken, Task<string>> render)
    {
        ArgumentNullException.ThrowIfNull(render);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _pages[token] = render;
            StartLocked();
            return new Uri($"http://127.0.0.1:{_port}/{PagesSegment}/{token}");
        }
    }

    /// <summary>Forgets a page of <see cref="PageAddress"/>: its address answers no more.</summary>
    public void RemovePage(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        lock (_gate) _pages.Remove(address.AbsolutePath.Split('/') is [_, PagesSegment, var token, ..] ? token : string.Empty);
    }

    /// <summary>Whether an address is one of a page CodeAlta makes.</summary>
    public bool IsPage(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        lock (_gate) return _listener is not null && url.Scheme == "http" && url.Port == _port && url.IsLoopback && url.AbsolutePath.StartsWith("/" + PagesSegment + "/", StringComparison.Ordinal);
    }

    /// <summary>The folder and the file an address of the server names, or null when it is not one of its addresses.</summary>
    public (string Root, string File)? Map(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        string? root;
        lock (_gate)
        {
            if (_listener is null || url.Scheme != "http" || url.Port != _port || !url.IsLoopback) return null;
            var segments = url.AbsolutePath.Split('/', 3, StringSplitOptions.None);
            if (segments.Length < 2 || !_roots.TryGetValue(segments[1], out root)) return null;
        }

        var rest = url.AbsolutePath.Split('/', 3, StringSplitOptions.None) is { Length: 3 } parts ? Uri.UnescapeDataString(parts[2]) : string.Empty;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, rest)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (Directory.Exists(full)) full = Path.Combine(full, "index.html");
        return BrowserFiles.IsWithin(full, root) ? (root, full) : null;
    }

    private void StartLocked()
    {
        if (_listener is not null) return;
        for (var attempt = 0; ; attempt++)
        {
            var port = LaunchHelpers.FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException) when (attempt < 3)
            {
                // Another program took the port meanwhile.
                listener.Close();
                continue;
            }

            _listener = listener;
            _port = port;
            _loop = Task.Run(() => ServeAsync(listener));
            return;
        }
    }

    private async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            var method = context.Request.HttpMethod;
            if (method is not ("GET" or "HEAD"))
            {
                response.StatusCode = 405;
                return;
            }

            if (context.Request.Url is { } address && Page(address) is { } render)
            {
                var html = System.Text.Encoding.UTF8.GetBytes(await render(CancellationToken.None).ConfigureAwait(false));
                response.ContentType = "text/html; charset=utf-8";
                // A page of CodeAlta loads nothing from elsewhere; its links and its form lead anywhere.
                response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:";
                response.ContentLength64 = html.Length;
                if (method == "GET") await response.OutputStream.WriteAsync(html).ConfigureAwait(false);
                return;
            }

            using var stream = context.Request.Url is { } url && Map(url) is { } found && Readable(found.Root, found.File, out var real) ? OpenChecked(found.Root, real) : null;
            if (stream is null)
            {
                response.StatusCode = 404;
                return;
            }

            response.ContentType = MediaTypes.GetValueOrDefault(Path.GetExtension(stream.Name), "application/octet-stream");
            response.ContentLength64 = stream.Length;
            if (method == "GET") stream.CopyTo(response.OutputStream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpListenerException or InvalidOperationException)
        {
            _ = exception;
            try { response.StatusCode = 500; }
            catch (InvalidOperationException) { /* the answer had started */ }
        }
        finally
        {
            try { response.Close(); }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException) { /* the browser went away */ }
        }
    }

    private Func<CancellationToken, Task<string>>? Page(Uri url)
    {
        lock (_gate) return url.AbsolutePath.Split('/') is [_, PagesSegment, var token] && _pages.TryGetValue(token, out var render) ? render : null;
    }

    // A file of the folder, also once its links are followed, that holds no secret, by its name or by the one it leads to.
    internal static bool Readable(string root, string file, out string real)
    {
        real = file;
        try
        {
            if (!File.Exists(file)) return false;
            real = BrowserFiles.RealPath(file);
            return BrowserFiles.IsWithin(real, root) && !IsPrivate(Path.GetRelativePath(root, file)) && !IsPrivate(Path.GetRelativePath(root, real));
        }
        catch (IOException)
        {
            return false;
        }
    }

    // Opens a file Readable accepted, then checks what was opened: a file or a folder of its path replaced by a link
    // between the check and the opening could lead out of the folder or to a file of secrets. Where the system does
    // not tell the path of an opened file, the path is resolved again, which leaves a smaller window.
    internal static FileStream? OpenChecked(string root, string real)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string opened;
        try
        {
            opened = BrowserFileHandles.PathOf(stream.SafeFileHandle) ?? BrowserFiles.RealPath(real);
        }
        catch (IOException)
        {
            opened = string.Empty;
        }

        if (opened.Length != 0 && BrowserFiles.IsWithin(opened, root) && !IsPrivate(Path.GetRelativePath(root, opened))) return stream;
        stream.Dispose();
        return null;
    }

    // A file of secrets, or a file of the version control of the folder. Any page this server serves can ask for any of
    // its files, unlike a file the agent chooses for a page, so the history of the project (the objects of every
    // version) and its .git/config, whose addresses of remotes can hold credentials, are kept out here.
    internal static bool IsPrivate(string relative)
        => BrowserFiles.IsSecret(relative)
            || relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).Any(static part => VersionControl.Contains(part));

    /// <summary>Stops the server.</summary>
    public async ValueTask DisposeAsync()
    {
        HttpListener? listener;
        Task? loop;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            listener = _listener;
            loop = _loop;
            _listener = null;
        }

        if (listener is null) return;
        try { listener.Close(); }
        catch (ObjectDisposedException) { /* closed */ }
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException) { /* stopped */ }
        }
    }
}