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

    /// <summary>The password of the sign-in of the fill_login tests: a form, an address and JSON each write it encoded.</summary>
    public const string SignInPassword = "hunter 2&gh+ub!é\"x";

    private int _signInsPosted;

    public int Port { get; }

    /// <summary>Gets how many times the user name and the password of the sign-in reached the site.</summary>
    public int SignInsPosted => Volatile.Read(ref _signInsPosted);

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
        TestSite? site = null;
        foreach (var (path, html) in Pages) app.MapGet(path, () => Results.Content(html.Replace("OTHER/", $"http://localhost:{port}/", StringComparison.Ordinal), "text/html; charset=utf-8"));
        app.MapGet("/api/data", () => Results.Json(new Dictionary<string, object> { ["answer"] = 42, ["items"] = new[] { "a", "b" } }));
        app.MapGet("/site.css", () => Results.Content(".record-btn {\n  color: rgb(200, 0, 0);\n  padding: 4px;\n}\n", "text/css"));
        app.MapGet("/app.js", () => Results.Content("function addItem(text) {\n  const item = document.createElement('p');\n  item.className = 'item';\n  item.textContent = text;\n  document.getElementById('list').appendChild(item);\n}\naddItem('Created by script');\n", "text/javascript"));
        app.MapPost("/api/echo", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            return Results.Content("echo:" + await reader.ReadToEndAsync(), "text/plain");
        });
        // The sign-in of the fill_login tests: octocat with its password, then a one-time code, then a welcome page.
        app.MapPost("/session", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            if (form["login"] != "octocat" || form["password"] != SignInPassword) return Results.Redirect("/login?error=1");
            Interlocked.Increment(ref site!._signInsPosted);
            return Results.Redirect("/two-factor");
        });
        app.MapPost("/two-factor", async (HttpRequest request) => (await request.ReadFormAsync())["otp"] == "123456" ? Results.Redirect("/welcome") : Results.Redirect("/two-factor?error=1"));
        site = new TestSite(app, port);
        await app.StartAsync();
        return site;
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
        ("/login", """
            <!doctype html><html><head><title>Sign in to Demo</title></head><body>
            <form method="post" action="/session">
              <label>Username or email <input id="login" name="login" autocomplete="username"></label>
              <label>Password <input id="password" name="password" type="password" autocomplete="current-password"></label>
              <button type="submit">Sign in</button>
            </form></body></html>
            """),
        ("/two-factor", """
            <!doctype html><html><head><title>Two-factor</title></head><body>
            <form method="post" action="/two-factor"><label>Code <input id="otp" name="otp" autocomplete="one-time-code" inputmode="numeric"></label><button type="submit">Verify</button></form>
            </body></html>
            """),
        ("/welcome", """
            <!doctype html><html><head><title>Welcome</title></head><body><h1>Welcome back</h1></body></html>
            """),
        // The password field is out of the page: a user would never see what is typed there.
        ("/login-hidden", """
            <!doctype html><html><head><title>Hidden</title></head><body>
            <form method="post" action="/session">
              <label>Username <input id="login" name="login" autocomplete="username"></label>
              <input id="password" name="password" type="password" style="position:absolute;left:-9999px">
              <button type="submit">Sign in</button>
            </form></body></html>
            """),
        // The form sends to another site than the page.
        ("/login-elsewhere", """
            <!doctype html><html><head><title>Elsewhere</title></head><body>
            <form method="post" action="OTHER/session">
              <label>Username <input id="login" name="login" autocomplete="username"></label>
              <label>Password <input id="password" name="password" type="password"></label>
              <button type="submit">Sign in</button>
            </form></body></html>
            """),
        // The sign-in form is in a frame of another site.
        ("/login-frame", """
            <!doctype html><html><head><title>Framed</title></head><body>
            <h1>Sign in below</h1>
            <iframe id="other" src="OTHER/login" style="width:400px;height:200px"></iframe>
            </body></html>
            """),
        // A button outside the form joins it and comes first in tree order: Enter would click it, and it sends to another site.
        ("/login-external-button", """
            <!doctype html><html><head><title>External button</title></head><body>
            <button form="login-form" formaction="OTHER/session" type="submit" style="position:absolute;left:-9999px">Go</button>
            <form id="login-form" method="post" action="/session">
              <label>Username <input id="login" name="login" autocomplete="username"></label>
              <label>Password <input id="password" name="password" type="password"></label>
              <button type="submit">Sign in</button>
            </form></body></html>
            """),
        // A page whose timer moves the focus into a frame of another site, whose document takes any text, once each field
        // of the sign-in is focused: the frame tells the page how much it got. The page takes the sign-in with a script.
        ("/login-focus-thief", """
            <!doctype html><html><head><title>Focus thief</title></head><body>
            <form id="form" onsubmit="event.preventDefault(); window.__typed = [document.getElementById('login').value.length, document.getElementById('password').value.length]; document.getElementById('login').value = ''; document.getElementById('password').value = ''; document.getElementById('done').textContent = 'taken';">
              <label>Username <input id="login" name="login" autocomplete="username"></label>
              <label>Password <input id="password" name="password" type="password"></label>
              <button type="submit">Sign in</button>
            </form><p id="done"></p>
            <iframe id="thief" src="OTHER/thief" style="width:300px;height:100px"></iframe>
            <script>
            window.__leaked = 0;
            addEventListener('message', event => { if (typeof event.data === 'number') window.__leaked = Math.max(window.__leaked, event.data); });
            for (const id of ['login', 'password']) document.getElementById(id).addEventListener('focus', () => setTimeout(() => document.getElementById('thief').focus(), 0), { once: true });
            </script></body></html>
            """),
        ("/thief", """
            <!doctype html><html><head><title>Thief</title></head><body contenteditable="true" style="min-height:80px">
            <script>
            const tell = () => parent.postMessage(document.body.innerText.trim().length, '*');
            document.body.addEventListener('input', tell);
            setInterval(tell, 50);
            </script>
            </body></html>
            """),
        // A page of scripts that refuses the sign-in and keeps what was typed.
        ("/login-script", """
            <!doctype html><html><head><title>Scripted</title></head><body>
            <form id="form" onsubmit="event.preventDefault(); document.getElementById('error').textContent = 'Incorrect username or password.';">
              <label>Username <input id="login" name="login" autocomplete="username"></label>
              <label>Password <input id="password" name="password" type="password"></label>
              <button type="submit">Sign in</button>
            </form><p id="error"></p></body></html>
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
        ("/styled", """
            <!doctype html><html><head><title>Styled</title><link rel="stylesheet" href="/site.css">
            <style>
            .hero { margin: 20px; }
            </style></head><body>
            <main><section class="hero"><button id="record" class="record-btn" type="button" onclick="document.title = 'clicked'">Start recording</button></section>
            <div id="list"></div></main>
            <script src="/app.js"></script>
            </body></html>
            """),
        ("/cards", """
            <!doctype html><html><head><title>Cards</title></head><body style="margin:0">
            <div id="wrap" style="padding:20px">
            <div class="card" id="c1" style="height:40px;margin:10px 0">One</div>
            <div class="card" id="c2" style="height:40px;margin:10px 0">Two</div>
            <div class="card" id="c3" style="height:40px;margin:10px 0">Three</div>
            <p id="other">Other things</p>
            </div></body></html>
            """),
        // A page that tries to reach the bar: it claims the name of a custom element before the bar is built, keeping the
        // internals of any such element, whose shadow root they would give.
        ("/hostile", """
            <!doctype html><html><head><title>Hostile</title>
            <script>
            window.__captured = [];
            customElements.define('codealta-toolbar', class extends HTMLElement { constructor() { super(); window.__captured.push(this.attachInternals()); } });
            </script></head><body style="margin:0">
            <button id="target" type="button" style="position:absolute;left:40px;top:40px;width:120px;height:40px">Target</button>
            </body></html>
            """),
        // A page whose elements hold what must not reach a prompt: tokens in addresses, secrets in data attributes and in
        // labels, scripts, styles and comments, and an element far larger than any bound.
        ("/secrets", """
            <!doctype html><html><head><title>Secrets</title></head><body style="margin:0">
            <div id="box" class="panel" data-session="s3cr3t-session-value" aria-label="Reset with password: hunter22" style="padding:10px 10px 200px">
            <a id="reset" href="/reset?token=abc123#done" data-api-key="sk-live-123" data-testid="reset-link" title="See https://example.com/cb?access_token=zzz999" onclick="steal()">Reset now</a>
            <img src="data:image/png;base64,AAAA" alt="logo">
            <script>window.hidden = 'in a script';</script>
            <style>.panel { color: red; }</style>
            <!-- a comment with password=hunter2 -->
            <input type="password" name="password" value="hunter2">
            </div>
            <div id="big" style="height:80px"></div>
            <script>
            const big = document.getElementById('big');
            big.appendChild(document.createTextNode('word '.repeat(40000)));
            for (let i = 0; i < 3000; i++) { const b = document.createElement('b'); b.style.display = 'none'; b.textContent = 'hidden ' + i; big.appendChild(b); }
            </script>
            </body></html>
            """),
        // A page built by tools that name its classes and ids with hashes of the build or of the render, as CSS in
        // JavaScript, CSS modules and React do: a selector of the Annotate tool skips those names.
        ("/generated", """
            <!doctype html><html><head><title>Generated</title></head><body style="margin:0">
            <div class="css-1x2y3z Card_card__3xYz1 sc-AxjAm kGtEvt" style="padding:10px">
            <ul class="menu"><li class="menu__item active" data-k="one">One</li><li class="menu__item" data-k="two">Two</li></ul>
            </div>
            <section class="Panel_panel__aB3cD panel"><button class="_primary_1x2y3_12 jsx-12345 buy" data-k="buy1" type="button">Buy one</button></section>
            <section class="Panel_panel__aB3cD panel"><button class="_primary_1x2y3_12 jsx-12345 buy" data-k="buy2" type="button">Buy two</button></section>
            <div id=":r5:"><span class="xYz12AbCdEf9GhIj" data-k="hashy">Hashy</span><span data-k="plain">Plain</span></div>
            </body></html>
            """),
        // A page that shows a modal dialog and a popover in the top layer, above every z-index of the page.
        ("/modal", """
            <!doctype html><html><head><title>Modal</title></head><body style="margin:0">
            <p id="behind">Behind the dialog</p>
            <dialog id="dialog" style="margin:0;left:20px;top:20px;width:300px;height:120px;padding:10px;border:0"><button id="inside" type="button">Inside the dialog</button></dialog>
            <div id="pop" popover="manual" style="margin:0;inset:auto 0 0 auto;width:100vw;height:100vh;padding:0;border:0;background:rgba(0,0,0,.2)">A popover over everything</div>
            </body></html>
            """),
        // A page that listens, before anything else of it, to every key, input, paste, copy, cut and drag on the window and
        // on the document as they start, and writes down what their data holds: none of what goes into the bar may reach it.
        ("/snoop", """
            <!doctype html><html><head><title>Snoop</title>
            <script>
            window.__snooped = [];
            const record = where => event => {
              const data = event.clipboardData || event.dataTransfer;
              let files = 0, text = '';
              try { files = data ? data.files.length : 0; text = data ? data.getData('text/plain') : event.key || event.data || ''; } catch (_) { }
              window.__snooped.push(where + ':' + event.type + ':' + (data ? [...data.types].join('|') : '') + ':' + files + ':' + text);
            };
            for (const type of ['paste', 'copy', 'cut', 'drop', 'dragenter', 'dragover', 'dragleave', 'keydown', 'keypress', 'keyup', 'beforeinput', 'input', 'textInput', 'compositionstart', 'compositionupdate', 'compositionend']) {
              window.addEventListener(type, record('window'), true);
              document.addEventListener(type, record('document'), true);
            }
            </script></head><body style="margin:0">
            <div id="wrap" style="padding:20px">
            <div class="card" id="c1" style="height:40px;margin:10px 0">One</div>
            <div class="card" id="c2" style="height:40px;margin:10px 0">Two</div>
            </div></body></html>
            """),
        // A page that shows its modal dialog as it loads, before a toolbar added to the open tab is there to see it open.
        ("/modal-open", """
            <!doctype html><html><head><title>Modal open</title></head><body style="margin:0">
            <p id="behind">Behind the dialog</p>
            <dialog id="dialog" style="margin:0;left:20px;top:20px;width:300px;height:120px;padding:10px;border:0"><button id="inside" type="button">Inside the dialog</button></dialog>
            <script>document.getElementById('dialog').showModal();</script>
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
