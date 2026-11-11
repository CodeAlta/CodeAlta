using System.Text.Json;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Plugin.Mcp;

namespace CodeAlta.Tests;

// The secrets kept in vaults that MCP servers are given: never in their command line, read once per start, and a start that
// failed for one of them is tried again once it can be given.
public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task AVaultSecretInTheArgumentsOfAServer_IsRefusedWithoutBeingRead()
    {
        using var project = TempDirectory.Create();
        var path = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object> { ["tiny"] = new { command = "dotnet", args = new[] { TinyServerAssemblyPath, "--token", "${secret:DEPLOY_KEY}" } } },
        }));
        BindInVault("DEPLOY_KEY");
        var vault = new OneVault("DEPLOY_KEY", "deploy-value-1234") { State = OneVault.Ready };
        await using var service = new McpRuntimeService(_home.Path) { BoundSecrets = () => vault };

        var search = await service.SearchToolsAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, serverFilter: "tiny", query: null, CancellationToken.None);

        var diagnostic = search.Diagnostics.Single();
        Assert.AreEqual("vault_secret_in_arguments", diagnostic.Code);
        StringAssert.Contains(diagnostic.Message, "${secret:DEPLOY_KEY}");
        StringAssert.Contains(diagnostic.Message, "'env'");
        Assert.AreEqual(0, vault.Reads, "the value is never read for a command line");
    }

    [TestMethod]
    public async Task AServerWhoseVaultIsLocked_StartsAtTheNextRequestOnceItIsUnlocked()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOL"] = "${secret:VAULT_TOOL}" });
        BindInVault("VAULT_TOOL");
        var vault = new OneVault("VAULT_TOOL", "from_the_vault") { State = OneVault.Locked };
        await using var service = new McpRuntimeService(_home.Path) { BoundSecrets = () => vault };
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        var locked = await service.SearchToolsAsync(request, serverFilter: "tiny", query: null, CancellationToken.None);
        Assert.AreEqual("environment_variable_not_found", locked.Diagnostics.Single().Code);

        vault.State = OneVault.Ready;
        var unlocked = await service.SearchToolsAsync(request, serverFilter: "tiny", query: null, CancellationToken.None);
        Assert.AreEqual(0, unlocked.Diagnostics.Count, string.Join(Environment.NewLine, unlocked.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsTrue(unlocked.Tools.Any(static tool => tool.Name == "from_the_vault"));
    }

    [TestMethod]
    public async Task AServerWaitingForAConfirmationOfEachUse_StartsWithTheValueOnceConfirmed()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: new Dictionary<string, string> { ["MCP_TEST_EXTRA_TOOL"] = "${secret:VAULT_TOOL}" });
        BindInVault("VAULT_TOOL");
        var vault = new OneVault("VAULT_TOOL", "confirmed_tool") { State = OneVault.Waiting };
        await using var service = new McpRuntimeService(_home.Path) { BoundSecrets = () => vault };
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        var waiting = await service.SearchToolsAsync(request, serverFilter: "tiny", query: null, CancellationToken.None);
        Assert.AreEqual("environment_variable_not_found", waiting.Diagnostics.Single().Code);

        // One confirmation of each use: the validation and the start of the server share it.
        vault.Grants = 1;
        var confirmed = await service.SearchToolsAsync(request, serverFilter: "tiny", query: null, CancellationToken.None);
        Assert.AreEqual(0, confirmed.Diagnostics.Count, string.Join(Environment.NewLine, confirmed.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        Assert.IsTrue(confirmed.Tools.Any(static tool => tool.Name == "confirmed_tool"), "the server got the value its one confirmation gave");
        Assert.AreEqual(0, vault.Grants);
        Assert.AreEqual(1, vault.Reads - vault.Refused, "the value was read once for the start");
    }

    [TestMethod]
    public void TheUseOfAServer_NamesItsFileAndItsSpace_SoThatTwoServersOfTheSameNameDiffer()
    {
        static McpServerDefinition Grafana(string source) => new() { Key = "grafana", SourcePath = source, SourceScope = McpConfigScope.Project, SourceFlavor = McpConfigFlavor.CodeAlta, Transport = McpTransportKind.Stdio, Command = "grafana-mcp" };
        var first = McpRuntimeService.UseOf(new McpRuntimeRequest(), Grafana(Path.Combine("a", ".alta", "mcp.json")));
        var second = McpRuntimeService.UseOf(new McpRuntimeRequest(), Grafana(Path.Combine("b", ".alta", "mcp.json")));
        var inSpace = McpRuntimeService.UseOf(new McpRuntimeRequest { SpaceId = "work" }, Grafana(Path.Combine("a", ".alta", "mcp.json")));

        StringAssert.StartsWith(first, "MCP server grafana of ");
        Assert.AreNotEqual(first, second);
        Assert.AreNotEqual(first, inSpace);
        StringAssert.Contains(inSpace, "work");
    }

    // Binds a secret of the test profile to a vault, as the user does in Settings.
    private void BindInVault(string name)
        => new LocalSecretStore(Path.Combine(_home.Path, ".alta")).Bind(name, "personal", "op://Private/Item/credential", "Private › Item › credential", "tests");

    // A vault of one secret, as the broker answers for it: locked, ready, or asking before each use (a grant is taken by a read).
    private sealed class OneVault(string name, string value) : IBoundSecretSource
    {
        public const int Locked = 0;
        public const int Ready = 1;
        public const int Waiting = 2;

        public int State { get; set; }

        public int Grants { get; set; }

        public int Reads { get; private set; }

        public int Refused { get; private set; }

        public List<string?> Uses { get; } = [];

        public BoundSecretRead ReadBound(string secret, string? use)
        {
            Reads++;
            Uses.Add(use);
            if (!string.Equals(secret, name, StringComparison.OrdinalIgnoreCase)) return Refuse($"{secret} is not kept here.");
            if (State == Locked) return Refuse($"{secret} is kept in 1Password, which is locked.");
            if (State == Waiting)
            {
                if (Grants == 0) return Refuse($"{secret} asks before use.");
                Grants--;
            }

            return BoundSecretRead.Of(value);
        }

        public string? AuthorizeUse(string secret, string? use) => null;

        private BoundSecretRead Refuse(string problem)
        {
            Refused++;
            return BoundSecretRead.Unavailable(problem);
        }
    }
}
