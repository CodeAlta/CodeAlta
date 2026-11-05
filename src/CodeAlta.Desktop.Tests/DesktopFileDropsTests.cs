using System.Text.Json;
using CodeAlta.Desktop.Rpc;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Dialogs;
using NeoAstra.Desktop.DragDrop;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The files and folders dropped from the file manager where the host gets the drop and the page does not (Linux and
/// Windows): NeoAstra's broker gives tokens, CodeAlta tells the page of the window where they were dropped, and the page
/// reads the content of each file once. Also the dialogs that a desktop does not have, which are never silent.
/// </summary>
[TestClass]
public sealed class DesktopFileDropsTests
{
    private static readonly NeoPluginOwner Owner = NeoPluginOwner.DocumentSession("document-1");

    [TestMethod]
    public async Task Drop_TellsThePageOfItsView_TheFoldersByPath_AndTheFilesByAnIdReadOnce()
    {
        var root = Folder();
        try
        {
            var notes = Path.Combine(root, "notes.txt");
            await File.WriteAllTextAsync(notes, "hello");
            var png = Path.Combine(root, "shot.png");
            await File.WriteAllBytesAsync(png, [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]);
            var docs = Directory.CreateDirectory(Path.Combine(root, "docs")).FullName;

            var drops = new DesktopFileDrops();
            var published = new List<(string Label, DesktopShellDrop Drop)>();
            await using var broker = new NeoDragDropBroker(new NeoFileScope([root]));
            using var following = drops.Follow(broker, (label, drop) => published.Add((label, drop)), pageGetsDrops: false, scaleOf: _ => 1.5, zoom: () => 1.25);
            var brokered = broker.BrokerInbound("main", "window-main", new NeoPoint(300, 150),
                [(NeoDragDataKind.File, notes), (NeoDragDataKind.File, docs), (NeoDragDataKind.File, png), (NeoDragDataKind.Text, "not a file")], Owner);
            Assert.AreEqual(NeoDesktopStatus.Success, brokered.Status);

            var (label, drop) = published.Single();
            Assert.AreEqual("main", label, "The drop goes to the page of the view it was dropped on.");
            CollectionAssert.AreEqual(new[] { docs }, drop.Folders.ToArray(), "A folder is attached by its path, as with Add folder….");
            CollectionAssert.AreEqual(new[] { "notes.txt", "shot.png" }, drop.Files.Select(static file => file.Name).ToArray());
            CollectionAssert.AreEqual(new[] { 5, 29 }, drop.Files.Select(static file => file.Size).ToArray());
            Assert.IsTrue(drop.Files.All(file => file.Id.Length == 32 && !file.Id.Contains(root, StringComparison.Ordinal)), "The page names a file by an id, never by its path.");
            var wire = JsonSerializer.Serialize(new DesktopShellEvent("files-dropped", 0) { Drop = drop with { Folders = [] } }, DesktopJsonContext.Default.DesktopShellEvent);
            Assert.IsFalse(wire.Contains("notes.txt\"", StringComparison.Ordinal) && wire.Contains(Path.GetFileName(root), StringComparison.Ordinal), "No path of a file reaches the page.");

            var text = await drops.ReadAsync(drop.Files[0].Id, CancellationToken.None);
            Assert.AreEqual(new DesktopShellDroppedFileResponse("ok", "notes.txt", "text/plain", Convert.ToBase64String("hello"u8)), text);
            Assert.AreEqual("image/png", (await drops.ReadAsync(drop.Files[1].Id, CancellationToken.None)).MediaType, "An image keeps its type: the page converts it as a dropped image.");
            Assert.AreEqual("not_found", (await drops.ReadAsync(drop.Files[0].Id, CancellationToken.None)).Status, "A dropped file is read once.");
            Assert.AreEqual("not_found", (await drops.ReadAsync("0123456789abcdef0123456789abcdef", CancellationToken.None)).Status);
            Assert.AreEqual("not_found", (await drops.ReadAsync(null, CancellationToken.None)).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Drop_WherePageGetsItAlready_IsNotAttachedTwice_AndADropWithoutFilesTellsNothing()
    {
        var root = Folder();
        try
        {
            var notes = Path.Combine(root, "notes.txt");
            await File.WriteAllTextAsync(notes, "hello");
            var published = new List<DesktopShellDrop>();
            await using var broker = new NeoDragDropBroker(new NeoFileScope([root]));
            // macOS gives the drop to the page (dataTransfer.files) and to the host: the page attaches it.
            using (new DesktopFileDrops().Follow(broker, (_, drop) => published.Add(drop), pageGetsDrops: true, scaleOf: _ => 1, zoom: () => 1))
                broker.BrokerInbound("main", null, new NeoPoint(1, 1), [(NeoDragDataKind.File, notes)], Owner);
            Assert.AreEqual(0, published.Count);

            using (new DesktopFileDrops().Follow(broker, (_, drop) => published.Add(drop), pageGetsDrops: false, scaleOf: _ => 1, zoom: () => 1))
                broker.BrokerInbound("main", null, new NeoPoint(1, 1), [(NeoDragDataKind.Text, "text"), (NeoDragDataKind.Url, "https://codealta.io/")], Owner);
            Assert.AreEqual(0, published.Count, "Text and links are not files to attach.");

            var drops = new DesktopFileDrops();
            var following = drops.Follow(broker, (_, drop) => published.Add(drop), pageGetsDrops: false, scaleOf: _ => 1, zoom: () => 1);
            following.Dispose();
            broker.BrokerInbound("main", null, new NeoPoint(1, 1), [(NeoDragDataKind.File, notes)], Owner);
            Assert.AreEqual(0, published.Count, "A disposed follower takes no more drops.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    // GTK gives the point in logical units of the view, which the zoom of the page enlarges.
    [DataRow(false, 300, 150, 1.5, 1.25, 240, 120)]
    // Windows gives it in pixels of the display: the scale of the window, then the zoom.
    [DataRow(true, 300, 150, 1.5, 1.25, 160, 80)]
    [DataRow(true, 300, 150, 1, 1, 300, 150)]
    public void PagePoint_IsInCssPixelsOfThePage(bool physical, int x, int y, double scale, double zoom, double expectedX, double expectedY)
    {
        var (pageX, pageY) = DesktopFileDrops.PagePoint(new NeoPoint(x, y), physical ? scale : 1, zoom);
        Assert.AreEqual(expectedX, pageX, 0.001);
        Assert.AreEqual(expectedY, pageY, 0.001);
        // A scale or a zoom that is not known counts as 1.
        Assert.AreEqual((x, y), ((int)DesktopFileDrops.PagePoint(new NeoPoint(x, y), double.NaN, 0).X, (int)DesktopFileDrops.PagePoint(new NeoPoint(x, y), double.NaN, 0).Y));
    }

    [TestMethod]
    public async Task DroppedFile_LargerThanAnAttachment_IsRefused_AndOneThatWentAwayHasFailed()
    {
        var root = Folder();
        try
        {
            var large = Path.Combine(root, "large.bin");
            await using (var stream = File.Create(large)) stream.SetLength(DesktopFileDrops.MaximumFileBytes + 1);
            var gone = Path.Combine(root, "gone.txt");
            await File.WriteAllTextAsync(gone, "soon gone");
            var drop = new DesktopFileDrops().Take(10, 20, [large, gone, Path.Combine(root, "missing.txt")])!;
            Assert.AreEqual(2, drop.Files.Length, "A path that does not exist is not offered.");
            File.Delete(gone);
            var drops = new DesktopFileDrops();
            drop = drops.Take(10, 20, [large])!;
            Assert.AreEqual(new DesktopShellDroppedFileResponse("too_large", "large.bin", null, null), await drops.ReadAsync(drop.Files[0].Id, CancellationToken.None));
            await File.WriteAllTextAsync(gone, "back");
            drop = drops.Take(10, 20, [gone])!;
            File.Delete(gone);
            Assert.AreEqual(new DesktopShellDroppedFileResponse("failed", "gone.txt", null, null), await drops.ReadAsync(drop.Files[0].Id, CancellationToken.None));
            Assert.IsNull(drops.Take(10, 20, [Path.Combine(root, "missing.txt")]), "A drop with nothing to attach tells the page nothing.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task DroppedFiles_ExpireAndAreBounded()
    {
        var root = Folder();
        try
        {
            var notes = Path.Combine(root, "notes.txt");
            await File.WriteAllTextAsync(notes, "hello");
            var time = new ManualTime();
            var drops = new DesktopFileDrops(time);
            var first = drops.Take(0, 0, [notes])!.Files[0].Id;
            time.Now += DesktopFileDrops.Lifetime + TimeSpan.FromSeconds(1);
            Assert.AreEqual("not_found", (await drops.ReadAsync(first, CancellationToken.None)).Status, "A drop the page did not read in time is forgotten.");
            var ids = Enumerable.Range(0, DesktopFileDrops.MaximumPendingFiles + 1).Select(_ => drops.Take(0, 0, [notes])!.Files[0].Id).ToArray();
            Assert.AreEqual("not_found", (await drops.ReadAsync(ids[0], CancellationToken.None)).Status, "The oldest file goes when too many wait.");
            Assert.AreEqual("ok", (await drops.ReadAsync(ids[^1], CancellationToken.None)).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReadDroppedFile_OverRpc_AnswersWithoutAShell()
    {
        var response = await new DesktopShellService().ReadDroppedFileAsync(new("0123456789abcdef0123456789abcdef"), CancellationToken.None);
        Assert.AreEqual("unavailable", response.Status);
        var wire = JsonSerializer.Serialize(new DesktopShellEvent("files-dropped", 0) { Drop = new(15, 2, ["/home/me/docs"], [new("0123456789abcdef0123456789abcdef", "a.txt", 3)]) },
            DesktopJsonContext.Default.DesktopShellEvent);
        StringAssert.Contains(wire, "\"drop\":{\"x\":15,\"y\":2,\"folders\":[\"/home/me/docs\"],\"files\":[{\"id\":\"0123456789abcdef0123456789abcdef\",\"name\":\"a.txt\",\"size\":3}]}");
        Assert.IsFalse(JsonSerializer.Serialize(new DesktopShellEvent("confirm-close", 0), DesktopJsonContext.Default.DesktopShellEvent).Contains("drop", StringComparison.Ordinal),
            "The other notices carry no drop.");
    }

    [TestMethod]
    public async Task FileChooser_WithoutADialogOnThisDesktop_SaysSo_AndAFailedDialogToo()
    {
        var request = new NeoFileChooserRequest([], AllowsMultipleSelection: true);
        var problems = new List<string>();
        Assert.AreEqual(NeoDecisionAction.Cancel, (await DesktopFileChooser.ChooseAsync(null, null, request, problems.Add)).Action);
        var dialogs = new NeoFakeDialogs();
        dialogs.Enqueue(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Unsupported));
        dialogs.Enqueue(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Failed, "portal"));
        dialogs.Enqueue(NeoDesktopResult<IReadOnlyList<string>>.Failure(NeoDesktopStatus.Canceled));
        foreach (var _ in Enumerable.Range(0, 3))
            Assert.AreEqual(NeoDecisionAction.Cancel, (await DesktopFileChooser.ChooseAsync(null, dialogs, request, problems.Add)).Action);
        // An empty queue makes the fake dialog throw, as a dialog that could not be shown.
        Assert.AreEqual(NeoDecisionAction.Cancel, (await DesktopFileChooser.ChooseAsync(null, dialogs, request, problems.Add)).Action);
        CollectionAssert.AreEqual(new[] { "unavailable", "unavailable", "failed", "failed" }, problems, "A cancel of the user says nothing; a missing or failed dialog is never silent.");
    }

    [TestMethod]
    public void DialogProblem_GoesToThePageOfItsWindow()
    {
        var wire = JsonSerializer.Serialize(new DesktopShellEvent("dialog-problem", 0) { Dialog = "files", Problem = "unavailable" }, DesktopJsonContext.Default.DesktopShellEvent);
        StringAssert.Contains(wire, "\"dialog\":\"files\"");
        StringAssert.Contains(wire, "\"problem\":\"unavailable\"");
    }

    private static string Folder() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-drops-" + Guid.NewGuid().ToString("N"))).FullName;

    private sealed class ManualTime : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
