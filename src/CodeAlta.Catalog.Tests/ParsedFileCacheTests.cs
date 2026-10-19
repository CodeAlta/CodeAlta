namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ParsedFileCacheTests
{
    private static readonly DateTime Written = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [TestMethod]
    public void AFileThatDidNotChange_IsParsedOnce_OnceItsLastWriteIsOldEnough()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Written);
        fixture.Clock.Now = Written + ParsedFileCache<string>.RacyWindow + TimeSpan.FromSeconds(1);

        Assert.AreEqual("alpha#1", fixture.Read());
        Assert.AreEqual("alpha#1", fixture.Read());
        Assert.AreEqual("alpha#1", fixture.Read());
        Assert.AreEqual(1, fixture.Parses, "The stamp stands for the text: the file is not parsed again.");
    }

    [TestMethod]
    public void AFileChangedWithTheSameLengthAndTime_IsReadAgain_WhileItsLastWriteIsRecent()
    {
        using var fixture = new Fixture();
        // A file system whose clock counts in steps gives two writes the same time.
        fixture.Write("alpha", Written);
        fixture.Clock.Now = Written + TimeSpan.FromMilliseconds(500);
        Assert.AreEqual("alpha#1", fixture.Read());

        fixture.Write("bravo", Written);
        Assert.AreEqual("bravo#2", fixture.Read(), "A recent stamp is not trusted: the text is compared.");

        Assert.AreEqual("bravo#2", fixture.Read(), "The same text gives the same value, without a parse.");
        Assert.AreEqual(2, fixture.Parses);
    }

    [TestMethod]
    public void ARecentStamp_IsTrustedOnceTheTextIsReadAgainAfterTheWindow()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Written);
        fixture.Clock.Now = Written;
        Assert.AreEqual("alpha#1", fixture.Read());

        // Read again past the window: the same text, now kept as settled.
        fixture.Clock.Now = Written + ParsedFileCache<string>.RacyWindow + TimeSpan.FromSeconds(1);
        Assert.AreEqual("alpha#1", fixture.Read());

        // The text changes under the same stamp, which no writer can do after the window: the stamp is trusted.
        fixture.Write("bravo", Written);
        Assert.AreEqual("alpha#1", fixture.Read());
        Assert.AreEqual(1, fixture.Parses);
    }

    [TestMethod]
    public void AStampSeenSettledLater_DoesNotSettleATextReadWhileItWasRecent()
    {
        using var fixture = new Fixture();
        fixture.Write("alpha", Written);
        fixture.Clock.Now = Written;
        Assert.AreEqual("alpha#1", fixture.Read());

        // A write in the same step of the clock, seen only after the window: the text is compared, not trusted.
        fixture.Write("bravo", Written);
        fixture.Clock.Now = Written + ParsedFileCache<string>.RacyWindow + TimeSpan.FromSeconds(1);
        Assert.AreEqual("bravo#2", fixture.Read());
    }

    [TestMethod]
    public void AChangeOfLengthOrTime_IsSeen()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = Written + TimeSpan.FromHours(1);
        fixture.Write("alpha", Written);
        Assert.AreEqual("alpha#1", fixture.Read());

        fixture.Write("alpha2", Written);
        Assert.AreEqual("alpha2#2", fixture.Read());

        fixture.Write("alpha3", Written + TimeSpan.FromSeconds(1));
        Assert.AreEqual("alpha3#3", fixture.Read());
    }

    [TestMethod]
    public void AChangeOfPermissions_IsSeen()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows keeps access rights apart from the stamp.");
            return;
        }

        using var fixture = new Fixture();
        fixture.Clock.Now = Written + TimeSpan.FromHours(1);
        fixture.Write("alpha", Written);
        Assert.AreEqual("alpha#1", fixture.Read());

        var mode = File.GetUnixFileMode(fixture.Path);
        File.SetUnixFileMode(fixture.Path, mode & ~UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        File.SetLastWriteTimeUtc(fixture.Path, Written);
        Assert.AreEqual("alpha#1", fixture.Read(), "The same text gives the same value.");
        Assert.AreEqual(1, fixture.Parses);
        File.WriteAllText(fixture.Path, "bravo");
        File.SetLastWriteTimeUtc(fixture.Path, Written);
        File.SetUnixFileMode(fixture.Path, mode);
        Assert.AreEqual("bravo#2", fixture.Read(), "The text of a file whose permissions changed is read again.");
    }

    [TestMethod]
    public void AParseThatThrows_IsNotKept_AndThrowsAtEachRead()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = Written + TimeSpan.FromHours(1);
        fixture.Write("alpha", Written);
        Assert.AreEqual("alpha#1", fixture.Read());

        fixture.Write("invalid", Written);
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Read());
        Assert.ThrowsExactly<InvalidDataException>(() => fixture.Read(), "A failure is never kept as a success.");
        Assert.AreEqual(3, fixture.Parses, "Each read of the file parses it again.");

        fixture.Write("charlie", Written);
        Assert.AreEqual("charlie#4", fixture.Read());
    }

    [TestMethod]
    public void AMissingFile_ThrowsLikeARead()
    {
        using var fixture = new Fixture();
        Assert.ThrowsExactly<FileNotFoundException>(() => fixture.Read());
        fixture.Write("alpha", Written);
        Assert.AreEqual("alpha#1", fixture.Read());
        File.Delete(fixture.Path);
        Assert.ThrowsExactly<FileNotFoundException>(() => fixture.Read(), "A file deleted is not read from what was kept.");
    }

    [TestMethod]
    public void Invalidate_ForgetsEveryFile_EvenWithTheSameStamp()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = Written + TimeSpan.FromHours(1);
        fixture.Write("alpha", Written);
        Assert.AreEqual("alpha#1", fixture.Read());

        // Same length, same time, long ago: only the owner's own write can tell.
        fixture.Write("bravo", Written);
        fixture.Cache.Invalidate();
        Assert.AreEqual("bravo#2", fixture.Read());
    }

    [TestMethod]
    public async Task ReadAsync_KeepsWhatReadKeeps()
    {
        using var fixture = new Fixture();
        fixture.Clock.Now = Written + TimeSpan.FromHours(1);
        fixture.Write("alpha", Written);
        Assert.AreEqual("alpha#1", await fixture.Cache.ReadAsync(fixture.Path, fixture.Parse, CancellationToken.None));
        Assert.AreEqual("alpha#1", fixture.Read());
        Assert.AreEqual(1, fixture.Parses);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        fixture.Write("bravo", Written + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Cache.ReadAsync(fixture.Path, fixture.Parse, canceled.Token));
    }

    [TestMethod]
    public void ReadsSideBySideWithWrites_AlwaysGiveAValueOfATextTheFileHad()
    {
        using var fixture = new Fixture(TimeProvider.System);
        File.WriteAllText(fixture.Path, "value-0000");
        var written = new HashSet<string> { "value-0000" };
        var failures = 0;
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var value = fixture.Cache.Read(fixture.Path, static (_, content) => content);
                    // A text being written may be read half way (the write truncates first); a whole one is one that was written.
                    bool known;
                    lock (written) known = written.Contains(value);
                    if (value.Length == "value-0000".Length && !known) Interlocked.Increment(ref failures);
                }
                catch (IOException)
                {
                }
            }
        })).ToArray();
        for (var index = 1; index <= 200; index++)
        {
            var text = $"value-{index:0000}";
            lock (written) written.Add(text);
            File.WriteAllText(fixture.Path, text);
            fixture.Cache.Invalidate();
        }

        stop.Cancel();
        Task.WaitAll(readers);
        Assert.AreEqual(0, failures);
        Assert.AreEqual("value-0200", fixture.Cache.Read(fixture.Path, static (_, content) => content), "The last write is what is read after it.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-parsed-file-cache-" + Guid.NewGuid().ToString("N"));

        public Fixture()
            : this(null)
        {
        }

        public Fixture(TimeProvider? time)
        {
            Directory.CreateDirectory(_root);
            Path = System.IO.Path.Combine(_root, "file.txt");
            Cache = new ParsedFileCache<string>(time ?? Clock);
            Parse = (_, content) =>
            {
                Parses++;
                if (content == "invalid") throw new InvalidDataException("Invalid.");
                return $"{content}#{Parses}";
            };
        }

        public ManualClock Clock { get; } = new();

        public ParsedFileCache<string> Cache { get; }

        public Func<string, string, string> Parse { get; }

        public string Path { get; }

        public int Parses { get; private set; }

        public void Write(string text, DateTime lastWriteUtc)
        {
            File.WriteAllText(Path, text);
            File.SetLastWriteTimeUtc(Path, lastWriteUtc);
        }

        public string Read() => Cache.Read(Path, Parse);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTime Now { get; set; } = Written;

        public override DateTimeOffset GetUtcNow() => new(Now);
    }
}
