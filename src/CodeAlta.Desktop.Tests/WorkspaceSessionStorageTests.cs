using System.Collections.Frozen;
using CodeAlta.Agent.Runtime;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>Where a session is stored, as the Session Info window asks it: the folder of its journal, told and shown in the file manager.</summary>
[TestClass]
public sealed class WorkspaceSessionStorageTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public async Task TellsAndShowsTheFolderOfAStoredSession_AndOfNothingElse()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-session-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var global = Path.Combine(root, "global"); var projectPath = Path.Combine(root, "project");
            var home = Path.Combine(root, "home"); var builtin = Path.Combine(root, "builtin");
            foreach (var path in new[] { global, projectPath, home, builtin }) Directory.CreateDirectory(path);
            await using var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions
            {
                GlobalRoot = global, CurrentProjectPath = projectPath, DiscoveryScope = new(home, root), BuiltInSkillRoot = builtin,
                PluginEnvironment = FrozenDictionary<string, string?>.Empty, StartPlugins = false, IsHeadless = true, OwnsLogging = false,
            });
            var created = new DateTimeOffset(2026, 11, 14, 23, 30, 0, TimeSpan.FromHours(-10));
            await host.SessionViewCatalog.JournalStore.CreateSessionStore().UpsertSessionAsync(new AgentSessionSummary
            {
                SessionId = "stored-session", ProviderId = new("storage-fixture"), ProtocolFamily = "storage-fixture", ProviderKey = "storage-fixture",
                WorkingDirectory = projectPath, Title = "Stored", CreatedAt = created, UpdatedAt = created,
            });
            var journal = Path.GetFullPath(Directory.EnumerateFiles(host.CatalogOptions.SessionsRoot, "stored-session.jsonl", SearchOption.AllDirectories).Single());
            var shown = new List<string>();
            var rpc = new WorkspaceService(host, Epoch) { RevealSessionFile = path => { shown.Add(path); return true; } };
            var request = new WorkspaceSessionStorageRequest(Epoch, "stored-session", created.ToString("O"));

            var told = rpc.SessionStorage(request);
            Assert.AreEqual(("ok", Epoch, "stored-session", Path.GetDirectoryName(journal), journal), (told.Status, told.HostEpoch, told.SessionId, told.Folder, told.File));
            Assert.IsEmpty(shown, "Reading where a session is opens nothing.");
            Assert.AreEqual("ok", rpc.RevealSessionStorage(request).Status);
            CollectionAssert.AreEqual(new[] { journal }, shown, "The file manager shows the journal in its folder.");

            // Only the journal of a session that is there, under the sessions of this instance, is told or shown.
            Assert.AreEqual("stale_epoch", rpc.RevealSessionStorage(request with { ExpectedHostEpoch = Guid.NewGuid().ToString("D") }).Status);
            Assert.AreEqual("invalid_scope", rpc.RevealSessionStorage(request with { CreatedAt = "yesterday" }).Status);
            Assert.AreEqual("invalid_scope", rpc.RevealSessionStorage(request with { SessionId = "../../../../global/config" }).Status);
            Assert.AreEqual("invalid_scope", rpc.RevealSessionStorage(request with { SessionId = " " }).Status);
            var missing = rpc.RevealSessionStorage(request with { SessionId = "no-such-session" });
            Assert.AreEqual(("session_missing", null, null), (missing.Status, missing.Folder, missing.File));
            Assert.AreEqual("session_missing", rpc.RevealSessionStorage(request with { CreatedAt = created.AddDays(3).ToString("O") }).Status);
            Assert.HasCount(1, shown);

            var closed = new WorkspaceService(host, Epoch) { RevealSessionFile = static _ => false };
            Assert.AreEqual("failed", closed.RevealSessionStorage(request).Status);
            Assert.AreEqual("ok", closed.SessionStorage(request).Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
