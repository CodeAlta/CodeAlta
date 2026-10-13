using CodeAlta.Catalog.Bookmarks;

namespace CodeAlta.Catalog.Tests;

/// <summary>The bookmarks in the application database: what is kept of one, what names it, and what tells that they changed.</summary>
[TestClass]
public sealed class BookmarkServiceTests
{
    private static readonly BookmarkTarget Session = new("session-1", "The first session", "project-1");
    private static readonly DateTimeOffset Written = new(2026, 10, 1, 8, 30, 0, TimeSpan.FromHours(2));

    [TestMethod]
    public async Task TheFirstUse_MakesTheTable_AndRecordsItsVersion()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var service = new BookmarkService(database, new FakeJournal());

        Assert.IsNull(service.BookmarkedSessionIds, "Nothing is known before the bookmarks were read.");
        Assert.IsEmpty(await service.ListAsync());

        Assert.AreEqual(1, await database.GetVersionAsync("bookmarks"));
        CollectionAssert.AreEqual(new[] { "bookmarks_item" }, (await database.ListTablesAsync("bookmarks")).ToArray());
        Assert.IsEmpty(service.BookmarkedSessionIds!);
    }

    [TestMethod]
    public async Task ASession_IsBookmarkedOnce_AndAnotherInstanceReadsIt()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var clock = new Clock(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
        var service = new BookmarkService(database, new FakeJournal(), clock);
        var changes = 0;
        service.Changed += () => changes++;

        var added = await service.AddSessionAsync(Session, "  Read this again  ", createdBy: null);

        Assert.AreEqual((BookmarkStatus.Ok, true), (added.Status, added.Created));
        var bookmark = added.Bookmark!;
        Assert.AreEqual((BookmarkKinds.Session, "session-1", "The first session", "project-1", "Read this again"),
            (bookmark.Kind, bookmark.SessionId, bookmark.SessionTitle, bookmark.ProjectId, bookmark.Label));
        Assert.AreEqual(clock.GetUtcNow(), bookmark.CreatedAt);
        Assert.IsNull(bookmark.Role);
        Assert.IsNull(bookmark.Offset);
        Assert.AreEqual(1, changes);

        // The session is bookmarked: asking again answers with the bookmark there is, and changes nothing.
        var again = await service.AddSessionAsync(Session with { Title = "Renamed since" }, "Another name", createdBy: "agent-session");
        Assert.AreEqual((BookmarkStatus.Ok, false, bookmark.Id, "Read this again"), (again.Status, again.Created, again.Bookmark!.Id, again.Bookmark.Label));
        Assert.AreEqual(1, changes);

        var read = await new BookmarkService(database, new FakeJournal()).ListAsync();
        Assert.AreEqual(bookmark, read.Single());
        CollectionAssert.AreEqual(new[] { "session-1" }, service.BookmarkedSessionIds!.ToArray());
    }

    [TestMethod]
    public async Task AMessage_IsKeptWithWhatNamesIt_AndIsBookmarkedOnce()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var journal = new FakeJournal();
        journal.Put("session-1", new("run-1", "assistant:1", BookmarkRoles.Assistant, Written, 420, "  The   plan\n\nhas *three* steps.\t"));
        journal.Put("session-1", new("run-1", "user:1", BookmarkRoles.User, Written, 100, "What is the plan?"));
        var service = new BookmarkService(database, journal);

        var added = await service.AddMessageAsync(Session, 420, label: null, createdBy: "agent-session");

        var bookmark = added.Bookmark!;
        Assert.AreEqual((BookmarkStatus.Ok, true), (added.Status, added.Created));
        Assert.AreEqual((BookmarkKinds.Message, BookmarkRoles.Assistant, "run-1", "assistant:1", 420L, Written, "agent-session"),
            (bookmark.Kind, bookmark.Role, bookmark.RunId, bookmark.ContentId, bookmark.Offset!.Value, bookmark.MessageAt!.Value, bookmark.CreatedBy));
        Assert.AreEqual("The plan has *three* steps.", bookmark.Excerpt);
        Assert.IsNull(bookmark.Label);

        // The same message at another offset is the same message; the session and another message are bookmarks of their own.
        journal.Move("session-1", "assistant:1", 9000);
        Assert.AreEqual((false, bookmark.Id), ((await service.AddMessageAsync(Session, 9000, "Again", null)).Created, (await service.AddMessageAsync(Session, 9000, null, null)).Bookmark!.Id));
        Assert.IsTrue((await service.AddMessageAsync(Session, 100, null, null)).Created);
        Assert.IsTrue((await service.AddSessionAsync(Session, null, null)).Created);
        Assert.HasCount(3, await service.ListAsync());
        Assert.HasCount(1, service.BookmarkedSessionIds!);
    }

    [TestMethod]
    public async Task AMessage_IsFoundByWhatNamesIt_WhenAnotherOneIsAtItsOffset()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var journal = new FakeJournal();
        journal.Put("session-1", new("run-1", "assistant:1", BookmarkRoles.Assistant, Written, 420, "The bookmarked answer"));
        var service = new BookmarkService(database, journal);
        var bookmark = (await service.AddMessageAsync(Session, 420, null, null)).Bookmark!;
        var changes = 0;
        service.Changed += () => changes++;

        // The journal was rewritten: the message is elsewhere, and its old offset is the start of another record.
        journal.Move("session-1", "assistant:1", 900);
        journal.Put("session-1", new("run-1", "user:1", BookmarkRoles.User, Written, 420, "Another message"));

        var location = await service.ResolveAsync(bookmark.Id);
        Assert.AreEqual((BookmarkStatus.Ok, 900L), (location.Status, location.Offset!.Value));
        Assert.AreEqual(900L, location.Bookmark!.Offset);
        Assert.AreEqual(900L, (await new BookmarkService(database, journal).GetAsync(bookmark.Id))!.Offset, "The offset is kept as where to look first.");
        Assert.AreEqual("The bookmarked answer", (await service.ReadTextAsync(bookmark.Id, 100)).Text);
        Assert.AreEqual(0, changes, "Where a message is found is not a change of the bookmarks.");

        // The same content identifier under another run is another message.
        journal.Move("session-1", "assistant:1", 900, runId: "run-2");
        var gone = await service.ResolveAsync(bookmark.Id);
        Assert.AreEqual((BookmarkStatus.Gone, (long?)null), (gone.Status, gone.Offset));
        Assert.AreEqual(BookmarkStatus.NotFound, (await service.ResolveAsync("no-such-bookmark")).Status);
    }

    [TestMethod]
    public async Task WhatIsNotAMessage_IsNotBookmarked()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var journal = new FakeJournal();
        journal.Put("session-1", new("run-1", new string('c', 5000), BookmarkRoles.Assistant, Written, 10, "An identifier that cannot be kept"));
        var service = new BookmarkService(database, journal);

        Assert.AreEqual(BookmarkStatus.NotBookmarkable, (await service.AddMessageAsync(Session, 77, null, null)).Status, "No message starts at this offset.");
        Assert.AreEqual(BookmarkStatus.NotBookmarkable, (await service.AddMessageAsync(Session, 10, null, null)).Status);
        Assert.AreEqual(BookmarkStatus.SessionMissing, (await service.AddMessageAsync(new("no-journal", "Gone", null), 10, null, null)).Status);
        Assert.AreEqual(BookmarkStatus.Invalid, (await service.AddMessageAsync(Session, -1, null, null)).Status);
        Assert.AreEqual(BookmarkStatus.Invalid, (await service.AddSessionAsync(Session, new string('x', BookmarkService.MaximumLabelLength + 1), null)).Status);
        Assert.AreEqual(BookmarkStatus.Invalid, (await service.AddSessionAsync(Session, "two\nlines", null)).Status);
        Assert.AreEqual(BookmarkStatus.Invalid, (await service.AddSessionAsync(new(" padded ", "Title", null), null, null)).Status);
        Assert.AreEqual(BookmarkStatus.NotBookmarkable, (await service.AddTurnMessageAsync(Session, "4", BookmarkRoles.User, null, null)).Status);
        Assert.AreEqual(BookmarkStatus.Invalid, (await service.AddTurnMessageAsync(Session, "4", "tool", null, null)).Status);
        Assert.IsEmpty(await service.ListAsync());
    }

    [TestMethod]
    public async Task ABookmark_IsNamedAndRemoved_AndEachChangeIsTold()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var service = new BookmarkService(database, new FakeJournal());
        var id = (await service.AddSessionAsync(Session, null, null)).Bookmark!.Id;
        var other = (await service.AddSessionAsync(new("session-2", "The second session", null), null, null)).Bookmark!.Id;
        var changes = 0;
        service.Changed += () => changes++;

        var named = await service.RenameAsync(id, " The one to keep ");
        Assert.AreEqual((BookmarkStatus.Ok, "The one to keep"), (named.Status, named.Bookmark!.Label));
        Assert.AreEqual(1, changes);
        Assert.AreEqual(BookmarkStatus.Ok, (await service.RenameAsync(id, "The one to keep")).Status);
        Assert.AreEqual(1, changes, "A name that is the same is no change.");
        Assert.IsNull((await service.RenameAsync(id.ToUpperInvariant(), "   ")).Bookmark!.Label, "An empty name takes the name away.");
        Assert.AreEqual(2, changes);
        Assert.AreEqual(BookmarkStatus.Invalid, (await service.RenameAsync(id, new string('x', 201))).Status);
        Assert.AreEqual(BookmarkStatus.NotFound, (await service.RenameAsync("no-such-bookmark", "Name")).Status);

        var removed = await service.RemoveAsync(id);
        Assert.AreEqual((BookmarkStatus.Ok, id), (removed.Status, removed.Bookmark!.Id));
        Assert.AreEqual(3, changes);
        Assert.AreEqual(BookmarkStatus.NotFound, (await service.RemoveAsync(id)).Status);
        Assert.AreEqual(3, changes);
        Assert.AreEqual(other, (await new BookmarkService(database, new FakeJournal()).ListAsync()).Single().Id);
        Assert.IsNull(await service.GetAsync(id));
        CollectionAssert.AreEqual(new[] { "session-2" }, service.BookmarkedSessionIds!.ToArray());
    }

    [TestMethod]
    public async Task TheBookmarks_AreListedTheNewestFirst_UpToAsManyAsAreKept()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var clock = new Clock(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
        var service = new BookmarkService(database, new FakeJournal(), clock, maximum: 3);
        foreach (var number in new[] { 1, 2, 3 })
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.IsTrue((await service.AddSessionAsync(new("session-" + number, "Session " + number, null), null, null)).Created);
        }

        var refused = await service.AddSessionAsync(new("session-4", "Session 4", null), null, null);
        Assert.AreEqual(BookmarkStatus.Limit, refused.Status);
        Assert.IsNull(refused.Bookmark);
        // What is bookmarked already is still answered: nothing is added.
        Assert.AreEqual(BookmarkStatus.Ok, (await service.AddSessionAsync(new("session-2", "Session 2", null), null, null)).Status);
        CollectionAssert.AreEqual(new[] { "session-3", "session-2", "session-1" }, (await service.ListAsync()).Select(static bookmark => bookmark.SessionId).ToArray());

        Assert.AreEqual(BookmarkStatus.Ok, (await service.RemoveAsync((await service.ListAsync())[0].Id)).Status);
        Assert.IsTrue((await service.AddSessionAsync(new("session-4", "Session 4", null), null, null)).Created);
    }

    [TestMethod]
    public async Task TheBookmarksOfADeletedSession_AreRemovedTogether()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var journal = new FakeJournal();
        journal.Put("session-1", new("run-1", "user:1", BookmarkRoles.User, Written, 100, "A prompt"));
        var service = new BookmarkService(database, journal);
        await service.AddSessionAsync(Session, null, null);
        await service.AddMessageAsync(Session, 100, null, null);
        var kept = (await service.AddSessionAsync(new("session-2", "Kept", null), null, null)).Bookmark!;
        var changes = 0;
        service.Changed += () => changes++;

        Assert.AreEqual(2, await service.RemoveSessionAsync("session-1"));
        Assert.AreEqual(0, await service.RemoveSessionAsync("session-1"));

        Assert.AreEqual(1, changes);
        Assert.AreEqual(kept, (await new BookmarkService(database, journal).ListAsync()).Single());
    }

    [TestMethod]
    public async Task WhatASessionIsCalledNow_IsKeptForWhenItIsGone_WithoutTellingAChange()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var service = new BookmarkService(database, new FakeJournal());
        var id = (await service.AddSessionAsync(Session, null, null)).Bookmark!.Id;
        var changes = 0;
        service.Changed += () => changes++;

        await service.RememberSessionsAsync([new("session-1", "Renamed\nsince", null), new("session-9", "Not bookmarked", "project-9")]);

        Assert.AreEqual(0, changes);
        var read = (await new BookmarkService(database, new FakeJournal()).ListAsync()).Single();
        Assert.AreEqual((id, "Renamed since", (string?)null), (read.Id, read.SessionTitle, read.ProjectId));
    }

    [TestMethod]
    public async Task ADatabaseThatCameBackEmpty_HasNoBookmarks_AndTakesNewOnes()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "data", "alta.sqlite3");
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path });
        var service = new BookmarkService(database, new FakeJournal());
        await service.AddSessionAsync(Session, null, null);
        var changes = 0;
        service.Changed += () => changes++;

        // The file is gone under the application: the next use makes an empty one, as a new generation.
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" }) File.Delete(path + suffix);

        Assert.IsEmpty(await service.ListAsync());
        Assert.IsTrue(database.Generation > 0);
        Assert.AreEqual(1, changes, "Who lists the bookmarks is told that they are others.");
        Assert.IsEmpty(service.BookmarkedSessionIds!);

        var added = await service.AddSessionAsync(Session, null, null);
        Assert.IsTrue(added.Created, "The table is made again in the new file.");
        Assert.AreEqual(added.Bookmark, (await new BookmarkService(database, new FakeJournal()).ListAsync()).Single());
    }

    [TestMethod]
    public async Task ADatabaseRestoredFromACopy_HasTheBookmarksOfTheCopy()
    {
        using var temp = TempFolder.Create();
        var path = Path.Combine(temp.Path, "data", "alta.sqlite3");
        await using var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = path });
        var service = new BookmarkService(database, new FakeJournal());
        var old = (await service.AddSessionAsync(Session, null, null)).Bookmark!;
        Assert.IsNotNull(await database.BackupAsync());
        await service.AddSessionAsync(new("session-2", "Added after the copy", null), null, null);

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" }) File.Delete(path + suffix);

        Assert.AreEqual(old, (await service.ListAsync()).Single(), "The file is the copy: what was added since is lost, nothing else.");
        CollectionAssert.AreEqual(new[] { "session-1" }, service.BookmarkedSessionIds!.ToArray());
    }

    [TestMethod]
    public void AnExcerpt_IsOneLine_OfWholeCharacters()
    {
        Assert.AreEqual(string.Empty, BookmarkService.MakeExcerpt(" \r\n\t "));
        Assert.AreEqual("a b c", BookmarkService.MakeExcerpt("\n a \r\n\r\n b\u0000\tc "));
        var text = new string('x', BookmarkService.MaximumExcerptLength - 1) + "\U0001F600 and more";
        var excerpt = BookmarkService.MakeExcerpt(text);
        Assert.AreEqual(BookmarkService.MaximumExcerptLength - 1, excerpt.Length, "A pair that does not fit whole is left out.");
        Assert.AreEqual(BookmarkService.MaximumExcerptLength, BookmarkService.MakeExcerpt(new string('x', 5000)).Length);
        Assert.AreEqual("ab", BookmarkService.MakeExcerpt("a\ud83db"), "Half of a pair is not text.");
        var spaced = BookmarkService.MakeExcerpt(new string('y', BookmarkService.MaximumExcerptLength - 1) + " z");
        Assert.AreEqual(new string('y', BookmarkService.MaximumExcerptLength - 1), spaced, "No space ends an excerpt.");
    }

    [TestMethod]
    public void AName_IsTrimmed_AndRefusedWhenItIsNotOneLineOfText()
    {
        Assert.IsTrue(BookmarkService.TryNormalizeLabel(null, out var none));
        Assert.IsNull(none);
        Assert.IsTrue(BookmarkService.TryNormalizeLabel("  ", out none));
        Assert.IsNull(none);
        Assert.IsTrue(BookmarkService.TryNormalizeLabel(" Kept \U0001F600 ", out var kept));
        Assert.AreEqual("Kept \U0001F600", kept);
        Assert.IsTrue(BookmarkService.TryNormalizeLabel(new string('x', 200), out _));
        Assert.IsFalse(BookmarkService.TryNormalizeLabel(new string('x', 201), out _));
        Assert.IsFalse(BookmarkService.TryNormalizeLabel("a\tb", out _));
        Assert.IsFalse(BookmarkService.TryNormalizeLabel("a\ud83d", out _));
    }

    private static ApplicationDatabase CreateDatabase(TempFolder temp)
        => new(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3") });

    // A journal that holds the messages a test puts in it.
    private sealed class FakeJournal : IBookmarkJournal
    {
        private readonly Dictionary<string, List<BookmarkMessage>> _sessions = new(StringComparer.Ordinal);

        internal void Put(string sessionId, BookmarkMessage message)
        {
            if (!_sessions.TryGetValue(sessionId, out var messages)) _sessions[sessionId] = messages = [];
            messages.Add(message);
        }

        internal void Move(string sessionId, string contentId, long offset, string? runId = null)
        {
            var messages = _sessions[sessionId];
            var index = messages.FindIndex(message => message.ContentId == contentId);
            messages[index] = messages[index] with { Offset = offset, RunId = runId ?? messages[index].RunId };
        }

        public ValueTask<BookmarkJournalRead> ReadAsync(string sessionId, long offset, CancellationToken cancellationToken = default)
            => Answer(sessionId, message => message.Offset == offset);

        public ValueTask<BookmarkJournalRead> FindAsync(string sessionId, string? runId, string contentId, string role, CancellationToken cancellationToken = default)
            => Answer(sessionId, message => message.RunId == runId && message.ContentId == contentId && message.Role == role);

        public ValueTask<BookmarkJournalRead> FindInTurnAsync(string sessionId, string turn, string role, CancellationToken cancellationToken = default)
            => Answer(sessionId, message => message.RunId == turn && message.Role == role);

        private ValueTask<BookmarkJournalRead> Answer(string sessionId, Predicate<BookmarkMessage> wanted)
            => new(sessionId == "no-journal" ? new BookmarkJournalRead(BookmarkJournalStatus.SessionMissing, null)
                : _sessions.GetValueOrDefault(sessionId)?.Find(wanted) is { } message ? new(BookmarkJournalStatus.Found, message)
                : new(BookmarkJournalStatus.NotFound, null));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan by) => _now += by;
    }

    private sealed class TempFolder : IDisposable
    {
        private TempFolder(string path) => Path = path;

        public string Path { get; }

        public static TempFolder Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alta-bookmarks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TempFolder(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
