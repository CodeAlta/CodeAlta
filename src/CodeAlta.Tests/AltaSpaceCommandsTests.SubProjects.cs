using System.Text.Json;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

public sealed partial class AltaSpaceCommandsTests
{
    private static readonly string[] ManifestLeaves = ["show", "scan", "add", "set", "remove", "label"];

    [TestMethod]
    public async Task ProjectManifest_CommandsExist_AndASandboxOnlyReadsTheMap()
    {
        using var fixture = await Fixture.CreateAsync();
        var tools = (await fixture.RunAsync("tool", "list")).Stdout;
        foreach (var leaf in ManifestLeaves)
        {
            Assert.IsTrue((await fixture.RunAsync("project", "manifest", leaf, "--help")).IsHelp, leaf);
            StringAssert.Contains(tools, $"project manifest {leaf}");
        }

        StringAssert.Contains((await fixture.RunAsync("project", "--help")).Stdout, "alta project manifest --help");
        StringAssert.Contains((await fixture.RunAsync("project", "manifest", "--help")).Stdout, "codealta-monorepo");
        Assert.IsTrue(AltaSandboxGate.Allows("alta project manifest show"));
        Assert.IsTrue(AltaSandboxGate.Allows("alta project manifest scan"));
        Assert.IsFalse(AltaSandboxGate.Allows("alta project manifest add"));
        Assert.IsFalse(AltaSandboxGate.Allows("alta project manifest label"));
    }

    [TestMethod]
    public async Task ProjectManifest_AnAgentSetsAMonorepoUp_AndTheProjectsFollow()
    {
        using var fixture = await Fixture.CreateAsync();
        var contoso = await fixture.AddProjectAsync("contoso");
        foreach (var folder in new[] { "services/billing", "apps/web", "apps/admin" }) Directory.CreateDirectory(Path.Combine(contoso.ProjectPath, folder));

        var label = fixture.Single(await fixture.OkAsync("project", "manifest", "label", "backend", "--project", contoso.Slug, "--name", "Backend", "--color", "#2d72d2"),
            "alta.project.manifest.changed");
        Assert.IsTrue(label.GetProperty("changed").GetBoolean());
        var added = fixture.Single(await fixture.OkAsync("project", "manifest", "add", "services/billing", "--project", contoso.Slug, "--name", "Billing", "--label", "backend"),
            "alta.project.manifest.changed");
        Assert.AreEqual("billing", added.GetProperty("key").GetString());
        await fixture.OkAsync("project", "manifest", "add", Path.Combine(contoso.ProjectPath, "apps", "web"), "--project", contoso.Slug, "--label", "frontend", "--write", "folder");
        await fixture.OkAsync("project", "manifest", "add", "apps/admin", "--project", contoso.Slug, "--optional");
        await fixture.FailsAsync(AltaExitCodes.Usage, "manifest.invalid", "project", "manifest", "add", "apps/none", "--project", contoso.Slug);
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalidWrite", "project", "manifest", "add", "apps/admin", "--project", contoso.Slug, "--write", "all");

        var refs = fixture.Single(await fixture.OkAsync("project", "list", "--parent", contoso.Slug), "alta.project.refs");
        CollectionAssert.AreEquivalent(new[] { "contoso-billing", "contoso-web" }, Slugs(refs), "An optional sub-project is listed for those who add it.");
        var items = Records(await fixture.OkAsync("project", "list", "--declared", "--detailed")).Where(static line => Type(line) == "alta.project.item").ToArray();
        Assert.AreEqual(2, items.Length);
        var billing = items.Single(static item => item.GetProperty("key").GetString() == "billing");
        Assert.AreEqual((contoso.Id, "services/billing"), (billing.GetProperty("parentId").GetString(), billing.GetProperty("subPath").GetString()));
        CollectionAssert.AreEqual(new[] { "contoso.backend" }, Strings(billing.GetProperty("declaredLabels")));
        Assert.AreEqual("folder", items.Single(static item => item.GetProperty("key").GetString() == "web").GetProperty("write").GetString());

        var show = fixture.Single(await fixture.OkAsync("project", "show", contoso.Slug), "alta.project.detail");
        Assert.AreEqual(2, show.GetProperty("subProjectCount").GetInt32());
        var sub = fixture.Single(await fixture.OkAsync("project", "show", "contoso-billing"), "alta.project.detail");
        Assert.AreEqual(contoso.Id, sub.GetProperty("repository").GetProperty("projectId").GetString());

        var labels = Records(await fixture.OkAsync("label", "list", "--all")).Where(static line => Type(line) == "alta.label.item").ToArray();
        var backend = labels.Single(static item => item.GetProperty("id").GetString() == "contoso.backend");
        Assert.AreEqual(contoso.Id, backend.GetProperty("repository").GetString());
        Assert.AreEqual(1, backend.GetProperty("projectCount").GetInt32());
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "label.refused", "label", "set", "contoso.backend", "--name", "Other");
        var byLabel = fixture.Single(await fixture.OkAsync("project", "list", "--all", "--label", "contoso.backend"), "alta.project.refs");
        CollectionAssert.AreEqual(new[] { "contoso-billing" }, Slugs(byLabel));

        var manifest = fixture.Single(await fixture.OkAsync("project", "manifest", "show", contoso.Slug), "alta.project.manifest");
        Assert.IsTrue(manifest.GetProperty("exists").GetBoolean());
        Assert.AreEqual(3, manifest.GetProperty("projects").GetArrayLength());
        Assert.AreEqual(0, manifest.GetProperty("problems").GetArrayLength());

        await fixture.OkAsync("project", "manifest", "set", "billing", "--project", contoso.Slug, "--name", "Billing service");
        await fixture.OkAsync("project", "manifest", "remove", "web", "--project", contoso.Slug);
        refs = fixture.Single(await fixture.OkAsync("project", "list", "--parent", contoso.Slug), "alta.project.refs");
        CollectionAssert.AreEqual(new[] { "contoso-billing" }, Slugs(refs));
        Assert.AreEqual("Billing service", (await fixture.Projects.LoadAsync()).Single(static project => project.DeclaredKey == "billing").DisplayName);
        await fixture.FailsAsync(AltaExitCodes.NotFound, "manifest.notFound", "project", "manifest", "remove", "web", "--project", contoso.Slug);
        Assert.IsTrue(fixture.View.Changes > 0, "The window reads the projects again.");
    }

    [TestMethod]
    public async Task ProjectManifest_AMapInsideASubProject_IsReadAndWrittenAtItsLevel()
    {
        using var fixture = await Fixture.CreateAsync();
        // platform-map declares repos/billing, a git clone of its own with a map of its own (#433).
        var platform = await fixture.AddProjectAsync("platform-map");
        var billingFolder = Directory.CreateDirectory(Path.Combine(platform.ProjectPath, "repos", "billing")).FullName;
        Directory.CreateDirectory(Path.Combine(billingFolder, ".git"));
        foreach (var folder in new[] { "services/invoices-api", "libs/ledger", "libs/pdf" }) Directory.CreateDirectory(Path.Combine(billingFolder, folder));
        await fixture.OkAsync("project", "manifest", "add", "repos/billing", "--project", platform.Slug);
        await fixture.OkAsync("project", "manifest", "add", "services/invoices-api", "--project", "platform-map-billing", "--label", "apps");
        await fixture.OkAsync("project", "manifest", "add", "libs/ledger", "--project", "platform-map-billing", "--label", "libs");
        Assert.IsTrue(File.Exists(Path.Combine(billingFolder, ".alta", "projects.toml")), "The map of billing is written in billing.");

        var refs = fixture.Single(await fixture.OkAsync("project", "list", "--parent", "platform-map-billing"), "alta.project.refs");
        CollectionAssert.AreEquivalent(new[] { "platform-map-billing-invoices-api", "platform-map-billing-ledger" }, Slugs(refs));
        var billing = fixture.Single(await fixture.OkAsync("project", "show", "platform-map-billing"), "alta.project.detail");
        Assert.AreEqual(2, billing.GetProperty("subProjectCount").GetInt32());
        Assert.AreEqual(platform.Id, billing.GetProperty("repository").GetProperty("projectId").GetString());
        var invoices = fixture.Single(await fixture.OkAsync("project", "show", "platform-map-billing-invoices-api"), "alta.project.detail");
        Assert.AreEqual(platform.Id, invoices.GetProperty("outerRepositories")[0].GetProperty("projectId").GetString());

        var outer = fixture.Single(await fixture.OkAsync("project", "manifest", "show", platform.Slug), "alta.project.manifest");
        Assert.AreEqual(2, outer.GetProperty("projects")[0].GetProperty("subProjects").GetInt32());
        var billingId = billing.GetProperty("projectId").GetString()!;
        // A session of billing reads and writes the map of billing; one of its sub-projects, the map of billing too.
        foreach (var caller in new[] { billingId, invoices.GetProperty("projectId").GetString()! })
        {
            var identity = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = caller };
            var inner = fixture.Single(await fixture.OkAsync(["project", "manifest", "show"], caller: identity), "alta.project.manifest");
            Assert.AreEqual((billingId, platform.Id), (inner.GetProperty("projectId").GetString(), inner.GetProperty("parentId").GetString()));
            Assert.AreEqual(JsonValueKind.Null, inner.GetProperty("claimedBy").ValueKind, "The map of a sub-project counts.");
            Assert.IsTrue(inner.GetProperty("projects").EnumerateArray().All(static entry => entry.GetProperty("projectId").ValueKind == JsonValueKind.String));
            Assert.AreEqual(0, inner.GetProperty("problems").GetArrayLength());
        }

        await fixture.OkAsync(["project", "manifest", "add", "libs/pdf"], caller: new AltaCallerIdentity { Kind = "agent", SourceSessionId = "session-1", SourceProjectId = billingId });
        StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(billingFolder, ".alta", "projects.toml")), "libs/pdf");
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(platform.ProjectPath, ".alta", "projects.toml"))).Contains("pdf", StringComparison.Ordinal));
        var labels = Records(await fixture.OkAsync("label", "list", "--all")).Where(static line => Type(line) == "alta.label.item").ToArray();
        Assert.AreEqual(billingId, labels.Single(static item => item.GetProperty("id").GetString() == "platform-map-billing.apps").GetProperty("repository").GetString());
    }

    [TestMethod]
    public async Task ProjectManifest_ScanProposesFolders_AndWritesNothing()
    {
        using var fixture = await Fixture.CreateAsync();
        var contoso = await fixture.AddProjectAsync("contoso");
        void Create(string file, string text = "{}")
        {
            var path = Path.Combine(contoso.ProjectPath, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        Create("pnpm-workspace.yaml", "packages:\n  - packages/*\n");
        Create("packages/ui/package.json");
        Create("packages/ui/node_modules/dep/package.json");
        Create("services/billing/Billing.csproj", "<Project />");
        Create("vendor/lib/.git", "gitdir: ../../.git/modules/vendor/lib\n");
        Create(".gitmodules", "[submodule \"lib\"]\n\tpath = vendor/lib\n\turl = https://example.com/lib.git\n");
        Create("docs/readme.md", "# Docs");

        var result = Records(await fixture.OkAsync("project", "manifest", "scan", "--project", contoso.Slug));
        var candidates = result.Where(static line => Type(line) == "alta.project.manifest.candidate").Select(static line => line.GetProperty("path").GetString()).ToArray();
        CollectionAssert.AreEquivalent(new[] { "packages/ui", "services/billing", "vendor/lib" }, candidates);
        Assert.IsTrue(result.Single(static line => Type(line) == "alta.project.manifest.candidate" && line.GetProperty("path").GetString() == "vendor/lib")
            .GetProperty("submodule").GetBoolean());
        var workspaces = result.Where(static line => Type(line) == "alta.project.manifest.workspace").Select(static line => line.GetProperty("kind").GetString()).ToArray();
        CollectionAssert.IsSubsetOf(new[] { "pnpm", "git-submodules" }, workspaces);
        var summary = result.Single(static line => Type(line) == "alta.project.manifest.scan");
        CollectionAssert.AreEqual(new[] { "vendor/lib" }, Strings(summary.GetProperty("submodules")));

        var globbed = Records(await fixture.OkAsync("project", "manifest", "scan", "--project", contoso.Slug, "--glob", "services/*"))
            .Where(static line => Type(line) == "alta.project.manifest.candidate").Select(static line => line.GetProperty("path").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "services/billing" }, globbed);
        Assert.IsFalse(File.Exists(Path.Combine(contoso.ProjectPath, ".alta", "projects.toml")));
    }
}
