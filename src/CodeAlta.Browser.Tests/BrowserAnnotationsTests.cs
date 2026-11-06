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
        // Its computed styles, without the defaults.
        var styles = element.Styles.ToDictionary(static style => style.Key, static style => style.Value);
        Assert.AreEqual("rgb(200, 0, 0)", styles["color"]);
        Assert.AreEqual("4px", styles["padding"]);
        Assert.IsTrue(styles.ContainsKey("font-family") && styles.ContainsKey("font-size"));
        Assert.IsFalse(styles.ContainsKey("position"), "static is the default");
        Assert.IsFalse(styles.ContainsKey("z-index"), "auto is the default");
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
        CollectionAssert.AreEqual(new[] { "before: div#c1.card \"One\"", "after: div#c3.card \"Three\"", "after: p#other \"Other things\"" }, cards[1].Nearby.ToArray());
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
    public async Task TheBarTellsNoSecretOfThePage_AndBoundsWhatItReads()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/secrets")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/secrets", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));

        // A link: its address loses the token of its query, a data attribute with a key goes, the hook of the tests stays,
        // and a title with a token in an address is replaced.
        var link = (await AnnotateAsync(session, state, "#reset", "Rename it", messages, timeout.Token)).Elements!.Single();
        Assert.AreEqual("<a id=\"reset\" href=\"/reset\" data-testid=\"reset-link\" title=\"[redacted]\">Reset now</a>", link.Html);
        Assert.AreEqual("See https://example.com/cb", link.Name, "an address in a text loses its query");
        Assert.AreEqual("Reset now", link.Text);

        // Around it: no script, no style, no comment, no value of a field, no data URL; a label with a password is replaced.
        var box = (await AnnotateAsync(session, state, "#box", "Less padding", messages, timeout.Token)).Elements!.Single();
        Assert.AreEqual("box", box.Id);
        StringAssert.StartsWith(box.Html, "<div id=\"box\" class=\"panel\" aria-label=\"[redacted]\">");
        foreach (var secret in new[] { "s3cr3t", "hunter2", "abc123", "sk-live", "zzz999", "in a script", "color: red", "comment", "AAAA", "onclick", "style=" })
        {
            Assert.IsFalse(box.Html.Contains(secret, StringComparison.Ordinal), $"\"{secret}\" left the page: {box.Html}");
        }

        StringAssert.Contains(box.Html, "<script></script>");
        StringAssert.Contains(box.Html, "<img src=\"data:…\" alt=\"logo\">");
        StringAssert.Contains(box.Html, "<input type=\"password\" name=\"password\">", "what names a field stays");
        Assert.AreEqual("Reset with password: [redacted]", box.Name);

        // An element larger than every bound: its HTML and its text are cut, and its hidden children are not read.
        var big = (await AnnotateAsync(session, state, "#big", "Too much text", messages, timeout.Token)).Elements!.Single();
        Assert.AreEqual("big", big.Id);
        Assert.IsTrue(big.Html.Length <= AnnotatedElement.MaximumHtml + 1, big.Html.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        StringAssert.EndsWith(big.Html, "…");
        Assert.IsTrue(big.Text.Length <= 301, big.Text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        StringAssert.StartsWith(big.Text, "word word");
        Assert.IsFalse(big.Text.Contains("hidden", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TheHostFiltersWhatTheBarReports_AsItWouldFromAnyPage()
    {
        // A report the bar would never make, as a page that reached it could: the host keeps only what a prompt may show.
        const string Report = """
            {"type":"annotation","id":"a1","note":"n","area":{"x":0,"y":0,"width":1,"height":1},"elements":[{"tag":"a","id":"go",
            "name":"Key: api_key=sk-12345678 here","text":"Open\nhttps://example.com/x?access_token=zzz#frag\n\nnow",
            "html":"<A HREF='/go?token=abc#x' data-secret=\"s3\" ONCLICK=\"steal()\" aria-label=\"x-amz-signature=1\" aria-current=\"page\" data-cy=\"go\">Go<script>steal()</script><!-- password=hunter2 --></A><svg/onload=steal()>",
            "ancestors":["main\nnav"]}]}
            """;
        var element = ToolbarMessage.TryParse(Report, 1)!.Elements!.Single();
        Assert.AreEqual("<a href=\"/go\" aria-label=\"[redacted]\" aria-current=\"page\" data-cy=\"go\">Go<script></script></a><svg>", element.Html);
        Assert.AreEqual("Key: api_key=[redacted] here", element.Name);
        Assert.AreEqual("Open https://example.com/x now", element.Text, "one line, the address without its query");
        Assert.AreEqual("main nav", element.Ancestors.Single());

        Assert.AreEqual("javascript:…", AnnotationRedaction.Url("javascript:alert(1)"));
        Assert.AreEqual("mailto:…", AnnotationRedaction.Url("MAILTO:someone@example.com"));
        Assert.AreEqual("#pricing", AnnotationRedaction.Url("#pricing"), "an anchor of the page stays");
        Assert.AreEqual("https://s3.example.com/bucket/file", AnnotationRedaction.Url("https://s3.example.com/bucket/file?X-Amz-Signature=abc"));
        Assert.AreEqual("Forgot password?", AnnotationRedaction.Text("Forgot password?"), "a word alone is no secret");
        Assert.AreEqual("password: [redacted] least 8 characters", AnnotationRedaction.Text("password: at least 8 characters"), "a value after the name of a credential goes, whatever it looks like");
        Assert.AreEqual("Password: [redacted]", AnnotationRedaction.Text("Password: hunter2"));
        Assert.AreEqual("[redacted]", AnnotationRedaction.Attribute("title", "Session_ID 42"));
        Assert.AreEqual("password", AnnotationRedaction.Attribute("name", "password"), "an attribute that names a field stays");
        Assert.IsNull(AnnotationRedaction.Attribute("data-token", "x"));
        Assert.IsNull(AnnotationRedaction.Attribute("style", "color: red"));

        Assert.AreEqual("http://localhost:5173/reset?…", AnnotationRedaction.PageAddress(new Uri("http://localhost:5173/reset?token=abc#x")));
        Assert.AreEqual("http://localhost:5173/app#…", AnnotationRedaction.PageAddress(new Uri("http://localhost:5173/app#/users/42")));
        Assert.AreEqual("http://localhost:5173/", AnnotationRedaction.PageAddress(new Uri("http://localhost:5173/")));
    }

    [TestMethod]
    public async Task TheBarDropsTheValueAfterTheNameOfACredential_AsTheHostDoes()
    {
        // The host filters again what the bar reports, which would hide a rule of the bar that lets a value pass: the rules
        // of the bar's own source run here, in a page of a real browser, on the texts of the host's test.
        var source = ToolbarScript.Source;
        string Part(string start, string end)
        {
            var index = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(index >= 0, $"The toolbar has no {start}");
            return source[index..(source.IndexOf(end, index, StringComparison.Ordinal) + end.Length)];
        }

        var texts = AnnotationRedactionTests.LabelledCredentials.Select(static vector => vector.Text).ToArray();
        var script = string.Join('\n', "() => {", Part("const REDACTED = ", "\n"), Part("const SECRET_VALUE = ", "\n"), Part("const URL_IN_TEXT = ", "\n"), Part("const USER_INFO = ", "\n"),
            Part("function safeText(", "\n  }"), $"return {System.Text.Json.JsonSerializer.Serialize(texts)}.map(safeText);", "}");
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var json = await session.Pages[0].Page.EvaluateAsync(script, isFunction: true, [], timeout.Token);
        var redacted = System.Text.Json.JsonSerializer.Deserialize<string[]>(json!)!;
        CollectionAssert.AreEqual(AnnotationRedactionTests.LabelledCredentials.Select(static vector => vector.Redacted).ToArray(), redacted, string.Join(" | ", redacted));
    }

    [TestMethod]
    public async Task TheSendPopupSwitchesTheDetailsOfThePage_AndTheHostShowsTheChoiceEverywhere()
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
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({\"details\":false})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));
        await AnnotateAsync(session, state, "#c1", "Rounder", messages, timeout.Token);

        // With the keyboard, as a person may: from a button of the page, Tab reaches Annotate, the count, then Send; Enter
        // opens the Send popup on its message, and Tab reaches the switch, which Space turns on.
        await session.OkAsync("evaluate_script", """{"function":"() => { const start = document.createElement('button'); start.textContent = 'start'; document.body.prepend(start); start.focus(); return 'focused'; }"}""");
        foreach (var key in new[] { "Tab", "Tab", "Tab", "Enter" }) await session.OkAsync("press_key", $$"""{"key":"{{key}}"}""");
        await Task.Delay(100, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting", timeout.Token), "the Send popup is open");
        await session.OkAsync("press_key", """{"key":"Tab"}""");
        await session.OkAsync("press_key", """{"key":" "}""");
        Assert.IsTrue(Next(messages, "details", timeout.Token).Details);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().details === true", timeout.Token));
        await session.OkAsync("press_key", """{"key":"Escape"}""");

        // A choice made in another tab reaches this one; a page that made up a click on the switch would change nothing.
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "details(false)", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().details === false", timeout.Token));
        Assert.IsFalse(messages.Any(static message => message.Type == "details"));
    }

    [TestMethod]
    public void TheStylesOfAnElement_AreTheKnownOnes_WithoutTheirDefaults()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""
            {"color":"rgb(1, 2, 3)","display":"block","position":"static","margin":"0px","border":"0px none rgb(0, 0, 0)","background-color":"rgba(0, 0, 0, 0)",
            "behavior":"url(x.htc)","font-family":"Inter,\n sans-serif","z-index":"auto","width":5,"padding":"4px 8px"}
            """);
        var styles = ElementStyles.Read(document.RootElement);
        CollectionAssert.AreEqual(new[] { "display: block", "padding: 4px 8px", "color: rgb(1, 2, 3)", "font-family: Inter, sans-serif" },
            styles.Select(static style => style.Key + ": " + style.Value).ToArray(), "known properties only, in their order, on one line");
        Assert.AreEqual(0, ElementStyles.Read(default).Count);
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
        Assert.IsTrue(ToolbarMessage.TryParse("""{"type":"details","on":true}""", 1)!.Details);
        Assert.IsFalse(ToolbarMessage.TryParse("""{"type":"details","on":false}""", 1)!.Details);
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"details","on":"yes"}""", 1));
        Assert.AreEqual(AnnotationIntent.Question, ToolbarMessage.TryParse("""{"type":"edit","id":"a1","note":"Why?","intent":"question"}""", 1)!.Intent);
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"edit","id":"a1","note":"Why?"}""", 1)!.Intent, "an edit that does not say keeps what the annotation was");
        Assert.AreEqual(AnnotationIntent.Question, ToolbarMessage.TryParse("""{"type":"annotation","id":"a1","note":"n","intent":"question","elements":[],"area":{"x":1,"y":2,"width":30,"height":40,"drawn":true}}""", 1)!.Intent);
        Assert.AreEqual(AnnotationIntent.Change, ToolbarMessage.TryParse("""{"type":"annotation","id":"a1","note":"n","intent":"other","elements":[],"area":{"x":1,"y":2,"width":30,"height":40,"drawn":true}}""", 1)!.Intent);
        Assert.AreEqual(AnnotationIntent.Question, ToolbarMessage.TryParse("""{"type":"drawing","id":"d1","note":"n","intent":"question","area":{"x":1,"y":2,"width":30,"height":40}}""", 1)!.Intent);

        // An annotation is about elements, an area drawn, or both; never about nothing.
        var drawn = ToolbarMessage.TryParse("""{"type":"annotation","id":"a1","note":"n","elements":[],"area":{"x":1,"y":2,"width":30,"height":40,"drawn":true}}""", 1);
        Assert.IsNotNull(drawn);
        Assert.AreEqual(new AnnotationArea(1, 2, 30, 40, true), drawn.Area);
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"annotation","id":"a1","note":"n","elements":[],"area":{"x":1,"y":2,"width":30,"height":40}}""", 1));
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"annotation","id":"a1","note":"n","elements":[{"tag":"p"}]}""", 1), "an annotation says its area");
        // An image is a PNG, a JPEG, a GIF or a WebP by its bytes, of 3 MB at most; only its message is that large.
        var png = Png(4, 4, noise: false);
        var image = ToolbarMessage.TryParse($$"""{"type":"image","id":"a1","data":"data:image/png;base64,{{Convert.ToBase64String(png)}}"}""", 1);
        Assert.AreEqual("image/png", image!.Image!.MediaType);
        CollectionAssert.AreEqual(png, image.Image.Bytes);
        Assert.AreEqual("image/png", ToolbarMessage.TryParse($$"""{"type":"image","id":"a1","data":"data:image/jpeg;base64,{{Convert.ToBase64String(png)}}"}""", 1)!.Image!.MediaType, "the bytes say the type");
        Assert.IsNull(ToolbarMessage.TryParse($$"""{"type":"image","id":"a1","data":"data:image/png;base64,{{Convert.ToBase64String("<svg/>"u8.ToArray())}}"}""", 1), "not an image of a known type");
        Assert.IsNull(ToolbarMessage.TryParse($$"""{"type":"image","id":"a1","data":"data:image/png;base64,{{Convert.ToBase64String(new byte[PastedImage.MaximumBytes + 1])}}"}""", 1), "too large");
        Assert.IsNull(ToolbarMessage.TryParse($$"""{"type":"edit","id":"a1","note":"{{new string('x', 300 * 1024)}}"}""", 1), "only an image makes a large message");
        CollectionAssert.AreEqual(new[] { "image-2" }, ToolbarMessage.TryParse("""{"type":"edit","id":"a1","note":"n","images":["image-2",3]}""", 1)!.KeptImages!.ToArray());
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

    [TestMethod]
    public async Task TheSelectorOfAnElement_SkipsTheNamesABuildOrARenderMakesUp()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/generated")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/generated", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));

        string[] generated = ["css-", "sc-", "kGtEvt", "Card_card", "Panel_panel", "_primary", "jsx-", ":r5:", "r5", "xYz12", "active"];
        var expected = new Dictionary<string, string?>
        {
            ["two"] = "li.menu__item:nth-of-type(2)",
            ["one"] = "li.menu__item:nth-of-type(1)",
            ["buy2"] = "section.panel:nth-of-type(2) > button.buy",
            ["hashy"] = null,
        };
        foreach (var (key, selector) in expected)
        {
            var annotation = await AnnotateAsync(session, state, $"[data-k={key}]", "Note on " + key, messages, timeout.Token);
            var found = annotation.Elements!.Single().Selector;
            foreach (var name in generated) Assert.IsFalse(found.Contains(name, StringComparison.Ordinal), $"{found} holds the made-up name {name}");
            if (selector is not null) Assert.AreEqual(selector, found);
            var function = $"() => {{ const nodes = document.querySelectorAll({System.Text.Json.JsonSerializer.Serialize(found)}); return 'count=' + nodes.length + ';key=' + (nodes[0] && nodes[0].dataset.k) + ';'; }}";
            var result = await session.OkAsync("evaluate_script", System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["function"] = function }));
            StringAssert.Contains(result, $"count=1;key={key};", found);
        }

        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));
    }

    [TestMethod]
    public async Task ANoteIsAChangeOrAQuestion_ChosenWithTheKeyboard()
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

        // A note is a change unless the user chooses Question, the second choice after the field.
        var change = await AnnotateAsync(session, state, "#c2", "More space", messages, timeout.Token);
        Assert.AreEqual(AnnotationIntent.Change, change.Intent);

        var center = await CenterAsync(session, "#c1");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, center[0], center[1]), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Which font is this?", timeout.Token);
        foreach (var key in new[] { "Tab", "Tab", "Enter", "Control+Enter" }) await session.OkAsync("press_key", $$"""{"key":"{{key}}"}""");
        var question = Next(messages, "annotation", timeout.Token, message => message.Note == "Which font is this?");
        Assert.AreEqual(AnnotationIntent.Question, question.Intent);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"marked({BrowserSession.JsonText(question.Id!)}, 1)", timeout.Token));

        // Its mark opens it again as a question; the arrows switch it back to a change.
        var corner = await session.OkAsync("evaluate_script", """{"function":"() => { const r = document.getElementById('c1').getBoundingClientRect(); return [r.left + 4, r.top + 4]; }"}""");
        var at = corner[(corner.IndexOf('[') + 1)..corner.IndexOf(']')].Split(',').Select(static text => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, at[0], at[1]), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting", timeout.Token), "the mark opened the note");
        foreach (var key in new[] { "Tab", "ArrowRight", "Control+Enter" }) await session.OkAsync("press_key", $$"""{"key":"{{key}}"}""");
        var edit = Next(messages, "edit", timeout.Token);
        Assert.AreEqual(question.Id, edit.Id);
        Assert.AreEqual("Which font is this?", edit.Note);
        Assert.AreEqual(AnnotationIntent.Change, edit.Intent);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));
    }

    [TestMethod]
    public async Task ImagesDroppedOnTheNote_GoWithTheAnnotation_TheLargeOnesMadeSmaller()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var folder = new BrowserPluginTests.TempFolder();
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/cards")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/cards", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));
        var small = Path.Combine(folder.Path, "mockup.png");
        await File.WriteAllBytesAsync(small, Png(8, 8, noise: false), timeout.Token);
        var large = Path.Combine(folder.Path, "photo.png");
        await File.WriteAllBytesAsync(large, Png(1100, 1100, noise: true), timeout.Token);
        Assert.IsTrue(new FileInfo(large).Length > PastedImage.MaximumBytes);

        // The popup of the note opens under the card; the images are dropped on it, and the note is saved at once: it
        // waits for the images being read.
        var center = await CenterAsync(session, "#c1");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, center[0], center[1]), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Like this mockup", timeout.Token);
        var corner = await session.OkAsync("evaluate_script", """{"function":"() => { const r = document.getElementById('c1').getBoundingClientRect(); return [r.left + 60, r.bottom + 60]; }"}""");
        var at = corner[(corner.IndexOf('[') + 1)..corner.IndexOf(']')].Split(',').Select(static text => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await DropAsync(state.Page, at[0], at[1], [small], timeout.Token);
        await DropAsync(state.Page, at[0], at[1], [large], timeout.Token);
        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");

        var annotation = Next(messages, "annotation", timeout.Token, message => message.Note == "Like this mockup");
        var first = Next(messages, "image", timeout.Token);
        var second = Next(messages, "image", timeout.Token);
        Assert.AreEqual(annotation.Id, first.Id);
        Assert.AreEqual(annotation.Id, second.Id);
        Assert.AreEqual("image/png", first.Image!.MediaType);
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(small, timeout.Token), first.Image.Bytes, "a small image goes as it is");
        Assert.AreEqual("image/jpeg", second.Image!.MediaType, "a large image is made smaller");
        Assert.IsTrue(second.Image.Bytes.Length <= PastedImage.MaximumBytes);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting === false", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));
    }

    [TestMethod]
    public async Task ANoteCancelledWhileItsImageIsRead_IsNotSaved_AndTheNextNoteStaysOpen()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var folder = new BrowserPluginTests.TempFolder();
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/cards")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/cards", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));
        var large = Path.Combine(folder.Path, "photo.png");
        await File.WriteAllBytesAsync(large, Png(1100, 1100, noise: true), timeout.Token);

        // A large image takes a while to be made smaller: the note is saved, and cancelled, while it is read.
        var center = await CenterAsync(session, "#c1");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, center[0], center[1]), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Cancelled note", timeout.Token);
        var corner = await session.OkAsync("evaluate_script", """{"function":"() => { const r = document.getElementById('c1').getBoundingClientRect(); return [r.left + 60, r.bottom + 60]; }"}""");
        var at = corner[(corner.IndexOf('[') + 1)..corner.IndexOf(']')].Split(',').Select(static text => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await DropAsync(state.Page, at[0], at[1], [large], timeout.Token);
        await SaveThenCancelAsync(state.Page, timeout.Token);

        // A newer note is opened before the image is read; the save of the cancelled one never comes, and closes nothing.
        center = await CenterAsync(session, "#c3");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, center[0], center[1]), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Newer note", timeout.Token);
        await Task.Delay(2000, timeout.Token);
        Assert.IsFalse(messages.Any(static message => message.Type is "annotation" or "image"), "the cancelled note was saved");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting", timeout.Token), "the newer note stays open");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().marks === 0", timeout.Token));

        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");
        var newer = Next(messages, "annotation", timeout.Token);
        Assert.AreEqual("Newer note", newer.Note);
        Assert.AreEqual("c3", newer.Elements!.Single().Id);
        await Task.Delay(300, timeout.Token);
        Assert.IsFalse(messages.Any(static message => message.Type == "image"), "the image went with the cancelled note");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));
    }

    [TestMethod]
    public async Task WhatIsTypedPastedOrDroppedInTheBar_NeverReachesThePage()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var folder = new BrowserPluginTests.TempFolder();
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/snoop")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/snoop", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));
        await AllowClipboardAsync(state.Page, Host.Site.Origin, timeout.Token);
        var dropped = Path.Combine(folder.Path, "mockup.png");
        await File.WriteAllBytesAsync(dropped, Png(8, 8, noise: false), timeout.Token);

        // The note of the first card: words typed and pasted, a picture pasted from the clipboard, an image dropped from the
        // desktop, all with the browser's own input.
        var center = await CenterAsync(session, "#c1");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, center[0], center[1]), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Like this ", timeout.Token);
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"async () => { await navigator.clipboard.writeText('and these words'); return 'copied'; }"}"""), "copied");
        await EditAsync(state.Page, "v", "paste", timeout.Token);
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"async () => { const canvas = document.createElement('canvas'); canvas.width = canvas.height = 8; const context = canvas.getContext('2d'); context.fillStyle = '#0a0'; context.fillRect(0, 0, 8, 8); const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png')); await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })]); return 'copied'; }"}"""), "copied");
        await EditAsync(state.Page, "v", "paste", timeout.Token);
        var corner = await session.OkAsync("evaluate_script", """{"function":"() => { const r = document.getElementById('c1').getBoundingClientRect(); return [r.left + 60, r.bottom + 60]; }"}""");
        var at = corner[(corner.IndexOf('[') + 1)..corner.IndexOf(']')].Split(',').Select(static text => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        await DropAsync(state.Page, at[0], at[1], [dropped], timeout.Token);
        await Task.Delay(300, timeout.Token);
        // The note copied, as the user may before saving it.
        await EditAsync(state.Page, "a", "selectAll", timeout.Token);
        await EditAsync(state.Page, "c", "copy", timeout.Token);
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"async () => 'copied=' + await navigator.clipboard.readText() + ';'"}"""), "copied=Like this and these words;");
        // The page heard of none of it, though it listened first, on the window, as the events started.
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'snooped=' + JSON.stringify(window.__snooped) + ';'"}"""), "snooped=[];");
        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");

        var annotation = Next(messages, "annotation", timeout.Token);
        Assert.AreEqual("Like this and these words", annotation.Note, "the words pasted went into the note");
        var pasted = Next(messages, "image", timeout.Token);
        var second = Next(messages, "image", timeout.Token);
        Assert.AreEqual(annotation.Id, pasted.Id);
        Assert.AreEqual(annotation.Id, second.Id);
        Assert.IsTrue(new[] { pasted, second }.Any(image => image.Image!.Bytes.SequenceEqual(File.ReadAllBytes(dropped))), "the image dropped went with the note");

        // Nor of the keys that saved it, but their release, once the note had closed and the focus was back on the page.
        var heard = await session.OkAsync("evaluate_script", """{"function":"() => 'snooped=' + JSON.stringify(window.__snooped.filter(entry => !/^(window|document):keyup:+0:(Control|Enter)$/.test(entry))) + ';'"}""");
        StringAssert.Contains(heard, "snooped=[];");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));
    }

    // Lets the page of an origin write to the clipboard of the browser, as a page the user copies from, and keeps the
    // page focused as a headless browser does not.
    private static async Task AllowClipboardAsync(IBrowserPage page, string origin, CancellationToken cancellationToken)
    {
        var session = CdpSessionOf(page);
        var info = await session.Target.GetTargetInfoAsync(cancellationToken: cancellationToken);
        using var parameters = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["permissions"] = new[] { "clipboardReadWrite", "clipboardSanitizedWrite" },
            ["origin"] = origin,
            ["browserContextId"] = info.TargetInfo.BrowserContextId,
        }));
        await session.Connection.Browser.SendAsync("Browser.grantPermissions", parameters.RootElement, CodeAlta.Browser.Cdp.CdpJsonContext.Default.JsonElement, cancellationToken);
        await session.Emulation.SetFocusEmulationEnabledAsync(new CodeAlta.Browser.Cdp.EmulationSetFocusEmulationEnabledParams { Enabled = true }, cancellationToken);
    }

    // A command of editing where the focus is, as the user's Ctrl and a letter run it: the browser's own paste, copy or
    // selection, trusted. A headless browser has a clipboard of its own, not the one of the computer.
    private static async Task EditAsync(IBrowserPage page, string letter, string command, CancellationToken cancellationToken)
    {
        var session = CdpSessionOf(page);
        var code = "Key" + letter.ToUpperInvariant();
        var keyCode = (int)char.ToUpperInvariant(letter[0]);
        await session.Input.DispatchKeyEventAsync(new CodeAlta.Browser.Cdp.InputDispatchKeyEventParams { Type = "rawKeyDown", Key = letter, Code = code, WindowsVirtualKeyCode = keyCode, Modifiers = 2, Commands = [command] }, cancellationToken);
        await session.Input.DispatchKeyEventAsync(new CodeAlta.Browser.Cdp.InputDispatchKeyEventParams { Type = "keyUp", Key = letter, Code = code, WindowsVirtualKeyCode = keyCode, Modifiers = 2 }, cancellationToken);
        await Task.Delay(200, cancellationToken);
    }

    // Saves a note with Ctrl+Enter and cancels it with Esc at once, as the user's keys, without waiting between them.
    internal static async Task SaveThenCancelAsync(IBrowserPage page, CancellationToken cancellationToken)
    {
        var session = CdpSessionOf(page);
        const int Control = 2;
        foreach (var (type, key, code, modifiers) in new[]
        {
            ("rawKeyDown", "Control", 17, Control), ("rawKeyDown", "Enter", 13, Control), ("keyUp", "Enter", 13, Control), ("keyUp", "Control", 17, 0),
            ("rawKeyDown", "Escape", 27, 0), ("keyUp", "Escape", 27, 0),
        })
        {
            await session.Input.DispatchKeyEventAsync(new CodeAlta.Browser.Cdp.InputDispatchKeyEventParams
            {
                Type = type, Key = key, Code = key == "Control" ? "ControlLeft" : key, WindowsVirtualKeyCode = code, Modifiers = modifiers,
            }, cancellationToken);
        }
    }

    private static CodeAlta.Browser.Cdp.CdpSession CdpSessionOf(IBrowserPage page)
    {
        var field = typeof(CodeAlta.Browser.Drivers.Cdp.CdpPage).GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (CodeAlta.Browser.Cdp.CdpSession)field.GetValue(page)!;
    }

    // Drops files on a point of the page, as the user drags them from the desktop: the browser's input, trusted as a person's.
    internal static async Task DropAsync(IBrowserPage page, double x, double y, string[] files, CancellationToken cancellationToken)
    {
        var field = typeof(CodeAlta.Browser.Drivers.Cdp.CdpPage).GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var session = (CodeAlta.Browser.Cdp.CdpSession)field.GetValue(page)!;
        var data = new CodeAlta.Browser.Cdp.InputDragData { Files = files, DragOperationsMask = 1 };
        foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
        {
            await session.Input.DispatchDragEventAsync(new CodeAlta.Browser.Cdp.InputDispatchDragEventParams { Type = type, X = x, Y = y, Data = data }, cancellationToken);
        }
    }

    // A PNG of a size, of one color or of noise that does not compress.
    internal static byte[] Png(int width, int height, bool noise)
    {
        var random = new Random(7);
        var raw = new byte[(width * 3 + 1) * height];
        for (var row = 0; row < height; row++)
        {
            var start = row * (width * 3 + 1) + 1;
            if (noise) random.NextBytes(raw.AsSpan(start, width * 3));
            else raw.AsSpan(start, width * 3).Fill(0x7c);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream stream, string type, byte[] data)
        {
            var bytes = new byte[12 + data.Length];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes, data.Length);
            System.Text.Encoding.ASCII.GetBytes(type, bytes.AsSpan(4));
            data.CopyTo(bytes, 8);
            var crc = 0xFFFFFFFFu;
            foreach (var value in bytes.AsSpan(4, 4 + data.Length))
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8 + data.Length), crc ^ 0xFFFFFFFFu);
            stream.Write(bytes);
        }
    }

    [TestMethod]
    public async Task TheOutlineSaysWhatIsUnderThePointer_ItsTagRoleTextAndSize()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/generated")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/generated", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));

        var size = await session.OkAsync("evaluate_script", """{"function":"() => { const r = document.querySelector('[data-k=two]').getBoundingClientRect(); return 'size=' + Math.round(r.width) + '×' + Math.round(r.height) + ';'; }"}""");
        var start = size.IndexOf("size=", StringComparison.Ordinal) + 5;
        var center = await CenterAsync(session, "[data-k=two]");
        await state.Page.MouseMoveAsync(BrowserPoint.OfPage(state.Page.MainFrame, center[0], center[1]), timeout.Token);
        await Task.Delay(100, timeout.Token);
        var expected = "li.menu__item · listitem · \"Two\" · " + size[start..size.IndexOf(';', start)];
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"state().hover === {System.Text.Json.JsonSerializer.Serialize(expected)}", timeout.Token), expected);

        // A button says no role beside its tag, which says it already.
        center = await CenterAsync(session, "[data-k=buy2]");
        await state.Page.MouseMoveAsync(BrowserPoint.OfPage(state.Page.MainFrame, center[0], center[1]), timeout.Token);
        await Task.Delay(100, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().hover.startsWith('button.buy · \"Buy two\" · ')", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(false)", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().hover === ''", timeout.Token), "the label goes with the Annotate mode");
    }

    [TestMethod]
    public async Task TheBarStaysAboveTheTopLayerOfThePage_ItsPopoversAndItsModalDialogs()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/modal")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/modal", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        var (left, y) = await BarAsync(session);
        Assert.IsTrue(left > 0, "the bar is at the bottom right");

        // A popover of the page over the whole window, shown after the bar: the bar is shown again above it, and its handle still moves it.
        await session.OkAsync("evaluate_script", """{"function":"() => { document.getElementById('pop').showPopover(); return 'shown'; }"}""");
        await Task.Delay(100, timeout.Token);
        Assert.IsTrue(await OnBarAsync(session, left + 20, y), "the bar is above the popover of the page");
        await state.Page.DragAsync(BrowserPoint.OfPage(state.Page.MainFrame, left + 20, y), BrowserPoint.OfPage(state.Page.MainFrame, left - 80, y - 60), timeout.Token);
        var moved = Next(messages, "position", timeout.Token);
        Assert.AreEqual(16 + 100, moved.Position!.Value.Right, 2);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "place(null)", timeout.Token));
        await session.OkAsync("evaluate_script", """{"function":"() => { document.getElementById('pop').hidePopover(); return 'hidden'; }"}""");

        // A modal dialog of the page: the user annotates what is in it, and writes the note in the popup of the bar above it.
        await session.OkAsync("evaluate_script", """{"function":"() => { document.getElementById('dialog').showModal(); return 'open'; }"}""");
        await Task.Delay(100, timeout.Token);
        Assert.IsTrue(await OnBarAsync(session, left + 20, y), "the bar is above the modal dialog of the page");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));
        var inside = await AnnotateAsync(session, state, "#inside", "Inside the dialog, larger", messages, timeout.Token);
        Assert.AreEqual("inside", inside.Elements!.Single().Id);
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'open=' + document.getElementById('dialog').open"}"""), "open=true", "the dialog stays open");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));

        // Once the dialog closes, or the page takes it out of the document while it is open, the bar is back at the root of the document.
        const string Home = """{"function":"() => 'home=' + (document.querySelector('[data-codealta-toolbar]')?.parentElement === document.documentElement)"}""";
        await session.OkAsync("evaluate_script", """{"function":"() => { document.getElementById('dialog').close(); return 'closed'; }"}""");
        await Task.Delay(100, timeout.Token);
        StringAssert.Contains(await session.OkAsync("evaluate_script", Home), "home=true");
        Assert.IsTrue(await OnBarAsync(session, left + 20, y));
        await session.OkAsync("evaluate_script", """{"function":"() => { const dialog = document.getElementById('dialog'); dialog.showModal(); setTimeout(() => dialog.remove(), 50); return 'removed'; }"}""");
        await Task.Delay(300, timeout.Token);
        StringAssert.Contains(await session.OkAsync("evaluate_script", Home), "home=true");
        Assert.IsTrue(await OnBarAsync(session, left + 20, y), "the bar is still shown");
    }

    [TestMethod]
    public async Task AModalDialogOpenBeforeTheToolbar_HoldsTheBar()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;

        // The page opened its dialog before the toolbar was added to the tab, so the toolbar never saw it open.
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/modal-open")}}"}""");
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'modal=' + document.getElementById('dialog').matches(':modal')"}"""), "modal=true");
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/modal-open", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "mode(true)", timeout.Token));
        await Task.Delay(100, timeout.Token);

        // The bar is in the dialog, where the rest of the document is inert: the note can be written.
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'in=' + (document.querySelector('[data-codealta-toolbar]')?.parentElement?.id)"}"""), "in=dialog");
        var inside = await AnnotateAsync(session, state, "#inside", "Inside the dialog open at load", messages, timeout.Token);
        Assert.AreEqual("inside", inside.Elements!.Single().Id);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "clear()", timeout.Token));
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
