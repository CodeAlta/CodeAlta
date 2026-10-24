using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bookmarks;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The bookmarks as the application has them: the service the page lists, adds and follows them through, and the
/// `alta bookmark` commands of a session, over the same database and the journals of a host.
/// </summary>
[TestClass]
public sealed class BookmarkHostTests
{
    private const string Epoch = "5b6c1f0e-8d0c-4e0b-a1b2-9f1d5f6f7a11";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task ThePage_BookmarksASessionAndAMessage_AndListsThemTheNewestFirst()
    {
        await using var fixture = await Fixture.CreateAsync();
        var answer = await fixture.OffsetOfAsync("in-project", AgentContentKind.Assistant, "The plan has three steps.");

        var session = await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, "  Come back to this  "), default);
        var message = await fixture.Rpc.AddAsync(new(Epoch, "in-project", Text(answer), null), default);

        Assert.AreEqual("ok", session.Status);
        var first = session.Bookmark!;
        Assert.AreEqual(("session", "in-project", fixture.Project.Id, "Planning session", false, "Come back to this"),
            (first.Kind, first.SessionId, first.ProjectId, first.SessionTitle, first.SessionMissing, first.Label));
        Assert.AreEqual(((string?)null, (string?)null, (string?)null, (string?)null, (string?)null, (string?)null),
            (first.Role, first.Excerpt, first.RunId, first.ContentId, first.Offset, first.CreatedBy));
        Assert.AreEqual("ok", message.Status);
        var second = message.Bookmark!;
        Assert.AreEqual(("message", "assistant", "The plan has three steps.", "run-1", Text(answer)), (second.Kind, second.Role, second.Excerpt, second.RunId, second.Offset));
        // A long identifier of a provider is the compact one the entries of the timeline carry.
        Assert.AreEqual(RuntimeDisplayProjection.CompactIdentifier(Fixture.LongContentId), second.ContentId);
        StringAssert.StartsWith(second.ContentId, "~sha256:");
        Assert.IsNotNull(second.MessageAt);

        // What is bookmarked already answers with the bookmark there is.
        Assert.AreEqual(("ok", second.Id), ((await fixture.Rpc.AddAsync(new(Epoch, "in-project", Text(answer), "Another name"), default)).Status, second.Id));
        Assert.AreEqual(first.Id, (await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, null), default)).Bookmark!.Id);
        var chat = (await fixture.Rpc.AddAsync(new(Epoch, "chat", null, null), default)).Bookmark!;
        Assert.AreEqual(((string?)null, "A chat"), (chat.ProjectId, chat.SessionTitle));

        var listed = await fixture.Rpc.ListAsync(new(Epoch), default);
        Assert.AreEqual(("ok", false), (listed.Status, listed.Truncated));
        CollectionAssert.AreEqual(new[] { chat.Id, second.Id, first.Id }, listed.Bookmarks.Select(static row => row.Id).ToArray());
        Assert.AreEqual(second, listed.Bookmarks[1]);
        Assert.AreEqual(listed.Bookmarks[1], JsonSerializer.Deserialize(JsonSerializer.Serialize(listed.Bookmarks[1], DesktopJsonContext.Default.BookmarkRow), DesktopJsonContext.Default.BookmarkRow));
    }

    [TestMethod]
    public async Task ThePage_IsRefused_WhatIsNotARequestOfThisHost()
    {
        await using var fixture = await Fixture.CreateAsync();
        var tool = await fixture.OffsetOfAsync("in-project", static value => value is AgentActivityEvent);
        var stale = Guid.NewGuid().ToString("D");

        Assert.AreEqual("stale_epoch", (await fixture.Rpc.ListAsync(new(stale), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.AddAsync(new(stale, "in-project", null, null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.RenameAsync(new(null, "id", "Name"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.RemoveAsync(new(stale, "id"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Rpc.ResolveAsync(new(stale, "id"), default)).Status);
        Assert.IsFalse(await fixture.Rpc.WatchAsync(new(stale), default).GetAsyncEnumerator().MoveNextAsync());

        foreach (var request in new BookmarkAddRequest[]
        {
            new(Epoch, "", null, null), new(Epoch, " in-project", null, null), new(Epoch, new string('s', 257), null, null),
            new(Epoch, "in-project", "12a", null), new(Epoch, "in-project", "-1", null), new(Epoch, "in-project", "", null),
            new(Epoch, "in-project", new string('9', 20), null), new(Epoch, "in-project", null, new string('x', 201)), new(Epoch, "in-project", null, "two\nlines"),
        })
        {
            Assert.AreEqual("invalid_request", (await fixture.Rpc.AddAsync(request, default)).Status, request.ToString());
        }

        Assert.AreEqual("invalid_request", (await fixture.Rpc.RenameAsync(new(Epoch, "", "Name"), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.RenameAsync(new(Epoch, "id", new string('x', 201)), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.RemoveAsync(new(Epoch, new string('i', 65)), default)).Status);
        Assert.AreEqual("invalid_request", (await fixture.Rpc.ResolveAsync(new(Epoch, "a\tb"), default)).Status);

        Assert.AreEqual("not_found", (await fixture.Rpc.AddAsync(new(Epoch, "no-such-session", null, null), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Rpc.AddAsync(new(Epoch, "no-such-session", "0", null), default)).Status);
        Assert.AreEqual("not_bookmarkable", (await fixture.Rpc.AddAsync(new(Epoch, "in-project", Text(tool), null), default)).Status);
        Assert.AreEqual("not_bookmarkable", (await fixture.Rpc.AddAsync(new(Epoch, "in-project", "7", null), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Rpc.RenameAsync(new(Epoch, "no-such-bookmark", "Name"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Rpc.RemoveAsync(new(Epoch, "no-such-bookmark"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Rpc.ResolveAsync(new(Epoch, "no-such-bookmark"), default)).Status);
        Assert.IsEmpty((await fixture.Rpc.ListAsync(new(Epoch), default)).Bookmarks);

        // A launch without an owned host has no bookmarks.
        var none = new BookmarksService();
        Assert.AreEqual("unavailable", (await none.ListAsync(new(Epoch), default)).Status);
        Assert.AreEqual("unavailable", (await none.AddAsync(new(Epoch, "in-project", null, null), default)).Status);
        Assert.AreEqual("unavailable", (await none.RenameAsync(new(Epoch, "id", null), default)).Status);
        Assert.AreEqual("unavailable", (await none.RemoveAsync(new(Epoch, "id"), default)).Status);
        Assert.AreEqual("unavailable", (await none.ResolveAsync(new(Epoch, "id"), default)).Status);
        Assert.IsFalse(await none.WatchAsync(new(Epoch), default).GetAsyncEnumerator().MoveNextAsync());
    }

    [TestMethod]
    public async Task ThePage_NamesAndRemovesABookmark_AndIsToldOfEveryChange()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var stop = new CancellationTokenSource();
        await using var watch = fixture.Rpc.WatchAsync(new(Epoch), stop.Token).GetAsyncEnumerator();
        Assert.IsTrue(await watch.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.AreEqual(0, watch.Current.Revision, "The page lists once when it starts listening.");

        var added = (await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, null), default)).Bookmark!;
        Assert.IsTrue(await watch.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.AreEqual(1, watch.Current.Revision);

        var named = await fixture.Rpc.RenameAsync(new(Epoch, added.Id, " The plan "), default);
        Assert.AreEqual(("ok", "The plan", added.Id, false), (named.Status, named.Bookmark!.Label, named.Bookmark.Id, named.Bookmark.SessionMissing));
        Assert.IsTrue(await watch.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.IsNull((await fixture.Rpc.RenameAsync(new(Epoch, added.Id, ""), default)).Bookmark!.Label);
        Assert.IsTrue(await watch.MoveNextAsync().AsTask().WaitAsync(Patience));

        // A change made by an agent is told too.
        var byAgent = await fixture.One("alta.bookmark.added", ["bookmark", "add", "--session", "chat"]);
        Assert.IsTrue(await watch.MoveNextAsync().AsTask().WaitAsync(Patience));

        var removed = await fixture.Rpc.RemoveAsync(new(Epoch, added.Id), default);
        Assert.AreEqual(("ok", added.Id, "Planning session"), (removed.Status, removed.Bookmark!.Id, removed.Bookmark.SessionTitle));
        Assert.IsTrue(await watch.MoveNextAsync().AsTask().WaitAsync(Patience));
        Assert.AreEqual(Value(byAgent, "id"), (await fixture.Rpc.ListAsync(new(Epoch), default)).Bookmarks.Single().Id);
        await stop.CancelAsync();
    }

    [TestMethod]
    public async Task ThePage_IsToldWhereABookmarkLeadsNow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var answer = await fixture.OffsetOfAsync("in-project", AgentContentKind.Assistant, "The plan has three steps.");
        var later = await fixture.OffsetOfAsync("in-project", AgentContentKind.User, "And then?");
        var session = (await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, null), default)).Bookmark!;
        var message = (await fixture.Rpc.AddAsync(new(Epoch, "in-project", Text(answer), null), default)).Bookmark!;
        var second = (await fixture.Rpc.AddAsync(new(Epoch, "in-project", Text(later), null), default)).Bookmark!;

        Assert.AreEqual(new BookmarkResolveResponse("ok", "in-project", fixture.Project.Id, null), await fixture.Rpc.ResolveAsync(new(Epoch, session.Id), default));
        Assert.AreEqual(new BookmarkResolveResponse("ok", "in-project", fixture.Project.Id, Text(answer)), await fixture.Rpc.ResolveAsync(new(Epoch, message.Id), default));

        // The first line is a longer one and every record moved: the message is where it is now, and the bookmark says so from then on.
        await fixture.RewriteAsync("in-project", static lines => [lines[0].TrimEnd('\r') + "    ", .. lines.Skip(1)]);
        var moved = await fixture.Rpc.ResolveAsync(new(Epoch, message.Id), default);
        Assert.AreEqual(new BookmarkResolveResponse("ok", "in-project", fixture.Project.Id, Text(await fixture.OffsetOfAsync("in-project", AgentContentKind.Assistant, "The plan has three steps."))), moved);
        Assert.AreNotEqual(Text(answer), moved.Offset);
        Assert.AreEqual(moved.Offset, (await fixture.Rpc.ListAsync(new(Epoch), default)).Bookmarks.Single(row => row.Id == message.Id).Offset);

        // The session was rewound past the second turn: its message is gone, the bookmark and the session are not.
        await fixture.RewriteAsync("in-project", static lines => [.. lines.TakeWhile(static line => !line.Contains("\"runId\":\"run-2\"", StringComparison.Ordinal))]);
        Assert.AreEqual(new BookmarkResolveResponse("gone", "in-project", fixture.Project.Id, null), await fixture.Rpc.ResolveAsync(new(Epoch, second.Id), default));
        Assert.AreEqual("ok", (await fixture.Rpc.ResolveAsync(new(Epoch, message.Id), default)).Status);
        Assert.IsFalse((await fixture.Rpc.ListAsync(new(Epoch), default)).Bookmarks.Any(static row => row.SessionMissing));
    }

    [TestMethod]
    public async Task ABookmarkOfASessionThatIsGone_Stays_AndSaysSo()
    {
        await using var fixture = await Fixture.CreateAsync();
        var session = (await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, null), default)).Bookmark!;
        var message = (await fixture.Rpc.AddAsync(new(Epoch, "in-project", Text(await fixture.OffsetOfAsync("in-project", AgentContentKind.User, "And then?")), null), default)).Bookmark!;
        var chat = (await fixture.Rpc.AddAsync(new(Epoch, "chat", null, null), default)).Bookmark!;
        await fixture.Rpc.ListAsync(new(Epoch), default);

        // The journal is removed without the application being asked: nothing removes the bookmarks.
        Assert.IsTrue(await fixture.Store.DeleteSessionAsync("in-project"));

        var listed = await fixture.Rpc.ListAsync(new(Epoch), default);
        Assert.AreEqual("ok", listed.Status);
        var rows = listed.Bookmarks.ToDictionary(static row => row.Id);
        Assert.AreEqual((true, "Planning session", fixture.Project.Id), (rows[session.Id].SessionMissing, rows[session.Id].SessionTitle, rows[session.Id].ProjectId));
        Assert.AreEqual((true, "And then?"), (rows[message.Id].SessionMissing, rows[message.Id].Excerpt));
        Assert.IsFalse(rows[chat.Id].SessionMissing);
        Assert.AreEqual(new BookmarkResolveResponse("session_missing", "in-project", fixture.Project.Id, null), await fixture.Rpc.ResolveAsync(new(Epoch, session.Id), default));
        Assert.AreEqual("session_missing", (await fixture.Rpc.ResolveAsync(new(Epoch, message.Id), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, "Again"), default)).Status, "A session that is gone is not bookmarked.");

        var removed = await fixture.Rpc.RemoveAsync(new(Epoch, message.Id), default);
        Assert.AreEqual(("ok", true), (removed.Status, removed.Bookmark!.SessionMissing));
        Assert.HasCount(2, (await fixture.Rpc.ListAsync(new(Epoch), default)).Bookmarks);
    }

    [TestMethod]
    public async Task SessionsThatCannotBeListed_MakeNoBookmarkLookGone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var added = (await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, null), default)).Bookmark!;
        var blind = new BookmarksService(fixture.Bookmarks, static (_, _) => throw new InvalidOperationException("Owned workspace read capacity reached."), Epoch);

        var listed = await blind.ListAsync(new(Epoch), default);
        Assert.AreEqual(("read_failed", 0), (listed.Status, listed.Bookmarks.Count));
        Assert.AreEqual("write_failed", (await blind.AddAsync(new(Epoch, "chat", null, null), default)).Status);
        Assert.AreEqual("read_failed", (await blind.ResolveAsync(new(Epoch, added.Id), default)).Status);
        var named = await blind.RenameAsync(new(Epoch, added.Id, "Named all the same"), default);
        Assert.AreEqual(("ok", "Named all the same", false, "Planning session"), (named.Status, named.Bookmark!.Label, named.Bookmark.SessionMissing, named.Bookmark.SessionTitle));
        Assert.AreEqual(added.Id, (await fixture.Bookmarks.ListAsync()).Single().Id, "Nothing was removed.");

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Rpc.ListAsync(new(Epoch), canceled.Token));
    }

    [TestMethod]
    public async Task AList_HoldsNoMoreThanAnAnswerCan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var sessions = Enumerable.Range(0, 500).Select(static number => new BookmarkTarget("s" + number.ToString("D4", CultureInfo.InvariantCulture), new string('é', 256), null)).ToArray();
        foreach (var session in sessions)
        {
            Assert.IsTrue((await fixture.Bookmarks.AddSessionAsync(session, new string('é', 200), null)).Created);
        }

        // The host has none of these sessions: each row carries the title that was kept, which JSON writes at six bytes a character.
        var listed = await fixture.Rpc.ListAsync(new(Epoch), default);

        Assert.AreEqual(("ok", true), (listed.Status, listed.Truncated));
        Assert.IsTrue(listed.Bookmarks.Count is > 100 and < 500, listed.Bookmarks.Count.ToString(CultureInfo.InvariantCulture));
        Assert.IsTrue(listed.Bookmarks.All(static row => row.SessionMissing));
        Assert.AreEqual("s0499", listed.Bookmarks[0].SessionId, "The newest come first, and are the ones that stay.");
        Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(listed, DesktopJsonContext.Default.BookmarksListResponse).Length < 1024 * 1024);
    }

    [TestMethod]
    public async Task ASession_ListsShowsAddsNamesAndRemovesBookmarks()
    {
        await using var fixture = await Fixture.CreateAsync();

        // The session that calls is the one that is bookmarked, and the one that is recorded as the author.
        var own = await fixture.One("alta.bookmark.added", ["bookmark", "add", "--label", "This session"]);
        Assert.AreEqual(("session", "in-project", "Planning session", "This session", "True", "in-project", fixture.Project.Id, fixture.Project.Slug),
            (Value(own, "kind"), Value(own, "sessionId"), Value(own, "sessionTitle"), Value(own, "label"), Value(own, "created"), Value(own, "createdBy"), Value(own, "projectId"), Value(own, "project")));
        Assert.AreEqual("False", Value(await fixture.One("alta.bookmark.added", ["bookmark", "add"]), "created"));

        // A message is named by its turn: the prompt of the turn, or the last word of the assistant.
        var prompt = await fixture.One("alta.bookmark.added", ["bookmark", "add", "--turn", "2"]);
        Assert.AreEqual(("message", "user", "And then?", "run-2"), (Value(prompt, "kind"), Value(prompt, "role"), Value(prompt, "excerpt"), Value(prompt, "runId")));
        var answer = await fixture.One("alta.bookmark.added", ["bookmark", "add", "--turn", "run-1", "--role", "assistant", "--label", "The plan"]);
        Assert.AreEqual(("assistant", "The plan has three steps.", Fixture.LongContentId), (Value(answer, "role"), Value(answer, "excerpt"), Value(answer, "contentId")));
        var byOffset = await fixture.One("alta.bookmark.added", ["bookmark", "add", "--offset", Value(answer, "offset")!]);
        Assert.AreEqual((Value(answer, "id"), "False"), (Value(byOffset, "id"), Value(byOffset, "created")));
        var chat = await fixture.One("alta.bookmark.added", ["bookmark", "add", "--session", "chat"]);
        Assert.IsNull(Value(chat, "projectId"));

        // Without an option, the bookmarks of the project of the caller.
        var (_, mine, _) = await fixture.Run(["bookmark", "list"]);
        CollectionAssert.AreEqual(new[] { Value(answer, "id"), Value(prompt, "id"), Value(own, "id") }, Of(mine, "alta.bookmark").Select(static record => Value(record, "id")).ToArray());
        Assert.AreEqual(("3", "False"), (Value(Of(mine, "alta.bookmark.summary").Single(), "count"), Value(Of(mine, "alta.bookmark.summary").Single(), "truncated")));
        Assert.IsTrue(Of(mine, "alta.bookmark").All(static record => Value(record, "sessionMissing") == "False"));
        var (_, all, _) = await fixture.Run(["bookmark", "list", "--all", "--limit", "2"]);
        CollectionAssert.AreEqual(new[] { Value(chat, "id"), Value(answer, "id") }, Of(all, "alta.bookmark").Select(static record => Value(record, "id")).ToArray());
        Assert.AreEqual(("2", "True"), (Value(Of(all, "alta.bookmark.summary").Single(), "count"), Value(Of(all, "alta.bookmark.summary").Single(), "truncated")));
        Assert.AreEqual(Value(chat, "id"), Value(Of((await fixture.Run(["bookmark", "list", "--session", "chat"])).Records, "alta.bookmark").Single(), "id"));
        Assert.HasCount(3, Of((await fixture.Run(["bookmark", "list", "--project", fixture.Project.Slug], caller: fixture.Outsider)).Records, "alta.bookmark"));
        Assert.HasCount(4, Of((await fixture.Run(["bookmark", "list"], caller: fixture.Outsider)).Records, "alta.bookmark"), "A caller of no project lists them all.");

        // The text of the message comes from the journal of its session.
        var shown = await fixture.One("alta.bookmark", ["bookmark", "show", Value(answer, "id")!]);
        Assert.AreEqual(("ok", "True", "The plan has three steps.", "The plan", Value(answer, "offset")), (Value(shown, "status"), Value(shown, "found"), Value(shown, "text"), Value(shown, "label"), Value(shown, "offset")));
        Assert.IsNull(Value(shown, "truncated"));
        var whole = await fixture.One("alta.bookmark", ["bookmark", "show", Value(own, "id")!]);
        Assert.AreEqual(("ok", "True", (string?)null), (Value(whole, "status"), Value(whole, "found"), Value(whole, "text")));

        var named = await fixture.One("alta.bookmark.changed", ["bookmark", "rename", Value(prompt, "id")!, "--label", "What comes next"]);
        Assert.AreEqual("What comes next", Value(named, "label"));
        Assert.IsNull(Value(await fixture.One("alta.bookmark.changed", ["bookmark", "rename", Value(prompt, "id")!, "--label", ""]), "label"));
        var removed = await fixture.One("alta.bookmark.removed", ["bookmark", "remove", Value(prompt, "id")!]);
        Assert.AreEqual(Value(prompt, "id"), Value(removed, "id"));
        Assert.HasCount(3, await fixture.Bookmarks.ListAsync());

        // What the page lists is what the session added.
        var rows = (await fixture.Rpc.ListAsync(new(Epoch), default)).Bookmarks;
        Assert.AreEqual("in-project", rows.Single(row => row.Id == Value(own, "id")).CreatedBy);
    }

    [TestMethod]
    public async Task ASession_IsToldWhatItCannotBookmark()
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = Value(await fixture.One("alta.bookmark.added", ["bookmark", "add", "--turn", "2"]), "id")!;

        async Task Refused(int code, string error, string[] arguments, AltaCallerIdentity? caller = null)
        {
            var (exit, _, text) = await fixture.Run(arguments, caller: caller);
            Assert.AreEqual(code, exit, text);
            StringAssert.Contains(text, "\"code\":\"" + error + "\"");
        }

        await Refused(AltaExitCodes.Usage, "usage.targetConflict", ["bookmark", "add", "--turn", "1", "--offset", "10"]);
        await Refused(AltaExitCodes.Usage, "usage.missingTurn", ["bookmark", "add", "--role", "assistant"]);
        await Refused(AltaExitCodes.Usage, "usage.invalidRole", ["bookmark", "add", "--turn", "1", "--role", "tool"]);
        await Refused(AltaExitCodes.Usage, "usage.invalidOffset", ["bookmark", "add", "--offset", "-4"]);
        await Refused(AltaExitCodes.Usage, "usage.invalidLabel", ["bookmark", "add", "--label", new string('x', 201)]);
        await Refused(AltaExitCodes.Usage, "usage.missingSession", ["bookmark", "add"], new AltaCallerIdentity { Kind = "mcp" });
        await Refused(AltaExitCodes.NotFound, "session.notFound", ["bookmark", "add", "--session", "no-such-session"]);
        await Refused(AltaExitCodes.NotFound, "bookmark.notBookmarkable", ["bookmark", "add", "--turn", "9"]);
        await Refused(AltaExitCodes.NotFound, "bookmark.notBookmarkable", ["bookmark", "add", "--offset", "7"]);
        await Refused(AltaExitCodes.Usage, "usage.scopeConflict", ["bookmark", "list", "--all", "--session", "chat"]);
        await Refused(AltaExitCodes.Usage, "usage.invalidLimit", ["bookmark", "list", "--limit", "0"]);
        await Refused(AltaExitCodes.NotFound, "project.notFound", ["bookmark", "list", "--project", "no-such-project"]);
        await Refused(AltaExitCodes.Usage, "usage.missingLabel", ["bookmark", "rename", id]);
        await Refused(AltaExitCodes.NotFound, "bookmark.notFound", ["bookmark", "rename", "no-such-bookmark", "--label", "Name"]);
        await Refused(AltaExitCodes.NotFound, "bookmark.notFound", ["bookmark", "show", "no-such-bookmark"]);
        await Refused(AltaExitCodes.NotFound, "bookmark.notFound", ["bookmark", "remove", "no-such-bookmark"]);
        Assert.AreEqual(id, (await fixture.Bookmarks.ListAsync()).Single().Id);

        // The message that a rewind removed, and the session that is gone.
        await fixture.RewriteAsync("in-project", static lines => [.. lines.TakeWhile(static line => !line.Contains("\"runId\":\"run-2\"", StringComparison.Ordinal))]);
        var gone = await fixture.One("alta.bookmark", ["bookmark", "show", id]);
        Assert.AreEqual(("gone", "False", (string?)null), (Value(gone, "status"), Value(gone, "found"), Value(gone, "text")));
        Assert.IsTrue(await fixture.Store.DeleteSessionAsync("in-project"));
        await fixture.Host.AgentSessionCatalog.InvalidateAsync();
        var missing = await fixture.One("alta.bookmark", ["bookmark", "show", id]);
        Assert.AreEqual(("session_missing", "False", "True", "Planning session"), (Value(missing, "status"), Value(missing, "found"), Value(missing, "sessionMissing"), Value(missing, "sessionTitle")));
        Assert.AreEqual("True", Value(Of((await fixture.Run(["bookmark", "list", "--all"])).Records, "alta.bookmark").Single(), "sessionMissing"));

        // The help says how a message is named, and a host that keeps no bookmarks has no such command.
        var help = await fixture.Alta.InvokeAsync(["bookmark", "add", "--help"], null, fixture.Session, fixture.Project.ProjectPath);
        StringAssert.Contains(help.Stdout, "alta session turns");
        var bare = DesktopAltaTools.Compose(fixture.Host, new AltaReminderService(new AltaServiceCollection()));
        Assert.AreEqual(AltaExitCodes.Usage, (await bare.InvokeAsync(["bookmark", "list"], null, fixture.Session, fixture.Project.ProjectPath)).ExitCode);
    }

    [TestMethod]
    public async Task ABookmarkedSession_IsNotRetired_AndTheBookmarksOfADeletedSessionGoWithIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var protection = fixture.Host.RetentionProtection;
        using (var free = protection.TryReserveRetirement("in-project")) Assert.IsNotNull(free, "Nothing protects the session yet.");
        var existing = (await fixture.Rpc.AddAsync(new(Epoch, "chat", null, null), default)).Bookmark!;

        using var life = new DesktopBookmarks(fixture.Bookmarks, protection, fixture.Host.RuntimeService);
        await life.Loaded.WaitAsync(Patience);

        Assert.IsNull(protection.TryReserveRetirement("chat"), "The bookmarks that were there protect their sessions.");
        using (var free = protection.TryReserveRetirement("in-project")) Assert.IsNotNull(free);
        var offset = await fixture.OffsetOfAsync("in-project", AgentContentKind.User, "And then?");
        var message = (await fixture.Rpc.AddAsync(new(Epoch, "in-project", Text(offset), null), default)).Bookmark!;
        Assert.IsNull(protection.TryReserveRetirement("in-project"), "A session that holds a bookmarked message is kept.");
        Assert.AreEqual("ok", (await fixture.Rpc.RemoveAsync(new(Epoch, message.Id), default)).Status);
        using (var free = protection.TryReserveRetirement("in-project"))
        {
            Assert.IsNotNull(free, "Without a bookmark the session is as any other.");
            // A session that is being retired is not bookmarked: the claim of the request is refused.
            Assert.AreEqual("not_found", (await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, null), default)).Status);
        }

        // The user deletes a session: its bookmarks, of the session and of its messages, are removed.
        await fixture.Rpc.AddAsync(new(Epoch, "in-project", null, null), default);
        await fixture.Rpc.AddAsync(new(Epoch, "in-project", Text(offset), null), default);
        Assert.AreEqual("ok", await fixture.Host.Commands.DeleteCatalogSessionAsync("in-project", fixture.Project.Id, fixture.Project.ProjectPath, "Planning session"));
        var deadline = DateTime.UtcNow + Patience;
        while ((await fixture.Bookmarks.ListAsync()).Count != 1 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.AreEqual(existing.Id, (await fixture.Bookmarks.ListAsync()).Single().Id);
        using (var free = protection.TryReserveRetirement("old-session")) Assert.IsNotNull(free);
    }

    [TestMethod]
    public async Task BookmarksThatCannotBeRead_ProtectEverySession()
    {
        await using var fixture = await Fixture.CreateAsync();
        var protection = fixture.Host.RetentionProtection;
        var database = new ApplicationDatabase(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(fixture.Root, "closed", "alta.sqlite3") });
        await database.DisposeAsync();

        using var life = new DesktopBookmarks(new BookmarkService(database, new SessionJournalBookmarks(fixture.Store)), protection);
        await life.Loaded.WaitAsync(Patience);

        Assert.IsNull(protection.TryReserveRetirement("old-session"), "What the bookmarks protect is not known: nothing is retired.");
    }

    private static string Text(long offset) => offset.ToString(CultureInfo.InvariantCulture);

    private static string? Value(JsonElement record, string name) => record.TryGetProperty(name, out var value) ? value.ToString() : null;

    private static JsonElement[] Of(List<JsonElement> records, string type) => [.. records.Where(record => Value(record, "type") == type)];

    // A host over a profile of its own, with two sessions whose journals hold what a run writes.
    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly string LongContentId = "assistant:" + new string('k', 400);
        private static readonly ModelProviderId Provider = new("bookmark-fixture");

        private Fixture(string root, CodeAltaHost host, ProjectDescriptor project)
        {
            (Root, Host, Project) = (root, host, project);
            Store = host.SessionViewCatalog.JournalStore.CreateSessionStore();
            Bookmarks = new BookmarkService(host.ApplicationDatabase, new SessionJournalBookmarks(Store));
            Rpc = new BookmarksService(Bookmarks, BookmarksService.Sessions(host.WorkspaceReads), Epoch, host.RetentionProtection);
            Alta = DesktopAltaTools.Compose(host, new AltaReminderService(new AltaServiceCollection()), bookmarks: Bookmarks);
            Session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "in-project", SourceProjectId = project.Id };
            Outsider = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "chat" };
        }

        public string Root { get; }
        public CodeAltaHost Host { get; }
        public ProjectDescriptor Project { get; }
        public FileSystemAgentSessionStore Store { get; }
        public BookmarkService Bookmarks { get; }
        public BookmarksService Rpc { get; }
        public AltaCommandDispatcher Alta { get; }
        public AltaCallerIdentity Session { get; }
        public AltaCallerIdentity Outsider { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Directory.CreateTempSubdirectory("codealta-bookmark-host-").FullName;
            string At(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            var (global, projectPath) = (At("global"), At("project"));
            // The owners of the retention of this host are known: only what the tests publish protects a session.
            var protection = new SessionRetentionProtection();
            protection.SealParticipants(["window"]);
            Assert.IsTrue(protection.Publish("window", [], true));
            var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(At("home"), root), BuiltInSkillRoot = At("builtin"),
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
                RetentionProtection = protection,
            });
            var project = await host.ProjectCatalog.UpsertFromPathAsync(projectPath);
            var fixture = new Fixture(root, host, project);
            await fixture.AddSessionAsync("in-project", projectPath, "Planning session", project.Id);
            await fixture.AppendTurnAsync("in-project", "run-1", "What is the plan?", LongContentId, "The plan has three steps.", tools: 1);
            await fixture.AppendTurnAsync("in-project", "run-2", "And then?", "assistant:run-2", "Then it is done.");
            await fixture.AddSessionAsync("chat", global, "A chat", null);
            await fixture.AppendTurnAsync("chat", "run-1", "Hello", "assistant:run-1", "Hello.");
            await host.AgentSessionCatalog.InvalidateAsync();
            return fixture;
        }

        private async Task AddSessionAsync(string id, string path, string title, string? projectId)
        {
            var created = DateTimeOffset.UtcNow;
            await Host.SessionViewCatalog.JournalStore.EnsureHeaderAsync(new SessionViewDescriptor
            {
                SessionId = id, Kind = projectId is null ? SessionViewKind.GlobalSession : SessionViewKind.ProjectSession, ProjectRef = projectId,
                ProviderId = Provider.Value, ProviderKey = Provider.Value, WorkingDirectory = path, Title = title, CreatedAt = created, UpdatedAt = created,
            });
            await Store.UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = id, ProviderId = Provider, ProtocolFamily = Provider.Value, ProviderKey = Provider.Value,
                WorkingDirectory = path, Title = title, CreatedAt = created, UpdatedAt = created,
            });
        }

        private Task AppendTurnAsync(string id, string run, string prompt, string answerId, string answer, int tools = 0)
        {
            var runId = new AgentRunId(run);
            var time = DateTimeOffset.UtcNow;
            var events = new List<AgentEvent> { new AgentContentCompletedEvent(Provider, id, time, runId, AgentContentKind.User, $"user:{run}", null, prompt) };
            for (var index = 0; index < tools; index++)
            {
                events.Add(new AgentActivityEvent(Provider, id, time, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Started, $"{run}-tool-{index}", null, "read_file", null));
                events.Add(new AgentActivityEvent(Provider, id, time, runId, AgentActivityKind.ToolCall, AgentActivityPhase.Completed, $"{run}-tool-{index}", null, "read_file", null));
            }

            events.Add(new AgentContentCompletedEvent(Provider, id, time, runId, AgentContentKind.Assistant, answerId, null, answer));
            events.Add(new AgentSessionUpdateEvent(Provider, id, time, runId, AgentSessionUpdateKind.Idle, null));
            return Store.AppendEventsAsync(Provider.Value, Provider.Value, id, events);
        }

        // The offset of a record as the timeline of the page reads it.
        public async Task<long> OffsetOfAsync(string id, Func<AgentEvent, bool> wanted)
        {
            AgentSessionHistoryCursor? cursor = null;
            do
            {
                var page = await Host.WorkspaceReads.ReadHistoryPageAsync(id, cursor, default);
                if (page.Entries.FirstOrDefault(entry => wanted(entry.Event)) is { } found) return found.Offset;
                cursor = page.Next;
            }
            while (cursor is not null);
            throw new AssertFailedException("The journal holds no such record.");
        }

        public Task<long> OffsetOfAsync(string id, AgentContentKind kind, string content)
            => OffsetOfAsync(id, value => value is AgentContentCompletedEvent message && message.Kind == kind && message.Content == content);

        // Writes the journal of a session again, as lines: what moves or removes its records.
        public async Task RewriteAsync(string id, Func<string[], string[]> change)
        {
            var path = Directory.EnumerateFiles(Host.CatalogOptions.StateRoot, id + ".jsonl", SearchOption.AllDirectories).Single();
            var lines = (await File.ReadAllTextAsync(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            await File.WriteAllTextAsync(path, string.Concat(change(lines).Select(static line => line + "\n")));
        }

        public async Task<(int Code, List<JsonElement> Records, string Text)> Run(string[] arguments, AltaCallerIdentity? caller = null)
        {
            var result = await Alta.InvokeAsync(arguments, null, caller ?? Session, Project.ProjectPath).AsTask().WaitAsync(Patience);
            var records = new List<JsonElement>();
            foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var document = JsonDocument.Parse(line);
                records.Add(document.RootElement.Clone());
            }

            return (result.ExitCode, records, result.Stdout + result.Stderr);
        }

        public async Task<JsonElement> One(string type, string[] arguments)
        {
            var (code, records, text) = await Run(arguments);
            Assert.AreEqual(AltaExitCodes.Success, code, text);
            return records.Single(record => record.GetProperty("type").GetString() == type);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
