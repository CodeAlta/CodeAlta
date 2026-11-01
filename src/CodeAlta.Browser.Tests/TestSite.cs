using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace CodeAlta.Browser.Tests;

/// <summary>
/// The pages the browser tests drive, served on the loopback: <c>http://127.0.0.1:port</c> and
/// <c>http://localhost:port</c> are two sites, so a frame of the other one runs in a process of its own.
/// </summary>
internal sealed class TestSite : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestSite(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public string Origin => $"http://127.0.0.1:{Port}";

    public string OtherOrigin => $"http://localhost:{Port}";

    public static async Task<TestSite> StartAsync()
    {
        var port = Launch.LaunchHelpers.FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Listen(IPAddress.IPv6Loopback, port);
        });
        var app = builder.Build();
        foreach (var (path, html) in Pages) app.MapGet(path, () => Results.Content(html.Replace("OTHER/", $"http://localhost:{port}/", StringComparison.Ordinal), "text/html; charset=utf-8"));
        app.MapGet("/api/data", () => Results.Json(new Dictionary<string, object> { ["answer"] = 42, ["items"] = new[] { "a", "b" } }));
        app.MapPost("/api/echo", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            return Results.Content("echo:" + await reader.ReadToEndAsync(), "text/plain");
        });
        await app.StartAsync();
        return new TestSite(app, port);
    }

    private static readonly (string Path, string Html)[] Pages =
    [
        ("/form", """
            <!doctype html><html><head><title>Sign in · Demo</title></head><body>
            <h1>Sign in</h1>
            <form id="form" onsubmit="event.preventDefault(); document.getElementById('result').textContent = 'Signed in as ' + document.getElementById('email').value + (document.getElementById('remember').checked ? ' (remembered)' : '') + ' in ' + document.getElementById('country').value;">
              <label>Email <input id="email" type="email" value="old@example.com"></label>
              <label>Password <input id="password" type="password"></label>
              <label><input id="remember" type="checkbox"> Remember me</label>
              <label>Country <select id="country"><option value="fr">France</option><option value="ch">Switzerland</option></select></label>
              <button type="submit">Continue</button>
            </form>
            <p id="result"></p>
            <button id="alert" onclick="alert('Hello from the page'); document.getElementById('result').textContent = 'alerted';">Alert</button>
            <button id="confirm" onclick="document.getElementById('result').textContent = confirm('Sure?') ? 'confirmed' : 'refused';">Confirm</button>
            <button id="log" onclick="console.log('clicked', 7, {a: 1}); console.error('bad thing'); fetch('/api/data').then(r => r.json()).then(d => document.getElementById('result').textContent = 'answer ' + d.answer);">Log and fetch</button>
            <button id="later" onclick="setTimeout(() => { const p = document.createElement('p'); p.textContent = 'Arrived later'; document.body.appendChild(p); }, 300);">Later</button>
            <a href="/second" id="link">Go to the second page</a>
            <a href="/second" target="_blank" id="popup">Open in a new tab</a>
            <input id="file" type="file">
            <p id="files"></p>
            <script>document.getElementById('file').addEventListener('change', e => document.getElementById('files').textContent = 'chose ' + [...e.target.files].map(f => f.name + ':' + f.size).join(','));</script>
            <div id="keys" tabindex="0">keys:</div>
            <script>document.getElementById('keys').addEventListener('keydown', e => document.getElementById('keys').textContent += ' ' + (e.ctrlKey ? 'Control+' : '') + e.key);</script>
            </body></html>
            """),
        ("/second", """
            <!doctype html><html><head><title>Second page</title></head><body><h1>The second page</h1><p>Welcome.</p></body></html>
            """),
        ("/frames", """
            <!doctype html><html><head><title>Frames</title></head><body>
            <h1>Outer</h1>
            <iframe id="same" src="/inner?name=same" style="width:300px;height:150px;border:4px solid red"></iframe>
            <iframe id="other" src="OTHER/inner?name=other" style="width:300px;height:150px;margin-left:50px;border:4px solid blue"></iframe>
            </body></html>
            """),
        ("/inner", """
            <!doctype html><html><head><title>Inner</title></head><body>
            <button onclick="this.textContent = 'Pressed in ' + location.host">Press me</button>
            </body></html>
            """),
        ("/beforeunload", """
            <!doctype html><html><head><title>Leave?</title></head><body><p>Stay</p>
            <script>window.addEventListener('beforeunload', e => { e.preventDefault(); e.returnValue = ''; });</script>
            </body></html>
            """),
    ];

    /// <summary>The page of a path, where <c>OTHER</c> in the frames page names the other origin.</summary>
    public string Url(string path) => Origin + path;

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}
