using System.Collections.Frozen;
using System.Net;
using System.Text;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Plugin.Browser;

/// <summary>
/// The page the browser of a session shows when the user opens it from the window: the servers that run in the folder
/// of the project, the HTML files of the project, and a field for any address.
/// </summary>
internal static class StartPage
{
    /// <summary>The most files of the project the page lists.</summary>
    internal const int MaximumFiles = 40;

    private static readonly FrozenSet<string> SkippedFolders = new[]
    {
        "node_modules", ".git", ".hg", ".svn", "bin", "obj", ".alta", ".vs", ".vscode", ".idea", ".next", ".nuxt", ".cache", "coverage", "vendor", "packages",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes the page for the folder of a session, or for no folder.</summary>
    public static async Task<string> RenderAsync(string? folder, IPortOwners owners, ProjectFileServer files, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(files);
        IReadOnlyList<ListeningPort> servers = [];
        if (folder is not null)
        {
            try
            {
                var root = BrowserFiles.RealPath(folder);
                servers = [.. (await owners.ListAsync(cancellationToken).ConfigureAwait(false))
                    .Where(item => item.Owner.FolderIn(root) is not null)];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The page says what it found.
            }
        }

        var builder = new StringBuilder();
        builder.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8"><title>CodeAlta browser</title>
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
              :root { color-scheme: light dark; --accent: #7c3aed; }
              body { font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; max-width: 760px; margin: 48px auto; padding: 0 24px; }
              h1 { font-size: 22px; margin: 0 0 4px; } h2 { font-size: 15px; margin: 28px 0 8px; }
              .lead, .muted { color: color-mix(in srgb, currentColor 65%, transparent); }
              form { display: flex; gap: 8px; margin: 20px 0 8px; }
              input { flex: 1; font: inherit; padding: 8px 10px; border-radius: 8px; border: 1px solid color-mix(in srgb, currentColor 30%, transparent); background: transparent; color: inherit; }
              button { font: inherit; padding: 8px 16px; border-radius: 8px; border: 0; background: var(--accent); color: #fff; cursor: pointer; }
              ul { list-style: none; padding: 0; margin: 0; } li { padding: 6px 0; border-bottom: 1px solid color-mix(in srgb, currentColor 12%, transparent); }
              a { color: var(--accent); text-decoration: none; font-weight: 600; } a:hover { text-decoration: underline; }
              code { font-size: 13px; }
            </style></head><body>
            <h1>CodeAlta browser</h1>
            <p class="lead">This browser belongs to the session. Go anywhere: the agent sees its tabs, so you can ask it about the page you show.
            With <b>Annotate</b> in the CodeAlta bar at the bottom, mark what should change.</p>
            <form id="go"><input id="address" placeholder="An address, such as localhost:5173 or example.com" autofocus><button type="submit">Open</button></form>
            <script>
              document.getElementById('go').addEventListener('submit', event => {
                event.preventDefault();
                const value = normalizeAddress(document.getElementById('address').value);
                if (value) location.href = value;
              });
              // A host and its port, such as localhost:5173, look like a scheme followed by its text: a number after the
              // colon makes it a port.
              function normalizeAddress(value) {
                value = value.trim();
                const hostAndPort = /^[^\s\/:?#]+:\d+(?:[\/?#]|$)/.test(value);
                if (!value || (!hostAndPort && /^[a-z][a-z0-9+.-]*:/i.test(value))) return value;
                return (/^(localhost|127\.|\[::1\])/i.test(value) ? 'http://' : 'https://') + value;
              }
            </script>
            """);

        builder.Append("<h2>Running in the project</h2>");
        if (servers.Count == 0)
        {
            builder.Append("<p class=\"muted\">No server of the project runs now. Ask the agent to start one, for example: <i>Start the app</i>.</p>");
        }
        else
        {
            builder.Append("<ul>");
            foreach (var server in servers)
            {
                var address = $"http://localhost:{server.Port}/";
                var command = server.Owner.Command.Length > 90 ? server.Owner.Command[..90] + "…" : server.Owner.Command;
                builder.Append("<li><a href=\"").Append(address).Append("\">").Append(address).Append("</a> <span class=\"muted\"><code>")
                    .Append(WebUtility.HtmlEncode(command)).Append("</code></span></li>");
            }

            builder.Append("</ul>");
        }

        if (folder is not null && HtmlFiles(folder) is { Count: > 0 } pages)
        {
            builder.Append("<h2>Pages of the project</h2><ul>");
            foreach (var file in pages)
            {
                Uri address;
                try
                {
                    address = files.Address(folder, file);
                }
                catch (Exception exception) when (exception is ArgumentException or HttpListenerException or IOException)
                {
                    continue;
                }

                builder.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(address.ToString())).Append("\">")
                    .Append(WebUtility.HtmlEncode(Path.GetRelativePath(folder, file).Replace('\\', '/'))).Append("</a></li>");
            }

            builder.Append("</ul>");
        }

        return builder.Append("</body></html>").ToString();
    }

    /// <summary>The HTML files of a folder, outside the folders of dependencies, builds and tools, in the order of their paths.</summary>
    internal static IReadOnlyList<string> HtmlFiles(string folder)
    {
        var found = new List<string>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((folder, 0));
        while (pending.Count != 0 && found.Count < 4 * MaximumFiles)
        {
            var (current, depth) = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    if (Path.GetExtension(file) is ".html" or ".htm" && !BrowserFiles.IsSecret(Path.GetRelativePath(folder, file))) found.Add(file);
                }

                if (depth == 6) continue;
                foreach (var directory in Directory.EnumerateDirectories(current))
                {
                    var name = Path.GetFileName(directory);
                    if (SkippedFolders.Contains(name) || (name.StartsWith('.') && name.Length > 1)) continue;
                    if (new DirectoryInfo(directory).LinkTarget is not null) continue;
                    pending.Push((directory, depth + 1));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be read lists nothing.
            }
        }

        return [.. found.Order(StringComparer.OrdinalIgnoreCase).Take(MaximumFiles)];
    }
}
