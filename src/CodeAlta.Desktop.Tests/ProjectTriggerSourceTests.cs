using System.Text.Json;
using CodeAlta.Automations;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Git;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class ProjectTriggerSourceTests
{
    [TestMethod]
    public async Task TheProjectTrigger_SaysWhatWasAddedArchivedAndRemoved_SinceItsCursor()
    {
        using var root = new AutomationTempRoot();
        var projects = new List<ProjectDescriptor> { Project(root, "alpha") };
        var source = new ProjectTriggerSource(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([.. projects]),
            (folder, _) => ValueTask.FromResult<GitRepositoryReference?>(folder.EndsWith("beta", StringComparison.Ordinal) ? GitRepositoryReference.GitHub("org", "beta") : null));

        // The first reading says nothing: what is there already is not an event.
        var first = await source.ReadAsync(Context(), null, default);
        Assert.AreEqual(0, first.Events.Count);

        // A project added, by alta in this process, is said once with what added it and where its repository is.
        var beta = Project(root, "beta");
        projects.Add(beta);
        source.OnCatalogChanged(new ProjectCatalogChange(beta, ProjectCatalogChangeKind.Added, ProjectOrigins.AltaAdd));
        var added = await source.ReadAsync(Context(), first.Cursor, default);
        var item = added.Events.Single();
        Assert.AreEqual((ProjectTriggerSource.Added, "Project Beta added", "https://github.com/org/beta"), (item.Event, item.Title, item.Url));
        CollectionAssert.Contains(item.Fields.ToArray(), new KeyValuePair<string, string>("Added by", ProjectOrigins.AltaAdd));
        using (var json = JsonDocument.Parse(item.DataJson!))
        {
            var data = json.RootElement;
            Assert.AreEqual(("id-beta", "beta", "Beta", beta.ProjectPath, "org/beta", "alta project add"),
                (data.GetProperty("projectId").GetString(), data.GetProperty("slug").GetString(), data.GetProperty("name").GetString(), data.GetProperty("path").GetString(),
                 data.GetProperty("repository").GetString(), data.GetProperty("addedBy").GetString()));
        }

        // Nothing changed: no event, and the same cursor.
        var same = await source.ReadAsync(Context(), added.Cursor, default);
        Assert.AreEqual((0, added.Cursor), (same.Events.Count, same.Cursor));

        // Archived, restored and archived again: each one is an event of its own; what added it this time is not known.
        beta.Archived = true;
        var archived = await source.ReadAsync(Context(), same.Cursor, default);
        beta.Archived = false;
        var restored = await source.ReadAsync(Context(), archived.Cursor, default);
        beta.Archived = true;
        var again = await source.ReadAsync(Context(), restored.Cursor, default);
        Assert.AreEqual(ProjectTriggerSource.Archived, archived.Events.Single().Event);
        Assert.AreEqual(ProjectTriggerSource.Added, restored.Events.Single().Event);
        CollectionAssert.Contains(restored.Events.Single().Fields.ToArray(), new KeyValuePair<string, string>("Added by", ProjectOrigins.Unknown));
        Assert.AreNotEqual(archived.Events.Single().Id, again.Events.Single().Id, "archived twice is two events");

        // Removed: it is described as it was last seen, and its folder is not read.
        projects.Remove(beta);
        var removed = (await source.ReadAsync(Context(), again.Cursor, default)).Events.Single();
        Assert.AreEqual((ProjectTriggerSource.Removed, "Project Beta removed", null), (removed.Event, removed.Title, removed.Url));
        using (var json = JsonDocument.Parse(removed.DataJson!))
            Assert.AreEqual(("beta", true), (json.RootElement.GetProperty("slug").GetString(), json.RootElement.GetProperty("archived").GetBoolean()));
    }

    [TestMethod]
    public async Task WithASpace_TheProjectTrigger_SeesTheProjectsThatJoinAndLeaveIt()
    {
        using var root = new AutomationTempRoot();
        var alpha = Project(root, "alpha");
        var transient = Project(root, "transient");
        transient.SourcePath = null;
        var projects = new List<ProjectDescriptor> { alpha, transient };
        var source = new ProjectTriggerSource(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([.. projects]), (_, _) => ValueTask.FromResult<GitRepositoryReference?>(null));
        var work = Context(("space", "work"));

        var first = await source.ReadAsync(work, null, default);
        var nothing = await source.ReadAsync(work, first.Cursor, default);
        Assert.AreEqual(0, nothing.Events.Count, "alpha is not in the space");

        alpha.Spaces.Add("work");
        transient.Spaces.Add("work");
        var joined = await source.ReadAsync(work, nothing.Cursor, default);
        Assert.AreEqual(("added", "Project Alpha added"), (joined.Events.Single().Event, joined.Events.Single().Title), "a project of no file of the catalog is not one of it");
        alpha.Spaces.Clear();
        Assert.AreEqual(ProjectTriggerSource.Removed, (await source.ReadAsync(work, joined.Cursor, default)).Events.Single().Event);

        // The default space holds every project.
        var all = Context(("space", SpaceDescriptor.DefaultId));
        var start = await source.ReadAsync(all, null, default);
        projects.Add(Project(root, "gamma"));
        Assert.AreEqual(1, (await source.ReadAsync(all, start.Cursor, default)).Events.Count);

        // A cursor that cannot be read starts again from now.
        var broken = await source.ReadAsync(all, "{not json", default);
        Assert.AreEqual(0, broken.Events.Count);
        Assert.IsNotNull(broken.Cursor);
    }

    [TestMethod]
    public void AChangeOfTheCatalog_AsksForALookAtOnce()
    {
        var source = new ProjectTriggerSource(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]));
        var raised = 0;
        source.Signal.Raised += () => raised++;
        source.OnCatalogChanged(new ProjectCatalogChange(new ProjectDescriptor { Id = "x", Slug = "x", Name = "x", ProjectPath = "x" }, ProjectCatalogChangeKind.Deleted, null));
        Assert.AreEqual(1, raised);
        Assert.AreEqual(AutomationSourceTypes.Project, source.Contribution.Type);
        CollectionAssert.AreEqual(new[] { "added", "archived", "removed" }, source.Contribution.Events.Select(static type => type.Id).ToArray());
    }

    [TestMethod]
    public async Task AnAutomationOnTheProjectTrigger_RunsForTheNewProject_WithItAsTheEventOfItsScript()
    {
        using var root = new AutomationTempRoot();
        var projects = new List<ProjectDescriptor>();
        var clock = new AutomationClock { Now = new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero) };
        var runner = new FakeAutomationRunner();
        var configPath = Path.Combine(root.Path, "home", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            [automations.0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77]
            name = "Onboard"
            prompt = "Describe the new project."
            triggers = [{ type = "project", on = ["added"] }]
            """);
        await using var service = new AutomationService(configPath, _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([.. projects]),
            new AutomationStateStore(Path.Combine(root.Path, "state.json"), false), runner, clock, TimeZoneInfo.Utc);
        var source = new ProjectTriggerSource(_ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([.. projects]), (_, _) => ValueTask.FromResult<GitRepositoryReference?>(null));
        service.Sources.AddBuiltIn(source.Contribution);
        await service.RefreshAsync();

        service.Tick(clock.Now);
        await service.SourceReadsAsync();
        Assert.IsNull(service.WatchProblem("0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77"));
        var gamma = Project(root, "gamma");
        projects.Add(gamma);
        source.OnCatalogChanged(new ProjectCatalogChange(gamma, ProjectCatalogChangeKind.Added, ProjectOrigins.Window));
        clock.Now = clock.Now.AddSeconds(10);
        service.Tick(clock.Now);
        await service.SourceReadsAsync();

        var start = await runner.NextAsync();
        Assert.AreEqual("Project Gamma added", start.Detail);
        StringAssert.Contains(start.Prompt, "This automation was started by the Project trigger (project): Added.\nTitle: Project Gamma added\nProject: Gamma (gamma)\n");
        StringAssert.Contains(start.EventJson, "\"addedBy\":\"window\"");
        StringAssert.StartsWith(start.EventJson, "{\"on\":\"added\",\"id\":\"added:id-gamma:1\",\"projectId\":\"id-gamma\"");
    }

    private static PluginTriggerContext Context(params (string Key, string Value)[] settings) => new()
    {
        AutomationId = "a", TriggerKey = "k", Type = AutomationSourceTypes.Project, Events = ["added", "archived", "removed"],
        Settings = new PluginTriggerSettings(settings.Select(static pair => new KeyValuePair<string, PluginTriggerValue>(pair.Key, PluginTriggerValue.OfText(pair.Value)))),
    };

    private static ProjectDescriptor Project(AutomationTempRoot root, string name) => new()
    {
        Id = "id-" + name, Slug = name, Name = name, DisplayName = char.ToUpperInvariant(name[0]) + name[1..],
        ProjectPath = Directory.CreateDirectory(Path.Combine(root.Path, "projects", name)).FullName, SourcePath = Path.Combine(root.Path, "catalog", name + ".md"),
    };
}
