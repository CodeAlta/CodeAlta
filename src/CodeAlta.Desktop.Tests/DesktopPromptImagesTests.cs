using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The image list of the history row of a user message or of a tool result, and the image read over a real
/// journal and a real prompt-image folder in a disposable catalog root.
/// </summary>
[TestClass]
public sealed class DesktopPromptImagesTests
{
    private const string Epoch = "epoch-1";
    private static readonly byte[] Png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3, 4];

    [TestMethod]
    public void History_ListsTheImagesOfAUserMessageWithoutTheirPaths()
    {
        var first = @"C:\Users\someone\.alta\sessions\2026\09\23\session.attachments\one.png";
        var second = "/home/someone/.alta/sessions/2026/09/23/session.attachments/two.png";
        var message = User($"Look at this\r\nsecond line\r\nLocal image (Screenshot): {first}\r\nLocal image: {second}",
            Items(("text", "Look at this\r\nsecond line", null, null), ("localImage", first, "Screenshot", "image/png"), ("localImage", second, null, null)));

        var row = WorkspaceService.ProjectHistory(new([new(0, message)], null, false)).Entries.Single();

        Assert.AreEqual("Look at this\r\nsecond line", row.Text);
        CollectionAssert.AreEqual(new[] { new HistoryImage(0, "Screenshot", "image/png"), new HistoryImage(1, "two.png", null) }, row.Images);
        Assert.IsFalse(row.TextTruncated);
        var wire = JsonSerializer.Serialize(row, DesktopJsonContext.Default.HistoryEntry);
        Assert.IsFalse(wire.Contains("someone", StringComparison.Ordinal));
        Assert.IsFalse(wire.Contains("attachments", StringComparison.Ordinal));
        // The details stay valid JSON and keep everything but the paths.
        using var details = JsonDocument.Parse(row.Details!);
        var items = details.RootElement.GetProperty("items");
        Assert.AreEqual("Look at this\r\nsecond line", items[0].GetProperty("text").GetString());
        Assert.AreEqual("Screenshot", items[1].GetProperty("displayName").GetString());
        Assert.IsFalse(items[1].TryGetProperty("path", out _));
        Assert.IsFalse(items[2].TryGetProperty("path", out _));
    }

    [TestMethod]
    public void History_KeepsWhatTheUserTyped()
    {
        var path = "/store/a.png";
        // Only the lines that name the images are removed, one per image and from the end.
        var typed = "Local image (A): /store/a.png\nis what the last message said\n";
        var message = User($"{typed}\nLocal image (A): {path}", Items(("text", typed, null, null), ("localImage", path, "A", "image/png")));
        Assert.AreEqual(typed, WorkspaceService.ProjectHistory(new([new(0, message)], null, false)).Entries.Single().Text);

        // A prompt of images only has an empty text.
        var only = User($"Local image (A): {path}{Environment.NewLine}Local image (B): /store/b.png",
            Items(("localImage", path, "A", "image/png"), ("localImage", "/store/b.png", "B", "image/png")));
        var row = WorkspaceService.ProjectHistory(new([new(0, only)], null, false)).Entries.Single();
        Assert.AreEqual(string.Empty, row.Text);
        Assert.HasCount(2, row.Images!);

        // Text that names no recorded image is left alone.
        var other = User("Local image (A): /elsewhere/a.png", Items(("localImage", path, "A", "image/png")));
        Assert.AreEqual("Local image (A): /elsewhere/a.png", WorkspaceService.ProjectHistory(new([new(0, other)], null, false)).Entries.Single().Text);
    }

    [TestMethod]
    public void History_ReadsLegacyAttachmentsAndBoundsWhatItSends()
    {
        var legacy = User("Describe", JsonDocument.Parse("""
            {"attachments":[{"path":"/store/old.bmp","title":"Old","mediaType":"image/bmp"},{"path":"","title":"No file"},{"title":"No path"},"text",
            {"path":"/store/odd.png","title":"  \u0007 ","mediaType":"image/png; charset=x"}]}
            """).RootElement.Clone());
        var row = WorkspaceService.ProjectHistory(new([new(0, legacy)], null, false)).Entries.Single();
        CollectionAssert.AreEqual(new[] { new HistoryImage(0, "Old", "image/bmp"), new HistoryImage(1, "image", null) }, row.Images);
        Assert.IsFalse(row.Details!.Contains("/store", StringComparison.Ordinal));

        var title = new string('t', 300);
        var many = User(string.Join('\n', Enumerable.Range(0, 80).Select(index => $"Local image ({title}): /store/{index}.png")),
            Items(Enumerable.Range(0, 80).Select(index => ("localImage", $"/store/{index}.png", (string?)title, (string?)"IMAGE/PNG")).ToArray()));
        var listed = WorkspaceService.ProjectHistory(new([new(0, many)], null, false)).Entries.Single();
        var images = listed.Images!;
        Assert.HasCount(HistoryImageProjection.MaximumImages, images);
        // The images that are not listed are not named either.
        Assert.AreEqual(string.Empty, listed.Text);
        Assert.IsFalse(listed.Details!.Contains("/store", StringComparison.Ordinal));
        Assert.AreEqual(new string('t', HistoryImageProjection.MaximumTitleLength), images[0].Title);
        Assert.AreEqual("image/png", images[0].MediaType);
        CollectionAssert.AreEqual(Enumerable.Range(0, images.Length).ToArray(), images.Select(image => image.Index).ToArray());
    }

    [TestMethod]
    public void History_ListsNoImageForOtherRecords()
    {
        var details = Items(("text", "Hello", null, null), ("localImage", "/store/a.png", "A", "image/png"));
        var assistant = new AgentContentCompletedEvent(new("p"), "s", DateTimeOffset.UnixEpoch, null, AgentContentKind.Assistant, "c", null,
            "Hello\nLocal image (A): /store/a.png", details);
        var plain = User("Hello", Items(("text", "Hello", null, null)));
        var rows = WorkspaceService.ProjectHistory(new([new(0, assistant), new(10, plain), new(20, User("No details", null))], null, false)).Entries;
        Assert.IsTrue(rows.All(row => row.Images is null));
        Assert.AreEqual("Hello\nLocal image (A): /store/a.png", rows[0].Text);
        StringAssert.Contains(rows[0].Details!, "/store/a.png");
        StringAssert.Contains(JsonSerializer.Serialize(rows[1], DesktopJsonContext.Default.HistoryEntry), "\"images\":null");
    }

    [TestMethod]
    public void History_ListsTheImagesOfAToolResultWithoutTheirPaths()
    {
        var path = @"C:\Users\someone\.alta\sessions\2026\09\23\session.attachments\20260923-tool-shot.png";
        var details = ToolResult(path);
        var activity = new AgentActivityEvent(new("fixture"), Fixture.SessionId, DateTimeOffset.UnixEpoch, new AgentRunId("run"), AgentActivityKind.ToolCall,
            AgentActivityPhase.Completed, "call-1", null, "view_image", null, details);
        var output = ToolOutput("Viewed image shot.png.\n[Image: shot.png (image/png, 640x480)]", details);

        var rows = WorkspaceService.ProjectHistory(new([new(0, activity), new(10, output)], null, false)).Entries;

        // The output lists the image the model was given; its text is what a text surface shows.
        CollectionAssert.AreEqual(new[] { new HistoryImage(0, "shot.png", "image/png") }, rows[1].Images);
        Assert.AreEqual("Viewed image shot.png.\n[Image: shot.png (image/png, 640x480)]", rows[1].Text);
        Assert.IsNull(rows[0].Images);
        // Neither row names the file: the page reads the image by its index.
        foreach (var row in rows)
        {
            var wire = JsonSerializer.Serialize(row, DesktopJsonContext.Default.HistoryEntry);
            Assert.IsFalse(wire.Contains("someone", StringComparison.Ordinal), wire);
            Assert.IsFalse(wire.Contains("attachments", StringComparison.Ordinal), wire);
            using var parsed = JsonDocument.Parse(row.Details!);
            var image = parsed.RootElement.GetProperty("result").GetProperty("items")[1];
            Assert.AreEqual("localImage", image.GetProperty("$type").GetString());
            Assert.AreEqual(640, image.GetProperty("width").GetInt32());
            Assert.IsFalse(image.TryGetProperty("path", out _));
            Assert.AreEqual("shot.png", parsed.RootElement.GetProperty("arguments").GetProperty("path").GetString());
        }

        // A tool result without an image is sent as it was recorded.
        var plain = ToolOutput("done", JsonDocument.Parse("""{"toolName":"x","result":{"success":true,"items":[{"$type":"text","value":"done"}]}}""").RootElement.Clone());
        var plainRow = WorkspaceService.ProjectHistory(new([new(0, plain)], null, false)).Entries.Single();
        Assert.IsNull(plainRow.Images);
        Assert.AreEqual(plain.Details!.Value.GetRawText(), plainRow.Details);
    }

    [TestMethod]
    public async Task Read_ServesAnImageOfAToolResult()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.SaveAsync(("shot", Png));
        var offset = await fixture.AddAsync(ToolOutput("Viewed image shot.png.", ToolResult(saved[0].Path)));

        Assert.AreEqual(new PromptImageResponse("ok", "image/png", Convert.ToBase64String(Png)),
            await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, 0), default));
        Assert.AreEqual("missing_image", (await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, 1), default)).Status);

        // A tool result that names a file outside the session's folder gets nothing.
        var outside = Path.Combine(fixture.Root, "outside.png");
        await File.WriteAllBytesAsync(outside, Png);
        var foreign = await fixture.AddAsync(ToolOutput("Viewed.", ToolResult(outside)));
        Assert.AreEqual("outside_store", (await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, foreign, 0), default)).Status);
    }

    [TestMethod]
    public async Task Read_ServesAnImageOfTheSessionsFolder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var jpeg = new byte[] { 0xff, 0xd8, 0xff, 0xe0, 0, 16 };
        var saved = await fixture.SaveAsync(("First", Png), ("Second", jpeg));
        var offset = await fixture.AddUserAsync("Look", saved);

        var response = await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, 0), default);
        Assert.AreEqual(new PromptImageResponse("ok", "image/png", Convert.ToBase64String(Png)), response);
        // The type is the one of the file's content, not of its name or of the record.
        Assert.AreEqual(new PromptImageResponse("ok", "image/jpeg", Convert.ToBase64String(jpeg)),
            await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, 1), default));
        var wire = JsonSerializer.Serialize(response, DesktopJsonContext.Default.PromptImageResponse);
        Assert.AreEqual(response, JsonSerializer.Deserialize(wire, DesktopJsonContext.Default.PromptImageResponse));

        // The history row of the same record names the same indexes.
        var page = await fixture.Reads.ReadTimelinePageAsync(Fixture.SessionId, null, default);
        var row = WorkspaceService.ProjectHistory(page, 2).Entries.Single(entry => entry.Offset == offset);
        CollectionAssert.AreEqual(new[] { new HistoryImage(0, "First", "image/png"), new HistoryImage(1, "Second", "image/png") }, row.Images);
        Assert.AreEqual("Look", row.Text);
    }

    [TestMethod]
    public async Task Read_ServesEverySupportedImageTypeAndRefusesTheOthers()
    {
        await using var fixture = await Fixture.CreateAsync();
        var webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        var bitmap = new byte[26]; bitmap[0] = (byte)'B'; bitmap[1] = (byte)'M';
        var saved = await fixture.SaveAsync(("gif", "GIF89a\u0001\0"u8.ToArray()), ("old gif", "GIF87a\u0001\0"u8.ToArray()), ("webp", webp), ("bmp", bitmap),
            ("svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray()), ("text", "plain text"u8.ToArray()), ("riff", "RIFF\0\0\0\0WAVEfmt "u8.ToArray()),
            ("short", [0x89, 0x50]));
        var offset = await fixture.AddUserAsync("Types", saved);
        var expected = new[] { "image/gif", "image/gif", "image/webp", "image/bmp", null, null, null, null };
        for (var index = 0; index < expected.Length; index++)
        {
            var response = await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, index), default);
            Assert.AreEqual(expected[index] is null ? "unsupported_type" : "ok", response.Status, saved[index].Title);
            Assert.AreEqual(expected[index], response.MediaType);
            Assert.AreEqual(expected[index] is null, response.Base64 is null);
        }
    }

    [TestMethod]
    public async Task Read_RefusesAStaleEpochAndMalformedRequests()
    {
        await using var fixture = await Fixture.CreateAsync();
        var offset = await fixture.AddUserAsync("Look", await fixture.SaveAsync(("First", Png)));
        var request = new PromptImageRequest(Epoch, Fixture.SessionId, offset, 0);

        Assert.AreEqual(new PromptImageResponse("unavailable", null, null), await new PromptImagesService().ReadAsync(request, default));
        Assert.AreEqual(new PromptImageResponse("stale_epoch", null, null), await fixture.Service.ReadAsync(request with { ExpectedEpoch = "epoch-2" }, default));
        foreach (var bad in new[] { request with { SessionId = "" }, request with { SessionId = " " + Fixture.SessionId }, request with { SessionId = new string('s', 257) },
            request with { SessionId = "a\nb" }, request with { Index = -1 }, request with { Offset = "" }, request with { Offset = "-1" }, request with { Offset = "1e3" },
            request with { Offset = " 12" }, request with { Offset = "92233720368547758070" }, request with { Offset = "9223372036854775808" }, request with { Offset = null! } })
            Assert.AreEqual("invalid", (await fixture.Service.ReadAsync(bad, default)).Status, $"{bad}");
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => fixture.Service.ReadAsync(null!, default));
    }

    [TestMethod]
    public async Task Read_RefusesAnUnknownSessionRecordOrIndex()
    {
        await using var fixture = await Fixture.CreateAsync();
        var offset = await fixture.AddUserAsync("Look", await fixture.SaveAsync(("First", Png)));
        var assistant = await fixture.AddAsync(new AgentContentCompletedEvent(new("fixture"), Fixture.SessionId, DateTimeOffset.UnixEpoch, null,
            AgentContentKind.Assistant, "assistant", null, "Seen", Items(("localImage", "/store/a.png", "A", "image/png"))));
        var request = new PromptImageRequest(Epoch, Fixture.SessionId, offset, 0);
        async Task<string> Status(PromptImageRequest value) => (await fixture.Service.ReadAsync(value, default)).Status;

        Assert.AreEqual("ok", await Status(request));
        Assert.AreEqual("missing_session", await Status(request with { SessionId = "absent" }));
        // Not the start of a record, past the journal, the header record and a record that is not a user message.
        Assert.AreEqual("missing_record", await Status(request with { Offset = (long.Parse(offset) + 1).ToString() }));
        Assert.AreEqual("missing_record", await Status(request with { Offset = "9223372036854775807" }));
        Assert.AreEqual("missing_record", await Status(request with { Offset = "0" }));
        Assert.AreEqual("missing_record", await Status(request with { Offset = assistant }));
        Assert.AreEqual("missing_image", await Status(request with { Index = 1 }));
        Assert.AreEqual("missing_image", await Status(request with { Index = int.MaxValue }));
        Assert.AreEqual("missing_image", await Status(request with { Offset = await fixture.AddUserAsync("No image") }));
    }

    [TestMethod]
    public async Task Read_RefusesAMissingOrOversizedFile()
    {
        await using var fixture = await Fixture.CreateAsync();
        var large = new byte[PromptImageHistory.MaximumImageBytes + 1];
        Png.CopyTo(large, 0);
        var saved = await fixture.SaveAsync(("Gone", Png), ("Large", large), ("Largest served", large[..^1]), ("Folder", Png));
        var offset = await fixture.AddUserAsync("Look", saved);
        File.Delete(saved[0].Path);
        File.Delete(saved[3].Path);
        Directory.CreateDirectory(saved[3].Path);
        async Task<PromptImageResponse> Read(int index) => await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, index), default);

        Assert.AreEqual(new PromptImageResponse("missing_file", null, null), await Read(0));
        Assert.AreEqual(new PromptImageResponse("too_large", null, null), await Read(1));
        var served = await Read(2);
        Assert.AreEqual("ok", served.Status);
        Assert.AreEqual(PromptImageHistory.MaximumImageBytes, Convert.FromBase64String(served.Base64!).Length);
        Assert.AreEqual("missing_file", (await Read(3)).Status);
    }

    [TestMethod]
    public async Task Read_RefusesAPathOutsideTheSessionsFolder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = (await fixture.SaveAsync(("Inside", Png))).Single();
        var folder = Path.GetDirectoryName(saved.Path)!;
        var outside = Path.Combine(fixture.Root, "outside.png");
        File.WriteAllBytes(outside, Png);
        // The folder of another session, a sibling whose name starts like the folder, and the journal itself.
        var other = Path.Combine(Path.GetDirectoryName(folder)!, "other.attachments", "image.png");
        var sibling = folder + "-copy" + Path.DirectorySeparatorChar + "image.png";
        foreach (var path in new[] { other, sibling })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, Png);
        }

        var paths = new[] { outside, other, sibling, Path.ChangeExtension(folder, ".jsonl"), folder,
            Path.Combine(folder, "..", "..", "..", "..", "..", "..", "outside.png"), Path.GetFileName(saved.Path), "outside.png",
            Path.Combine(folder, "..", Path.GetFileName(folder), Path.GetFileName(saved.Path)) };
        var offset = await fixture.AddUserAsync("Look", paths.Select(path => new PromptImageAttachmentReference("Image", path, "image/png")).ToArray());
        for (var index = 0; index < paths.Length - 1; index++)
            Assert.AreEqual(new PromptImageResponse("outside_store", null, null),
                await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, index), default), paths[index]);
        // A path that only wanders and comes back to a file of the folder is that file.
        Assert.AreEqual("ok", (await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, paths.Length - 1), default)).Status);
    }

    [TestMethod]
    public async Task Read_RefusesALinkThatLeavesTheSessionsFolder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var saved = (await fixture.SaveAsync(("Inside", Png))).Single();
        var folder = Path.GetDirectoryName(saved.Path)!;
        var outside = Directory.CreateDirectory(Path.Combine(fixture.Root, "outside")).FullName;
        File.WriteAllBytes(Path.Combine(outside, "secret.png"), Png);
        var fileLink = Path.Combine(folder, "link.png");
        var folderLink = Path.Combine(folder, "linked");
        try
        {
            File.CreateSymbolicLink(fileLink, Path.Combine(outside, "secret.png"));
            Directory.CreateSymbolicLink(folderLink, outside);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Inconclusive("Creating symbolic links requires platform permission.");
        }

        var offset = await fixture.AddUserAsync("Look", new PromptImageAttachmentReference("File link", fileLink, "image/png"),
            new PromptImageAttachmentReference("Through a folder link", Path.Combine(folderLink, "secret.png"), "image/png"));
        Assert.AreEqual("outside_store", (await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, 0), default)).Status);
        Assert.AreEqual("outside_store", (await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, 1), default)).Status);

        // The folder itself replaced by a link: nothing below it is served.
        var moved = folder + ".moved";
        Directory.Delete(folderLink);
        File.Delete(fileLink);
        Directory.Move(folder, moved);
        Directory.CreateSymbolicLink(folder, moved);
        var direct = await fixture.AddUserAsync("Again", saved);
        Assert.AreEqual("outside_store", (await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, direct, 0), default)).Status);
        Directory.Delete(folder);
    }

    [TestMethod]
    public async Task Read_ServesTheImagesOfAnArchivedProjectsSession()
    {
        await using var fixture = await Fixture.CreateAsync();
        var offset = await fixture.AddUserAsync("Look", await fixture.SaveAsync(("First", Png)));
        var project = (await fixture.Catalog.LoadAsync()).Single();
        project.Archived = true;
        await fixture.Catalog.SaveAsync(project);
        Assert.IsTrue((await fixture.Catalog.LoadAsync()).Single().Archived);

        Assert.AreEqual("ok", (await fixture.Service.ReadAsync(new(Epoch, Fixture.SessionId, offset, 0), default)).Status);
    }

    [TestMethod]
    public async Task Read_ReportsAFullOrClosedHostAndAFailedRead()
    {
        var request = new PromptImageRequest(Epoch, "session", "12", 3);
        PromptImagesService Service(Func<string, long, int, CancellationToken, Task<PromptImageReadResult>> read) => new(read, Epoch);

        Assert.AreEqual("capacity", (await Service((_, _, _, _) => throw new InvalidOperationException()).ReadAsync(request, default)).Status);
        Assert.AreEqual("closed", (await Service((_, _, _, _) => throw new ObjectDisposedException("reads")).ReadAsync(request, default)).Status);
        Assert.AreEqual("closed", (await Service((_, _, _, _) => Task.FromException<PromptImageReadResult>(new ObjectDisposedException("reads"))).ReadAsync(request, default)).Status);
        // An admitted read that fails names no path.
        var failed = await Service((_, _, _, _) => Task.FromException<PromptImageReadResult>(new IOException(@"C:\secret\path"))).ReadAsync(request, default);
        Assert.AreEqual(new PromptImageResponse("read_failed", null, null), failed);
        Assert.AreEqual("read_failed", (await Service((_, _, _, _) => Task.FromResult(new PromptImageReadResult(PromptImageReadStatus.ReadFailed))).ReadAsync(request, default)).Status);
        Assert.AreEqual("read_failed", (await Service((_, _, _, _) => Task.FromResult(new PromptImageReadResult(PromptImageReadStatus.Ok))).ReadAsync(request, default)).Status);

        (string, long, int) seen = default;
        var served = await Service((session, offset, index, _) => { seen = (session, offset, index); return Task.FromResult(new PromptImageReadResult(PromptImageReadStatus.Ok, "image/png", Png)); })
            .ReadAsync(request, default);
        Assert.AreEqual(("session", 12L, 3), seen);
        Assert.AreEqual("ok", served.Status);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Service((_, _, _, _) => throw new AssertFailedException("A canceled request reached the read."))
            .ReadAsync(request, canceled.Token));
    }

    private static AgentContentCompletedEvent User(string content, JsonElement? details)
        => new(new("fixture"), Fixture.SessionId, DateTimeOffset.UnixEpoch, new AgentRunId("run"), AgentContentKind.User, "user:" + Guid.NewGuid().ToString("N"), null, content, details);

    private static AgentContentCompletedEvent ToolOutput(string content, JsonElement details)
        => new(new("fixture"), Fixture.SessionId, DateTimeOffset.UnixEpoch, new AgentRunId("run"), AgentContentKind.ToolOutput,
            "tool-output:" + Guid.NewGuid().ToString("N"), "call-1", content, details);

    // The details a session records for a tool call that returned an image it saved.
    private static JsonElement ToolResult(string imagePath)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("toolCallId", "call-1");
            writer.WriteString("toolName", "view_image");
            writer.WriteStartObject("arguments");
            writer.WriteString("path", "shot.png");
            writer.WriteEndObject();
            writer.WriteStartObject("result");
            writer.WriteBoolean("success", true);
            writer.WriteStartArray("items");
            writer.WriteStartObject();
            writer.WriteString("$type", "text");
            writer.WriteString("value", "Viewed image shot.png.");
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("$type", "localImage");
            writer.WriteString("path", imagePath);
            writer.WriteString("mediaType", "image/png");
            writer.WriteString("displayName", "shot.png");
            writer.WriteNumber("width", 640);
            writer.WriteNumber("height", 480);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    // The details a prompt records: its input items, each with the fields its kind has.
    private static JsonElement Items(params (string Type, string Value, string? DisplayName, string? MediaType)[] items)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");
            foreach (var item in items)
            {
                writer.WriteStartObject();
                writer.WriteString("$type", item.Type);
                writer.WriteString(item.Type == "text" ? "text" : "path", item.Value);
                if (item.DisplayName is not null) writer.WriteString("displayName", item.DisplayName);
                if (item.MediaType is not null) writer.WriteString("mediaType", item.MediaType);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public const string SessionId = "session-1";
        private static readonly DateTimeOffset Created = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        private readonly FileSystemAgentSessionStore _store;
        private readonly SessionViewDescriptor _session;

        private Fixture(string root, ProjectCatalog catalog, FileSystemAgentSessionStore store, SessionViewDescriptor session, OwnedSessionWorkspace reads)
        {
            Root = root;
            Catalog = catalog;
            _store = store;
            _session = session;
            Reads = reads;
            Service = new PromptImagesService(reads, Epoch);
        }

        public string Root { get; }

        public ProjectCatalog Catalog { get; }

        public OwnedSessionWorkspace Reads { get; }

        public PromptImagesService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "codealta-prompt-images-" + Guid.NewGuid().ToString("N"));
            var projectPath = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var catalog = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await catalog.UpsertFromPathAsync(projectPath);
            var journals = new SessionViewJournalStore(catalog.Options);
            var store = journals.CreateSessionStore();
            var session = new SessionViewDescriptor
            {
                SessionId = SessionId, Kind = SessionViewKind.ProjectSession, ProjectRef = project.Id, ProviderId = "fixture", ProviderKey = "fixture",
                WorkingDirectory = projectPath, Title = "Images", CreatedAt = Created, UpdatedAt = Created,
            };
            await journals.EnsureHeaderAsync(session);
            await store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = SessionId, ProviderId = new("fixture"), ProviderKey = "fixture", WorkingDirectory = projectPath,
                Title = "Images", CreatedAt = Created, UpdatedAt = Created,
            });
            return new(root, catalog, store, session, new OwnedSessionWorkspace(catalog, journals));
        }

        /// <summary>Saves images the way a sent prompt does; each has the name of its title.</summary>
        public async Task<PromptImageAttachmentReference[]> SaveAsync(params (string Title, byte[] Bytes)[] images)
            => [.. await new PromptImageAttachmentStore(Catalog.Options).SaveAsync(_session,
                images.Select((image, index) => new PromptImageAttachment($"image{index}", image.Title, image.Bytes, "image/png", ".png")).ToArray())];

        /// <summary>Appends a user message that records the images, and returns its journal offset.</summary>
        public Task<string> AddUserAsync(string text, params PromptImageAttachmentReference[] images)
            => AddAsync(User(string.Join(Environment.NewLine, images.Select(image => $"Local image ({image.Title}): {image.Path}").Prepend(text)),
                Items([("text", text, null, null), .. images.Select(image => ("localImage", image.Path, (string?)image.Title, (string?)image.MediaType))])));

        public async Task<string> AddAsync(AgentContentCompletedEvent message)
        {
            await _store.AppendEventsAsync("fixture", "fixture", SessionId, [message]);
            var page = await Reads.ReadTimelinePageAsync(SessionId, null, default);
            return page.Entries.Single(entry => entry.Event is AgentContentCompletedEvent content && content.ContentId == message.ContentId)
                .Offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public async ValueTask DisposeAsync()
        {
            await Reads.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }
}
