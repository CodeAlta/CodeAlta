using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary><c>alta secret</c>: an agent lists the names of the secrets of the machine, to name them in the configuration; it never reads or sets a value.</summary>
[TestClass]
public sealed class AltaSecretCommandsTests
{
    private const string Value = "sk-agent-never-sees-0123";

    [TestMethod]
    public async Task List_GivesNamesAndReferences_NeverValues_AndSetIsRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-alta-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new CatalogOptions { GlobalRoot = root };
        var store = new LocalSecretStore(root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "key"))));
        var projectId = ProjectId.NewVersion7().ToString();
        store.SetSecret("GRAFANA_TOKEN", Value, "tests");
        store.Bindings.SetProjects("GRAFANA_TOKEN", [new(projectId, "dashboard")]);
        store.SetVariable("GRAFANA_URL", "https://grafana.example", "tests");
        try
        {
            var registry = new AltaCommandRegistry(AltaCommandRegistryOptions.Cli);
            var dispatcher = new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(registry));

            var list = await dispatcher.InvokeAsync(["secret", "list"]);
            Assert.AreEqual(AltaExitCodes.Success, list.ExitCode, list.Transcript);
            StringAssert.Contains(list.Stdout, "\"name\":\"GRAFANA_TOKEN\"");
            StringAssert.Contains(list.Stdout, "\"reference\":\"${secret:GRAFANA_TOKEN}\"");
            StringAssert.Contains(list.Stdout, "\"reference\":\"${var:GRAFANA_URL}\"");
            StringAssert.Contains(list.Stdout, $"\"projects\":[{{\"id\":\"{projectId}\",\"name\":\"dashboard\"}}]");
            Assert.IsFalse(list.Stdout.Contains(Value, StringComparison.Ordinal), "An agent never reads a value of a secret.");
            Assert.IsFalse(list.Stdout.Contains("https://grafana.example", StringComparison.Ordinal), "Names only.");

            var set = await dispatcher.InvokeAsync(["secret", "set", "GRAFANA_TOKEN", "another"]);
            Assert.AreEqual(AltaExitCodes.PolicyDenied, set.ExitCode);
            StringAssert.Contains(set.Stdout + set.Stderr, "secret.setByAgent");
            StringAssert.Contains(set.Stdout + set.Stderr, "altanode secrets set");
            var resolver = SecretReferenceResolver.ForScope(root, new SecretScope(null, projectId, null), shell: true);
            Assert.AreEqual(Value, resolver.Resolve("${secret:GRAFANA_TOKEN}"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
