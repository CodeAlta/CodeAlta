using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class LabelsRpcTests
{
    private const string Epoch = "5f1c2d3e-8a7b-4c6d-9e0f-1a2b3c4d5e6f";

    [TestMethod]
    public async Task TheLabels_AreListedWithTheirProjects_AndChangedFromThePage()
    {
        using var fixture = new Fixture();
        var alpha = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("alpha"));
        var beta = await fixture.Projects.UpsertFromPathAsync(fixture.Folder("beta"));
        var changes = 0;
        using var watch = fixture.View.Watch(value => { if (value.Kind == "changed") changes++; });

        var empty = await fixture.Service.ListAsync(new(Epoch), default);
        Assert.AreEqual(("ok", 0), (empty.Status, empty.Labels.Count));
        var infra = await fixture.Service.CreateAsync(new(Epoch, "Infra", null, "cube", "#2d72d2", null, true, "Servers.", [alpha.Id]), default);
        Assert.AreEqual(("ok", "infra", true), (infra.Status, infra.Label!.Id, infra.Label.Favorite));
        var helm = await fixture.Service.CreateAsync(new(Epoch, "Helm", "infra", null, null, null, false, null, null), default);
        Assert.AreEqual(("ok", "Infra › Helm"), (helm.Status, helm.Label!.Path));
        Assert.AreEqual("ok", (await fixture.Service.AssignAsync(new(Epoch, beta.Id, ["helm", "infra"], null), default)).Status);
        Assert.AreEqual(3, changes, "Every change asks the windows to read the labels again.");

        var listed = await fixture.Service.ListAsync(new(Epoch), default);
        CollectionAssert.AreEqual(new[] { "infra", "helm" }, listed.Labels.Select(static label => label.Id).ToArray());
        CollectionAssert.AreEqual(new[] { alpha.Id, beta.Id }, listed.Labels[0].ProjectIds.ToArray());
        Assert.AreEqual(("Servers.", "cube", "#2d72d2", 1, (string?)null), (listed.Labels[0].Description, listed.Labels[0].Icon, listed.Labels[0].Color, listed.Labels[0].Order, listed.Labels[0].Parent));
        Assert.AreEqual("infra", listed.Labels[1].Parent);
        StringAssert.Contains(JsonSerializer.Serialize(listed, DesktopJsonContext.Default.LabelsResponse), "\"projectIds\":[");

        var renamed = await fixture.Service.UpdateAsync(new(Epoch, "infra", "Infrastructure", null, null, "", false, null), default);
        Assert.AreEqual(("ok", "infra", "Infrastructure", (string?)null, false), (renamed.Status, renamed.Label!.Id, renamed.Label.Name, renamed.Label.Color, renamed.Label.Favorite));
        Assert.AreEqual("ok", (await fixture.Service.MoveAsync(new(Epoch, "helm", null, 0), default)).Status);
        CollectionAssert.AreEqual(new[] { "helm", "infra" }, (await fixture.Service.ListAsync(new(Epoch), default)).Labels.Select(static label => label.Id).ToArray());

        // What cannot be done says why, and writes nothing.
        Assert.AreEqual("invalid", (await fixture.Service.UpdateAsync(new(Epoch, "infra", null, null, null, "blue", null, null), default)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.MoveAsync(new(Epoch, "infra", "infra", 0), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.AssignAsync(new(Epoch, beta.Id, ["missing"], null), default)).Status);
        Assert.AreEqual("not_found", (await fixture.Service.DeleteAsync(new(Epoch, "missing"), default)).Status);

        Assert.AreEqual("ok", (await fixture.Service.DeleteAsync(new(Epoch, "infra"), default)).Status);
        Assert.IsFalse((await fixture.Projects.LoadAsync()).Any(static project => project.Labels.Contains("infra")));
    }

    [TestMethod]
    public async Task ALabelForSessions_IsListedAsSuch_AndOnlyItGoesToASession()
    {
        using var fixture = new Fixture();
        var given = new List<string>();
        var service = new LabelsService(new SpaceCatalog(fixture.Projects).Labels, fixture.View, Epoch)
        {
            AssignSession = (sessionId, add, remove, _) => Task.FromResult<IReadOnlyList<string>?>(sessionId == "s1"
                ? [.. given.Except(remove).Concat(add).Distinct()] : null),
        };
        var audit = await service.CreateAsync(new(Epoch, "Audit", null, "shield", null, null, false, null, null) { ForSessions = true }, default);
        Assert.AreEqual(("ok", (bool?)true), (audit.Status, audit.Label!.ForSessions));
        Assert.IsNull((await service.CreateAsync(new(Epoch, "Infra", null, null, null, null, false, null, null), default)).Label!.ForSessions);
        StringAssert.Contains(JsonSerializer.Serialize((await service.ListAsync(new(Epoch), default)).Labels[0], DesktopJsonContext.Default.LabelItem), "\"forSessions\":true");

        var assigned = await service.AssignSessionAsync(new(Epoch, "s1", ["audit"], null), default);
        Assert.AreEqual("ok", assigned.Status);
        CollectionAssert.AreEqual(new[] { "audit" }, assigned.Labels.ToArray());
        Assert.AreEqual("invalid", (await service.AssignSessionAsync(new(Epoch, "s1", ["infra"], null), default)).Status, "A label for projects is not given to a session.");
        Assert.AreEqual("not_found", (await service.AssignSessionAsync(new(Epoch, "s1", ["missing"], null), default)).Status);
        Assert.AreEqual("not_found", (await service.AssignSessionAsync(new(Epoch, "s2", ["audit"], null), default)).Status);
        Assert.AreEqual("unavailable", (await fixture.Service.AssignSessionAsync(new(Epoch, "s1", ["audit"], null), default)).Status,
            "A window without the runtime of the sessions does not change them.");
    }

    [TestMethod]
    public async Task ARequestOfAnotherHost_OrOfAWindowWithoutLabels_IsRefused()
    {
        using var fixture = new Fixture();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ListAsync(new("other"), default)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.CreateAsync(new("other", "Infra", null, null, null, null, false, null, null), default)).Status);
        Assert.IsFalse(Directory.Exists(fixture.Options.LabelsRoot));

        var none = new LabelsService();
        Assert.AreEqual("unavailable", (await none.ListAsync(new(Epoch), default)).Status);
        Assert.AreEqual("unavailable", (await none.MoveAsync(new(Epoch, "infra", null, 0), default)).Status);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "codealta-labels-rpc-" + Guid.NewGuid().ToString("N"));
        public CatalogOptions Options { get; }
        public ProjectCatalog Projects { get; }
        public DesktopSpaceView View { get; } = new();
        public LabelsService Service { get; }

        public Fixture()
        {
            Options = new CatalogOptions { GlobalRoot = Directory.CreateDirectory(Path.Combine(Root, "global")).FullName };
            Projects = new ProjectCatalog(Options);
            Service = new LabelsService(new SpaceCatalog(Projects).Labels, View, Epoch);
        }

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root, "folders", name)).FullName;

        public void Dispose() => Directory.Delete(Root, true);
    }
}
