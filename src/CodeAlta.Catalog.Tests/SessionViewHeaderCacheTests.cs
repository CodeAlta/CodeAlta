using System.Text;
using System.Text.Json;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SessionViewHeaderCacheTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task CachedHeader_IsTheReadHeader_WhateverHappensToTheJournal()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-header-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionViewJournalStore(new CatalogOptions { GlobalRoot = root });
            var journal = new AgentRuntimePathLayout(root).GetSessionFilePath("session-1", At);
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            async Task AssertSame(string what)
            {
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var expected = await store.ReadHeaderAsync("session-1", At);
                    var actual = await store.ReadHeaderCachedAsync("session-1", At);
                    Assert.AreEqual(Json(expected), Json(actual), what);
                }
            }

            await AssertSame("missing journal");
            File.WriteAllText(journal, Header("project-1", "/one") + "\n", new UTF8Encoding(false));
            await AssertSame("written");
            File.AppendAllText(journal, "{\"$type\":\"raw\",\"backendEventType\":\"other\",\"raw\":{}}\n");
            await AssertSame("appended");
            // A move rewrites the header in a new journal, as a provider change does.
            var replacement = journal + ".moved";
            File.WriteAllText(replacement, Header("project-2", "/two") + "\n", new UTF8Encoding(false));
            File.Move(replacement, journal, overwrite: true);
            await AssertSame("moved");
            Assert.AreEqual("project-2", (await store.ReadHeaderCachedAsync("session-1", At))!.ProjectRef);
            File.WriteAllText(journal, "not a header\n");
            await AssertSame("no header");
            File.Delete(journal);
            await AssertSame("deleted");
            File.WriteAllText(journal, Header("project-3", "/three") + "\n", new UTF8Encoding(false));
            await AssertSame("written again");

            // Each read is a header of its own: one caller changing it changes nothing for the next.
            var first = (await store.ReadHeaderCachedAsync("session-1", At))!;
            first.ProjectRef = "changed by a caller";
            Assert.AreEqual("project-3", (await store.ReadHeaderCachedAsync("session-1", At))!.ProjectRef);
            Assert.IsNull(await store.ReadHeaderCachedAsync("session-1", default));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ReadHeaderCachedAsync(" ", At));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Json(SessionViewJournalHeader? header)
        => header is null ? "null" : JsonSerializer.Serialize(header, SessionViewJournalJsonSerializerContext.Default.SessionViewJournalHeader);

    private static string Header(string project, string path)
    {
        var raw = JsonSerializer.Serialize(new { session_id = "session-1", kind = "ProjectSession", project_ref = project,
            working_directory = path, backend_id = "provider", provider_key = "provider", created_at = At });
        return "{\"$type\":\"raw\",\"backendEventType\":\"codealta.sessionHeader\",\"raw\":" + raw + "}";
    }
}
