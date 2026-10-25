namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class SandboxSettingsTests
{
    [TestMethod]
    public void TheSandboxTable_IsReadAndSaved_KeepsTheOtherSettings_AndNothingRemovesIt()
    {
        using var temp = TempFolder.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        Assert.AreEqual(CodeAltaSandboxSettings.Empty.AllSessions, store.LoadGlobalSandbox().AllSessions);
        File.WriteAllText(store.ConfigPath, """
            # mine
            [tools]
            disabled = ["webget"]

            [sandbox]
            all_sessions = "workspace"

            [sandbox.profiles.lib-feed]
            extends = "workspace"
            read = ["~/code/XenoAtom"]
            hosts = ["pkgs.dev.azure.com"]
            local_ports = [5432]
            network = "Hosts"
            outside = "never"
            """);

        var read = store.LoadGlobalSandbox();
        Assert.AreEqual("workspace", read.AllSessions);
        var profile = read.Profiles.Single();
        Assert.AreEqual("lib-feed", profile.Name);
        Assert.AreEqual("workspace", profile.Extends);
        CollectionAssert.AreEqual(new[] { "pkgs.dev.azure.com" }, profile.Hosts!.ToArray());
        CollectionAssert.AreEqual(new[] { 5432 }, profile.LocalPorts!.ToArray());
        Assert.AreEqual("hosts", profile.Network);
        Assert.AreEqual("never", profile.Outside);
        Assert.IsNull(profile.Write, "a key the file does not set stays unset, for the profile it extends");

        store.SaveGlobalSandbox(read with { AllSessions = null, Profiles = [profile with { Hosts = ["nuget.org"] }] });
        var saved = store.LoadGlobalSandbox();
        Assert.IsNull(saved.AllSessions);
        CollectionAssert.AreEqual(new[] { "nuget.org" }, saved.Profiles.Single().Hosts!.ToArray());
        CollectionAssert.AreEquivalent(new[] { "webget" }, store.LoadGlobalDisabledTools().ToArray(), "the other settings stay");

        store.SaveGlobalSandbox(CodeAltaSandboxSettings.Empty);
        Assert.DoesNotContain("[sandbox", File.ReadAllText(store.ConfigPath));
    }

    [TestMethod]
    public void ARepository_CannotSetTheSandbox_ItsTableIsOnlyReported()
    {
        using var temp = TempFolder.Create();
        var root = Path.Combine(temp.Path, "global");
        var project = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(Path.Combine(project, ".alta"));
        Directory.CreateDirectory(root);
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
        Assert.IsFalse(store.ProjectSetsSandbox(project));
        File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), "[sandbox]\nall_sessions = \"off\"\n");

        Assert.IsTrue(store.ProjectSetsSandbox(project));
        Assert.IsNull(store.LoadGlobalSandbox().AllSessions);
    }

    [TestMethod]
    public void ProvidersAndModels_KeepTheirSandbox()
    {
        using var temp = TempFolder.Create();
        var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = temp.Path });
        File.WriteAllText(store.ConfigPath, """
            [providers.local]
            type = "openai-chat"
            api_key_env = "LOCAL_KEY"
            api_url = "http://127.0.0.1:1234/v1"
            sandbox = " strict "

            [providers.local.model_overrides."qwen"]
            sandbox = "workspace"
            """);

        var provider = store.LoadGlobal().Providers!.Values.Single();

        Assert.AreEqual("strict", provider.Sandbox);
        Assert.AreEqual("workspace", provider.ModelOverrides!["qwen"].Sandbox);
        Assert.AreEqual("workspace", CodeAltaConfigStore.CloneProviderDefinition(provider).ModelOverrides!["qwen"].Sandbox);
    }

    [TestMethod]
    public void SpacesAndProjects_WriteTheirSandboxInTheUsersFiles()
    {
        var serializer = new CatalogYamlSerializer();
        var space = SpaceDescriptor.CreateDefault();
        space.Settings = new SpaceSettings(new SpaceNewSessions(Sandbox: "strict"));

        var spaceText = serializer.SerializeSpaceMarkdown(space);
        Assert.Contains("sandbox: strict", spaceText);
        Assert.AreEqual("strict", serializer.DeserializeSpaceMarkdown(spaceText).Settings.NewSessions!.Sandbox);
        Assert.IsNull(new SpaceNewSessions(Sandbox: "not a profile!").Sanitized(), "a value that is no profile name is left out");

        var project = new ProjectDescriptor { Id = "p1", Slug = "lib", Name = "lib", DisplayName = "lib", ProjectPath = "C:/code/lib", Sandbox = "workspace" };
        var projectText = serializer.SerializeProjectMarkdown(project);
        Assert.Contains("sandbox: workspace", projectText);
        Assert.AreEqual("workspace", serializer.DeserializeProjectMarkdown(projectText).Sandbox);
        Assert.IsNull(serializer.DeserializeProjectMarkdown(projectText.Replace("sandbox: workspace", "sandbox: '../x'")).Sandbox);
        Assert.DoesNotContain("sandbox", serializer.SerializeProjectMarkdown(new ProjectDescriptor { Id = "p2", Slug = "b", Name = "b", ProjectPath = "C:/b" }));
    }

    private sealed class TempFolder : IDisposable
    {
        private TempFolder(string path) => Path = path;

        public string Path { get; }

        public static TempFolder Create() => new(Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "alta-sandbox-settings-" + Guid.NewGuid().ToString("N"))).FullName);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
