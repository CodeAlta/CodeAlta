using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugin.Mcp;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Settings &gt; Secrets: the page lists the secrets of this machine by name, sets, rotates and deletes them, and edits
/// its variables; it never receives a value of a secret. A provider's key moves into a secret the configuration names.
/// </summary>
[TestClass]
public sealed class SecretsRpcTests
{
    private const string Epoch = "epoch-1";
    private const string Value = "sk-desktop-0123456789";

    [TestMethod]
    public void ASecret_IsSetRotatedAndDeleted_AndListedByNameOnly()
    {
        using var root = new TempRoot();
        var changes = 0;
        var service = new SecretsService(FileStore(root.Path), Epoch, () => changes++) { SetBy = "alex (CodeAlta Desktop)" };

        Assert.AreEqual("ok", service.SetSecret(new(Epoch, "GRAFANA_TOKEN", Value)).Status);
        Assert.AreEqual("ok", service.RotateSecret(new(Epoch, "GRAFANA_TOKEN", "sk-rotated")).Status);
        Assert.AreEqual("not_found", service.RotateSecret(new(Epoch, "MISSING", "x-value")).Status);
        Assert.AreEqual("ok", service.SetVariable(new(Epoch, "GRAFANA_URL", "https://grafana.example")).Status);

        var list = service.List(new(Epoch));
        Assert.AreEqual("ok", list.Status);
        var secret = list.Secrets.Single();
        Assert.AreEqual(("GRAFANA_TOKEN", 2, "alex (CodeAlta Desktop)"), (secret.Name, secret.Version, secret.SetBy));
        Assert.AreEqual("https://grafana.example", list.Variables.Single().Value);
        Assert.IsFalse(System.Text.Json.JsonSerializer.Serialize(list).Contains("sk-rotated", StringComparison.Ordinal), "The page never receives a value of a secret.");
        Assert.AreEqual(3, changes, "What masks the secrets reads them again after each change.");

        Assert.AreEqual("ok", service.DeleteSecret(new(Epoch, "GRAFANA_TOKEN")).Status);
        Assert.AreEqual("not_found", service.DeleteSecret(new(Epoch, "GRAFANA_TOKEN")).Status);
        Assert.AreEqual("ok", service.DeleteVariable(new(Epoch, "GRAFANA_URL")).Status);
        Assert.AreEqual(0, service.List(new(Epoch)).Secrets.Length);
    }

    [TestMethod]
    public void Refusals_AreSaid()
    {
        using var root = new TempRoot();
        var service = new SecretsService(FileStore(root.Path), Epoch);

        Assert.AreEqual("stale_epoch", service.List(new("another")).Status);
        Assert.AreEqual("stale_epoch", service.SetSecret(new("another", "TOKEN", Value)).Status);
        Assert.AreEqual("invalid", service.SetSecret(new(Epoch, "MY-TOKEN", Value)).Status);
        Assert.AreEqual("invalid", service.SetSecret(new(Epoch, "TOKEN", "")).Status);
        Assert.AreEqual("unavailable", new SecretsService().List(new(Epoch)).Status);
    }

    [TestMethod]
    public async Task EnvironmentListsLocalStatusAndUsageWithoutReadingAnySecret()
    {
        using var root = new TempRoot();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new ProjectCatalog(options);
        var spaces = new SpaceCatalog(catalog);
        var project = await catalog.GetOrAddTransientAsync(Directory.CreateDirectory(Path.Combine(root.Path, "acme")).FullName);
        var other = await catalog.GetOrAddTransientAsync(Directory.CreateDirectory(Path.Combine(root.Path, "other")).FullName);
        var config = new CodeAltaConfigStore(options);
        var store = FileStore(root.Path);
        var service = new SecretsService(store, Epoch)
        {
            Spaces = spaces, Config = config,
            McpHome = new McpHome(Directory.CreateDirectory(Path.Combine(root.Path, "home")).FullName, root.Path),
        };
        File.WriteAllText(Path.Combine(root.Path, "mcp.json"), """{ "mcpServers": { "grafana": { "url": "https://grafana.test/mcp", "headers": { "Authorization": "Bearer ${secret:ACME_TOKEN}" } } } }""");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(project.ProjectPath, ".alta")).FullName, "mcp.json"),
            """{ "mcpServers": { "deploy": { "command": "deploy-mcp", "env": { "DEPLOY_TOKEN": "${secret:ACME_TOKEN}" } } } }""");
        var space = (await spaces.CreateAsync("Work")).Space!;
        Assert.IsTrue((await spaces.UpdateSettingsAsync(space.Id, _ => new SpaceSettings(Identity: new SpaceIdentity(Cli:
            [new("gh", [new("GH_TOKEN", "${secret:ACME_TOKEN}")])])))).Succeeded);
        File.WriteAllText(options.ConfigPath, """
            [providers.work]
            type = "openai-responses"
            api_key_secret = "ACME_TOKEN"
            """);

        Assert.AreEqual("ok", service.SetSecret(new(Epoch, "ACME_TOKEN", Value)).Status);
        Assert.AreEqual("ok", (await service.SetSecretProjectsAsync(new(Epoch, "ACME_TOKEN", [project.Id]), default)).Status);
        Assert.AreEqual("invalid", (await service.SetSecretProjectsAsync(new(Epoch, "ACME_TOKEN", [Guid.NewGuid().ToString()]), default)).Status);
        Assert.AreEqual("invalid", (await service.SetSecretProjectsAsync(new(Epoch, "ACME_TOKEN", null!), default)).Status,
            "An omitted list does not remove the restriction.");
        Assert.AreEqual("ok", (await service.SetProjectEnvironmentAsync(new(Epoch, project.Id, "GH_TOKEN", "${secret:ACME_TOKEN}"), default)).Status);
        Assert.AreEqual("ok", (await service.SetProjectEnvironmentAsync(new(Epoch, other.Id, "GH_TOKEN", "${secret:ACME_TOKEN}"), default)).Status);
        Assert.AreEqual("ok", (await service.SetProjectEnvironmentAsync(new(Epoch, other.Id, "AWS_TOKEN", "${secret:TEST_ABSENT}"), default)).Status);
        var projectConfig = Path.Combine(project.ProjectPath, ".alta", "config.toml");
        await File.AppendAllTextAsync(projectConfig, "\n[identity.cli.azure.env]\nAZURE_CONFIG_DIR = \"~/.azure-acme\"\n");

        var listing = await service.ListEnvironmentAsync(new(Epoch), default);
        Assert.AreEqual("ok", listing.Status);
        var acme = listing.Projects.Single(item => item.Id == project.Id);
        Assert.AreEqual(projectConfig, acme.Path);
        Assert.IsTrue(acme.Variables.Single().Local);
        Assert.AreEqual("${secret:ACME_TOKEN}", acme.Variables.Single().Value);
        Assert.IsEmpty(acme.Issues);
        CollectionAssert.AreEqual(new[] { "AZURE_CONFIG_DIR" }, acme.Profiles.Single().Variables);
        var unfamiliar = listing.Projects.Single(item => item.Id == other.Id);
        Assert.AreEqual("restricted", unfamiliar.Variables.Single(item => item.Name == "GH_TOKEN").Issues.Single().State);
        Assert.AreEqual("not_set", unfamiliar.Variables.Single(item => item.Name == "AWS_TOKEN").Issues.Single().State);
        Assert.AreEqual("gh", listing.Spaces.Single().Profiles.Single().Name);

        var secrets = await service.ListAsync(new(Epoch), default);
        CollectionAssert.AreEqual(new[] { project.Id }, secrets.Secrets.Single().ProjectIds);
        CollectionAssert.AreEquivalent(new[]
        {
            new SecretUsageView("provider", "work"),
            new SecretUsageView("mcp", "grafana"),
            new SecretUsageView("mcp", $"deploy ({project.DisplayName})"),
            new SecretUsageView("space", "Work"),
            new SecretUsageView("project", project.DisplayName),
            new SecretUsageView("project", other.DisplayName),
        }, secrets.Secrets.Single().UsedBy);
        Assert.DoesNotContain(Value, System.Text.Json.JsonSerializer.Serialize(listing) + System.Text.Json.JsonSerializer.Serialize(secrets));
    }

    [TestMethod]
    public async Task ProjectEnvironmentWritesValidateTheCatalogAndPreserveOtherSettings()
    {
        using var root = new TempRoot();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new ProjectCatalog(options);
        var project = await catalog.GetOrAddTransientAsync(Directory.CreateDirectory(Path.Combine(root.Path, "project")).FullName);
        var directory = Directory.CreateDirectory(Path.Combine(project.ProjectPath, ".alta")).FullName;
        var path = Path.Combine(directory, "config.toml");
        const string original = "# Keep this comment\n[unknown]\nquery = [ 1, 2 ]\n";
        await File.WriteAllTextAsync(path, original);
        var service = new SecretsService(FileStore(root.Path), Epoch) { Spaces = new SpaceCatalog(catalog), Config = new CodeAltaConfigStore(options) };

        Assert.AreEqual("stale_epoch", (await service.SetProjectEnvironmentAsync(new("stale", project.Id, "NODE_ENV", "test"), default)).Status);
        Assert.AreEqual("not_found", (await service.SetProjectEnvironmentAsync(new(Epoch, Guid.NewGuid().ToString(), "NODE_ENV", "test"), default)).Status);
        var invalid = await service.SetProjectEnvironmentAsync(new(Epoch, project.Id, "GH_TOKEN", Value), default);
        Assert.AreEqual("invalid", invalid.Status);
        StringAssert.Contains(invalid.Message, "GH_TOKEN holds a secret");
        Assert.AreEqual(original, await File.ReadAllTextAsync(path));

        Assert.AreEqual("ok", (await service.SetProjectEnvironmentAsync(new(Epoch, project.Id, "NODE_ENV", "test"), default)).Status);
        StringAssert.StartsWith(await File.ReadAllTextAsync(path), original);
        Assert.AreEqual("ok", (await service.DeleteProjectEnvironmentAsync(new(Epoch, project.Id, "NODE_ENV"), default)).Status);
        Assert.AreEqual("not_found", (await service.DeleteProjectEnvironmentAsync(new(Epoch, project.Id, "NODE_ENV"), default)).Status);
        StringAssert.StartsWith(await File.ReadAllTextAsync(path), original);
    }

    [TestMethod]
    public async Task TheEnvironmentEditorListsInheritedVariablesAndCanOverrideThemLocally()
    {
        using var root = new TempRoot();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        var catalog = new ProjectCatalog(options);
        options.InheritedProjectRoots = catalog.GetInheritedRootsOfFolder;
        var folder = Directory.CreateDirectory(Path.Combine(root.Path, "monorepo")).FullName;
        Directory.CreateDirectory(Path.Combine(folder, "app"));
        Directory.CreateDirectory(Path.Combine(folder, ".alta"));
        await File.WriteAllTextAsync(ProjectManifest.PathOf(folder), """
            [projects.app]
            path = "app"
            """);
        await catalog.GetOrAddTransientAsync(folder);
        var project = (await catalog.LoadAsync()).Single(project => project.DeclaredKey == "app");
        var config = new CodeAltaConfigStore(options);
        config.SaveProjectEnvironmentVariable(folder, "NODE_ENV", "production");
        var service = new SecretsService(FileStore(root.Path), Epoch) { Spaces = new SpaceCatalog(catalog), Config = config };

        async Task<ProjectEnvironmentVariableView> Variable() => (await service.ListEnvironmentAsync(new(Epoch), default))
            .Projects.Single(item => item.Id == project.Id).Variables.Single();

        var inherited = await Variable();
        Assert.AreEqual(("production", false), (inherited.Value, inherited.Local));
        Assert.AreEqual("ok", (await service.SetProjectEnvironmentAsync(new(Epoch, project.Id, "NODE_ENV", "test"), default)).Status);
        var changed = await Variable();
        Assert.AreEqual(("test", true), (changed.Value, changed.Local));
        Assert.AreEqual("ok", (await service.DeleteProjectEnvironmentAsync(new(Epoch, project.Id, "NODE_ENV"), default)).Status);
        var restored = await Variable();
        Assert.AreEqual(("production", false), (restored.Value, restored.Local));
        Assert.AreEqual("production", config.LoadProjectEnvironment(folder).Single().Value);
    }

    [TestMethod]
    public async Task TheKeyOfAProvider_MovesIntoASecret_ThatTheConfigurationNames()
    {
        using var root = new TempRoot();
        var options = new CatalogOptions { GlobalRoot = root.Path };
        File.WriteAllText(options.ConfigPath, """
            [providers.work]
            enabled = true
            type = "openai-responses"
            api_key = "sk-literal-key-in-the-file"
            """);
        var store = new CodeAltaConfigStore(options);
        await using var registry = new ModelProviderRegistry();
        var changed = 0;
        var service = new GlobalConfigService(store, registry, root.Path, Epoch) { SecretChanged = () => changed++ };
        var listed = service.Providers(new(Epoch));
        Assert.IsTrue(listed.Providers.Single(static provider => provider.Key == "work").HasApiKey);

        var kept = service.KeepKeyAsSecret(new(Epoch, listed.Revision, "work", "WORK_OPENAI_KEY"));

        Assert.AreEqual("ok", kept.Status, kept.Message);
        Assert.AreEqual(1, changed);
        var content = File.ReadAllText(options.ConfigPath);
        StringAssert.Contains(content, "api_key_secret = \"WORK_OPENAI_KEY\"");
        Assert.IsFalse(content.Contains("sk-literal-key-in-the-file", StringComparison.Ordinal), "The configuration file no longer holds the key.");
        var secrets = new LocalSecretStore(root.Path);
        try
        {
            Assert.IsTrue(secrets.TryGetSecret("WORK_OPENAI_KEY", out var value));
            Assert.AreEqual("sk-literal-key-in-the-file", value);
            var provider = service.Providers(new(Epoch)).Providers.Single(static provider => provider.Key == "work");
            Assert.AreEqual(("WORK_OPENAI_KEY", (bool?)true, false), (provider.ApiKeySecret, provider.ApiKeySecretSet, provider.HasApiKey));
            Assert.AreEqual("invalid", service.KeepKeyAsSecret(new(Epoch, service.Providers(new(Epoch)).Revision, "work", "OTHER")).Status,
                "A provider whose key is a secret already has no key to keep.");
        }
        finally
        {
            secrets.Delete();
        }
    }

    private static LocalSecretStore FileStore(string root)
        => new(root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "key"))));

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-secrets-rpc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
