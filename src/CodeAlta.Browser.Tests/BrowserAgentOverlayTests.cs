using System.Diagnostics;
using System.Text.Json;
using CodeAlta.Browser.Annotations;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Drivers.Cdp;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Tests;

/// <summary>
/// The overlay that shows the user where the agent acts, in a real headless Chromium that is told to show it as a browser
/// with a window does: the cursor, the glow and the label while the tools act, never in what the agent sees of the page.
/// </summary>
[TestClass]
public sealed class BrowserAgentOverlayTests
{
    // A white page with a button that records the clicks it gets, where they land.
    private const string PageScript = """
        () => {
          document.title = 'Overlay';
          document.body.innerHTML = '';
          document.body.style.cssText = 'margin:0;background:#fff;height:100vh';
          const button = document.createElement('button');
          button.textContent = 'Record';
          button.style.cssText = 'position:fixed;left:200px;top:150px;width:120px;height:40px;margin:0;border:0;background:#fff;color:#fff';
          window.__clicks = [];
          document.addEventListener('click', event => window.__clicks.push({ x: event.clientX, y: event.clientY, target: event.target.localName, trusted: event.isTrusted }), true);
          document.body.appendChild(button);
          return true;
        }
        """;

    private BrowserTestHost? _host;

    private BrowserTestHost Host => _host ?? throw new AssertInconclusiveException("No Chromium browser is installed on this computer.");

    [TestInitialize]
    public async Task StartAsync()
    {
        _host = await BrowserTestHost.StartAsync(BrowserEngine.Chromium);
        if (_host is null) Assert.Inconclusive("No Chromium browser is installed on this computer.");
    }

    [TestCleanup]
    public async Task StopAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [TestMethod]
    public async Task TheCursorGoesWhereTheAgentClicks_AndTheClickLandsThere()
    {
        await using var session = NewSession(showAgent: true);
        await OpenAsync(session);
        var page = (CdpPage)session.Pages[0].Page;
        var button = BrowserToolTesting.Uid(await session.OkAsync("take_snapshot"), "button \"Record\"");
        await session.OkAsync("click", $$"""{"uid":"{{button}}"}""");
        var state = (await StateAsync(page))!.Value;
        Assert.IsTrue(state.GetProperty("present").GetBoolean());
        Assert.IsTrue(state.GetProperty("topLayer").GetBoolean(), "The overlay is in the top layer.");
        Assert.IsTrue(state.GetProperty("active").GetBoolean(), "The glow and the label show while the agent acts.");
        Assert.AreEqual("CodeAlta agent is using this page", state.GetProperty("label").GetString());
        var cursor = state.GetProperty("cursor");
        Assert.AreEqual(260, cursor.GetProperty("x").GetDouble(), 1);
        Assert.AreEqual(170, cursor.GetProperty("y").GetDouble(), 1);

        // The overlay takes no pointer: the click reached the button, where the cursor is.
        var clicks = JsonDocument.Parse(Json(await session.OkAsync("evaluate_script", """{"function":"() => window.__clicks"}"""))).RootElement;
        Assert.AreEqual(1, clicks.GetArrayLength());
        Assert.AreEqual("button", clicks[0].GetProperty("target").GetString());
        Assert.IsTrue(clicks[0].GetProperty("trusted").GetBoolean());
        Assert.AreEqual(cursor.GetProperty("x").GetDouble(), clicks[0].GetProperty("x").GetDouble(), 1);
        Assert.AreEqual(cursor.GetProperty("y").GetDouble(), clicks[0].GetProperty("y").GetDouble(), 1);

        // click_at goes where it is asked, through the overlay, and the cursor with it.
        await session.OkAsync("click_at", """{"x":40,"y":400}""");
        clicks = JsonDocument.Parse(Json(await session.OkAsync("evaluate_script", """{"function":"() => window.__clicks"}"""))).RootElement;
        Assert.AreEqual(2, clicks.GetArrayLength());
        Assert.AreEqual("body", clicks[1].GetProperty("target").GetString());
        Assert.AreEqual(40, clicks[1].GetProperty("x").GetDouble(), 1);
        Assert.AreEqual(400, clicks[1].GetProperty("y").GetDouble(), 1);
        cursor = (await StateAsync(page))!.Value.GetProperty("cursor");
        Assert.AreEqual(40, cursor.GetProperty("x").GetDouble(), 1);
        Assert.AreEqual(400, cursor.GetProperty("y").GetDouble(), 1);

        // A cursor at rest dims, and stays where the agent left it.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.IsTrue((await StateAsync(page))!.Value.GetProperty("cursor").GetProperty("idle").GetBoolean());
    }

    [TestMethod]
    public async Task AMoveWaitsForTheCursorToArrive_UnlessMotionIsReduced()
    {
        await using var session = NewSession(showAgent: true);
        await OpenAsync(session);
        var page = (CdpPage)session.Pages[0].Page;
        using var timeout = new CancellationTokenSource(BrowserToolTesting.Deadline);
        var main = page.MainFrame;
        // A system without animations (Windows Server) asks pages for reduced motion: the motion is asked for here.
        await session.OkAsync("emulate", """{"prefersReducedMotion":"no-preference"}""");
        await page.MouseMoveAsync(BrowserPoint.OfPage(main, 10, 10), timeout.Token);

        // The cursor travels to a far point before the pointer gets there: at least the shortest flight, at most the limit.
        var watch = Stopwatch.StartNew();
        await page.MouseMoveAsync(BrowserPoint.OfPage(main, 700, 500), timeout.Token);
        watch.Stop();
        Assert.IsTrue(watch.ElapsedMilliseconds >= 150, $"The move took {watch.ElapsedMilliseconds} ms.");

        // With reduced motion, the cursor jumps.
        await session.OkAsync("emulate", """{"prefersReducedMotion":"reduce"}""");
        var state = (await StateAsync(page))!.Value;
        Assert.IsTrue(state.GetProperty("reducedMotion").GetBoolean());
        await page.MouseMoveAsync(BrowserPoint.OfPage(main, 20, 20), timeout.Token);
        var cursor = (await StateAsync(page))!.Value.GetProperty("cursor");
        Assert.AreEqual(20, cursor.GetProperty("x").GetDouble(), 1);
        Assert.AreEqual(20, cursor.GetProperty("y").GetDouble(), 1);
    }

    [TestMethod]
    public async Task TheOverlayIsLeftOutOfSnapshotsAndPictures()
    {
        await using var session = NewSession(showAgent: true);
        await OpenAsync(session);
        var page = (CdpPage)session.Pages[0].Page;
        using var timeout = new CancellationTokenSource(BrowserToolTesting.Deadline);
        var clean = await page.ScreenshotAsync(new ScreenshotRequest(), timeout.Token);
        var area = new AnnotationArea(100, 100, 400, 300, Drawn: true);
        var cleanPicture = await ((IAnnotatablePage)page).PictureAsync(area, timeout.Token);
        Assert.IsNotNull(cleanPicture);
        var cleanDrawing = await ((IAnnotatablePage)page).ViewPictureAsync(area, timeout.Token);
        Assert.IsNotNull(cleanDrawing);

        // An action on the white page changes nothing of it, but shows the cursor, the glow and the label.
        await session.OkAsync("click_at", """{"x":600,"y":300}""");
        Assert.IsTrue((await StateAsync(page))!.Value.GetProperty("active").GetBoolean());
        var raw = await RawScreenshotAsync(page, timeout.Token);
        CollectionAssert.AreNotEqual(clean, raw, "The overlay shows in the window.");

        var snapshot = await session.OkAsync("take_snapshot");
        Assert.IsFalse(snapshot.Contains("CodeAlta agent", StringComparison.Ordinal), snapshot);
        Assert.IsFalse(snapshot.Contains("using this page", StringComparison.Ordinal), snapshot);

        CollectionAssert.AreEqual(clean, await page.ScreenshotAsync(new ScreenshotRequest(), timeout.Token), "take_screenshot sees the page alone.");
        var shot = await session.CallAsync("take_screenshot");
        Assert.IsFalse(shot.IsError, shot.Text);
        // The picture of an annotation, the cursor in it, is of the page alone too.
        await session.OkAsync("click_at", """{"x":300,"y":250}""");
        CollectionAssert.AreEqual(cleanPicture, await ((IAnnotatablePage)page).PictureAsync(area, timeout.Token), "The picture of an annotation sees the page alone.");
        // The picture of a drawing, which view_annotation shows, too: the cursor is still where the agent clicked, in its area.
        Assert.IsTrue((await StateAsync(page))!.Value.GetProperty("active").GetBoolean());
        CollectionAssert.AreEqual(cleanDrawing, await ((IAnnotatablePage)page).ViewPictureAsync(area, timeout.Token), "The picture of a drawing sees the page alone.");

        // Shown again after each picture.
        var state = (await StateAsync(page))!.Value;
        Assert.IsFalse(state.GetProperty("hidden").GetBoolean());
        Assert.IsTrue(state.GetProperty("active").GetBoolean());
        CollectionAssert.AreNotEqual(clean, await RawScreenshotAsync(page, timeout.Token));
    }

    [TestMethod]
    public async Task ThePageCannotReachTheOverlay_AndRemovingItOnlyLastsUntilTheNextAction()
    {
        await using var session = NewSession(showAgent: true);
        await OpenAsync(session);
        var page = (CdpPage)session.Pages[0].Page;
        await session.OkAsync("click_at", """{"x":100,"y":100}""");

        var seen = Json(await session.OkAsync("evaluate_script", """
            {"function":"() => { const host = document.querySelector('[data-codealta-agent-overlay]'); return { host: !!host, shadow: host ? host.shadowRoot === null : null, hidden: host ? host.getAttribute('aria-hidden') : null, global: typeof window.__codealtaAgentOverlay, popovers: document.querySelectorAll('[popover]').length }; }"}
            """));
        var reached = JsonDocument.Parse(seen).RootElement;
        Assert.IsTrue(reached.GetProperty("host").GetBoolean());
        Assert.IsTrue(reached.GetProperty("shadow").GetBoolean(), "The shadow root is closed.");
        Assert.AreEqual("true", reached.GetProperty("hidden").GetString());
        Assert.AreEqual("undefined", reached.GetProperty("global").GetString());
        Assert.AreEqual(0, reached.GetProperty("popovers").GetInt32(), "The popover of the overlay is inside its shadow root.");

        // A script of the agent is an action too: the overlay its page removed is back once it ran.
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => { document.querySelector('[data-codealta-agent-overlay]').remove(); return document.querySelector('[data-codealta-agent-overlay]') === null; }"}"""), "true");
        Assert.IsTrue((await StateAsync(page))!.Value.GetProperty("present").GetBoolean());
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => document.querySelector('[data-codealta-agent-overlay]') !== null"}"""), "true");
    }

    [TestMethod]
    public async Task ADragTakesTheCursorAlong_AndANewDocumentShowsTheActivityAgain()
    {
        await using var session = NewSession(showAgent: true);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/cards")}}"}""");
        var page = (CdpPage)session.Pages[0].Page;
        var snapshot = await session.OkAsync("take_snapshot");
        await session.OkAsync("drag", $$"""{"from_uid":"{{BrowserToolTesting.Uid(snapshot, "One")}}","to_uid":"{{BrowserToolTesting.Uid(snapshot, "Three")}}"}""");
        var end = JsonDocument.Parse(Json(await session.OkAsync("evaluate_script", """{"function":"() => { const box = document.getElementById('c3').getBoundingClientRect(); return { x: box.left + box.width / 2, y: box.top + box.height / 2 }; }"}"""))).RootElement;
        var cursor = (await StateAsync(page))!.Value.GetProperty("cursor");
        Assert.AreEqual(end.GetProperty("x").GetDouble(), cursor.GetProperty("x").GetDouble(), 2);
        Assert.AreEqual(end.GetProperty("y").GetDouble(), cursor.GetProperty("y").GetDouble(), 2);

        // A click that loads another document leaves the activity shown in that one.
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/form")}}"}""");
        var link = BrowserToolTesting.Uid(await session.OkAsync("take_snapshot"), "link \"Go to the second page\"");
        StringAssert.Contains(await session.OkAsync("click", $$"""{"uid":"{{link}}"}"""), "Page navigated to");
        var state = (await StateAsync(page))!.Value;
        Assert.IsTrue(state.GetProperty("active").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, state.GetProperty("cursor").ValueKind, "A new document shows the cursor at the next pointer input.");
    }

    [TestMethod]
    public async Task AModalDialogOfThePage_KeepsTheOverlayInTheTopLayer_AndStillGetsTheClicks()
    {
        await using var session = NewSession(showAgent: true);
        await OpenAsync(session);
        var page = (CdpPage)session.Pages[0].Page;
        await session.OkAsync("click_at", """{"x":10,"y":10}""");
        await session.OkAsync("evaluate_script", """
            {"function":"() => { const dialog = document.createElement('dialog'); dialog.id = 'modal'; dialog.style.cssText = 'margin:0;left:100px;top:100px;padding:0;border:0'; const ok = document.createElement('button'); ok.textContent = 'OK'; ok.style.cssText = 'width:100px;height:40px'; dialog.appendChild(ok); document.body.appendChild(dialog); dialog.showModal(); return true; }"}
            """);
        var ok = BrowserToolTesting.Uid(await session.OkAsync("take_snapshot"), "button \"OK\"");
        await session.OkAsync("click", $$"""{"uid":"{{ok}}"}""");
        Assert.IsTrue((await StateAsync(page))!.Value.GetProperty("topLayer").GetBoolean());
        var clicks = JsonDocument.Parse(Json(await session.OkAsync("evaluate_script", """{"function":"() => window.__clicks.map(click => click.target)"}"""))).RootElement;
        Assert.AreEqual("button", clicks[clicks.GetArrayLength() - 1].GetString(), clicks.ToString());
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => document.activeElement && document.activeElement.textContent"}"""), "OK");
    }

    [TestMethod]
    public async Task TheOverlayComesWithTheFirstAction_NotWithThePage()
    {
        await using var session = NewSession(showAgent: true);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/form")}}"}""");
        var page = (CdpPage)session.Pages[0].Page;
        Assert.IsTrue(page.ShowsAgent);
        await session.OkAsync("take_snapshot");
        await session.OkAsync("take_screenshot");
        Assert.IsNull(await StateAsync(page));
    }

    [TestMethod]
    public async Task ABrowserWithoutAWindow_ShowsNothing()
    {
        await using var session = NewSession(showAgent: false);
        await OpenAsync(session);
        var page = (CdpPage)session.Pages[0].Page;
        Assert.IsFalse(page.ShowsAgent);
        await session.OkAsync("click_at", """{"x":260,"y":170}""");
        Assert.IsNull(await StateAsync(page));
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => document.querySelector('[data-codealta-agent-overlay]') === null"}"""), "true");
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => window.__clicks.length"}"""), "1");
    }

    // A session of the tools whose browser shows the agent, as one with a window does, or not.
    private BrowserSession NewSession(bool showAgent)
    {
        var files = new BrowserFiles(Host.Folder, [Host.Folder], [Host.Folder], Path.Combine(Host.Folder, "tmp"));
        return new BrowserSession(new BrowserSessionOptions
        {
            OpenContext = cancellationToken => Host.Connection.CreateContextAsync(new BrowserContextOptions { ShowAgent = showAgent, DownloadDirectory = Path.Combine(Host.Folder, "downloads") }, cancellationToken),
            Files = _ => ValueTask.FromResult(files),
        });
    }

    private async Task OpenAsync(BrowserSession session)
    {
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/second")}}"}""");
        await session.OkAsync("evaluate_script", JsonSerializer.Serialize(new Dictionary<string, string> { ["function"] = PageScript }));
        await session.OkAsync("resize_page", """{"width":800,"height":600}""");
    }

    private static async Task<JsonElement?> StateAsync(CdpPage page)
    {
        using var timeout = new CancellationTokenSource(BrowserToolTesting.Deadline);
        return await page.AgentOverlayStateAsync(timeout.Token);
    }

    // A picture of the tab as the window shows it, taken past the tools, overlay included.
    private async Task<byte[]> RawScreenshotAsync(CdpPage page, CancellationToken cancellationToken)
    {
        var connection = ((CdpBrowserConnection)Host.Connection).Connection;
        var attached = await connection.Browser.Target.AttachToTargetAsync(new() { TargetId = page.TargetId, Flatten = true }, cancellationToken);
        try
        {
            var shot = await connection.Session(attached.SessionId).Page.CaptureScreenshotAsync(new() { Format = "png", OptimizeForSpeed = true }, cancellationToken);
            return Convert.FromBase64String(shot.Data);
        }
        finally
        {
            await connection.Browser.Target.DetachFromTargetAsync(new() { SessionId = attached.SessionId }, cancellationToken);
        }
    }

    // The JSON a call of evaluate_script gives back, in its fence.
    private static string Json(string text)
    {
        var start = text.IndexOf("```json\n", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, text);
        start += "```json\n".Length;
        return text[start..text.IndexOf("\n```", start, StringComparison.Ordinal)];
    }
}
