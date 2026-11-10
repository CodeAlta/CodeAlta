using System.Collections.Concurrent;
using CodeAlta.Browser.Annotations;
using CodeAlta.Browser.Discovery;
using CodeAlta.Browser.Drivers;
using CodeAlta.Browser.Toolbar;
using CodeAlta.Browser.Tools;
using CodeAlta.Plugin.Browser;

namespace CodeAlta.Browser.Tests;

/// <summary>The Draw tool of the CodeAlta toolbar in a real Chromium, headless: the user draws over the page, and the drawing becomes an annotation with its picture.</summary>
[TestClass]
public sealed class BrowserDrawingTests
{
    // The color the Draw tool starts with, and the margin of the picture around the drawing.
    private static readonly (byte R, byte G, byte B) Red = (0xef, 0x44, 0x44);
    // The color of the marks of the annotations.
    private static readonly (byte R, byte G, byte B) Violet = (0x7c, 0x3a, 0xed);
    private const double Margin = 24;

    // Counts, in the page, the pointer and mouse events it gets: none of those that draw.
    private const string CountEvents = """{"function":"() => { window.__seen = 0; for (const type of ['pointerdown', 'pointermove', 'pointerup', 'mousedown', 'mouseup', 'click']) addEventListener(type, () => window.__seen++, true); return 'counting'; }"}""";

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
    public async Task TheUserDrawsWithRealInput_UndoesAStroke_AndSavesTheDrawingWithANote()
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
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "draw(true)", timeout.Token));
        // An expression for the toolbar calls it once: one question at a time.
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().drawing", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().picking === false", timeout.Token), "Draw and Annotate take the pointer one at a time");
        await session.OkAsync("evaluate_script", CountEvents);

        // Two strokes with the browser's own input, as a person draws; the second one is undone, redone, and undone again.
        await DrawAsync(state, (100, 150), (300, 250), timeout.Token);
        await DrawAsync(state, (120, 330), (320, 350), timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().shapes === 2", timeout.Token));
        await session.OkAsync("press_key", """{"key":"Control+z"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().shapes === 1", timeout.Token), "Ctrl+Z undoes the last stroke");
        await session.OkAsync("press_key", """{"key":"Control+Shift+z"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().shapes === 2", timeout.Token), "Ctrl+Shift+Z redoes it");
        await session.OkAsync("press_key", """{"key":"Control+z"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().shapes === 1", timeout.Token));

        // Enter opens the note of the drawing, Ctrl+Enter saves it.
        await session.OkAsync("press_key", """{"key":"Enter"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting", timeout.Token));
        await state.Page.InsertTextAsync("Bigger, like this", timeout.Token);
        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");
        var drawing = Next(messages, "drawing", timeout.Token);
        Assert.AreEqual("Bigger, like this", drawing.Note);
        Assert.AreEqual(0, drawing.Elements!.Count, "the elements under a drawing are not looked for");
        Assert.IsNotNull(drawing.Area);
        Assert.IsTrue(drawing.Area.Drawn);
        // The first stroke with the room around it; the second one was undone.
        Assert.AreEqual(100 - Margin, drawing.Area.X, 4);
        Assert.AreEqual(150 - Margin, drawing.Area.Y, 4);
        Assert.AreEqual(200 + 2 * Margin, drawing.Area.Width, 6);
        Assert.AreEqual(100 + 2 * Margin, drawing.Area.Height, 6);
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'seen=' + window.__seen + ';'"}"""), "seen=0;", "the page got none of what drew");

        // The drawing shows until the host took its picture, then leaves the page; its mark stays.
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().saving", timeout.Token));
        var picture = await page.ViewPictureAsync(drawing.Area, timeout.Token);
        Assert.IsNotNull(picture);
        Assert.IsTrue(PngTesting.Count(picture, Red, 40) > 100, "the picture shows the drawing over the page");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"drawn({BrowserSession.JsonText(drawing.Id!)})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().shapes === 0", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().saving === false", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().marks === 1", timeout.Token));
        var after = await page.ViewPictureAsync(drawing.Area, timeout.Token);
        Assert.AreEqual(0, PngTesting.Count(after!, Red, 40), "the drawing left the page");

        // Esc leaves Draw.
        await session.OkAsync("press_key", """{"key":"Escape"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().drawing === false", timeout.Token));
    }

    [TestMethod]
    public async Task TheKeysOfDraw_PickATool_AlsoWhileAButtonOfTheBarHasTheFocus()
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
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "draw(true)", timeout.Token));

        // The focus in the bar, as after a click on Draw: the page has nothing else to focus.
        await session.OkAsync("press_key", """{"key":"Tab"}""");
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'active=' + (document.activeElement && document.activeElement.hasAttribute('data-codealta-toolbar')) + ';'"}"""), "active=true;");
        await session.OkAsync("press_key", """{"key":"r"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().tool === 'rect'", timeout.Token), "R picks the rectangle");
        await session.OkAsync("press_key", """{"key":"3"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().color === '#22c55e'", timeout.Token), "a digit picks a color");
        await session.OkAsync("press_key", """{"key":"Escape"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().drawing === false", timeout.Token), "Esc leaves Draw");

        // A click on a button of the bar leaves the focus to the page, as the keys then belong to the mode it turned on:
        // Enter after a drawing opens its note, it does not press the button again. The last button is the theme's.
        var end = await session.OkAsync("evaluate_script", """{"function":"() => { document.activeElement.blur(); const y = innerHeight - 31; for (let x = innerWidth - 1; x > 0; x--) { const e = document.elementFromPoint(x, y); if (e && e.hasAttribute('data-codealta-toolbar')) return [x, y]; } return [-1, y]; }"}""");
        var at = end[(end.IndexOf('[') + 1)..end.IndexOf(']')].Split(',').Select(static value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.IsTrue(at[0] > 0, "the bar is at the bottom right");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, at[0] - 20, at[1]), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'active=' + (document.activeElement && document.activeElement.hasAttribute('data-codealta-toolbar')) + ';'"}"""), "active=false;", "the button clicked keeps no focus");
    }

    [TestMethod]
    public async Task EveryTool_DrawsWithRealInput_AndTheEraserRemovesAShape()
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
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "draw(true)", timeout.Token));

        // A letter picks a tool, as the title of its button says.
        var shapes = 0;
        foreach (var (key, tool, from, to) in new[]
        {
            ("h", "highlighter", (100d, 100d), (250d, 100d)), ("a", "arrow", (100d, 150d), (250d, 200d)),
            ("r", "rect", (300d, 100d), (450d, 200d)), ("o", "ellipse", (100d, 250d), (250d, 330d)), ("p", "pen", (300d, 250d), (420d, 330d)),
        })
        {
            await session.OkAsync("press_key", $$"""{"key":"{{key}}"}""");
            Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"drawState().tool === '{tool}'", timeout.Token), tool);
            await DrawAsync(state, from, to, timeout.Token);
            Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"drawState().shapes === {++shapes}", timeout.Token), tool + " draws");
        }

        // A click with a shape tool draws nothing: too small to mean anything.
        await session.OkAsync("press_key", """{"key":"r"}""");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, 500, 300), 1, timeout.Token);
        await Task.Delay(100, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"drawState().shapes === {shapes}", timeout.Token));

        // Text goes where the user clicks, in the color picked by its digit, once Enter ends it.
        await session.OkAsync("press_key", """{"key":"2"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().color === '#f59e0b'", timeout.Token));
        await session.OkAsync("press_key", """{"key":"t"}""");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, 480, 120), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().typing", timeout.Token));
        await state.Page.InsertTextAsync("Here", timeout.Token);
        await session.OkAsync("press_key", """{"key":"Enter"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().typing === false", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"drawState().shapes === {++shapes}", timeout.Token), "the text is a shape");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting === false", timeout.Token), "the Enter that ends the text opens no note");

        // The eraser removes the rectangle it touches, its outline; Ctrl+Z brings it back.
        await session.OkAsync("press_key", """{"key":"e"}""");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, 375, 150), 1, timeout.Token);
        await Task.Delay(100, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"drawState().shapes === {shapes}", timeout.Token), "inside a rectangle is the page, not the rectangle");
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, 300, 150), 1, timeout.Token);
        await Task.Delay(100, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"drawState().shapes === {shapes - 1}", timeout.Token), "the eraser removes the rectangle");
        await session.OkAsync("press_key", """{"key":"Control+z"}""");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, $"drawState().shapes === {shapes}", timeout.Token));
    }

    [TestMethod]
    public async Task ThePageCannotDraw_WithEventsItMakesUp()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        var page = (IAnnotatablePage)state.Page;
        var messages = new BlockingCollection<ToolbarMessage>();
        await using var registration = await page.StartToolbarAsync(messages.Add, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/hostile")}}"}""");
        var ready = Next(messages, "ready", timeout.Token, message => message.Url?.EndsWith("/hostile", StringComparison.Ordinal) == true);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "enable({})", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "draw(true)", timeout.Token));
        await Task.Delay(200, timeout.Token);

        // Strokes, keys and clicks the page makes up, on the element it sees at the point (the host of the bar) and on
        // the document, draw nothing, undo nothing and open no note.
        await session.OkAsync("evaluate_script", """{"function":"() => { const at = (x, y) => ({ bubbles: true, cancelable: true, composed: true, clientX: x, clientY: y, button: 0, buttons: 1, pointerId: 1, isPrimary: true }); for (const target of [document.elementFromPoint(200, 200), document.body, document]) { target.dispatchEvent(new PointerEvent('pointerdown', at(100, 100))); for (let x = 100; x < 300; x += 20) target.dispatchEvent(new PointerEvent('pointermove', at(x, x))); target.dispatchEvent(new PointerEvent('pointerup', at(300, 300))); target.dispatchEvent(new MouseEvent('click', at(300, 300))); } for (const key of ['Enter', 'z', 'Escape']) document.dispatchEvent(new KeyboardEvent('keydown', { key, ctrlKey: key === 'z', bubbles: true, composed: true })); const host = document.querySelector('[data-codealta-toolbar]'); return 'open=' + !!(host && host.shadowRoot) + ';'; }"}""");
        await Task.Delay(300, timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().shapes === 0", timeout.Token), "nothing was drawn");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().drawing", timeout.Token), "Draw is still on: Esc made up by the page does nothing");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting === false", timeout.Token), "no note was opened");

        // The same stroke with the browser's input draws.
        await DrawAsync(state, (100, 120), (300, 200), timeout.Token);
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().shapes === 1", timeout.Token));
        Assert.IsFalse(messages.Any(static message => message.Type == "drawing"));
    }

    [TestMethod]
    public async Task ADrawing_BecomesAnAnnotationOfItsOwnKind_WithAPictureOfThePageAndTheDrawing()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        await using var files = new ProjectFileServer();
        var pictures = Path.Combine(Host.Folder, "pictures");
        var browserFiles = new BrowserFiles(Host.Folder, [Host.Folder], [], Host.Folder);
        // A browser the user opened to browse in: every page has the toolbar, and none is a page of a project.
        await using var annotations = new BrowserAnnotations(new BrowserAnnotationsHost(new ProjectPages(files, new NoPortOwners()),
            new SourceLocator((_, _) => Task.FromResult<string?>(null), _ => ValueTask.FromResult(browserFiles)), _ => ValueTask.FromResult<string?>(null), pictures,
            () => { }, () => { }, () => "dark", _ => Task.CompletedTask, () => null, _ => Task.CompletedTask, _ => Task.FromResult(false), () => true, _ => false));
        annotations.Attach(state.Page);
        await Task.Delay(500, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/cards")}}"}""");
        Assert.IsTrue(await annotations.DrawAsync(state.Page, TimeSpan.FromSeconds(10), timeout.Token), "the toolbar of the page takes the Draw tool");
        await session.OkAsync("evaluate_script", CountEvents);
        // A page scrolled down: the drawing stays on what it was drawn over, and its picture shows it there.
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => { document.body.style.height = '3000px'; scrollTo(0, 200); return 'y=' + scrollY + ';'; }"}"""), "y=200;");

        await DrawAsync(state, (150, 120), (350, 220), timeout.Token);
        await session.OkAsync("press_key", """{"key":"Enter"}""");
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Move this up", timeout.Token);
        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");
        for (var wait = DateTime.UtcNow.AddSeconds(20); (annotations.Pending.Count == 0 || annotations.Pending[0].Picture is null) && DateTime.UtcNow < wait;) await Task.Delay(100, timeout.Token);

        var drawing = annotations.Pending.Single();
        Assert.IsTrue(drawing.IsDrawing);
        Assert.AreEqual(1, drawing.Number);
        Assert.AreEqual("Move this up", drawing.Note);
        Assert.AreEqual(0, drawing.Elements.Count);
        StringAssert.StartsWith(drawing.Label, "Drawing ");
        Assert.AreEqual(200 + 120 - Margin, drawing.Area.Y, 4, "the area is in pixels of the document");
        Assert.IsNotNull(drawing.Picture);
        Assert.IsTrue(PngTesting.Count(await File.ReadAllBytesAsync(drawing.Picture, timeout.Token), Red, 40) > 100, "the picture shows the drawing over the page");
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => 'seen=' + window.__seen + ';'"}"""), "seen=0;", "the page got none of what drew");

        // Once its picture is taken, the drawing leaves the page.
        var gone = false;
        for (var wait = DateTime.UtcNow.AddSeconds(10); !gone && DateTime.UtcNow < wait; await Task.Delay(100, timeout.Token))
        {
            gone = PngTesting.Count((await ((IAnnotatablePage)state.Page).ViewPictureAsync(drawing.Area, timeout.Token))!, Red, 40) == 0;
        }

        Assert.IsTrue(gone, "the drawing left the page");

        var text = AnnotationPrompt.Format(annotations.Pending);
        StringAssert.Contains(text, "**[1]** A drawing on the page\n");
        StringAssert.Contains(text, "- Picture: call `view_annotation` with number 1 to see the drawing over the page");
        StringAssert.Contains(text, "- Note of the user: Move this up");
    }

    [TestMethod]
    public async Task ADrawing_NamesTheElementsItsShapesSurroundOrPointAt_AndItsPictureKeepsTheMarksOfTheOtherAnnotations()
    {
        await using var session = Host.NewSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await session.ContextAsync(timeout.Token);
        var state = session.Pages[0];
        await using var files = new ProjectFileServer();
        var pictures = Path.Combine(Host.Folder, "pictures");
        var browserFiles = new BrowserFiles(Host.Folder, [Host.Folder], [], Host.Folder);
        await using var annotations = new BrowserAnnotations(new BrowserAnnotationsHost(new ProjectPages(files, new NoPortOwners()),
            new SourceLocator((_, _) => Task.FromResult<string?>(null), _ => ValueTask.FromResult(browserFiles)), _ => ValueTask.FromResult<string?>(null), pictures,
            () => { }, () => { }, () => "dark", _ => Task.CompletedTask, () => null, _ => Task.CompletedTask, _ => Task.FromResult(false), () => true, _ => false));
        annotations.Attach(state.Page);
        await Task.Delay(500, timeout.Token);
        await session.OkAsync("navigate_page", $$"""{"url":"{{Host.Site.Url("/cards")}}"}""");
        // Cards of a known place, in a list no wider than them: One at 40-240 by 50-90, Two at 100-140, Three at 150-190.
        StringAssert.Contains(await session.OkAsync("evaluate_script", """{"function":"() => { for (const card of document.querySelectorAll('.card')) card.style.width = '200px'; const wrap = document.getElementById('wrap'); wrap.style.width = '200px'; wrap.style.margin = '20px'; return 'bottom=' + Math.ceil(wrap.getBoundingClientRect().bottom) + ';'; }"}"""), "bottom=");

        // The second card is annotated first: its mark stays on the page.
        Assert.IsTrue(await annotations.AnnotateAsync(state.Page, TimeSpan.FromSeconds(10), timeout.Token));
        await state.Page.ClickAsync(BrowserPoint.OfPage(state.Page.MainFrame, 140, 120), 1, timeout.Token);
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Bolder", timeout.Token);
        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");
        for (var wait = DateTime.UtcNow.AddSeconds(20); annotations.Pending.Count == 0 && DateTime.UtcNow < wait;) await Task.Delay(100, timeout.Token);
        await session.OkAsync("press_key", """{"key":"Escape"}""");

        // A rectangle around the list and an arrow that points at the third card in it.
        Assert.IsTrue(await annotations.DrawAsync(state.Page, TimeSpan.FromSeconds(10), timeout.Token));
        await session.OkAsync("press_key", """{"key":"r"}""");
        await DrawAsync(state, (6, 6), (320, 330), timeout.Token);
        await session.OkAsync("press_key", """{"key":"a"}""");
        await DrawAsync(state, (440, 230), (140, 170), timeout.Token);
        await session.OkAsync("press_key", """{"key":"Enter"}""");
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Swap these", timeout.Token);
        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");
        for (var wait = DateTime.UtcNow.AddSeconds(20); (annotations.Pending.Count < 2 || annotations.Pending[1].Picture is null) && DateTime.UtcNow < wait;) await Task.Delay(100, timeout.Token);

        var drawing = annotations.Pending[1];
        Assert.IsTrue(drawing.IsDrawing);
        CollectionAssert.AreEqual(new[] { "#wrap", "#c3" }, drawing.Elements.Select(static element => element.Selector).ToArray(), "what the rectangle surrounds, then what the arrow points at, though it is inside: " + string.Join(", ", drawing.Elements.Select(static element => element.Selector)));
        Assert.IsNotNull(drawing.Picture);
        Assert.IsTrue(PngTesting.Count(await File.ReadAllBytesAsync(drawing.Picture, timeout.Token), Violet, 30) > 150, "the mark of the annotation of the second card shows in the picture");

        var text = AnnotationPrompt.Format(annotations.Pending);
        var part = text[text.IndexOf("**[2]**", StringComparison.Ordinal)..];
        StringAssert.Contains(part, "**[2]** A drawing on the page, over 2 elements\n");
        StringAssert.Contains(part, "- Element 1 of 2: ");
        StringAssert.Contains(part, "- Selector: `#wrap`");
        StringAssert.Contains(part, "- Selector: `#c3`");
        StringAssert.Contains(part, "- Over the mark of annotation [1], which the picture shows\n");
    }

    [TestMethod]
    public async Task ANoteOfADrawingCancelledWhileItsImageIsRead_IsNotSaved_AndTheNextNoteStaysOpen()
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
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "draw(true)", timeout.Token));
        var large = Path.Combine(folder.Path, "photo.png");
        // The largest image kept at its size: its read outlasts the keys that save and cancel the note, on a fast machine too.
        await File.WriteAllBytesAsync(large, BrowserAnnotationsTests.Png(2400, 2400, noise: true), timeout.Token);

        // The note of the drawing opens under it; a large image is dropped on it, and the note is saved and cancelled
        // while the image is read.
        await DrawAsync(state, (100, 150), (300, 250), timeout.Token);
        await session.OkAsync("press_key", """{"key":"Enter"}""");
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Cancelled drawing", timeout.Token);
        await BrowserAnnotationsTests.DropAsync(state.Page, 160, 250 + Margin + 60, [large], timeout.Token);
        await BrowserAnnotationsTests.SaveThenCancelAsync(state.Page, timeout.Token);

        // A newer note of the same drawing is opened before the image is read; the save of the cancelled one never comes.
        await session.OkAsync("press_key", """{"key":"Enter"}""");
        await Task.Delay(200, timeout.Token);
        await state.Page.InsertTextAsync("Newer drawing", timeout.Token);
        await Task.Delay(2000, timeout.Token);
        Assert.IsFalse(messages.Any(static message => message.Type is "drawing" or "image"), "the cancelled drawing was saved");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "state().noting", timeout.Token), "the newer note stays open");
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().saving === false", timeout.Token));
        Assert.IsTrue(await page.CallToolbarAsync(ready.ContextId, "drawState().shapes === 1", timeout.Token));

        await session.OkAsync("press_key", """{"key":"Control+Enter"}""");
        var drawing = Next(messages, "drawing", timeout.Token);
        Assert.AreEqual("Newer drawing", drawing.Note);
        await Task.Delay(300, timeout.Token);
        Assert.IsFalse(messages.Any(static message => message.Type == "image"), "the image went with the cancelled note");
    }

    [TestMethod]
    public void ToolbarMessages_OfADrawing_AreAboutTheAreaItCovers()
    {
        var drawing = ToolbarMessage.TryParse("""{"type":"drawing","id":"d1","note":"Like this","url":"http://localhost/","area":{"x":10,"y":20,"width":300,"height":200}}""", 1);
        Assert.IsNotNull(drawing);
        Assert.AreEqual(new AnnotationArea(10, 20, 300, 200, true), drawing.Area, "a drawing covers its area");
        Assert.AreEqual(0, drawing.Elements!.Count);

        // A drawing names the elements its shapes are about, as many as a note covers at most; it has a note and an area.
        Assert.AreEqual(1, ToolbarMessage.TryParse("""{"type":"drawing","id":"d1","note":"n","elements":[{"tag":"p"}],"area":{"x":0,"y":0,"width":5,"height":5}}""", 1)!.Elements!.Count);
        var many = string.Join(",", Enumerable.Repeat("""{"tag":"p"}""", 20));
        Assert.AreEqual(ToolbarMessage.MaximumElements, ToolbarMessage.TryParse($$$"""{"type":"drawing","id":"d1","note":"n","elements":[{{{many}}}],"area":{"x":0,"y":0,"width":5,"height":5}}""", 1)!.Elements!.Count);
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"drawing","id":"d1","note":"","area":{"x":0,"y":0,"width":5,"height":5}}""", 1));
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"drawing","id":"d1","note":"n"}""", 1));
        Assert.IsNull(ToolbarMessage.TryParse("""{"type":"drawing","id":"d1","note":"n","area":{"x":0,"y":0,"width":0,"height":5}}""", 1));
    }

    [TestMethod]
    public void AnnotationPrompt_OfADrawing_SaysItIsADrawing_AndWhereItsPictureIs()
    {
        var page = new Uri("http://localhost:5173/");
        var drawing = new SessionAnnotation(4, "d1", "Bigger\nlike this", page, [], new AnnotationArea(76.4, 126, 252, 152.6, true), new ProjectPage("/p", "a server of the project"))
        {
            IsDrawing = true,
            Picture = "annotation-4.png",
        };
        Assert.AreEqual("Drawing 252×153", drawing.Label);
        Assert.IsFalse(drawing.IsSingleElement);

        var text = AnnotationPrompt.Format([drawing]);
        StringAssert.Contains(text, "**[4]** A drawing on the page\n- Page: http://localhost:5173/ (a server of the project)\n- Drawn over: 252×153 pixels at 76, 126 of the page\n");
        StringAssert.Contains(text, "- Picture: call `view_annotation` with number 4 to see the drawing over the page");
        StringAssert.Contains(text, "- Note of the user: Bigger\n  like this");

        var without = new SessionAnnotation(5, "d2", "Here", page, [], new AnnotationArea(0, 0, 50, 50, true), null) { IsDrawing = true };
        var other = AnnotationPrompt.Format([without]);
        StringAssert.Contains(other, "- Page: http://localhost:5173/ (not a page of the project)");
        StringAssert.Contains(other, "- Picture: the browser gave none");
    }

    // A stroke of the left button from one point of the viewport to another, as the browser's own input.
    private static async Task DrawAsync(BrowserPageState state, (double X, double Y) from, (double X, double Y) to, CancellationToken cancellationToken)
    {
        await state.Page.DragAsync(BrowserPoint.OfPage(state.Page.MainFrame, from.X, from.Y), BrowserPoint.OfPage(state.Page.MainFrame, to.X, to.Y), cancellationToken);
        await Task.Delay(100, cancellationToken);
    }

    private static ToolbarMessage Next(BlockingCollection<ToolbarMessage> messages, string type, CancellationToken cancellationToken, Func<ToolbarMessage, bool>? accept = null)
    {
        while (true)
        {
            var message = messages.Take(cancellationToken);
            if (message.Type == type && (accept is null || accept(message))) return message;
        }
    }

    private sealed class NoPortOwners : IPortOwners
    {
        public Task<PortOwner?> FindAsync(string host, int port, CancellationToken cancellationToken) => Task.FromResult<PortOwner?>(null);

        public Task<IReadOnlyList<ListeningPort>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ListeningPort>>([]);
    }
}
