using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Desktop.Rpc;

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
