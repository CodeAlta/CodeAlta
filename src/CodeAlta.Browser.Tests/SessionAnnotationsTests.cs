using CodeAlta.Browser.Annotations;
using CodeAlta.Browser.Tools;
using CodeAlta.Plugin.Browser;

namespace CodeAlta.Browser.Tests;

/// <summary>
/// The annotations of a session while their pictures and images are still on their way: a page without a browser reports
/// them, and the test says when the browser and the disk answer.
/// </summary>
[TestClass]
public sealed class SessionAnnotationsTests
{
    private const string Annotation = """{"type":"annotation","id":"a1","note":"Rounder","elements":[{"tag":"button","id":"save"}],"area":{"x":0,"y":0,"width":80,"height":20}}""";

    [TestMethod]
    public async Task AnImageStillWritten_GoesWithThePromptOfASend()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var annotations = Create(folder.Path, async (file, bytes, cancellationToken) =>
        {
            await written.Task;
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
        });
        var page = new FakeAnnotatablePage("http://localhost:5173/");
        annotations.Attach(page);
        await page.ReadyAsync();
        await page.ReportAsync(Annotation);
        var annotation = await PendingAsync(annotations);
        await annotation.Ready;

        // The user pastes an image into the note, then sends at once, while the disk still writes the image.
        await page.ReportAsync($$"""{"type":"image","id":"a1","data":"data:image/png;base64,{{Convert.ToBase64String(BrowserAnnotationsTests.Png(4, 4, noise: false))}}"}""");
        var offer = annotations.OfferPromptAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.IsFalse(offer.IsCompleted, "the prompt waits for the image being written");
        written.SetResult();
        var text = (await offer)!;
        StringAssert.Contains(text, "Image the user pasted into the note");

        Assert.IsTrue(await annotations.PromptRecordedAsync(text));
        var image = annotation.Images.Single();
        Assert.IsTrue(File.Exists(image.File), "the image of an annotation sent is kept");
        StringAssert.Contains(text, image.File);
    }

    [TestMethod]
    public async Task AnAnnotationRemovedAfterAPromptThatWasCancelled_KeepsNoPictureThatComesLate()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var picture = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var annotations = Create(folder.Path);
        var page = new FakeAnnotatablePage("http://localhost:5173/") { Picture = (_, _) => picture.Task };
        annotations.Attach(page);
        await page.ReadyAsync();
        await page.ReportAsync(Annotation);
        var annotation = await PendingAsync(annotations);

        // The prompt stops waiting for the picture, and a hook of another plugin cancels it: the annotation was offered with
        // it. Then the user removes the annotation, before the browser gives its picture.
        Assert.IsNotNull(await annotations.OfferPromptAsync(CancellationToken.None));
        Assert.IsTrue(await annotations.RemoveAsync("a1"));
        picture.SetResult(BrowserAnnotationsTests.Png(4, 4, noise: false));
        await annotation.Ready;

        Assert.IsNotNull(annotation.Picture, "the browser gave the picture");
        Assert.IsFalse(File.Exists(annotation.Picture), "the picture of an annotation that nothing shows any more is deleted");
        Assert.AreEqual(0, Directory.EnumerateFiles(folder.Path).Count(static file => Path.GetFileName(file) != BrowserAnnotations.LockFileName));
    }

    [TestMethod]
    public async Task TheBrowserThatReplacesAnother_TakesItsAnnotationsAndTheirPictures()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var first = Path.Combine(folder.Path, "first");
        var second = Path.Combine(folder.Path, "second");
        var previous = Create(first);
        var page = new FakeAnnotatablePage("http://localhost:5173/") { Picture = static (_, _) => Task.FromResult<byte[]?>(BrowserAnnotationsTests.Png(4, 4, noise: false)) };
        previous.Attach(page);
        await page.ReadyAsync();
        await page.ReportAsync(Annotation);
        var annotation = await PendingAsync(previous);
        await annotation.Ready;
        StringAssert.StartsWith(annotation.Picture, first);

        // The session gets another browser: the annotation waits there, with its number and its picture.
        await using var annotations = Create(second);
        await annotations.TakeOverAsync(previous);
        await previous.DisposeAsync();
        Assert.AreEqual(0, previous.Pending.Count);
        Assert.AreSame(annotation, annotations.Pending.Single());
        Assert.AreEqual(1, annotation.Number);
        StringAssert.StartsWith(annotation.Picture, second);
        Assert.IsTrue(File.Exists(annotation.Picture), "the picture moved with it");
        Assert.IsFalse(Directory.Exists(first), "the folder of the browser replaced is gone");
        StringAssert.Contains(await annotations.OfferPromptAsync(CancellationToken.None), "Rounder");

        // The next one the user makes there follows it.
        var next = new FakeAnnotatablePage("http://localhost:5173/");
        annotations.Attach(next);
        await next.ReadyAsync();
        StringAssert.Contains(next.Calls.First(static call => call.StartsWith("enable(", StringComparison.Ordinal)), "Rounder", "its toolbar shows the mark of the annotation taken over");
        await next.ReportAsync(Annotation.Replace("\"a1\"", "\"a2\"", StringComparison.Ordinal));
        for (var wait = DateTime.UtcNow.AddSeconds(10); annotations.Pending.Count < 2 && DateTime.UtcNow < wait;) await Task.Delay(20);
        Assert.AreEqual(2, annotations.Pending[^1].Number);
    }

    [TestMethod]
    public async Task AnnotationsDetachedFromTheirBrowser_KeepWaitingWithTheirPictures()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        await using var annotations = Create(folder.Path);
        var page = new FakeAnnotatablePage("http://localhost:5173/") { Picture = static (_, _) => Task.FromResult<byte[]?>(BrowserAnnotationsTests.Png(4, 4, noise: false)) };
        annotations.Attach(page);
        await page.ReadyAsync();
        await page.ReportAsync(Annotation);
        var annotation = await PendingAsync(annotations);
        await annotation.Ready;

        await annotations.DetachAsync();
        Assert.IsTrue(annotations.HasWaiting);
        Assert.IsTrue(File.Exists(annotation.Picture));
        var text = (await annotations.OfferPromptAsync(CancellationToken.None))!;
        StringAssert.Contains(text, "Rounder", "the next prompt of the session carries them");
        Assert.IsTrue(await annotations.PromptRecordedAsync(text));
        Assert.IsFalse(annotations.HasWaiting);

        // A tab that comes afterwards gets no toolbar from them.
        var later = new FakeAnnotatablePage("http://localhost:5173/");
        annotations.Attach(later);
        await Task.Delay(200);
        Assert.AreEqual(0, later.Calls.Count);
    }

    [TestMethod]
    public async Task TheFoldersOfPicturesLeftBehind_AreSwept_ButNotOneInUse()
    {
        using var folder = new BrowserPluginTests.TempFolder();
        var old = Directory.CreateDirectory(Path.Combine(folder.Path, "s1-left")).FullName;
        await File.WriteAllTextAsync(Path.Combine(old, "annotation-1.png"), "png");
        var recent = Directory.CreateDirectory(Path.Combine(folder.Path, "s2-recent")).FullName;
        var used = Path.Combine(folder.Path, "s3-used");
        await using var annotations = Create(used);
        var page = new FakeAnnotatablePage("http://localhost:5173/") { Picture = static (_, _) => Task.FromResult<byte[]?>(BrowserAnnotationsTests.Png(4, 4, noise: false)) };
        annotations.Attach(page);
        await page.ReadyAsync();
        await page.ReportAsync(Annotation);
        await (await PendingAsync(annotations)).Ready;
        foreach (var path in new[] { old, used }) Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));

        BrowserAnnotations.DeleteLeftovers(folder.Path, TimeSpan.FromHours(1));

        Assert.IsFalse(Directory.Exists(old), "left by a CodeAlta that ended without closing its browser");
        Assert.IsTrue(Directory.Exists(recent), "a folder made a moment ago may not hold its lock yet");
        Assert.IsTrue(File.Exists(annotations.Pending.Single().Picture), "the annotations of a session of another CodeAlta hold their folder");
    }

    // The annotations of a session whose toolbar shows on every page, with what a test gives of the disk.
    private static BrowserAnnotations Create(string pictures, Func<string, byte[], CancellationToken, Task>? writeFile = null)
    {
        var files = new BrowserFiles(pictures, [pictures], [], pictures);
        var host = new BrowserAnnotationsHost(new ProjectPages(new ProjectFileServer(), new NoPortOwners()), new SourceLocator(static (_, _) => Task.FromResult<string?>(null), _ => ValueTask.FromResult(files)),
            static _ => ValueTask.FromResult<string?>(null), pictures, static () => { }, static () => { }, static () => "dark", static _ => Task.CompletedTask,
            static () => null, static _ => Task.CompletedTask, static _ => Task.FromResult(false), static () => true, static _ => false);
        return new BrowserAnnotations(writeFile is null ? host : host with { WriteFile = writeFile });
    }

    private static async Task<SessionAnnotation> PendingAsync(BrowserAnnotations annotations)
    {
        for (var wait = DateTime.UtcNow.AddSeconds(10); annotations.Pending.Count == 0 && DateTime.UtcNow < wait;) await Task.Delay(20);
        return annotations.Pending.Single();
    }

    private sealed class NoPortOwners : IPortOwners
    {
        public Task<PortOwner?> FindAsync(string host, int port, CancellationToken cancellationToken) => Task.FromResult<PortOwner?>(null);

        public Task<IReadOnlyList<ListeningPort>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ListeningPort>>([]);
    }
}
