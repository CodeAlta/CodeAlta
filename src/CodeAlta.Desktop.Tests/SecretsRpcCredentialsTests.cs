using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// Settings &gt; Secrets, the vaults: a credential provider is saved and checked, unlocked from the page, a secret is kept in
/// one of its items, and the page lists the source and the state of each secret; no answer ever holds a value.
/// </summary>
[TestClass]
public sealed class SecretsRpcCredentialsTests
{
    private const string Epoch = "epoch-1";
    private const string VaultValue = "sk-ant-fake-value-1234";

    [TestMethod]
    public async Task AProvider_IsSaved_Unlocked_AndASecretKeptInIt_WithoutAValueInAnAnswer()
    {
        using var vault = new FakeOp();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        var changes = 0;
        var service = new SecretsService(new LocalSecretStore(vault.Profile), Epoch, () => changes++) { Credentials = broker, SetBy = "alex (CodeAlta Desktop)" };
        var answers = new List<object>();

        var saved = await service.SaveProviderAsync(new CredentialProviderWriteRequest(Epoch, "personal", "1password", "Personal", CredentialCacheDurations.OneHour, true,
            [new CredentialSettingInput("path", vault.ProgramPath, null, null)]), CancellationToken.None);
        answers.Add(saved);
        Assert.AreEqual("ok", saved.Status, saved.Message);
        Assert.AreEqual("locked", saved.Provider!.State, "1Password is not unlocked until the user asks");
        StringAssert.Contains(File.ReadAllText(Path.Combine(vault.Profile, "config.toml")), "cache = \"1h\"");

        var unlocked = await service.UnlockProviderAsync(new CredentialUnlockRequest(Epoch, "personal", null), CancellationToken.None);
        answers.Add(unlocked);
        Assert.AreEqual("ok", unlocked.Status, unlocked.Message);
        Assert.AreEqual(("ready", "alice@example.com"), (unlocked.Provider!.State, unlocked.Provider.Account));

        var browsed = await service.BrowseAsync(new CredentialBrowseRpcRequest(Epoch, "personal", null), CancellationToken.None);
        answers.Add(browsed);
        Assert.AreEqual("Private", browsed.Entries.Single().Label);

        var bound = await service.BindSecretAsync(new SecretBindRequest(Epoch, "ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "Private › Anthropic › credential", false), CancellationToken.None);
        answers.Add(bound);
        Assert.AreEqual(("ok", (string?)null), (bound.Status, bound.Message), "a secret of an unlocked vault is read at once");
        Assert.AreEqual(VaultValue, broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value);
        Assert.IsGreaterThan(0, changes, "what masks the secrets reads them again");

        var list = await service.ListAsync(new(Epoch), CancellationToken.None);
        answers.Add(list);
        var secret = list.Secrets.Single();
        Assert.AreEqual(("personal", "Personal", "ready", "Private › Anthropic › credential"), (secret.Provider, secret.ProviderName, secret.State, secret.Label));
        var provider = list.Providers.Single();
        Assert.AreEqual((1, 1), (provider.Secrets, provider.Held));
        Assert.IsTrue(list.Types.Any(static type => type.Type == "1password" && type.BuiltIn));
        Assert.IsTrue(list.CanPrompt);

        foreach (var answer in answers)
        {
            Assert.DoesNotContain(VaultValue, JsonSerializer.Serialize(answer, answer.GetType()), "the page never receives a value: " + answer.GetType().Name);
        }

        var store = File.ReadAllText(Path.Combine(vault.Profile, "secrets", "bindings.json"));
        Assert.DoesNotContain(VaultValue, store, "a binding holds no value");

        Assert.AreEqual("ok", service.LockProvider(new CredentialProviderRequest(Epoch, "personal")).Status);
        Assert.IsNull(broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value, "a locked provider forgets what it read");

        Assert.AreEqual("ok", service.RemoveProvider(new CredentialProviderRequest(Epoch, "personal")).Status);
        Assert.IsEmpty((await service.ListAsync(new(Epoch), CancellationToken.None)).Secrets, "the secrets kept in a provider go with it");
        Assert.DoesNotContain("[credentials.personal]", File.ReadAllText(Path.Combine(vault.Profile, "config.toml")));
    }

    [TestMethod]
    public async Task Refusals_AreSaid()
    {
        using var vault = new FakeOp();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        var store = new LocalSecretStore(vault.Profile, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(vault.Profile, "secrets", "key"))));
        var service = new SecretsService(store, Epoch) { Credentials = broker };

        Assert.AreEqual("unavailable", (await new SecretsService(store, Epoch).UnlockProviderAsync(new CredentialUnlockRequest(Epoch, "personal", null), CancellationToken.None)).Status,
            "a host without a broker offers no vault");
        Assert.AreEqual("stale_epoch", service.LockProvider(new CredentialProviderRequest("another", "")).Status);

        Assert.AreEqual("invalid", (await service.SaveProviderAsync(new CredentialProviderWriteRequest(Epoch, "Personal", "1password", null, CredentialCacheDurations.UntilClosed, true, []), CancellationToken.None)).Status,
            "an id is lowercase");
        Assert.AreEqual("invalid", (await service.SaveProviderAsync(new CredentialProviderWriteRequest(Epoch, "x", "no-such-vault", null, CredentialCacheDurations.UntilClosed, true, []), CancellationToken.None)).Status);
        var token = await service.SaveProviderAsync(new CredentialProviderWriteRequest(Epoch, "ci", "1password", null, CredentialCacheDurations.UntilClosed, true,
            [new CredentialSettingInput("service_account_token", "ops_raw_token_value", null, null)]), CancellationToken.None);
        Assert.AreEqual("invalid", token.Status, "a token is named as a secret of this computer, never written");
        Assert.IsFalse(File.Exists(Path.Combine(vault.Profile, "config.toml")) && File.ReadAllText(Path.Combine(vault.Profile, "config.toml")).Contains("ops_raw_token_value", StringComparison.Ordinal));
        Assert.AreEqual("invalid", (await service.SaveProviderAsync(new CredentialProviderWriteRequest(Epoch, "kp", "keepassxc", null, CredentialCacheDurations.UntilClosed, true, []), CancellationToken.None)).Status,
            "a required setting is missing");

        Assert.AreEqual("ok", (await service.SaveProviderAsync(new CredentialProviderWriteRequest(Epoch, "personal", "1password", null, CredentialCacheDurations.UntilClosed, true,
            [new CredentialSettingInput("path", vault.ProgramPath, null, null)]), CancellationToken.None)).Status);
        Assert.AreEqual("refused", (await service.UnlockProviderAsync(new CredentialUnlockRequest(Epoch, "personal", null), CancellationToken.None)).Status,
            "an instance that cannot prompt never asks a vault to unlock");

        Assert.AreEqual("ok", service.SetSecret(new(Epoch, "GRAFANA_TOKEN", "glsa-local-value")).Status);
        var exists = await service.BindSecretAsync(new SecretBindRequest(Epoch, "GRAFANA_TOKEN", "personal", "op://Private/Grafana/credential", null, false), CancellationToken.None);
        Assert.AreEqual("exists", exists.Status, "a value of this computer is replaced only when the page says so");
        Assert.AreEqual("not_found", (await service.BindSecretAsync(new SecretBindRequest(Epoch, "TOKEN", "nobody", "x", null, false), CancellationToken.None)).Status);
        Assert.AreEqual("invalid", (await service.BindSecretAsync(new SecretBindRequest(Epoch, "MY-TOKEN", "personal", "x", null, false), CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await service.SetAskBeforeUseAsync(new SecretAskRequest(Epoch, "GRAFANA_TOKEN", SecretAskDurations.FifteenMinutes), CancellationToken.None)).Status,
            "where nothing verifies the user, a secret cannot ask before use");
    }

    [TestMethod]
    public async Task ASignIn_IsSaved_Listed_ItsSiteRevoked_AndRemoved()
    {
        using var vault = new FakeOp();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        var service = new SecretsService(new LocalSecretStore(vault.Profile), Epoch) { Credentials = broker, SetBy = "alex (CodeAlta Desktop)" };
        Assert.AreEqual("ok", (await service.SaveProviderAsync(new CredentialProviderWriteRequest(Epoch, "personal", "1password", null, CredentialCacheDurations.UntilClosed, true,
            [new CredentialSettingInput("path", vault.ProgramPath, null, null)]), CancellationToken.None)).Status);

        Assert.AreEqual("invalid", service.SaveSignIn(new SignInWriteRequest(Epoch, null, "GitHub", "personal", ["https://github.com/login"], "op://P/G/username", "op://P/G/password", null)).Status,
            "a site is an origin, without a path");
        Assert.AreEqual("not_found", service.SaveSignIn(new SignInWriteRequest(Epoch, null, "GitHub", "nobody", ["github.com"], "op://P/G/username", "op://P/G/password", null)).Status);
        var saved = service.SaveSignIn(new SignInWriteRequest(Epoch, null, "GitHub", "personal", ["github.com"], "op://P/G/username", "op://P/G/password", " "));
        Assert.AreEqual("ok", saved.Status, saved.Message);
        Assert.AreEqual("ok", service.SaveSignIn(new SignInWriteRequest(Epoch, null, "GitHub", "personal", ["https://github.com"], "op://W/G/username", "op://W/G/password", null)).Status);

        var signIns = (await service.ListAsync(new(Epoch), CancellationToken.None)).SignIns;
        CollectionAssert.AreEqual(new[] { "github", "github-2" }, signIns.Select(static signIn => signIn.Id).ToArray(), "a new sign-in of the same name gets an id of its own");
        var first = signIns[0];
        Assert.AreEqual(("https://github.com", (string?)null, "1Password"), (first.Sites.Single(), first.Totp, first.ProviderName));

        broker.SignIns.Grant("github", "https://github.com");
        Assert.AreEqual("https://github.com", (await service.ListAsync(new(Epoch), CancellationToken.None)).SignIns[0].Grants.Single().Origin);
        Assert.AreEqual("ok", service.RevokeSiteGrant(new SiteGrantRequest(Epoch, "github", "https://github.com")).Status);
        Assert.IsEmpty((await service.ListAsync(new(Epoch), CancellationToken.None)).SignIns[0].Grants, "a revoked site is asked for again");

        Assert.AreEqual("ok", service.SaveSignIn(new SignInWriteRequest(Epoch, "github", "GitHub (work)", "personal", ["https://github.com"], "op://W/G/username", "op://W/G/password", null)).Status);
        Assert.AreEqual("GitHub (work)", broker.SignIns.List().Single(static signIn => signIn.Id == "github").Label, "an edit keeps its id");
        Assert.AreEqual("ok", service.RemoveSignIn(new SignInRequest(Epoch, "github")).Status);
        Assert.AreEqual("not_found", service.RemoveSignIn(new SignInRequest(Epoch, "github")).Status);
        Assert.HasCount(1, broker.SignIns.List());
    }

    /// <summary>The fake 1Password command line, signed out, under the name <c>op</c>, beside a profile root.</summary>
    private sealed class FakeOp : IDisposable
    {
        private const string Fake = "CodeAlta.Tests.FakeVaultCli";

        public FakeOp()
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-secrets-rpc-vault-" + Guid.NewGuid().ToString("N"));
            var bin = Path.Combine(Root, "bin");
            Profile = Path.Combine(Root, "profile");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(Profile);
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, Fake + ".*"))
            {
                File.Copy(file, Path.Combine(bin, Path.GetFileName(file)));
            }

            var apphost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? Fake + ".exe" : Fake);
            if (!File.Exists(apphost)) Assert.Inconclusive($"The fake vault program is missing: {apphost}");
            ProgramPath = Path.Combine(bin, OperatingSystem.IsWindows() ? "op.exe" : "op");
            File.Copy(apphost, ProgramPath);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ProgramPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var state = new JsonObject
            {
                ["accounts"] = true,
                ["signedIn"] = false,
                ["approve"] = true,
                ["vaults"] = new JsonArray(new JsonObject
                {
                    ["id"] = "v1",
                    ["name"] = "Private",
                    ["items"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "i1",
                        ["title"] = "Anthropic",
                        ["category"] = "API_CREDENTIAL",
                        ["fields"] = new JsonArray(new JsonObject { ["id"] = "credential", ["label"] = "credential", ["type"] = "CONCEALED", ["value"] = VaultValue }),
                    }),
                }),
            };
            File.WriteAllText(Path.Combine(bin, "fake-vault.json"), state.ToJsonString());
        }

        public string Root { get; }

        public string Profile { get; }

        public string ProgramPath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A program still runs: the folder of the system cleans it later.
            }
        }
    }
}
