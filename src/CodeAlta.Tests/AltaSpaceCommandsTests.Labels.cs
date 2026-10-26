using System.Text.Json;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

public sealed partial class AltaSpaceCommandsTests
{
    private static readonly string[] LabelLeaves = ["list", "show", "create", "set", "move", "delete", "assign", "unassign"];

    [TestMethod]
    public async Task LabelGroup_ExistsWithASpaceCatalog()
    {
        using var none = await Fixture.CreateAsync(spaces: false, view: false);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync("label", "list")).ExitCode);

        using var fixture = await Fixture.CreateAsync();
        var tools = (await fixture.RunAsync("tool", "list")).Stdout;
        foreach (var leaf in LabelLeaves)
        {
            Assert.IsTrue((await fixture.RunAsync("label", leaf, "--help")).IsHelp, leaf);
            StringAssert.Contains(tools, $"label {leaf}");
        }

        StringAssert.Contains((await fixture.RunAsync("label", "--help")).Stdout, "alta project list --label");
        Assert.IsTrue(AltaSandboxGate.Allows("alta label list"));
        Assert.IsTrue(AltaSandboxGate.Allows("alta label show"));
        Assert.IsFalse(AltaSandboxGate.Allows("alta label create"));
        Assert.IsFalse(AltaSandboxGate.Allows("alta label assign"));
    }

    [TestMethod]
    public async Task Label_CreateNestAssignAndListTheProjects()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        var beta = await fixture.AddProjectAsync("beta");
        var gamma = await fixture.AddProjectAsync("gamma");

        var infra = fixture.Single(await fixture.OkAsync("label", "create", "Infra", "--icon", "cube", "--color", "#2d72d2", "--favorite"), "alta.label.created");
        Assert.AreEqual("infra", infra.GetProperty("id").GetString());
        Assert.IsTrue(infra.GetProperty("favorite").GetBoolean());
        Assert.IsFalse(infra.TryGetProperty("space", out _), "Created by the user, a label is in every space.");
        var helm = fixture.Single(await fixture.OkAsync(["label", "create", "Helm charts", "--parent", "Infra", "--stdin"], "Charts of the platform."), "alta.label.created");
        Assert.AreEqual("helm-charts", helm.GetProperty("id").GetString());
        Assert.AreEqual("infra", helm.GetProperty("parent").GetString());
        Assert.AreEqual("Infra › Helm charts", helm.GetProperty("path").GetString());
        Assert.AreEqual("Charts of the platform.", helm.GetProperty("description").GetString());
        await fixture.OkAsync("label", "create", "Terraform");
        Assert.AreEqual(3, fixture.View.Changes);

        var assigned = fixture.Single(await fixture.OkAsync("label", "assign", "infra", alpha.Slug, beta.ProjectPath), "alta.label.projects");
        CollectionAssert.AreEquivalent(new[] { alpha.Id, beta.Id }, Strings(assigned.GetProperty("assigned")));
        await fixture.OkAsync("label", "assign", "Infra/Helm charts", gamma.Slug);
        var again = fixture.Single(await fixture.OkAsync("label", "assign", "helm", gamma.Slug), "alta.label.projects");
        CollectionAssert.AreEqual(new[] { gamma.Id }, Strings(again.GetProperty("unchanged")));

        var list = Records(await fixture.OkAsync("label", "list"));
        var items = list.Where(static line => Type(line) == "alta.label.item").ToArray();
        CollectionAssert.AreEqual(new[] { "infra", "helm-charts", "terraform" }, items.Select(static item => item.GetProperty("id").GetString()).ToArray());
        Assert.AreEqual(2, items[0].GetProperty("projectCount").GetInt32());
        Assert.AreEqual(1, items[1].GetProperty("projectCount").GetInt32());

        // A label shows the projects of its sub-labels too, and the project list filters on a label.
        var detail = fixture.Single(await fixture.OkAsync("label", "show", "infra"), "alta.label.detail");
        CollectionAssert.AreEqual(new[] { "helm-charts" }, Strings(detail.GetProperty("subLabels")));
        var projects = detail.GetProperty("projects").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { alpha.Id, beta.Id, gamma.Id }, projects.Select(static project => project.GetProperty("id").GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "helm-charts" }, Strings(projects[2].GetProperty("labels")));
        Assert.AreEqual(gamma.ProjectPath, projects[2].GetProperty("path").GetString());
        CollectionAssert.AreEqual(new[] { alpha.Slug, beta.Slug, gamma.Slug }, Slugs(fixture.Single(await fixture.OkAsync("project", "list", "--label", "infra"), "alta.project.refs")));
        CollectionAssert.AreEqual(new[] { alpha.Slug, beta.Slug }, Slugs(fixture.Single(await fixture.OkAsync("project", "list", "--label", "infra", "--direct"), "alta.project.refs")));
        var shown = fixture.Single(await fixture.OkAsync("project", "show", gamma.Slug), "alta.project.detail");
        CollectionAssert.AreEqual(new[] { "helm-charts" }, Strings(shown.GetProperty("labels")));

        var updated = fixture.Single(await fixture.OkAsync("label", "set", "infra", "--name", "Infrastructure", "--no-favorite", "--color", ""), "alta.label.updated");
        Assert.AreEqual("infra", updated.GetProperty("id").GetString());
        Assert.AreEqual("Infrastructure", updated.GetProperty("name").GetString());
        Assert.IsFalse(updated.GetProperty("favorite").GetBoolean());
        Assert.IsFalse(updated.TryGetProperty("color", out _));
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingChange", "label", "set", "infra");

        var moved = fixture.Single(await fixture.OkAsync("label", "move", "terraform", "--before", "infra"), "alta.label.moved");
        Assert.AreEqual(1, moved.GetProperty("order").GetInt32());
        fixture.Single(await fixture.OkAsync("label", "move", "helm", "--root", "--after", "terraform"), "alta.label.moved");
        CollectionAssert.AreEqual(new[] { "terraform", "helm-charts", "infra" },
            Records(await fixture.OkAsync("label", "list")).Where(static line => Type(line) == "alta.label.item").Select(static item => item.GetProperty("id").GetString()).ToArray());
        await fixture.FailsAsync(AltaExitCodes.Usage, "label.invalid", "label", "move", "infra", "--parent", "infra");

        var unassigned = fixture.Single(await fixture.OkAsync("label", "unassign", "infra", alpha.Slug), "alta.label.projects");
        CollectionAssert.AreEqual(new[] { alpha.Id }, Strings(unassigned.GetProperty("unassigned")));

        var deleted = fixture.Single(await fixture.OkAsync("label", "delete", "infra"), "alta.label.deleted");
        StringAssert.Contains(deleted.GetProperty("note").GetString(), "lost it");
        Assert.IsFalse((await fixture.Projects.LoadAsync()).Any(static project => project.Labels.Contains("infra")));
        await fixture.FailsAsync(AltaExitCodes.NotFound, "label.notFound", "label", "show", "infra");
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.RunAsync("label", "create")).ExitCode);
    }

    [TestMethod]
    public async Task Label_FavoriteTakesTrueOrFalse_AndRefusesAnyOtherValue()
    {
        using var fixture = await Fixture.CreateAsync();
        bool Favorite(string type, AltaCommandResult result) => fixture.Single(result, type).GetProperty("favorite").GetBoolean();

        Assert.IsFalse(Favorite("alta.label.created", await fixture.OkAsync("label", "create", "Infra", "--favorite=false")));
        Assert.IsTrue(Favorite("alta.label.updated", await fixture.OkAsync("label", "set", "infra", "--favorite")));
        // An agent writes the value: `false` takes the favorite off, as --no-favorite does, and never the reverse.
        Assert.IsFalse(Favorite("alta.label.updated", await fixture.OkAsync("label", "set", "infra", "--favorite=false")));
        Assert.IsTrue(Favorite("alta.label.updated", await fixture.OkAsync("label", "set", "infra", "--favorite=True")));
        Assert.IsTrue(Favorite("alta.label.created", await fixture.OkAsync("label", "create", "Ops", "--favorite=true")));

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidFavorite", "label", "set", "infra", "--favorite=no");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidFavorite", "label", "create", "Tools", "--favorite=yes");
        Assert.IsTrue(Favorite("alta.label.detail", await fixture.OkAsync("label", "show", "infra")), "A refused value changes nothing.");
        Assert.AreEqual(2, Records(await fixture.OkAsync("label", "list")).Count(static line => Type(line) == "alta.label.item"));
    }

    [TestMethod]
    public async Task Label_ASessionSeesAndChangesTheLabelsOfItsOwnSpace()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.OkAsync("space", "create", "--name", "Work");
        await fixture.OkAsync("space", "create", "--name", "Personal");
        var alpha = await fixture.AddProjectAsync("alpha");
        await fixture.OkAsync("space", "add", "work", alpha.Slug);
        await fixture.OkAsync("label", "create", "Home", "--space", "personal");
        await fixture.OkAsync("label", "create", "Shared");

        // A session of the space Work (its project is only there) creates its labels there, and does not see those of Personal.
        var session = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = alpha.Id };
        var created = fixture.Single(await fixture.OkAsync(["label", "create", "Clients"], caller: session), "alta.label.created");
        Assert.AreEqual("work", created.GetProperty("space").GetString());
        var seen = Records(await fixture.OkAsync(["label", "list"], caller: session)).Where(static line => Type(line) == "alta.label.item")
            .Select(static item => item.GetProperty("id").GetString()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "clients", "shared" }, seen);

        var refused = await fixture.RunAsync(["label", "create", "Other", "--space", "personal"], session);
        Assert.AreEqual(AltaExitCodes.Unsupported, refused.ExitCode);
        StringAssert.Contains(refused.Stdout, "space.notOwnSpace");
        Assert.AreEqual(AltaExitCodes.NotFound, (await fixture.RunAsync(["label", "delete", "home"], session)).ExitCode);

        // The user sees every label.
        Assert.AreEqual(3, Records(await fixture.OkAsync("label", "list", "--all")).Count(static line => Type(line) == "alta.label.item"));
        Assert.AreEqual(2, Records(await fixture.OkAsync("label", "list", "--space", "personal")).Count(static line => Type(line) == "alta.label.item"));
    }
}
