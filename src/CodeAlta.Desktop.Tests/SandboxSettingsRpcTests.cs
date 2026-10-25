using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>The sandbox as the window reads and changes it: the setting of every session, the profiles and the scopes.</summary>
[TestClass]
public sealed class SandboxSettingsRpcTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    public async Task ThePage_ReadsTheProfiles_AndSavesWhatResolves()
    {
        await using var f = await Fixture.CreateAsync();
        Assert.AreEqual("stale_epoch", (await f.Service.ReadAsync(new("other"), CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await new SandboxSettingsService().ReadAsync(new(Epoch), CancellationToken.None)).Status);

        var read = await f.Service.ReadAsync(new(Epoch), CancellationToken.None);
        Assert.AreEqual("ok", read.Status);
        Assert.IsNotNull(read.Availability);
        CollectionAssert.AreEqual(new[] { "workspace", "strict" }, read.Profiles.Where(static profile => profile.BuiltIn).Select(static profile => profile.Name).ToArray());
        Assert.Contains("nuget.org", read.Profiles[0].Hosts);

        var mine = new SandboxProfileItem("mine", false, "workspace", null, "never", ["pkgs.dev.azure.com"], [], [], [], [], [], []);
        Assert.AreEqual("ok", f.Service.SaveProfile(new(Epoch, null, mine)).Status);
        Assert.AreEqual("exists", f.Service.SaveProfile(new(Epoch, null, mine)).Status, "a new profile does not replace another");
        Assert.AreEqual("invalid", f.Service.SaveProfile(new(Epoch, null, mine with { Name = "strict" })).Status, "the profiles of CodeAlta are extended, not replaced");
        var broken = f.Service.SaveProfile(new(Epoch, null, mine with { Name = "broken", Extends = "nowhere" }));
        Assert.AreEqual("invalid", broken.Status);
        Assert.Contains("nowhere", broken.Message!);

        Assert.AreEqual("ok", f.Service.SaveAllSessions(new(Epoch, "mine")).Status);
        Assert.AreEqual("invalid", f.Service.SaveAllSessions(new(Epoch, "nowhere")).Status);
        Assert.AreEqual("ok", f.Service.SaveProfile(new(Epoch, "mine", mine with { Name = "ours" })).Status);
        read = await f.Service.ReadAsync(new(Epoch), CancellationToken.None);
        Assert.AreEqual("ours", read.AllSessions, "a renamed profile stays the one of every session");
        var ours = read.Profiles.Single(static profile => profile.Name == "ours");
        Assert.AreEqual(("workspace", "never", false), (ours.Extends, ours.Outside, ours.BuiltIn));
        Assert.IsFalse(read.Profiles.Any(static profile => profile.Name is "broken" or "mine"), "a profile that does not resolve is not kept");

        Assert.AreEqual("ok", f.Service.DeleteProfile(new(Epoch, "ours")).Status);
        Assert.IsFalse((await f.Service.ReadAsync(new(Epoch), CancellationToken.None)).Profiles.Any(static profile => profile.Name == "ours"));
    }

    [TestMethod]
    public async Task AScope_ChoosesTheSandbox_AndTheDraftOfANewSessionFollowsIt()
    {
        await using var f = await Fixture.CreateAsync();
        var project = await f.Host.ProjectCatalog.UpsertFromPathAsync(f.Host.CurrentProject!.ProjectPath);

        var set = await f.Service.SetScopeAsync(new(Epoch, "project", project.Id, null, "strict"), CancellationToken.None);
        Assert.AreEqual("ok", set.Status, set.Message);
        Assert.AreEqual("ok", (await f.Service.SetScopeAsync(new(Epoch, "space", SpaceDescriptor.DefaultId, null, "workspace"), CancellationToken.None)).Status);
        Assert.AreEqual("invalid", (await f.Service.SetScopeAsync(new(Epoch, "project", project.Id, null, "nowhere"), CancellationToken.None)).Status);
        Assert.AreEqual("not_found", (await f.Service.SetScopeAsync(new(Epoch, "provider", "missing", null, "strict"), CancellationToken.None)).Status);
        Assert.AreEqual("strict", (await f.Host.ProjectCatalog.GetByIdAsync(project.Id))!.Sandbox);

        var scopes = (await f.Service.ReadAsync(new(Epoch), CancellationToken.None)).Scopes;
        Assert.IsTrue(scopes.Any(scope => scope is { Kind: "project", Profile: "strict" } && scope.Id == project.Id));
        Assert.IsTrue(scopes.Any(static scope => scope is { Kind: "space", Profile: "workspace" }));

        var draft = await f.Service.DraftAsync(new(Epoch, null, null, project.Id, SpaceDescriptor.DefaultId, null), CancellationToken.None);
        Assert.AreEqual(("strict", "project"), (draft.Profile, draft.Source), "the project comes before its space");
        CollectionAssert.AreEqual(new[] { "off", "workspace", "strict" }, draft.Choices.ToArray());
        var own = await f.Service.DraftAsync(new(Epoch, null, null, project.Id, SpaceDescriptor.DefaultId, "off"), CancellationToken.None);
        Assert.AreEqual((null, "session"), (own.Profile, own.Source));

        Assert.AreEqual("ok", (await f.Service.SetScopeAsync(new(Epoch, "project", project.Id, null, null), CancellationToken.None)).Status);
        Assert.IsNull((await f.Host.ProjectCatalog.GetByIdAsync(project.Id))!.Sandbox);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root;

        private Fixture(string root, CodeAltaHost host, SandboxSettingsService service) => (_root, Host, Service) = (root, host, service);

        public CodeAltaHost Host { get; }

        public SandboxSettingsService Service { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-sandbox-rpc-" + Guid.NewGuid().ToString("N"))).FullName;
            var global = Directory.CreateDirectory(Path.Combine(root, "global")).FullName;
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            var host = await CodeAltaHost.CreateAsync(new CodeAltaHostOptions { GlobalRoot = global, CurrentProjectPath = project, IsHeadless = true, StartPlugins = false },
                CancellationToken.None);
            var service = new SandboxSettingsService(new CodeAltaConfigStore(host.CatalogOptions), host.RuntimeService, host.Commands, host.ProjectCatalog, host.SpaceCatalog, Epoch);
            return new(root, host, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
