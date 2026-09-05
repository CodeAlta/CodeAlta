namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class PromptImageAttachmentStoreTests
{
    [TestMethod]
    public async Task SaveAsync_SameNamePreservesExistingFilesAndUsesCollisionSuffixes()
    {
        using var temp = new AttachmentDirectory();
        var store = temp.CreateStore();
        var image = Image();
        var directory = store.GetAttachmentDirectory(Session());
        Directory.CreateDirectory(directory);
        var original = Path.Combine(directory, "20260905123456789-01-Screen shot-12345678.png");
        await File.WriteAllBytesAsync(original, [9]);

        var first = (await store.SaveAsync(Session(), [image]))[0];
        var second = (await store.SaveAsync(Session(), [image]))[0];

        Assert.AreEqual(Path.Combine(temp.Root, "sessions", "2026", "04", "26", "session-one.attachments"), directory);
        Assert.AreEqual(original[..^4] + "-2.png", first.Path);
        Assert.AreEqual(original[..^4] + "-3.png", second.Path);
        CollectionAssert.AreEqual(new byte[] { 9 }, await File.ReadAllBytesAsync(original));
        CollectionAssert.AreEqual(image.Bytes, await File.ReadAllBytesAsync(first.Path));
        Assert.AreEqual(image.Title, first.Title);
        Assert.AreEqual(image.MediaType, first.MediaType);
    }

    [TestMethod]
    public void GetAttachmentDirectory_DefaultDateAndSanitizationStayInShard()
    {
        using var temp = new AttachmentDirectory();
        var store = temp.CreateStore();
        Assert.AreEqual(Path.Combine(temp.Root, "sessions", "2026", "09", "05", "a_b_c.attachments"),
            store.GetAttachmentDirectory(new SessionViewDescriptor { SessionId = "../a\\b/c\n" }));
        Assert.ThrowsExactly<ArgumentException>(() => store.GetAttachmentDirectory(new SessionViewDescriptor { SessionId = "../\\" }));
    }

    [TestMethod]
    public async Task SaveAsync_RejectsMalformedSessionComponentsBeforeWriting()
    {
        using var temp = new AttachmentDirectory();
        var store = temp.CreateStore();
        foreach (var id in new[] { null, "", " ", ".", "..", "/\\\0", new string('a', 201) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new SessionViewDescriptor { SessionId = id! }, [Image()]));
            Assert.IsFalse(Directory.Exists(temp.Root));
        }
    }

    [TestMethod]
    [DataRow("../a\\b/c\n:d", "a_b_c__d")]
    [DataRow("...", "image")]
    [DataRow("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuv")]
    public async Task SaveAsync_SanitizesAndTruncatesFilenameWithoutChangingDisplayTitle(string title, string expected)
    {
        using var temp = new AttachmentDirectory();
        var reference = (await temp.CreateStore().SaveAsync(Session(), [Image() with { Title = title }]))[0];
        Assert.AreEqual($"20260905123456789-01-{expected}-12345678.png", Path.GetFileName(reference.Path));
        Assert.AreEqual(title, reference.Title);
    }

    [TestMethod]
    public async Task SaveAsync_PrevalidatesWholeBatchBeforeCreatingFiles()
    {
        using var temp = new AttachmentDirectory();
        var store = temp.CreateStore();
        var valid = Image();
        PromptImageAttachment[] invalid =
        [
            valid with { Id = "" }, valid with { Id = "../evil" }, valid with { Id = "abc\\def" },
            valid with { Id = "12345678/ignored-tail" }, valid with { Id = "a\nb" },
            valid with { Title = null! }, valid with { Title = " " },
            valid with { Bytes = null! }, valid with { Bytes = [] },
            valid with { MediaType = "" }, valid with { MediaType = "text/plain" },
            valid with { MediaType = "image/png\r\n" },
            valid with { FileExtension = "." }, valid with { FileExtension = "../png" },
            valid with { FileExtension = ".png\\evil" }, valid with { FileExtension = ".png/evil" },
            valid with { FileExtension = ".png\0" }, valid with { FileExtension = ".png:evil" },
            valid with { FileExtension = ".png\n" }, valid with { FileExtension = "\tpng" },
            valid with { FileExtension = ".png.exe" }, valid with { FileExtension = new string('a', 40) },
            null!,
        ];
        foreach (var image in invalid)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Session(), [valid, image]));
            Assert.IsFalse(Directory.Exists(Path.Combine(temp.Root, "sessions")));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SaveAsync_PartialFailureOrCancellationCleansOnlyThisCallsFiles(bool cancel)
    {
        using var temp = new AttachmentDirectory();
        using var cancellation = new CancellationTokenSource();
        var prior = (await temp.CreateStore().SaveAsync(Session(), [Image()]))[0];
        var writes = 0;
        var store = temp.CreateStore(async (stream, bytes, token) =>
        {
            await stream.WriteAsync(bytes[..1], token);
            if (++writes == 2)
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                throw new IOException("Injected mid-write failure, not a collision.");
            }

            await stream.WriteAsync(bytes[1..], token);
        });

        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => store.SaveAsync(Session(), [Image(), Image()], cancellation.Token));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => store.SaveAsync(Session(), [Image(), Image()]));
        }

        Assert.AreEqual(2, writes);
        CollectionAssert.AreEqual(new[] { prior.Path }, Directory.GetFiles(Path.GetDirectoryName(prior.Path)!));
        CollectionAssert.AreEqual(Image().Bytes, await File.ReadAllBytesAsync(prior.Path));
    }

    [TestMethod]
    public async Task SaveAsync_PreCancelledDoesNotCreateDirectory()
    {
        using var temp = new AttachmentDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => temp.CreateStore().SaveAsync(Session(), [Image()], cancellation.Token));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Root, "sessions")));
    }

    [TestMethod]
    public async Task SaveAsync_BlockedDirectoryFailsWithoutRetryingOrDeletingBlocker()
    {
        using var temp = new AttachmentDirectory();
        var store = temp.CreateStore();
        var directory = store.GetAttachmentDirectory(Session());
        Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        await File.WriteAllTextAsync(directory, "owned by someone else");
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Session(), [Image()]));
        Assert.AreEqual("owned by someone else", await File.ReadAllTextAsync(directory));
    }

    private static PromptImageAttachment Image() => new("123456789abcdef", "Screen shot", [1, 2, 3], "image/png", "png");

    private static SessionViewDescriptor Session() => new()
    {
        SessionId = "session-one",
        CreatedAt = new DateTimeOffset(2026, 4, 27, 0, 30, 0, TimeSpan.FromHours(2)),
    };

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 5, 12, 34, 56, 789, TimeSpan.Zero);
    }

    private sealed class AttachmentDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"CodeAlta.Attachments.Tests.{Guid.NewGuid():N}");

        public PromptImageAttachmentStore CreateStore(Func<Stream, ReadOnlyMemory<byte>, CancellationToken, Task>? write = null)
            => new(new CatalogOptions { GlobalRoot = Root }, new FixedClock(), write);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
