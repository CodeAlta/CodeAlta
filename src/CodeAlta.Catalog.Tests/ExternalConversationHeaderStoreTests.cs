using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

/// <summary>
/// What the providers read of the files of conversations other programs started, kept in the application database: one
/// row per provider and path, written and removed by each provider for its own rows only.
/// </summary>
[TestClass]
public sealed class ExternalConversationHeaderStoreTests
{
    [TestMethod]
    public async Task Rows_AreKeptPerProvider_ReplacedByPath_AndRemovedOnlyForTheirProvider()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var store = new ExternalConversationHeaderStore(database);
        Assert.IsEmpty((await store.ReadAsync("claude-code"))!);
        Assert.AreEqual(1, await database.GetVersionAsync("external_conversation_headers"));
        CollectionAssert.AreEqual(new[] { "external_conversation_header" }, (await database.ListTablesAsync("external_conversation_header")).ToArray());

        var first = new AgentExternalConversationHeaderRecord(@"C:\claude\projects\p\a.jsonl", 10, 100, 1, Importable: true, "{\"title\":\"A\"}");
        var second = new AgentExternalConversationHeaderRecord(@"C:\claude\projects\p\b.jsonl", 20, 200, 1, Importable: false, HeaderJson: null);
        Assert.IsTrue(await store.WriteAsync("claude-code", [first, second], []));
        Assert.IsTrue(await store.WriteAsync("other-client", [first with { HeaderJson = "{}" }], []));

        // A file that grew replaces its row; one that is gone is removed, for its provider only.
        var grown = first with { Length = 15, LastWriteUtcTicks = 150, HeaderJson = "{\"title\":\"A again\"}" };
        Assert.IsTrue(await store.WriteAsync("claude-code", [grown], [second.Path, first.Path + ".unknown"]));
        CollectionAssert.AreEqual(new[] { grown }, (await store.ReadAsync("claude-code"))!.ToArray());
        CollectionAssert.AreEqual(new[] { first with { HeaderJson = "{}" } }, (await new ExternalConversationHeaderStore(database).ReadAsync("other-client"))!.ToArray());
        Assert.IsTrue(await store.WriteAsync("other-client", [], []), "Nothing to write is written.");
    }

    [TestMethod]
    public async Task ATableANewerBuildWrote_IsMadeAgainEmpty()
    {
        using var temp = TempFolder.Create();
        await using var database = CreateDatabase(temp);
        var store = new ExternalConversationHeaderStore(database);
        var row = new AgentExternalConversationHeaderRecord("/home/me/.claude/projects/p/a.jsonl", 10, 100, 1, Importable: true, "{}");
        Assert.IsTrue(await store.WriteAsync("claude-code", [row], []));

        // A newer build migrated the table to a layout this one does not know: the rows are derived, the table is made again.
        await database.MigrateAsync("external_conversation_headers", "external_conversation_header", 2, static (_, _, _, _) => ValueTask.CompletedTask);
        var restarted = new ExternalConversationHeaderStore(database);
        Assert.IsEmpty((await restarted.ReadAsync("claude-code"))!);
        Assert.AreEqual(1, await database.GetVersionAsync("external_conversation_headers"));
        Assert.IsTrue(await restarted.WriteAsync("claude-code", [row], []));
        CollectionAssert.AreEqual(new[] { row }, (await restarted.ReadAsync("claude-code"))!.ToArray());
    }

    [TestMethod]
    public async Task AClosedDatabase_IsNeitherReadNorWritten_WithoutThrowing()
    {
        using var temp = TempFolder.Create();
        var database = CreateDatabase(temp);
        var store = new ExternalConversationHeaderStore(database);
        Assert.IsEmpty((await store.ReadAsync("claude-code"))!);
        await database.DisposeAsync();

        Assert.IsNull(await store.ReadAsync("claude-code"));
        Assert.IsFalse(await store.WriteAsync("claude-code", [new("/a.jsonl", 1, 1, 1, true, null)], []));
    }

    private static ApplicationDatabase CreateDatabase(TempFolder temp)
        => new(new ApplicationDatabaseOptions { DatabasePath = Path.Combine(temp.Path, "data", "alta.sqlite3") });

    private sealed class TempFolder : IDisposable
    {
        private TempFolder(string path) => Path = path;

        public string Path { get; }

        public static TempFolder Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alta-external-headers-" + Guid.NewGuid().ToString("N"));
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
