using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// A headless host that leaves the shared state of the global root as it is: what it writes, sessions in a folder
/// outside the project catalog and their resume from another process, identifiers chosen by the caller, and the
/// private database such a host runs with.
/// </summary>
[TestClass]
public sealed class HeadlessHostStateTests
{
    [TestMethod]
    public async Task ReadOnlyGlobalState_WithItsOwnStateRoot_WritesNothingUnderTheGlobalRoot()
    {
        await using var root = new HeadlessTestRoot();
        File.WriteAllText(Path.Combine(root.GlobalRoot, "config.toml"), "[sessions]\n");
        var before = HeadlessTestRoot.Snapshot(root.GlobalRoot);
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, separateStateRoot: true, scratchpadRoot: Path.Combine(root.Root, "scratch"));

        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot), "Read-only");
        await host.RuntimeService.SendAsync(session, HeadlessTestRoot.Options(provider, root.ProjectRoot), new AgentSendOptions { Input = AgentInput.Text("hello") });
        await host.DisposeAsync();

        CollectionAssert.AreEquivalent(before.ToArray(), HeadlessTestRoot.Snapshot(root.GlobalRoot).ToArray(),
            "Nothing is written under the global root: " + string.Join(", ", HeadlessTestRoot.Snapshot(root.GlobalRoot).Keys.Except(before.Keys)));
        Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(root.StateRoot, "sessions"), session.SessionId + ".jsonl", SearchOption.AllDirectories).Any(),
            "The journal is written under the state root.");
    }

    [TestMethod]
    public async Task ReadOnlyGlobalState_SharingTheGlobalRoot_WritesOnlyTheJournalsOfItsSessions()
    {
        await using var root = new HeadlessTestRoot();
        File.WriteAllText(Path.Combine(root.GlobalRoot, "config.toml"), "[sessions]\n");
        var before = HeadlessTestRoot.Snapshot(root.GlobalRoot);
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, scratchpadRoot: Path.Combine(root.Root, "scratch"));

        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot), "Visible");
        await host.RuntimeService.SendAsync(session, HeadlessTestRoot.Options(provider, root.ProjectRoot), new AgentSendOptions { Input = AgentInput.Text("hello") });
        await host.DisposeAsync();

        var after = HeadlessTestRoot.Snapshot(root.GlobalRoot);
        var written = after.Where(file => !before.TryGetValue(file.Key, out var old) || old != file.Value).Select(static file => file.Key).ToArray();
        Assert.IsTrue(written.Length > 0);
        Assert.IsTrue(written.All(static path => path.StartsWith("sessions" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
            "Only the journals are written: " + string.Join(", ", written));
        Assert.IsFalse(Directory.Exists(Path.Combine(root.GlobalRoot, "projects")), "No project is registered.");
        Assert.IsFalse(File.Exists(Path.Combine(root.GlobalRoot, "AGENTS.md")), "The coordinator instructions are not written.");
    }

    [TestMethod]
    public async Task SessionOfAFolderOutsideTheCatalog_IsResumedByIdFromAnotherHostWithAFreshDatabase()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var first = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        Assert.IsNull(await first.ProjectCatalog.GetByPathAsync(root.ProjectRoot), "The folder is no project of the catalog.");
        var created = await first.RuntimeService.CreateProjectSessionAsync(first.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot), "Uncataloged");
        await first.RuntimeService.SendAsync(created, HeadlessTestRoot.Options(provider, root.ProjectRoot), new AgentSendOptions { Input = AgentInput.Text("first") });
        await first.DisposeAsync();

        var second = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        // The listings leave it out as before: no project of the catalog has its folder.
        Assert.IsNull(await second.RuntimeService.TryGetRecoverableSessionAsync(created.SessionId));
        var resumed = await second.RuntimeService.TryGetResumableSessionAsync(created.SessionId);

        Assert.IsNotNull(resumed);
        Assert.AreEqual(created.SessionId, resumed.SessionId);
        Assert.AreEqual(SessionViewKind.ProjectSession, resumed.Kind);
        Assert.IsNotNull(resumed.ProjectRef);
        var transient = await second.ProjectCatalog.GetByIdAsync(resumed.ProjectRef);
        Assert.IsNotNull(transient, "The folder has a transient project of the catalog of this host.");
        Assert.AreEqual(Path.GetFullPath(root.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar), transient.ProjectPath);
        Assert.IsNull(transient.SourcePath, "A transient project has no file.");

        await second.RuntimeService.SendAsync(resumed, HeadlessTestRoot.Options(provider, root.ProjectRoot), new AgentSendOptions { Input = AgentInput.Text("second") });
        var history = await second.RuntimeService.TryReadStoredHistoryAsync(resumed, null);
        Assert.IsNotNull(history);
        var prompts = history.OfType<AgentContentCompletedEvent>().Where(static item => item.Kind == AgentContentKind.User).Select(static item => item.Content).ToArray();
        CollectionAssert.AreEqual(new[] { "first", "second" }, prompts, "The second prompt continues the same journal.");
        Assert.IsFalse(Directory.Exists(Path.Combine(root.GlobalRoot, "projects")));
    }

    [TestMethod]
    public async Task ExplicitSessionId_NamesTheSessionAndItsJournal_AndIsRefusedWhenTaken()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var id = Guid.NewGuid().ToString("D").ToUpperInvariant();

        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot),
            "Chosen id", parentSessionId: null, createdBy: null, worktreeDirectory: null, spaceId: null, sessionId: id);

        Assert.AreEqual(id.ToLowerInvariant(), session.SessionId, "The identifier is kept, in lower case.");
        Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(root.GlobalRoot, "sessions"), session.SessionId + ".jsonl", SearchOption.AllDirectories).Any());
        await Assert.ThrowsExactlyAsync<SessionIdConflictException>(() => host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject,
            HeadlessTestRoot.Options(provider, root.ProjectRoot), "Again", null, null, null, null, sessionId: id));
        await Assert.ThrowsExactlyAsync<SessionIdConflictException>(() => host.RuntimeService.CreateGlobalSessionAsync(
            HeadlessTestRoot.Options(provider, root.GlobalRoot), "Again", null, null, null, sessionId: id));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject,
            HeadlessTestRoot.Options(provider, root.ProjectRoot), "Invalid", null, null, null, null, sessionId: "not-a-session-id"));
        Assert.IsFalse(SessionRuntimeService.TryNormalizeSessionId("{" + Guid.NewGuid() + "}", out _));
        Assert.IsFalse(SessionRuntimeService.TryNormalizeSessionId(Guid.Empty.ToString(), out _));
    }

    [TestMethod]
    public async Task ExplicitSessionId_OfAJournalAnotherProcessWrote_IsRefused()
    {
        await using var root = new HeadlessTestRoot();
        var provider = new ScriptedTurnProvider();
        var first = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        var created = await first.RuntimeService.CreateProjectSessionAsync(first.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot), "First");
        await first.DisposeAsync();

        // A fresh private database knows nothing of it: the journal decides.
        var second = await root.CreateHostAsync(provider, readOnlyGlobalState: true);
        await Assert.ThrowsExactlyAsync<SessionIdConflictException>(() => second.RuntimeService.CreateProjectSessionAsync(second.CurrentProject,
            HeadlessTestRoot.Options(provider, root.ProjectRoot), "Again", null, null, null, null, sessionId: created.SessionId));
    }

    [TestMethod]
    public async Task ScratchpadRoot_IsTheFolderTheSessionContextNames()
    {
        await using var root = new HeadlessTestRoot();
        var scratchpad = Path.Combine(root.Root, "cli-scratchpad");
        var provider = new ScriptedTurnProvider();
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, scratchpadRoot: scratchpad);

        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot), "Scratchpad");
        await host.RuntimeService.SendAsync(session, HeadlessTestRoot.Options(provider, root.ProjectRoot), new AgentSendOptions { Input = AgentInput.Text("hello") });

        Assert.IsTrue(provider.Requests.TryPeek(out var request));
        var instructions = request.SystemMessage + "\n" + request.DeveloperInstructions + "\n" + request.SessionInstructions;
        StringAssert.Contains(request.SessionInstructions, Path.Combine(scratchpad, "<yyyy-mm-dd>-<short-name>-"));
        Assert.IsFalse(instructions.Contains(Path.Combine(root.GlobalRoot, "scratchpad"), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Config_OfTheHost_IsWhatItsStoresRead_WithItsOverlays_AndNoWrite()
    {
        await using var root = new HeadlessTestRoot();
        File.WriteAllText(Path.Combine(root.GlobalRoot, "config.toml"), "[tools]\ndisabled = [\"grep\"]\n");
        var provider = new ScriptedTurnProvider();
        var config = new CodeAltaConfigOptions
        {
            Overlays = [CodeAltaConfigOverlay.FromAssignment("tools.disabled=[\"list_dir\"]")],
            ReadOnly = true,
        };
        var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, config: config);

        Assert.AreSame(config, host.CatalogOptions.Config);
        CollectionAssert.AreEquivalent(new[] { "list_dir" }, host.RuntimeService.ConfigStore.LoadGlobalDisabledTools().ToArray(), "The overlay wins over the file.");
        var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot), "Overlay");
        await host.RuntimeService.SendAsync(session, HeadlessTestRoot.Options(provider, root.ProjectRoot), new AgentSendOptions { Input = AgentInput.Text("hello") });
        Assert.IsTrue(provider.Requests.TryPeek(out var request));
        CollectionAssert.DoesNotContain(request.Tools.Select(static tool => tool.Spec.Name).ToArray(), "list_dir");
        Assert.AreEqual("[tools]\ndisabled = [\"grep\"]\n", File.ReadAllText(Path.Combine(root.GlobalRoot, "config.toml")));
    }

    [TestMethod]
    public async Task ListsSkills_Off_ListsNoSkillInThePrompt()
    {
        await using var root = new HeadlessTestRoot();
        var skill = Path.Combine(root.GlobalRoot, "skills", "zebra-skill");
        Directory.CreateDirectory(skill);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: zebra-skill\ndescription: Paints zebras in stripes.\n---\n\nPaint.\n");

        foreach (var listsSkills in new[] { true, false })
        {
            var provider = new ScriptedTurnProvider();
            var host = await root.CreateHostAsync(provider, readOnlyGlobalState: true, listsSkills: listsSkills);
            var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, HeadlessTestRoot.Options(provider, root.ProjectRoot), "Skills");
            await host.RuntimeService.SendAsync(session, HeadlessTestRoot.Options(provider, root.ProjectRoot), new AgentSendOptions { Input = AgentInput.Text("hello") });

            Assert.IsTrue(provider.Requests.TryPeek(out var request));
            var instructions = request.SystemMessage + "\n" + request.DeveloperInstructions;
            Assert.AreEqual(listsSkills, instructions.Contains("zebra-skill", StringComparison.Ordinal), $"Lists skills: {listsSkills}.");
        }
    }

    [TestMethod]
    public async Task PrivateDatabase_LivesInAFolderOfItsOwn_DeletedWithIt()
    {
        var databases = Path.Combine(Path.GetTempPath(), "CodeAlta-private-db-" + Guid.NewGuid().ToString("N"));
        try
        {
            var database = PrivateApplicationDatabase.Create(databases);
            StringAssert.StartsWith(Path.GetFileName(database.Directory), Environment.ProcessId + "-");
            await database.Database.WriteAsync("test", static (_, _) => ValueTask.CompletedTask, CancellationToken.None);
            Assert.IsTrue(File.Exists(database.Database.DatabasePath));
            Assert.AreEqual(0, PrivateApplicationDatabase.SweepLeftovers(databases), "The folder of this process is never swept.");

            await database.DisposeAsync();

            Assert.IsFalse(Directory.Exists(database.Directory));
        }
        finally
        {
            if (Directory.Exists(databases)) Directory.Delete(databases, recursive: true);
        }
    }

    [TestMethod]
    public void PrivateDatabase_SweepDeletesTheFoldersOfProcessesThatAreGone()
    {
        var databases = Path.Combine(Path.GetTempPath(), "CodeAlta-private-db-" + Guid.NewGuid().ToString("N"));
        try
        {
            // A folder whose owner file nobody holds, and a folder that is not one of a private database.
            var leftover = Path.Combine(databases, $"{int.MaxValue}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(leftover);
            File.WriteAllText(Path.Combine(leftover, "owner.lock"), string.Empty);
            File.WriteAllText(Path.Combine(leftover, "alta.sqlite3"), string.Empty);
            var other = Path.Combine(databases, "kept");
            Directory.CreateDirectory(other);

            Assert.AreEqual(1, PrivateApplicationDatabase.SweepLeftovers(databases));

            Assert.IsFalse(Directory.Exists(leftover));
            Assert.IsTrue(Directory.Exists(other));
        }
        finally
        {
            if (Directory.Exists(databases)) Directory.Delete(databases, recursive: true);
        }
    }
}
