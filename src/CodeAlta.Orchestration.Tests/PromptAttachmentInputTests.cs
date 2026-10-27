using System.Text.Json.Nodes;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class PromptAttachmentInputTests
{
    [TestMethod]
    public async Task MaterializeAsync_SavesAnImageForAModelThatReadsImages_AndAFileForAnyModel()
    {
        using var root = new TempRoot();
        var png = OwnedPromptImageTests.Png(1, 1);
        var path = Attachments(root,
            new("photo.png", "image/png", Convert.ToBase64String(png)),
            new("notes.txt", "text/plain", Convert.ToBase64String("hello"u8.ToArray())));
        var converted = false;
        var documents = new AttachedDocumentConverter(null, (_, _, _) => { converted = true; return Task.FromResult(0); });

        var items = await PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog, path, Model(images: true), documents, CancellationToken.None);

        Assert.IsFalse(converted, "A text file is not converted.");
        Assert.HasCount(2, items);
        var image = (AgentInputItem.LocalImage)items[0];
        Assert.AreEqual("photo.png", image.DisplayName);
        Assert.AreEqual("image/png", image.MediaType);
        CollectionAssert.AreEqual(png, await File.ReadAllBytesAsync(image.Path));
        var file = (AgentInputItem.LocalFile)items[1];
        Assert.AreEqual("notes.txt", file.DisplayName);
        Assert.AreEqual("text/plain", file.MediaType);
        Assert.AreEqual("hello", await File.ReadAllTextAsync(file.Path));
    }

    [TestMethod]
    public async Task MaterializeAsync_RefusesAnImageAModelDoesNotRead_AndWritesNothing()
    {
        using var root = new TempRoot();
        var path = Attachments(root, ("photo.png", "image/png", Convert.ToBase64String(OwnedPromptImageTests.Png(1, 1))));

        var error = await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog, path, Model(images: false), CancellationToken.None));

        Assert.AreEqual("This model does not read images.", error.Message);
        Assert.IsFalse(Directory.Exists(Path.Combine(root.Path, "sessions")));
    }

    [TestMethod]
    public async Task MaterializeAsync_RefusesAFileThatIsNotAttachments()
    {
        using var root = new TempRoot();
        var missing = Path.Combine(root.Path, "missing.json");
        var empty = Path.Combine(root.Path, "empty.json");
        await File.WriteAllTextAsync(empty, "{}");

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog, missing, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog, empty, null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog,
            Attachments(root, ("a/b.txt", "text/plain", Convert.ToBase64String("hi"u8.ToArray()))), null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog,
            Attachments(root, ("notes.txt", "text/plain", "%%%%")), null, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog,
            Attachments(root, ("photo.png", "image/png", Convert.ToBase64String(OwnedPromptImageTests.Png(1, 1)))), null, CancellationToken.None));
    }

    [TestMethod]
    public async Task MaterializeAsync_ConvertsOfficeDocumentsAlways_AndPdfsOnlyWhenTheModelDoesNotReadThem()
    {
        using var root = new TempRoot();
        byte[] pdf = "%PDF-1.4\n%%EOF\n"u8.ToArray(), docx = [0x50, 0x4B, 0x03, 0x04, 1, 2, 3];
        var path = Attachments(root,
            ("scan.pdf", "application/pdf", Convert.ToBase64String(pdf)),
            ("plan.docx", "application/octet-stream", Convert.ToBase64String(docx)));
        var converted = new List<string>();
        var documents = new AttachedDocumentConverter("dotnet", (start, _, _) =>
        {
            converted.Add(Path.GetFileName(start.ArgumentList[start.ArgumentList.IndexOf("--") + 1]));
            return Task.FromResult(0);
        });

        await PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog, path, Model(images: false, pdf: true), documents, CancellationToken.None);
        Assert.HasCount(1, converted, "A model that reads PDFs still gets a Word document converted.");
        StringAssert.EndsWith(converted[0], ".docx");

        converted.Clear();
        var again = Attachments(root,
            ("scan.pdf", "application/pdf", Convert.ToBase64String(pdf)),
            ("plan.docx", "application/octet-stream", Convert.ToBase64String(docx)));
        await PromptAttachmentInput.MaterializeAsync(Session(), root.Catalog, again, Model(images: false, pdf: false), documents, CancellationToken.None);
        Assert.HasCount(2, converted);
        StringAssert.EndsWith(converted[0], ".pdf");
        StringAssert.EndsWith(converted[1], ".docx");
    }

    private static AgentModelInfo Model(bool images, bool pdf = false)
        => new("m", Capabilities: new Dictionary<string, object?> { ["supportsImageInput"] = images, ["supportsPdfInput"] = pdf });

    private static SessionViewDescriptor Session() => new()
    {
        SessionId = "session-one",
        CreatedAt = new DateTimeOffset(2026, 4, 27, 0, 30, 0, TimeSpan.Zero),
    };

    private static string Attachments(TempRoot root, params (string Title, string MediaType, string Base64)[] files)
    {
        var path = Path.Combine(root.Path, "attachments.json");
        File.WriteAllText(path, new JsonArray(files.Select(static file => new JsonObject
        {
            ["title"] = file.Title, ["mediaType"] = file.MediaType, ["base64"] = file.Base64,
        }).ToArray<JsonNode>()).ToJsonString());
        return path;
    }

    private sealed class TempRoot : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alta-attach-" + Guid.NewGuid().ToString("N"));

        internal CatalogOptions Catalog => new() { GlobalRoot = Path };

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
