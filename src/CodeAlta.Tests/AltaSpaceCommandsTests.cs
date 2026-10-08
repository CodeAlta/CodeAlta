using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Tests;

/// <summary>Tests of <c>alta space</c> and of the <c>alta project</c> commands that read and change the spaces.</summary>
[TestClass]
public sealed class AltaSpaceCommandsTests
{
    private static readonly string[] SpaceLeaves = ["list", "show", "current", "create", "update", "delete", "add", "remove", "reorder"];

    [TestMethod]
    public async Task SpaceGroup_ExistsWithASpaceCatalog_AndSwitchWithAView()
    {
        using var none = await Fixture.CreateAsync(spaces: false, view: false);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync("space", "list")).ExitCode);
        Assert.IsFalse((await none.RunAsync("tool", "list")).Stdout.Contains("space list", StringComparison.Ordinal));

        using var catalogOnly = await Fixture.CreateAsync(view: false);
        foreach (var leaf in SpaceLeaves)
        {
            var help = await catalogOnly.RunAsync("space", leaf, "--help");
            Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, leaf);
            Assert.IsTrue(help.IsHelp, leaf);
        }

        Assert.AreEqual(AltaExitCodes.Usage, (await catalogOnly.RunAsync("space", "switch", "work")).ExitCode);
        var tools = (await catalogOnly.RunAsync("tool", "list")).Stdout;
        foreach (var leaf in SpaceLeaves) StringAssert.Contains(tools, $"space {leaf}");
        Assert.IsFalse(tools.Contains("space switch", StringComparison.Ordinal));
        foreach (var leaf in new[] { "add", "rename", "archive", "unarchive", "remove" }) StringAssert.Contains(tools, $"project {leaf}");

        using var window = await Fixture.CreateAsync();
        Assert.IsTrue((await window.RunAsync("space", "switch", "--help")).IsHelp);
        StringAssert.Contains((await window.RunAsync("tool", "list")).Stdout, "space switch");
        var groupHelp = await window.RunAsync("space", "--help");
        Assert.IsTrue(groupHelp.IsHelp);
        StringAssert.Contains(groupHelp.Stdout, "A space is a named group of projects.");
        StringAssert.Contains((await window.RunAsync("space", "list", "--help")).Stdout, "tells what the space is for");
        foreach (var leaf in new[] { "list", "show", "resolve", "current", "upsert", "add", "rename", "archive", "unarchive", "remove" })
        {
            Assert.IsTrue((await window.RunAsync("project", leaf, "--help")).IsHelp, leaf);
        }
    }

    [TestMethod]
    public async Task Space_CreateListShowUpdateReorderDelete()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        var beta = await fixture.AddProjectAsync("beta");

        var created = fixture.Single(await fixture.OkAsync("space", "create", "--name", "Work", "--description", "The projects of my job.",
            "--icon", "briefcase", "--color", "#2d72d2", "--project", alpha.Slug, "--project", beta.ProjectPath), "alta.space.created");
        Assert.AreEqual("work", created.GetProperty("id").GetString());
        Assert.AreEqual("The projects of my job.", created.GetProperty("description").GetString());
        Assert.AreEqual(2, created.GetProperty("projectCount").GetInt32());
        CollectionAssert.AreEquivalent(new[] { alpha.Id, beta.Id }, Strings(created.GetProperty("projects")));
        Assert.AreEqual(1, fixture.View.Changes);

        // The description may come from stdin.
        var personal = fixture.Single(await fixture.OkAsync(["space", "create", "--name", "Personal", "--stdin"], "What I do at home.\n"), "alta.space.created");
        Assert.AreEqual("What I do at home.", personal.GetProperty("description").GetString());
        Assert.AreEqual(2, fixture.View.Changes);

        fixture.View.ShownSpaceId = "work";
        var list = Records(await fixture.OkAsync("space", "list"));
        var items = list.Where(static line => Type(line) == "alta.space.item").ToArray();
        CollectionAssert.AreEqual(new[] { "default", "work", "personal" }, items.Select(static item => item.GetProperty("id").GetString()).ToArray());
        Assert.IsTrue(items[0].GetProperty("default").GetBoolean());
        Assert.AreEqual(2, items[0].GetProperty("projectCount").GetInt32());
        Assert.IsFalse(items[0].GetProperty("current").GetBoolean());
        Assert.IsTrue(items[1].GetProperty("current").GetBoolean());
        Assert.AreEqual("briefcase", items[1].GetProperty("icon").GetString());
        Assert.AreEqual("#2d72d2", items[1].GetProperty("color").GetString());
        Assert.AreEqual(3, list.Single(static line => Type(line) == "alta.space.summary").GetProperty("count").GetInt32());

        // A space is named by its id, by the start of its id, or by its name.
        foreach (var reference in new[] { "work", "wo", "WORK", "Work" })
        {
            var detail = fixture.Single(await fixture.OkAsync("space", "show", reference), "alta.space.detail");
            Assert.AreEqual("work", detail.GetProperty("id").GetString(), reference);
            var projects = detail.GetProperty("projects").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { alpha.Id, beta.Id }, projects.Select(static project => project.GetProperty("id").GetString()).ToArray());
            Assert.AreEqual(alpha.Slug, projects[0].GetProperty("slug").GetString());
            Assert.AreEqual(alpha.DisplayName, projects[0].GetProperty("name").GetString());
            Assert.AreEqual(alpha.ProjectPath, projects[0].GetProperty("path").GetString());
            Assert.IsFalse(projects[0].GetProperty("archived").GetBoolean());
        }

        var updated = fixture.Single(await fixture.OkAsync("space", "update", "work", "--name", "Day job", "--color", "", "--icon", "briefcase"), "alta.space.updated");
        Assert.AreEqual("work", updated.GetProperty("id").GetString());
        Assert.AreEqual("Day job", updated.GetProperty("name").GetString());
        Assert.IsFalse(updated.TryGetProperty("color", out _));
        CollectionAssert.AreEqual(new[] { "name", "color" }, Strings(updated.GetProperty("changed")));
        var described = fixture.Single(await fixture.OkAsync(["space", "update", "Day job", "--stdin"], "Everything for the office."), "alta.space.updated");
        Assert.AreEqual("Everything for the office.", described.GetProperty("description").GetString());
        CollectionAssert.AreEqual(new[] { "description" }, Strings(described.GetProperty("changed")));
        Assert.AreEqual(4, fixture.View.Changes);

        var order = fixture.Single(await fixture.OkAsync("space", "reorder", "personal", "work"), "alta.space.order");
        CollectionAssert.AreEqual(new[] { "default", "personal", "work" }, Strings(order.GetProperty("ids")));
        Assert.AreEqual(5, fixture.View.Changes);

        var deleted = fixture.Single(await fixture.OkAsync("space", "delete", "work"), "alta.space.deleted");
        Assert.AreEqual("work", deleted.GetProperty("id").GetString());
        Assert.AreEqual(2, deleted.GetProperty("projectCount").GetInt32());
        StringAssert.Contains(deleted.GetProperty("note").GetString(), "stay in the catalog");
        Assert.AreEqual(6, fixture.View.Changes);
        Assert.AreEqual(2, (await fixture.Projects.LoadAsync()).Count);
        Assert.IsTrue((await fixture.Projects.LoadAsync()).All(static project => project.Spaces.Count == 0));
        Assert.IsTrue(Directory.Exists(alpha.ProjectPath));
    }

    [TestMethod]
    public async Task Space_Current_FollowsTheWindow()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        await fixture.AddProjectAsync("beta");
        await fixture.OkAsync("space", "create", "--name", "Work", "--project", alpha.Id);
        var changes = fixture.View.Changes;

        var none = fixture.Single(await fixture.OkAsync("space", "current"), "alta.space.detail");
        Assert.AreEqual("default", none.GetProperty("id").GetString());
        Assert.IsFalse(none.GetProperty("shown").GetBoolean());
        Assert.AreEqual(2, none.GetProperty("projects").GetArrayLength());

        fixture.View.ShownSpaceId = "work";
        var shown = fixture.Single(await fixture.OkAsync("space", "current"), "alta.space.detail");
        Assert.AreEqual("work", shown.GetProperty("id").GetString());
        Assert.IsTrue(shown.GetProperty("shown").GetBoolean());
        Assert.IsTrue(shown.GetProperty("current").GetBoolean());
        Assert.AreEqual(1, shown.GetProperty("projects").GetArrayLength());

        // A space that is gone is not the current one any more.
        fixture.View.ShownSpaceId = "gone";
        var gone = fixture.Single(await fixture.OkAsync("space", "current"), "alta.space.detail");
        Assert.AreEqual("default", gone.GetProperty("id").GetString());
        Assert.IsFalse(gone.GetProperty("shown").GetBoolean());

        // Reading notifies nobody.
        await fixture.OkAsync("space", "list");
        await fixture.OkAsync("space", "show", "work", "--include-archived");
        await fixture.OkAsync("project", "list");
        await fixture.OkAsync("project", "show", alpha.Id);
        Assert.AreEqual(changes, fixture.View.Changes);

        using var noWindow = await Fixture.CreateAsync(view: false);
        var hostWithoutWindow = noWindow.Single(await noWindow.OkAsync("space", "current"), "alta.space.detail");
        Assert.AreEqual("default", hostWithoutWindow.GetProperty("id").GetString());
        Assert.IsFalse(hostWithoutWindow.GetProperty("shown").GetBoolean());
    }

    [TestMethod]
    public async Task Space_AddAndRemoveProjects()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        var beta = await fixture.AddProjectAsync("beta");
        await fixture.OkAsync("space", "create", "--name", "Work");
        var changes = fixture.View.Changes;

        var added = fixture.Single(await fixture.OkAsync("space", "add", "work", alpha.Slug, beta.Id), "alta.space.projects");
        Assert.AreEqual("work", added.GetProperty("spaceId").GetString());
        CollectionAssert.AreEqual(new[] { alpha.Id, beta.Id }, Strings(added.GetProperty("added")));
        Assert.AreEqual(2, added.GetProperty("projectCount").GetInt32());
        Assert.AreEqual(changes + 1, fixture.View.Changes);

        var again = fixture.Single(await fixture.OkAsync("space", "add", "work", alpha.ProjectPath), "alta.space.projects");
        Assert.AreEqual(0, again.GetProperty("added").GetArrayLength());
        CollectionAssert.AreEqual(new[] { alpha.Id }, Strings(again.GetProperty("unchanged")));

        var removed = fixture.Single(await fixture.OkAsync("space", "remove", "Work", alpha.Id), "alta.space.projects");
        CollectionAssert.AreEqual(new[] { alpha.Id }, Strings(removed.GetProperty("removed")));
        Assert.AreEqual(1, removed.GetProperty("projectCount").GetInt32());
        CollectionAssert.AreEqual(new[] { "work" }, (await fixture.Projects.GetByIdAsync(beta.Id))!.Spaces);
        Assert.AreEqual(0, (await fixture.Projects.GetByIdAsync(alpha.Id))!.Spaces.Count);

        await fixture.FailsAsync(AltaExitCodes.NotFound, "project.notFound", "space", "add", "work", "no-such-project");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingProject", "space", "add", "work");
    }

    [TestMethod]
    public async Task Space_Errors()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        await fixture.OkAsync("space", "create", "--name", "Work");
        await fixture.OkAsync("space", "create", "--name", "Workshop");
        var changes = fixture.View.Changes;

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.ambiguousSpace", "space", "show", "wor");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "space.notFound", "space", "show", "missing");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "space.notFound", "space", "delete", "missing");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "space.notFound", "space", "reorder", "work", "missing");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingSpace", "space", "reorder");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingName", "space", "create");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.contentConflict", "space", "create", "--name", "Other", "--description", "a", "--stdin");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingChange", "space", "update", "work");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "project.notFound", "space", "create", "--name", "Other", "--project", "no-such-project");
        Assert.IsNull(await fixture.Spaces.GetAsync("other"));

        // What the catalog refuses is passed on with its message.
        var invalid = await fixture.FailsAsync(AltaExitCodes.Usage, "space.invalid", "space", "create", "--name", "Colored", "--color", "blue");
        StringAssert.Contains(invalid.GetProperty("message").GetString(), "blue");
        await fixture.FailsAsync(AltaExitCodes.Usage, "space.invalid", "space", "update", "workshop", "--name", "work");

        // The default space holds every project: it is neither deleted nor given or taken projects.
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "space.refused", "space", "delete", "default");
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "space.refused", "space", "add", "default", alpha.Id);
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "space.refused", "space", "remove", "default", alpha.Id);
        Assert.AreEqual(changes, fixture.View.Changes);

        // It still takes a name, a description, an icon and a color.
        var renamed = fixture.Single(await fixture.OkAsync("space", "update", "default", "--name", "Everything"), "alta.space.updated");
        Assert.AreEqual("default", renamed.GetProperty("id").GetString());
        Assert.IsTrue(renamed.GetProperty("default").GetBoolean());
    }

    [TestMethod]
    public async Task Space_Switch_AsksTheWindow()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.OkAsync("space", "create", "--name", "Work");
        var changes = fixture.View.Changes;

        var shown = fixture.Single(await fixture.OkAsync("space", "switch", "Work"), "alta.space.shown");
        Assert.AreEqual("work", shown.GetProperty("spaceId").GetString());
        CollectionAssert.AreEqual(new[] { "work" }, fixture.View.Shown);
        Assert.AreEqual(changes, fixture.View.Changes);

        await fixture.FailsAsync(AltaExitCodes.NotFound, "space.notFound", "space", "switch", "missing");
        fixture.View.Open = false;
        await fixture.FailsAsync(AltaExitCodes.ServiceUnavailable, "view.unavailable", "space", "switch", "work");
    }

    [TestMethod]
    public async Task ProjectList_ReadsTheCurrentSpace()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        var beta = await fixture.AddProjectAsync("beta");
        await fixture.OkAsync("space", "create", "--name", "Work", "--project", alpha.Id);

        // No window said what it shows: every project.
        var everything = fixture.Single(await fixture.OkAsync("project", "list"), "alta.project.refs");
        Assert.AreEqual("default", everything.GetProperty("spaceId").GetString());
        CollectionAssert.AreEqual(new[] { alpha.Slug, beta.Slug }, Slugs(everything));

        fixture.View.ShownSpaceId = "work";
        var current = fixture.Single(await fixture.OkAsync("project", "list"), "alta.project.refs");
        Assert.AreEqual("work", current.GetProperty("spaceId").GetString());
        CollectionAssert.AreEqual(new[] { alpha.Slug }, Slugs(current));

        foreach (var arguments in new[] { new[] { "--all" }, ["--space", "default"] })
        {
            var all = fixture.Single(await fixture.OkAsync(["project", "list", .. arguments]), "alta.project.refs");
            Assert.AreEqual("default", all.GetProperty("spaceId").GetString());
            CollectionAssert.AreEqual(new[] { alpha.Slug, beta.Slug }, Slugs(all));
        }

        await fixture.OkAsync("space", "create", "--name", "Personal", "--project", beta.Id);
        var named = Records(await fixture.OkAsync("project", "list", "--space", "pers", "--detailed"));
        var item = named.Single(static line => Type(line) == "alta.project.item");
        Assert.AreEqual(beta.Id, item.GetProperty("projectId").GetString());
        CollectionAssert.AreEqual(new[] { "personal" }, Strings(item.GetProperty("spaces")));
        var summary = named.Single(static line => Type(line) == "alta.project.summary");
        Assert.AreEqual("personal", summary.GetProperty("spaceId").GetString());
        Assert.AreEqual(1, summary.GetProperty("count").GetInt32());

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.scopeConflict", "project", "list", "--all", "--space", "work");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "space.notFound", "project", "list", "--space", "missing");

        // A space that is gone is not the current one: every project again.
        fixture.View.ShownSpaceId = "gone";
        CollectionAssert.AreEqual(new[] { alpha.Slug, beta.Slug }, Slugs(fixture.Single(await fixture.OkAsync("project", "list"), "alta.project.refs")));

        var detail = fixture.Single(await fixture.OkAsync("project", "show", alpha.Slug), "alta.project.detail");
        CollectionAssert.AreEqual(new[] { "work" }, Strings(detail.GetProperty("spaces")));
        var resolution = fixture.Single(await fixture.OkAsync("project", "resolve", "--path", beta.ProjectPath), "alta.project.resolution");
        CollectionAssert.AreEqual(new[] { "personal" }, Strings(resolution.GetProperty("spaces")));
    }

    [TestMethod]
    public async Task ProjectList_WithoutAWindow_ListsEveryProject()
    {
        using var fixture = await Fixture.CreateAsync(view: false);
        var alpha = await fixture.AddProjectAsync("alpha");
        var beta = await fixture.AddProjectAsync("beta");
        await fixture.OkAsync("space", "create", "--name", "Work", "--project", alpha.Id);

        CollectionAssert.AreEqual(new[] { alpha.Slug, beta.Slug }, Slugs(fixture.Single(await fixture.OkAsync("project", "list"), "alta.project.refs")));
        CollectionAssert.AreEqual(new[] { alpha.Slug }, Slugs(fixture.Single(await fixture.OkAsync("project", "list", "--space", "work"), "alta.project.refs")));

        // A host that keeps no spaces lists as before.
        using var none = await Fixture.CreateAsync(spaces: false, view: false);
        var gamma = await none.AddProjectAsync("gamma");
        var refs = none.Single(await none.OkAsync("project", "list"), "alta.project.refs");
        CollectionAssert.AreEqual(new[] { gamma.Slug }, Slugs(refs));
        Assert.AreEqual(AltaExitCodes.ServiceUnavailable, (await none.RunAsync("project", "list", "--space", "work")).ExitCode);
    }

    [TestMethod]
    public async Task ProjectCurrent_PrefersTheProjectOfTheCallingSession()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        var beta = await fixture.AddProjectAsync("beta");
        var worktree = Path.Combine(fixture.Root, "elsewhere");
        Directory.CreateDirectory(worktree);

        // A session that works in a worktree outside the folder of its project still names its project.
        var session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = alpha.Id };
        var own = fixture.Single(await fixture.OkAsync(["project", "current"], caller: session, cwd: worktree), "alta.project.resolution");
        Assert.AreEqual(alpha.Id, own.GetProperty("projectId").GetString());
        Assert.AreEqual(0, own.GetProperty("spaces").GetArrayLength());

        var byFolder = fixture.Single(await fixture.OkAsync(["project", "current"], cwd: beta.ProjectPath), "alta.project.resolution");
        Assert.AreEqual(beta.Id, byFolder.GetProperty("projectId").GetString());

        // A project that left the catalog is not the current one: the folder decides.
        var stale = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = "no-such-project" };
        var fallback = fixture.Single(await fixture.OkAsync(["project", "current"], caller: stale, cwd: beta.ProjectPath), "alta.project.resolution");
        Assert.AreEqual(beta.Id, fallback.GetProperty("projectId").GetString());
    }

    [TestMethod]
    public async Task Project_AddRenameArchive()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.OkAsync("space", "create", "--name", "Work");
        await fixture.OkAsync("space", "create", "--name", "Personal");
        var changes = fixture.View.Changes;
        var folder = Path.Combine(fixture.Root, "code", "gamma");
        Directory.CreateDirectory(folder);

        var added = fixture.Single(await fixture.OkAsync("project", "add", folder, "--space", "work"), "alta.project.added");
        Assert.IsTrue(added.GetProperty("created").GetBoolean());
        CollectionAssert.AreEqual(new[] { "work" }, Strings(added.GetProperty("spaces")));
        var id = added.GetProperty("projectId").GetString()!;
        Assert.AreEqual(++changes, fixture.View.Changes);

        // A folder that is a project already only joins the spaces named.
        var joined = fixture.Single(await fixture.OkAsync("project", "add", folder, "--space", "Personal", "--space", "default"), "alta.project.added");
        Assert.IsFalse(joined.GetProperty("created").GetBoolean());
        Assert.AreEqual(id, joined.GetProperty("projectId").GetString());
        CollectionAssert.AreEquivalent(new[] { "work", "personal" }, Strings(joined.GetProperty("spaces")));
        Assert.AreEqual(++changes, fixture.View.Changes);

        await fixture.FailsAsync(AltaExitCodes.NotFound, "space.notFound", "project", "add", folder, "--space", "missing");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "project.pathNotFound", "project", "add", Path.Combine(fixture.Root, "missing"));
        Assert.AreEqual(changes, fixture.View.Changes);

        var upserted = fixture.Single(await fixture.OkAsync("project", "upsert", folder), "alta.project.upserted");
        Assert.AreEqual(id, upserted.GetProperty("projectId").GetString());
        Assert.AreEqual(++changes, fixture.View.Changes);

        var renamed = fixture.Single(await fixture.OkAsync("project", "rename", id, "Gamma (main)"), "alta.project.renamed");
        Assert.AreEqual("Gamma (main)", renamed.GetProperty("displayName").GetString());
        Assert.AreEqual("Gamma (main)", (await fixture.Projects.GetByIdAsync(id))!.DisplayName);
        Assert.IsTrue(renamed.TryGetProperty("previousName", out _));
        Assert.AreEqual(++changes, fixture.View.Changes);
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidName", "project", "rename", id, new string('x', 300));
        await fixture.FailsAsync(AltaExitCodes.NotFound, "project.notFound", "project", "rename", "no-such-project", "Name");

        var archived = fixture.Single(await fixture.OkAsync("project", "archive", id), "alta.project.archived");
        Assert.IsTrue(archived.GetProperty("archived").GetBoolean());
        Assert.IsTrue(archived.GetProperty("changed").GetBoolean());
        Assert.IsTrue((await fixture.Projects.GetByIdAsync(id))!.Archived);
        Assert.AreEqual(++changes, fixture.View.Changes);
        Assert.AreEqual(0, Slugs(fixture.Single(await fixture.OkAsync("project", "list", "--all"), "alta.project.refs")).Length);
        Assert.AreEqual(1, Slugs(fixture.Single(await fixture.OkAsync("project", "list", "--all", "--include-archived"), "alta.project.refs")).Length);
        Assert.IsFalse(fixture.Single(await fixture.OkAsync("project", "archive", id), "alta.project.archived").GetProperty("changed").GetBoolean());

        var restored = fixture.Single(await fixture.OkAsync("project", "unarchive", id), "alta.project.archived");
        Assert.IsFalse(restored.GetProperty("archived").GetBoolean());
        Assert.IsFalse((await fixture.Projects.GetByIdAsync(id))!.Archived);
        // The spaces of a project stay through its changes.
        CollectionAssert.AreEquivalent(new[] { "work", "personal" }, (await fixture.Projects.GetByIdAsync(id))!.Spaces);
    }

    [TestMethod]
    public async Task Project_Remove_KeepsTheFolder_AndRefusesAProjectWithSessions()
    {
        using var fixture = await Fixture.CreateAsync(sessions: true);
        var alpha = await fixture.AddProjectAsync("alpha");
        var beta = await fixture.AddProjectAsync("beta");
        await fixture.SessionCatalog!.SaveInternalAsync(new SessionViewDescriptor
        {
            SessionId = "session-of-beta",
            Kind = SessionViewKind.InternalSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProviderKey = ModelProviderIds.Codex.Value,
            ProjectRef = beta.Id,
            WorkingDirectory = beta.ProjectPath,
            Title = "A session",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        });
        var changes = fixture.View.Changes;

        var removed = fixture.Single(await fixture.OkAsync("project", "remove", alpha.Slug), "alta.project.removed");
        Assert.AreEqual(alpha.Id, removed.GetProperty("projectId").GetString());
        Assert.AreEqual(0, removed.GetProperty("deletedSessions").GetInt32());
        Assert.IsNull(await fixture.Projects.GetByIdAsync(alpha.Id));
        Assert.IsTrue(Directory.Exists(alpha.ProjectPath));
        Assert.AreEqual(++changes, fixture.View.Changes);

        var refused = await fixture.FailsAsync(AltaExitCodes.Unsupported, "project.hasSessions", "project", "remove", beta.Id);
        StringAssert.Contains(refused.GetProperty("message").GetString(), "1 session(s)");
        StringAssert.Contains(refused.GetProperty("message").GetString(), "alta project archive");
        Assert.IsNotNull(await fixture.Projects.GetByIdAsync(beta.Id));
        Assert.AreEqual(changes, fixture.View.Changes);

        // Deleting sessions takes the runtime that owns them: without it nothing is removed.
        Assert.AreEqual(AltaExitCodes.ServiceUnavailable, (await fixture.RunAsync("project", "remove", beta.Id, "--delete-sessions")).ExitCode);
        Assert.IsNotNull(await fixture.Projects.GetByIdAsync(beta.Id));
        await fixture.FailsAsync(AltaExitCodes.NotFound, "project.notFound", "project", "remove", "no-such-project");

        // A host that cannot say which sessions a project has removes nothing.
        using var blind = await Fixture.CreateAsync();
        var gamma = await blind.AddProjectAsync("gamma");
        Assert.AreEqual(AltaExitCodes.ServiceUnavailable, (await blind.RunAsync("project", "remove", gamma.Id)).ExitCode);
        Assert.IsNotNull(await blind.Projects.GetByIdAsync(gamma.Id));
    }

    private static SessionViewDescriptor SessionOf(ProjectDescriptor project, string sessionId)
        => new()
        {
            SessionId = sessionId,
            Kind = SessionViewKind.InternalSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProviderKey = ModelProviderIds.Codex.Value,
            ProjectRef = project.Id,
            WorkingDirectory = project.ProjectPath,
            Title = "A session",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private static string? Type(JsonElement line) => line.GetProperty("type").GetString();

    private static string?[] Strings(JsonElement array) => array.EnumerateArray().Select(static item => item.GetString()).ToArray();

    private static string?[] Slugs(JsonElement refs) => refs.GetProperty("projects").EnumerateArray().Select(static item => item[0].GetString()).ToArray();

    private static List<JsonElement> Records(AltaCommandResult result)
    {
        var values = new List<JsonElement>();
        foreach (var line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            values.Add(document.RootElement.Clone());
        }

        return values;
    }

    private sealed class FakeSpaceView : IAltaSpaceView
    {
        public string? ShownSpaceId { get; set; }

        public bool Open { get; set; } = true;

        public List<string> Shown { get; } = [];

        public int Changes { get; private set; }

        public bool Show(string spaceId)
        {
            if (!Open) return false;
            Shown.Add(spaceId);
            ShownSpaceId = spaceId;
            return true;
        }

        public void NotifyChanged() => Changes++;
    }

    private sealed class Fixture : IDisposable
    {
        // The commands are run by a caller that belongs to no session, as the MCP server of the window does.
        private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };

        private Fixture(string root)
        {
            Root = root;
            Options = new CatalogOptions { GlobalRoot = Path.Combine(root, "home") };
            Projects = new ProjectCatalog(Options);
            Spaces = new SpaceCatalog(Projects);
        }

        public string Root { get; }

        public CatalogOptions Options { get; }

        public ProjectCatalog Projects { get; }

        public SpaceCatalog Spaces { get; }

        public FakeSpaceView View { get; } = new();

        public SessionViewCatalog? SessionCatalog { get; private set; }

        private AltaCommandDispatcher Dispatcher { get; set; } = null!;

        public static Task<Fixture> CreateAsync(bool spaces = true, bool view = true, bool sessions = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta.AltaSpaceCommandsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "home"));
            var fixture = new Fixture(root);
            var services = new AltaServiceCollection().Add(fixture.Options).Add(fixture.Projects);
            if (spaces) services.Add(fixture.Spaces);
            if (view) services.Add<IAltaSpaceView>(fixture.View);
            if (sessions)
            {
                fixture.SessionCatalog = new SessionViewCatalog(fixture.Options);
                services.Add(fixture.SessionCatalog);
            }

            var registry = new AltaCommandRegistry();
            services.Add(registry);
            fixture.Dispatcher = new AltaCommandDispatcher(registry, services);
            return Task.FromResult(fixture);
        }

        public async Task<ProjectDescriptor> AddProjectAsync(string name)
        {
            var folder = Path.Combine(Root, "code", name);
            Directory.CreateDirectory(folder);
            return await Projects.UpsertFromPathAsync(folder);
        }

        public Task<AltaCommandResult> RunAsync(params string[] args)
            => Dispatcher.InvokeAsync(args, caller: Mcp).AsTask();

        public Task<AltaCommandResult> OkAsync(params string[] args) => OkAsync(args, stdin: null);

        public async Task<AltaCommandResult> OkAsync(string[] args, string? stdin = null, AltaCallerIdentity? caller = null, string? cwd = null)
        {
            var result = await Dispatcher.InvokeAsync(args, stdin, caller ?? Mcp, cwd);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            return result;
        }

        public async Task<JsonElement> FailsAsync(int exitCode, string code, params string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args, caller: Mcp);
            Assert.AreEqual(exitCode, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            var error = Records(result).Single(static line => Type(line) == "alta.error");
            Assert.AreEqual(code, error.GetProperty("code").GetString(), string.Join(' ', args));
            return error;
        }

        public JsonElement Single(AltaCommandResult result, string type)
            => Records(result).Single(line => Type(line) == type);

        public void Dispose()
        {
            for (var attempt = 0; Directory.Exists(Root); attempt++)
            {
                try
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(Root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
