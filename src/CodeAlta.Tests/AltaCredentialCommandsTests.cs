using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>
/// <c>alta credential</c> and the vault commands of <c>alta secret</c>: an agent sees the providers and the state of each secret,
/// never a value nor the item a secret names; a change asks the user, and a sandboxed session gets none of these commands.
/// </summary>
[TestClass]
public sealed class AltaCredentialCommandsTests
{
    private const string Value = "sk-agent-never-sees-0123";

    [TestMethod]
    public async Task Providers_AndSecrets_AreListedWithTheirState_NeverWithAValueOrAnItem()
    {
        using var root = new Root();
        root.Config("[credentials.personal]\ntype = \"1password\"\npath = \"C:/nowhere/op.exe\"\n");
        root.Store.SetSecret("GRAFANA_TOKEN", Value, "tests");
        root.Store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Very private title/credential", "Private › Very private title › credential", "tests");
        using var broker = root.Broker();
        var dispatcher = root.Dispatcher(broker);

        var providers = await dispatcher.InvokeAsync(["credential", "list"]);
        Assert.AreEqual(AltaExitCodes.Success, providers.ExitCode, providers.Transcript);
        StringAssert.Contains(providers.Stdout, "\"id\":\"personal\"");
        StringAssert.Contains(providers.Stdout, "\"providerType\":\"1password\"");

        var status = await dispatcher.InvokeAsync(["credential", "status", "personal"]);
        StringAssert.Contains(status.Stdout, "\"state\":\"not_installed\"");

        var secrets = await dispatcher.InvokeAsync(["secret", "list"]);
        StringAssert.Contains(secrets.Stdout, "\"name\":\"ANTHROPIC_API_KEY\"");
        StringAssert.Contains(secrets.Stdout, "\"provider\":\"personal\"");
        StringAssert.Contains(secrets.Stdout, "\"provider\":\"this-computer\"");
        Assert.DoesNotContain("Very private title", secrets.Stdout, "the titles of a vault are private");
        Assert.DoesNotContain(Value, secrets.Stdout + providers.Stdout + status.Stdout);

        var types = await dispatcher.InvokeAsync(["credential", "types"]);
        StringAssert.Contains(types.Stdout, "\"providerType\":\"bitwarden\"");
        StringAssert.Contains(types.Stdout, "\"providerType\":\"command\"");
    }

    [TestMethod]
    public async Task A_change_asks_the_user_and_is_refused_where_nobody_can_be_asked()
    {
        using var root = new Root();
        root.Config("[credentials.personal]\ntype = \"1password\"\n");
        using var broker = root.Broker();
        var dispatcher = root.Dispatcher(broker);

        var bind = await dispatcher.InvokeAsync(["secret", "bind", "TOKEN", "--provider", "personal", "--ref", "op://Private/x/y"]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, bind.ExitCode, bind.Transcript);
        StringAssert.Contains(bind.Stdout + bind.Stderr, "credential.noUser");
        Assert.IsEmpty(root.Store.Bindings.List());

        var command = await dispatcher.InvokeAsync(["credential", "add", "command", "--id", "mine", "--set", "read=tool get {ref}"]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, command.ExitCode);
        StringAssert.Contains(command.Stdout + command.Stderr, "credential.commandRefused");

        var path = await dispatcher.InvokeAsync(["credential", "add", "1password", "--id", "other", "--set", "path=C:/tools/evil.exe"]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, path.ExitCode);
        StringAssert.Contains(path.Stdout + path.Stderr, "credential.pathRefused");

        var token = await dispatcher.InvokeAsync(["credential", "add", "1password", "--id", "ci", "--set", "service_account_token=ops_raw_value"]);
        Assert.AreEqual(AltaExitCodes.Usage, token.ExitCode);
        StringAssert.Contains(token.Stdout + token.Stderr, "credential.secretSetting");

        var unlock = await dispatcher.InvokeAsync(["credential", "unlock", "personal"]);
        Assert.AreEqual(AltaExitCodes.PolicyDenied, unlock.ExitCode, "a host that cannot prompt never asks a vault to unlock");
        StringAssert.Contains(unlock.Stdout + unlock.Stderr, "credential.noPrompt");
    }

    [TestMethod]
    public void A_sandboxed_session_gets_none_of_the_commands_of_the_vaults()
    {
        foreach (var path in new[] { "secret list", "secret test", "secret uses", "secret bind", "secret unbind", "credential list", "credential types", "credential status",
            "credential unlock", "credential lock", "credential add", "credential remove" })
        {
            Assert.IsFalse(AltaSandboxGate.Allows("alta " + path), $"alta {path} runs outside the sandbox, with the vaults of the user");
        }
    }

    private sealed class Root : IDisposable
    {
        public Root()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-alta-credential-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            Store = new LocalSecretStore(Path, new MasterKeySecretProtector(new FileSecretKeyStore(System.IO.Path.Combine(Path, "secrets", "key"))));
        }

        public string Path { get; }

        public LocalSecretStore Store { get; }

        public void Config(string toml) => File.WriteAllText(System.IO.Path.Combine(Path, "config.toml"), toml);

        public CredentialBroker Broker() => new(new CredentialBrokerOptions { GlobalRoot = Path });

        public AltaCommandDispatcher Dispatcher(CredentialBroker broker)
        {
            var options = new CatalogOptions { GlobalRoot = Path };
            var registry = new AltaCommandRegistry(AltaCommandRegistryOptions.Cli);
            return new AltaCommandDispatcher(registry, new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options)).Add(broker).Add(registry));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Left for the system to clean.
            }
        }
    }
}
