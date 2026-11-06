using System.Collections.Concurrent;
using CodeAlta.Browser.Annotations;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Toolbar;
using CodeAlta.Browser.Tools;

namespace CodeAlta.Browser.Tests;

/// <summary>The CodeAlta toolbar and its Annotate tool in a real Chromium, headless: its world, its binding, and what DevTools tells of an element.</summary>
[TestClass]
public sealed class BrowserAnnotationsTests
{
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
    public async Task AUserAnnotatesElementsAndTheBrowserTellsWhereTheyComeFrom()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await page.RecordNodeCreationAsync(timeout.Token);
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);

        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/styled")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/styled", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token), "The toolbar is enabled in the document.");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));

        // The page never gets the click that chooses an element.
        var button = await AnnotateAsync(session, state, "#record", "Make it bigger", messages, timeout.Token);
        Assert.AreEqual("Make it bigger", button.Note);
        var element = button.Elements!.Single();
        Assert.AreEqual("record", element.Id);
        Assert.AreEqual("#record", element.Selector);
        Assert.AreEqual("button", element.Role);
        Assert.AreEqual("Start recording", element.Text);
        StringAssert.Contains(element.Html, "class=\"record-btn\"");
        CollectionAssert.AreEqual(new[] { "main", "section.hero" }, element.Ancestors.ToArray());
        Assert.IsNotNull(button.Area);
        Assert.IsFalse(button.Area.Drawn, "one element clicked is no area drawn");
        Assert.AreEqual(element.Rect.Width, button.Area.Width, 1);
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => document.title"}"""), "Styled");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"marked({BrowserSession.JsonText(button.Id!)}, 1)", timeout.Token));

        var evidence = await page.InspectAsync(button.ContextId, button.Id!, 0, element, timeout.Token);
        var rule = evidence.Rules.FirstOrDefault(candidate => candidate.Selector == ".record-btn");
        Assert.IsNotNull(rule, "The rule of the stylesheet: " + string.Join(", ", evidence.Rules.Select(static item => item.Selector)));
        StringAssert.EndsWith(rule.SheetUrl, "/site.css");
        Assert.AreEqual(0, rule.Line);
        Assert.IsFalse(rule.Inline);
        var picture = await page.PictureAsync(button.Area, timeout.Token);
        Assert.IsNotNull(picture);
        CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71 }, picture.Take(4).ToArray());

        var item = await AnnotateAsync(session, state, "#list > p", "Shorter text", messages, timeout.Token);
        Assert.AreEqual("p", item.Elements!.Single().Tag);
        var created = await page.InspectAsync(item.ContextId, item.Id!, 0, item.Elements![0], timeout.Token);
        var frame = created.Creation.FirstOrDefault();
        Assert.IsNotNull(frame, "The browser recorded which script created the element.");
        StringAssert.EndsWith(frame.Url, "/app.js");
        Assert.AreEqual("addItem", frame.Function);
        Assert.AreEqual(1, frame.Line);

        var inline = created.Rules.FirstOrDefault(candidate => candidate.Selector == ".hero");
        Assert.IsNull(inline, "Only the rules of the element itself.");

        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));
    }

    [TestMethod]
    public async Task SeveralElementsGetOneNote_AndAnAreaGetsTheElementsInIt()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/cards")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/cards", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));

        // Shift+click gathers the first two cards; a click on the third adds it and opens the one note for all. The clicks
        // are the browser's input, as a person's are: events the page made up would choose nothing.
        foreach (var id in new[] { "c1", "c2" })
        {
            var center = await CenterAsync(session, "#" + id);
            await ((CodeAlta.Browser.Drivers.Cdp.CdpPage)state.Page).ClickWithModifiersAsync(BrowserPoint.OfPage(state.Page.MainFrame, center[0], center[1]), 8, timeout.Token);
            await Task.Delay(100, timeout.Token);
        }
        var group = await AnnotateAsync(session, state, "#c3", "The same spacing for all three", messages, timeout.Token);
        var cards = group.Elements!;
        CollectionAssert.AreEqual(new[] { "c1", "c2", "c3" }, cards.Select(static element => element.Id).ToArray());
        Assert.IsNotNull(group.Area);
        Assert.IsFalse(group.Area.Drawn);
        Assert.AreEqual(cards[2].Rect.Y + cards[2].Rect.Height - cards[0].Rect.Y, group.Area.Height, 1, "the box around the three cards");
        var third = await page.InspectAsync(group.ContextId, group.Id!, 2, cards[2], timeout.Token);
        Assert.IsNotNull(third);
        Assert.IsNotNull(await page.PictureAsync(group.Area, timeout.Token));

        // A drag draws an area, here from its bottom right to its top left, where it ends over the mark of the cards: its
        // note is about the cards wholly inside it, not about what holds them.
        var box = await session.OkAsync("evaluate_script", """{"function":"() => { const a = document.getElementById('c1').getBoundingClientRect(), b = document.getElementById('c2').getBoundingClientRect(); return 'box=' + [a.left - 5, a.top - 5, b.right + 5, b.bottom + 5].join(',') + ';'; }"}""");
        var start = box.IndexOf("box=", StringComparison.Ordinal) + 4;
        var edges = box[start..box.IndexOf(';', start)].Split(',').Select(static value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await state.Page.DragAsync(BrowserPoint.OfPage(state.Page.MainFrame, edges[2], edges[3]), BrowserPoint.OfPage(state.Page.MainFrame, edges[0], edges[1]), timeout.Token);
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Less space between these two", timeout.Token);
        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");
        var area = Next(messages, "annotation", timeout.Token, message => message.Note == "Less space between these two");
        Assert.IsNotNull(area.Area);
        Assert.IsTrue(area.Area.Drawn);
        Assert.AreEqual(edges[2] - edges[0], area.Area.Width, 2);
        CollectionAssert.AreEqual(new[] { "c1", "c2" }, area.Elements!.Select(static element => element.Id).ToArray());
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'selected=' + String(getSelection()) + ';'"}"""), "selected=;", "the drag selects no text of the page");

        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));
    }

    [TestMethod]
    public async Task TheBarMovesOutOfTheWayByItsHandle_AndComesBackToItsPlace()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/styled")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/styled", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));

        var (left, y) = await BarAsync(session);
        Assert.IsTrue(left > 0, "the bar is at the bottom right");
        // The handle is the brand at the start of the bar.
        var from = (X: left + 20, Y: y);
        var to = (X: from.X - 300, Y: from.Y - 200);
        await state.Page.DragAsync(BrowserPoint.OfPage(state.Page.MainFrame, from.X, from.Y), BrowserPoint.OfPage(state.Page.MainFrame, to.X, to.Y), timeout.Token);
        var moved = Next(messages, "position", timeout.Token);
        Assert.IsNotNull(moved.Position);
        Assert.AreEqual(16 + 300, moved.Position.Value.Right, 2);
        Assert.AreEqual(16 + 200, moved.Position.Value.Bottom, 2);
        Assert.IsFalse(await OnBarAsync(session, from.X, from.Y), "what was under the bar can be reached");
        Assert.IsTrue(await OnBarAsync(session, to.X, to.Y));

        // The host puts it back, as it does when the user double-clicks the handle in any tab.
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "place(null)", timeout.Token));
        Assert.IsTrue(await OnBarAsync(session, from.X, from.Y));

        // A place out of the window keeps the bar inside it.
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, """place({"right":99999,"bottom":99999})""", timeout.Token));
        Assert.IsTrue(await OnBarAsync(session, 20, 20), "the bar stays in the top left corner of the viewport");
    }

    [TestMethod]
    public void ToolbarMessages_OfTheBarItself_AreRead()
    {
        Assert.AreEqual("light", ToolbarMessage.TryParse("""{"type":"theme","theme":"light"}""", 1)!.Theme);
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"theme","theme":"blue"}""", 1));
        Assert.AreEqual((10.5, 20d), ToolbarMessage.TryParse("""{"type":"position","position":{"right":10.5,"bottom":20}}""", 1)!.Position);
        var back = ToolbarMessage.TryParse("""{"type":"position","position":null}""", 1);
        Assert.IsNotNull(back);
        Assert.IsNull(back.Position);
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"position","position":{"right":"x"}}""", 1));
        Assert.AreEqual("Shorter", ToolbarMessage.TryParse("""{"type":"edit","id":"a1","note":"Shorter"}""", 1)!.Note);
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"edit","id":"a1","note":""}""", 1));
        Assert.AreEqual(string.Empty, ToolbarMessage.TryParse("""{"type":"send"}""", 1)!.Note);

        // An annotation is about elements, an area drawn, or both; never about nothing.
        var drawn = ToolbarMessage.TryParse("""{"type":"annotation","id":"a1","note":"n","elements":[],"area":{"x":1,"y":2,"width":30,"height":40,"drawn":true}}""", 1);
        Assert.IsNotNull(drawn);
        Assert.AreEqual(new AnnotationArea(1, 2, 30, 40, true), drawn.Area);
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"annotation","id":"a1","note":"n","elements":[],"area":{"x":1,"y":2,"width":30,"height":40}}""", 1));
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"annotation","id":"a1","note":"n","elements":[{"tag":"p"}]}""", 1), "an annotation says its area");
        var many = string.Join(',', Enumerable.Repeat("""{"tag":"p"}""", 20));
        Assert.AreEqual(ToolbarMessage.MaximumElements, ToolbarMessage.TryParse($$$"""{"type":"annotation","id":"a1","note":"n","elements":[{{{many}}}],"area":{"x":0,"y":0,"width":1,"height":1}}""", 1)!.Elements!.Count);
    }

    // The left edge of the bar at the height of its middle: the bar is in a closed shadow root, so the page sees only its host element.
    private static async Task<(double Left, double Y)> BarAsync(BrowserSession session)
    {
        var text = await session.OkAsync("evaluate_script", """{"function":"() => { const y = innerHeight - 31; for (let x = 0; x < innerWidth; x++) { const e = document.elementFromPoint(x, y); if (e && e.hasAttribute('data-codealta-toolbar')) return [x, y]; } return [-1, y]; }"}""");
        var numbers = text[(text.IndexOf('[') + 1)..text.IndexOf(']')].Split(',').Select(static value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return (numbers[0], numbers[1]);
    }

    private static async Task<bool> OnBarAsync(BrowserSession session, double x, double y)
        => (await session.OkAsync("evaluate_script", $$"""{"function":"() => 'on=' + (document.elementFromPoint({{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, {{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}})?.hasAttribute('data-codealta-toolbar') === true)"}""")).Contains("on=true", StringComparison.Ordinal);

    [TestMethod]
    public async Task TheAnnotationsAPromptTook_LeaveThePage_AndOneSavedMeanwhileKeepsItsMark()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/cards")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/cards", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));

        var sent = await AnnotateAsync(session, state, "#c1", "Sent with the prompt", messages, timeout.Token);
        var meanwhile = await AnnotateAsync(session, state, "#c2", "Saved while the prompt was on its way", messages, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().marks === 2", timeout.Token));

        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"taken([{BrowserSession.JsonText(sent.Id!)}])", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().marks === 1", timeout.Token), "only the mark of the annotation taken leaves the page");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"node({BrowserSession.JsonText(meanwhile.Id!)}, 0) !== null", timeout.Token), "the other one keeps its element");
    }

    [TestMethod]
    public async Task ThePageCannotReportAnAnnotation()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var page = (IAnnotatablePage)session.Pages[0].Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/styled")}}"}""");
        Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/styled", StringComparison.Ordinal) == true);

        var seen = await session.OkAsync("evaluate_script", """{"function":"() => [typeof globalThis.__codealtaToolbarMessage, typeof globalThis.__codealtaToolbar].join(',')"}""");
        StringAssert.Contains(seen, "undefined,undefined");
        await Task.Delay(300);
        Assert.IsFalse(messages.Any(static message => message.Type == "annotation"));
    }

    [TestMethod]
    public async Task ThePageCannotReachTheBar_NorChooseWithEventsItMakesUp()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var page = (IAnnotatablePage)session.Pages[0].Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/hostile")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/hostile", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));
        await Task.Delay(200);

        // The bar is no element the page can define: the page gets neither its internals nor its shadow root.
        var reach = await session.OkAsync("evaluate_script", """{"function":"() => { const host = document.querySelector('[data-codealta-toolbar]'); return 'captured=' + window.__captured.filter(internals => internals.shadowRoot).length + ';host=' + !!host + ';open=' + !!(host && host.shadowRoot) + ';'; }"}""");
        StringAssert.Contains(reach, "captured=0;host=true;open=false;");

        // Clicks and keys the page makes up choose nothing, while the user annotates.
        await session.OkAsync("evaluate_script", """{"function":"() => { const target = document.getElementById('target'); const at = { bubbles: true, cancelable: true, composed: true, clientX: 60, clientY: 60 }; for (const type of ['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click']) target.dispatchEvent(new (type.startsWith('pointer') ? PointerEvent : MouseEvent)(type, at)); target.dispatchEvent(new MouseEvent('click', { ...at, shiftKey: true })); target.click(); document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); return 'sent'; }"}""");
        await Task.Delay(300);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().chosen === 0", timeout.Token), "no element was chosen");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting === false", timeout.Token), "no note was opened");
        Assert.IsFalse(messages.Any(static message => message.Type == "annotation"));
    }

    // The center of an element, in the CSS pixels of the page.
    private static async Task<double[]> CenterAsync(BrowserSession session, string selector)
    {
        var center = await session.OkAsync("evaluate_script", $$"""{"function":"() => { const r = document.querySelector('{{selector}}').getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2]; }"}""");
        return center[(center.IndexOf('[') + 1)..center.IndexOf(']')].Split(',').Select(static text => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static async Task<ToolbarMessage> AnnotateAsync(BrowserSession session, BrowserPageState state, string selector, string note,
        BlockingCollection<ToolbarMessage> messages, CancellationToken cancellationToken)
    {
        var numbers = await CenterAsync(session, selector);
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, numbers[0], numbers[1]), 1, cancellationToken);
        await Task.Delay(200, cancellationToken);
        await state.Page.InsertTextAsync(note, cancellationToken);
        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");
        return Next(messages, "annotation", cancellationToken, message => message.Note == note);
    }

    private static ToolbarMessage Next(BlockingCollection<ToolbarMessage> messages, string type, CancellationToken cancellationToken, Func<ToolbarMessage, bool>? accept = null)
    {
        while (true)
        {
            var message = messages.Take(cancellationToken);
            if (message.Type == type && (accept is null || accept(message))) return message;
        }
    }
}
