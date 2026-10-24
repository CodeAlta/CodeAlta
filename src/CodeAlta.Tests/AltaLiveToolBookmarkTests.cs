using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Bookmarks;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

/// <summary>The `alta bookmark` commands in a host: where they exist, and what they ask of the retention of sessions.</summary>
[TestClass]
public sealed class AltaLiveToolBookmarkTests
{
    [TestMethod]
    public async Task BookmarkCommands_ExistWhereBookmarksAreKept()
    {
        using var root = TestTempDirectory.Create();
        await using var database = CreateDatabase(root);
        var with = CreateDispatcher(new AltaServiceCollection().Add(CreateBookmarks(root, database)));
        var without = CreateDispatcher(new AltaServiceCollection());

        var help = await with.InvokeAsync(["bookmark", "--help"]);
        Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, help.Transcript);
        foreach (var command in new[] { "list", "show", "add", "rename", "remove" })
        {
            StringAssert.Contains(help.Stdout, command);
        }

        var capabilities = await with.InvokeAsync(["tool", "capability", "list", "--detailed"]);
        StringAssert.Contains(capabilities.Stdout, "bookmark list");
        StringAssert.Contains(capabilities.Stdout, "bookmark add");
        Assert.IsFalse((await without.InvokeAsync(["tool", "capability", "list", "--detailed"])).Stdout.Contains("bookmark", StringComparison.Ordinal));
        Assert.AreEqual(AltaExitCodes.Usage, (await without.InvokeAsync(["bookmark", "list"])).ExitCode);
        Assert.IsFalse((await without.InvokeAsync(["--help"])).Stdout.Contains("bookmark", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ASessionTheHostLists_IsBookmarked_AndOneItDoesNotListIsShownAsGone()
    {
        using var root = TestTempDirectory.Create();
        await using var database = CreateDatabase(root);
        var bookmarks = CreateBookmarks(root, database);
        var query = new ListedSessions(new SessionViewDescriptor { SessionId = "listed", Title = "A listed session", Kind = SessionViewKind.GlobalSession });
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(bookmarks).Add<IAltaSessionQueryService>(query));
        await bookmarks.AddSessionAsync(new("deleted-since", "A session that is gone", "project-1"), "Kept", null);

        var added = await dispatcher.InvokeAsync(["bookmark", "add"], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "listed" });

        Assert.AreEqual(AltaExitCodes.Success, added.ExitCode, added.Transcript);
        StringAssert.Contains(added.Stdout, "\"type\":\"alta.bookmark.added\"");
        StringAssert.Contains(added.Stdout, "\"sessionTitle\":\"A listed session\"");
        StringAssert.Contains(added.Stdout, "\"createdBy\":\"listed\"");
        var listed = await dispatcher.InvokeAsync(["bookmark", "list", "--all"]);
        var lines = listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(static line => line.Contains("\"type\":\"alta.bookmark\"", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(2, lines);
        StringAssert.Contains(lines[0], "\"sessionId\":\"listed\"");
        StringAssert.Contains(lines[0], "\"sessionMissing\":false");
        StringAssert.Contains(lines[1], "\"sessionId\":\"deleted-since\"");
        StringAssert.Contains(lines[1], "\"sessionMissing\":true");
        StringAssert.Contains(lines[1], "\"sessionTitle\":\"A session that is gone\"");
        StringAssert.Contains(lines[1], "\"projectId\":\"project-1\"");

        // A host that does not list its sessions says of none that it is gone.
        var blind = await CreateDispatcher(new AltaServiceCollection().Add(bookmarks)).InvokeAsync(["bookmark", "list", "--all"]);
        Assert.AreEqual(AltaExitCodes.Success, blind.ExitCode, blind.Transcript);
        Assert.IsFalse(blind.Transcript.Contains("sessionMissing", StringComparison.Ordinal));
        Assert.IsFalse(blind.Transcript.Contains("alta.error", StringComparison.Ordinal), blind.Transcript);
    }

    [TestMethod]
    public async Task Retention_RefusesABookmarkOfASessionThatIsBeingRetired_BeforeTheSessionIsResolved()
    {
        using var root = TestTempDirectory.Create();
        await using var database = CreateDatabase(root);
        var bookmarks = CreateBookmarks(root, database);
        var protection = new SessionRetentionProtection();
        protection.SealParticipants(["fixture"]);
        Assert.IsTrue(protection.Publish("fixture", [], ready: true));
        var query = new ListedSessions(new SessionViewDescriptor { SessionId = "target", Title = "Target", Kind = SessionViewKind.GlobalSession });
        var dispatcher = CreateDispatcher(new AltaServiceCollection().Add(bookmarks)
            .Add<ISessionRetentionAdmission>(protection).Add<IAltaSessionQueryService>(query));
        using (var reservation = protection.TryReserveRetirement("target"))
        {
            Assert.IsNotNull(reservation);

            var refused = await dispatcher.InvokeAsync(["bookmark", "add", "--session", " target "], caller: AltaCallerIdentity.Cli);

            Assert.AreEqual(AltaExitCodes.Failure, refused.ExitCode, refused.Transcript);
            StringAssert.Contains(refused.Transcript, "retirement");
            Assert.AreEqual(0, query.Loads, "Refusal must precede session resolution.");
            Assert.IsEmpty(await bookmarks.ListAsync());
        }

        // Once it is no longer being retired, the session is bookmarked, and the claim of the command ends with it.
        var added = await dispatcher.InvokeAsync(["bookmark", "add", "--session", "target"], caller: AltaCallerIdentity.Cli);
        Assert.AreEqual(AltaExitCodes.Success, added.ExitCode, added.Transcript);
        Assert.IsFalse(added.Stdout.Contains("createdBy", StringComparison.Ordinal), "A caller that is no session is no author.");
        using var later = protection.TryReserveRetirement("target");
        Assert.IsNotNull(later, "The command holds no claim after it ended: what protects the session is what its host publishes.");
    }

    private static ApplicationDatabase CreateDatabase(TestTempDirectory root)
        => new(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(root.Path, "data", "alta.sqlite3") });

    private static BookmarkService CreateBookmarks(TestTempDirectory root, ApplicationDatabase database)
        => new(database, new SessionJournalBookmarks(new FileSystemAgentSessionStore(new AgentRuntimePathLayout(root.Path))));

    private static AltaCommandDispatcher CreateDispatcher(AltaServiceCollection services)
    {
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(dispatcher);
        return dispatcher;
    }

    // The sessions of a host, as the commands list them.
    private sealed class ListedSessions(params SessionViewDescriptor[] sessions) : IAltaSessionQueryService
    {
        public int Loads { get; private set; }

        public async IAsyncEnumerable<AltaSessionInfo> LoadAsync(AltaCommandContext context)
        {
            Loads++;
            await Task.Yield();
            foreach (var session in sessions)
            {
                yield return new AltaSessionInfo(session, null, IsRunning: false, HasActiveSession: false, "idle");
            }
        }
    }
}
