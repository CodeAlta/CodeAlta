using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AgentPromptsRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable_AndAnotherEpochIsRefused()
    {
        var unavailable = new AgentPromptsService();
        Assert.AreEqual("unavailable", (await unavailable.ListAsync(new(Epoch, null), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.ReadAsync(new(Epoch, null, "Agent", "BuiltIn", "default"), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.SaveAsync(Save("Agent", "Global", "mine", null), default)).Status);
        Assert.AreEqual("unavailable", (await unavailable.DeleteAsync(new(Epoch, null, "Agent", "Global", "mine", "revision"), default)).Status);

        using var fixture = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new("another", null), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.ReadAsync(new(null, null, "Agent", "BuiltIn", "default"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.SaveAsync(Save("Agent", "Global", "mine", null) with { ExpectedEpoch = "another" }, default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.DeleteAsync(new("another", null, "Agent", "Global", "mine", "revision"), default)).Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.GlobalRoot, "prompts")), "Refused requests must not write.");
    }

    [TestMethod]
    public async Task List_ReturnsAgentAndSystemPromptsOfEveryScopeWithShadowing()
    {
        using var fixture = await Fixture.CreateAsync();
        Write(fixture.GlobalRoot, "agents", "default.prompt.md", "---\nname: \"My default\"\ndescription: \"Mine\"\nsystem: \"strict\"\n---\nGlobal body\n");
        Write(fixture.GlobalRoot, "system", "strict.system-prompt.md", "Strict system\n");
        Write(Path.Combine(fixture.ProjectPath, ".alta"), "agents", "local.prompt.md", "---\nname: \"Local\"\nmode: append\n---\nProject body\n");

        var global = await fixture.Service.ListAsync(new(Epoch, null), default);
        Assert.AreEqual("ok", global.Status);
        Assert.AreEqual(0, global.Omitted);
        Assert.HasCount(4, global.Prompts);
        var builtIn = global.Prompts.Single(prompt => prompt is { Kind: "Agent", Scope: "BuiltIn", Id: "default" });
        Assert.IsTrue(builtIn.ReadOnly);
        Assert.IsTrue(builtIn.Shadowed);
        Assert.AreEqual("Global", builtIn.ShadowedByScope);
        Assert.AreEqual("Default", builtIn.Name);
        var mine = global.Prompts.Single(prompt => prompt is { Kind: "Agent", Scope: "Global" });
        Assert.IsFalse(mine.ReadOnly);
        Assert.IsFalse(mine.Shadowed);
        Assert.AreEqual("My default", mine.Name);
        Assert.AreEqual("Mine", mine.Description);
        Assert.AreEqual("strict", mine.SystemPromptId);
        Assert.IsTrue(global.Prompts.Single(prompt => prompt is { Kind: "System", Scope: "BuiltIn" }).ReadOnly);
        Assert.AreEqual("strict", global.Prompts.Single(prompt => prompt is { Kind: "System", Scope: "Global" }).Id);

        var scoped = await fixture.Service.ListAsync(new(Epoch, fixture.Project.Id), default);
        Assert.AreEqual(fixture.Project.Id, scoped.ProjectId);
        Assert.HasCount(5, scoped.Prompts);
        var local = scoped.Prompts.Single(prompt => prompt.Scope == "Project");
        Assert.AreEqual("local", local.Id);
        Assert.IsTrue(local.Append);
        Assert.IsFalse(scoped.Prompts.Any(prompt => prompt.Name.Contains(fixture.ProjectPath, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task BuiltInPrompts_CanBeReadButNeverSavedOrDeleted()
    {
        using var fixture = await Fixture.CreateAsync();
        var read = await fixture.Service.ReadAsync(new(Epoch, null, "Agent", "BuiltIn", "default"), default);
        Assert.AreEqual("ok", read.Status, read.Message);
        Assert.IsTrue(read.Prompt!.ReadOnly);
        Assert.AreEqual("Default", read.Prompt.Name);
        Assert.AreEqual("Built-in body", read.Prompt.Body);
        var system = await fixture.Service.ReadAsync(new(Epoch, null, "System", "BuiltIn", "default"), default);
        Assert.AreEqual("Built-in system", system.Prompt!.Body);
        Assert.IsNull(system.Prompt.SystemPromptId);

        var before = File.ReadAllText(fixture.BuiltInAgent);
        Assert.AreEqual("read_only", (await fixture.Service.SaveAsync(Save("Agent", "BuiltIn", "default", read.Prompt.Revision), default)).Status);
        Assert.AreEqual("read_only", (await fixture.Service.SaveAsync(Save("Agent", "BuiltIn", "added", null), default)).Status);
        Assert.AreEqual("read_only", (await fixture.Service.DeleteAsync(new(Epoch, null, "Agent", "BuiltIn", "default", read.Prompt.Revision), default)).Status);
        Assert.AreEqual(before, File.ReadAllText(fixture.BuiltInAgent));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.BuiltInAgent)!, "added.prompt.md")));
    }

    [TestMethod]
    public async Task Save_CreatesThenUpdatesOnlyTheRevisionThatWasRead_AndDeleteRemovesIt()
    {
        using var fixture = await Fixture.CreateAsync();
        var path = Path.Combine(fixture.GlobalRoot, "prompts", "agents", "reviewer.prompt.md");
        var created = await fixture.Service.SaveAsync(Save("Agent", "Global", "reviewer", null), default);
        Assert.AreEqual("ok", created.Status, created.Message);
        StringAssert.Contains(File.ReadAllText(path), "Review the change.");
        Assert.AreEqual("conflict", (await fixture.Service.SaveAsync(Save("Agent", "Global", "reviewer", null), default)).Status,
            "Creating must not replace an existing prompt.");

        var read = await fixture.Service.ReadAsync(new(Epoch, null, "Agent", "Global", "reviewer"), default);
        Assert.AreEqual("ok", read.Status, read.Message);
        Assert.AreEqual(created.Revision, read.Prompt!.Revision);
        Assert.AreEqual("Reviewer", read.Prompt.Name);
        Assert.AreEqual("Reviews changes", read.Prompt.Description);
        Assert.AreEqual("strict", read.Prompt.SystemPromptId);
        Assert.AreEqual("Review the change.", read.Prompt.Body);
        Assert.IsFalse(read.Prompt.ReadOnly);
        Assert.IsFalse(read.Prompt.Append);

        var edit = Save("Agent", "Global", "reviewer", read.Prompt.Revision) with { Name = "Senior reviewer", Body = "Review it twice." };
        Assert.AreEqual("conflict", (await fixture.Service.SaveAsync(edit with { ExpectedRevision = "not-the-revision" }, default)).Status);
        var updated = await fixture.Service.SaveAsync(edit, default);
        Assert.AreEqual("ok", updated.Status, updated.Message);
        Assert.AreNotEqual(read.Prompt.Revision, updated.Revision);
        var reread = await fixture.Service.ReadAsync(new(Epoch, null, "Agent", "Global", "reviewer"), default);
        Assert.AreEqual(updated.Revision, reread.Prompt!.Revision);
        Assert.AreEqual("Senior reviewer", reread.Prompt.Name);
        Assert.AreEqual("Review it twice.", reread.Prompt.Body);
        Assert.AreEqual("Senior reviewer", (await fixture.Service.ListAsync(new(Epoch, null), default)).Prompts.Single(prompt => prompt.Id == "reviewer").Name);

        // The editor's first revision is now stale, and so is its second once the file changes elsewhere.
        Assert.AreEqual("conflict", (await fixture.Service.SaveAsync(edit, default)).Status);
        File.AppendAllText(path, "\nChanged elsewhere.\n");
        Assert.AreEqual("conflict", (await fixture.Service.SaveAsync(edit with { ExpectedRevision = updated.Revision }, default)).Status);
        Assert.AreEqual("conflict", (await fixture.Service.DeleteAsync(new(Epoch, null, "Agent", "Global", "reviewer", updated.Revision), default)).Status);
        StringAssert.Contains(File.ReadAllText(path), "Changed elsewhere.");

        var current = (await fixture.Service.ReadAsync(new(Epoch, null, "Agent", "Global", "reviewer"), default)).Prompt!.Revision;
        Assert.AreEqual("ok", (await fixture.Service.DeleteAsync(new(Epoch, null, "Agent", "Global", "reviewer", current), default)).Status);
        Assert.IsFalse(File.Exists(path));
        Assert.AreEqual("not_found", (await fixture.Service.ReadAsync(new(Epoch, null, "Agent", "Global", "reviewer"), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.SaveAsync(edit with { ExpectedRevision = current }, default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.DeleteAsync(new(Epoch, null, "Agent", "Global", "reviewer", current), default)).Status);
        Assert.IsFalse((await fixture.Service.ListAsync(new(Epoch, null), default)).Prompts.Any(prompt => prompt.Id == "reviewer"));
    }

    [TestMethod]
    public async Task ProjectAndSystemPrompts_AreWrittenUnderTheProjectResolvedFromTheCatalog()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = fixture.Project.Id;
        var path = Path.Combine(fixture.ProjectPath, ".alta", "prompts", "system", "house.system-prompt.md");
        var request = Save("System", "Project", "house", null) with { ProjectId = project, Name = null, Description = null, SystemPromptId = null, Body = "House rules." };
        var created = await fixture.Service.SaveAsync(request, default);
        Assert.AreEqual("ok", created.Status, created.Message);
        Assert.AreEqual("House rules.", File.ReadAllText(path).Trim());
        var read = await fixture.Service.ReadAsync(new(Epoch, project, "System", "Project", "house"), default);
        Assert.AreEqual("System", read.Prompt!.Kind);
        Assert.AreEqual("Project", read.Prompt.Scope);
        Assert.IsNull(read.Prompt.Name);
        Assert.AreEqual("House rules.", read.Prompt.Body);
        Assert.IsTrue((await fixture.Service.ListAsync(new(Epoch, project), default)).Prompts.Any(prompt => prompt is { Kind: "System", Scope: "Project", Id: "house" }));

        Assert.AreEqual("invalid", (await fixture.Service.SaveAsync(request with { ProjectId = null, Id = "other" }, default)).Status, "The project scope requires a project.");
        Assert.AreEqual("invalid", (await fixture.Service.ReadAsync(new(Epoch, null, "System", "Project", "house"), default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.SaveAsync(request with { ProjectId = Guid.NewGuid().ToString("D"), Id = "other" }, default)).Status);
        Assert.AreEqual("unknown_project", (await fixture.Service.ListAsync(new(Epoch, fixture.ProjectPath), default)).Status, "A path is not a project id.");

        fixture.Project.Archived = true;
        await fixture.Projects.SaveAsync(fixture.Project);
        Assert.AreEqual("archived_project", (await fixture.Service.ListAsync(new(Epoch, project), default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.SaveAsync(request with { Id = "other" }, default)).Status);
        Assert.AreEqual("archived_project", (await fixture.Service.DeleteAsync(new(Epoch, project, "System", "Project", "house", created.Revision), default)).Status);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "other.system-prompt.md")));
    }

    [TestMethod]
    public async Task InvalidRequests_AreRefusedWithABoundedReasonAndWriteNothing()
    {
        using var fixture = await Fixture.CreateAsync();
        var valid = Save("Agent", "Global", "mine", null);
        var refusals = new[]
        {
            valid with { Id = "../escape" }, valid with { Id = "con" }, valid with { Id = new string('a', 129) }, valid with { Id = null },
            valid with { Kind = "Tool" }, valid with { Scope = "Everywhere" }, valid with { Body = "   " }, valid with { Name = " " },
            valid with { Name = "two\nlines" }, valid with { SystemPromptId = "bad/id" }, valid with { Description = new string('d', 1025) },
            valid with { Body = null },
        };
        foreach (var refusal in refusals)
        {
            var response = await fixture.Service.SaveAsync(refusal, default);
            Assert.AreEqual("invalid", response.Status, refusal.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(response.Message));
            Assert.IsTrue(response.Message!.Length <= 512);
            Assert.IsFalse(response.Message.Contains("Parameter", StringComparison.Ordinal), response.Message);
        }

        Assert.AreEqual("too_large", (await fixture.Service.SaveAsync(valid with { Body = new string('b', AgentPromptsService.MaximumBodyLength + 1) }, default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.ReadAsync(new(Epoch, null, "Agent", "Global", "..\\escape"), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.DeleteAsync(new(Epoch, null, "Agent", "Global", "a/b", "revision"), default)).Status);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.GlobalRoot, "prompts")));
    }

    private static AgentPromptSaveRequest Save(string kind, string scope, string id, string? revision)
        => new(Epoch, null, kind, scope, id, revision, "Reviewer", "Reviews changes", "strict", "Review the change.", false);

    private static void Write(string root, string kind, string name, string content)
    {
        var path = Path.Combine(root, "prompts", kind, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        private Fixture(string root, ProjectCatalog projects, ProjectDescriptor project)
        {
            _root = root;
            Projects = projects;
            Project = project;
            var application = Path.Combine(root, "app");
            Write(Path.Combine(application, "content"), "agents", "default.prompt.md", "---\nname: \"Default\"\n---\nBuilt-in body\n");
            Write(Path.Combine(application, "content"), "system", "default.system-prompt.md", "Built-in system\n");
            Service = new AgentPromptsService(projects, Epoch, application);
        }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta-agent-prompts-" + Guid.NewGuid().ToString("N"));
            var projects = new ProjectCatalog(new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(root, "global")).FullName });
            var project = await projects.UpsertFromPathAsync(Directory.CreateDirectory(Path.Combine(root, "project")).FullName);
            return new Fixture(root, projects, project);
        }

        public ProjectCatalog Projects { get; }
        public ProjectDescriptor Project { get; }
        public AgentPromptsService Service { get; }
        public string GlobalRoot => Projects.Options.GlobalRoot;
        public string ProjectPath => Project.ProjectPath;
        public string BuiltInAgent => Path.Combine(_root, "app", "content", "prompts", "agents", "default.prompt.md");

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }
}
