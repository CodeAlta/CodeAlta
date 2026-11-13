using System.Text.Json.Nodes;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class VaultSecretWaiterTests
{
    private static readonly string[] Key = ["ANTHROPIC_API_KEY"];

    [TestMethod]
    public async Task Runs_that_wait_for_a_locked_vault_share_one_unlock_and_get_the_secret()
    {
        using var vault = new FakeVault();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\nunlock_at_start = false\n");
        new LocalSecretStore(vault.Profile).Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();
        var waiter = new VaultSecretWaiter(broker);

        var waitsFor = waiter.WaitsFor(Key, "provider anthropic");
        StringAssert.Contains(waitsFor, "ANTHROPIC_API_KEY");
        StringAssert.Contains(waitsFor, "locked");
        Assert.IsNull(waiter.WaitsFor(["NOT_IN_A_VAULT"], "provider anthropic"), "a secret of this computer waits for nothing");

        var given = await Task.WhenAll(waiter.WaitAsync(Key, "provider anthropic", CancellationToken.None),
            waiter.WaitAsync(Key, "provider anthropic", CancellationToken.None)).WithinAsync(TestTime.Seconds(30));

        CollectionAssert.AreEqual(new[] { true, true }, given);
        Assert.AreEqual(1, vault.Calls().Count(static call => call.StartsWith("vault list", StringComparison.Ordinal)), "one prompt for every run: " + string.Join(" | ", vault.Calls()));
        Assert.AreEqual("sk-ant-fake-value-1234", broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value);
        Assert.IsNull(waiter.WaitsFor(Key, "provider anthropic"));
    }

    [TestMethod]
    public async Task Nothing_is_asked_where_nobody_can_answer()
    {
        using var vault = new FakeVault();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\nunlock_at_start = false\n");
        new LocalSecretStore(vault.Profile).Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        await broker.RefreshAsync();

        Assert.IsFalse(await new VaultSecretWaiter(broker).WaitAsync(Key, "provider anthropic", CancellationToken.None).WithinAsync(TestTime.Seconds(30)));
        Assert.IsFalse(vault.Calls().Any(static call => call.StartsWith("vault", StringComparison.Ordinal)), "no prompt of the app");
    }

    [TestMethod]
    public async Task A_dismissed_unlock_is_not_asked_again_at_once()
    {
        using var vault = new FakeVault();
        vault.State["accounts"] = false;
        vault.Save();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\nunlock_at_start = false\n");
        new LocalSecretStore(vault.Profile).Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();
        var time = new Clock();
        var waiter = new VaultSecretWaiter(broker, time);

        Assert.IsFalse(await waiter.WaitAsync(Key, "provider anthropic", CancellationToken.None).WithinAsync(TestTime.Seconds(30)), "the vault stayed locked");
        var asked = vault.Calls().Count;
        Assert.IsFalse(await waiter.WaitAsync(Key, "provider anthropic", CancellationToken.None).WithinAsync(TestTime.Seconds(30)));
        Assert.AreEqual(asked, vault.Calls().Count, "the next run fails at once rather than show the prompt again");

        vault.Reload();
        vault.State["accounts"] = true;
        vault.Save();
        time.Now += TimeSpan.FromMinutes(3); // not a limit
        Assert.IsTrue(await waiter.WaitAsync(Key, "provider anthropic", CancellationToken.None).WithinAsync(TestTime.Seconds(30)), "a while later a run asks again");
    }

    [TestMethod]
    public async Task A_secret_that_asks_before_use_is_confirmed_once_for_the_runs_of_a_provider()
    {
        using var vault = new FakeVault();
        vault.State["signedIn"] = true;
        vault.Save();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\n");
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        store.Bindings.SetAskBeforeUse("ANTHROPIC_API_KEY", SecretAskDurations.FifteenMinutes);
        var verifier = new Verifier();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });
        await broker.RefreshAsync();
        broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic");
        var waiter = new VaultSecretWaiter(broker);
        StringAssert.Contains(waiter.WaitsFor(Key, "provider anthropic"), "asks before use");

        verifier.Answer = UserPresenceResult.Canceled;
        Assert.IsFalse(await waiter.WaitAsync(Key, "provider anthropic", CancellationToken.None).WithinAsync(TestTime.Seconds(30)));

        verifier.Answer = UserPresenceResult.Verified;
        Assert.IsTrue(await waiter.WaitAsync(Key, "provider anthropic", CancellationToken.None).WithinAsync(TestTime.Seconds(30)));
        Assert.AreEqual("sk-ant-fake-value-1234", broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value);
        Assert.HasCount(2, verifier.Messages);
    }

    // altacli has no window: what a provider waits for says what to run, and a vault that cannot be read at all is said too.
    [TestMethod]
    public async Task A_host_without_a_window_says_in_its_own_words_how_to_unlock_and_what_cannot_be_read()
    {
        using var vault = new FakeVault();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\nunlock_at_start = false\n");
        new LocalSecretStore(vault.Profile).Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        await broker.RefreshAsync();
        var waiter = new VaultSecretWaiter(broker, new VaultSecretWaiterOptions
        {
            WhenLocked = static name => $"Run it again with --interactive-vault to let {name} ask.",
            SaysWhatCannotBeRead = true,
        });

        var waitsFor = waiter.WaitsFor(Key, "provider anthropic")!;
        StringAssert.Contains(waitsFor, "its secret ANTHROPIC_API_KEY is kept in ");
        StringAssert.Contains(waitsFor, "which is locked. Run it again with --interactive-vault to let ");
        Assert.IsFalse(waitsFor.Contains("Settings", StringComparison.Ordinal), waitsFor);
        Assert.IsFalse(await waiter.WaitAsync(Key, "provider anthropic", CancellationToken.None).WithinAsync(TestTime.Seconds(30)), "nobody is asked where the host cannot prompt");

        // The program of the vault is gone: CodeAlta Desktop shows that in Settings, a host without a window says it here.
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}-gone\"\n");
        await broker.RefreshAsync();
        Assert.IsNull(new VaultSecretWaiter(broker).WaitsFor(Key, "provider anthropic"));
        StringAssert.Contains(waiter.WaitsFor(Key, "provider anthropic"), "its secret ANTHROPIC_API_KEY cannot be read from ");
    }

    [TestMethod]
    public async Task A_vault_that_takes_a_password_is_asked_for_it_only_where_the_host_has_a_way_to_type_it()
    {
        using var vault = new FakeVault("keepassxc-cli");
        var database = Path.Combine(vault.Root, "Passwords.kdbx");
        File.WriteAllText(database, "not a real database");
        vault.State["keepassxc"] = new JsonObject
        {
            ["password"] = "kdbx-password-0001",
            ["groups"] = new JsonArray("Work/"),
            ["entries"] = new JsonArray(new JsonObject
            {
                ["path"] = "Work/GitHub", ["totp"] = false,
                ["attributes"] = new JsonObject { ["Title"] = "GitHub", ["Password"] = "kp-password-value-111", ["UserName"] = "kp-user", ["URL"] = "https://kp.example.com", ["Notes"] = "note" },
            }),
        };
        vault.Save();
        vault.WriteConfig($"[credentials.kp]\ntype = \"keepassxc\"\npath = \"{vault.TomlPath}\"\ndatabase = \"{database.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n");
        new LocalSecretStore(vault.Profile).Bind("GITHUB_TOKEN", "kp", "Work/GitHub#Password", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();
        string[] token = ["GITHUB_TOKEN"];

        Assert.IsFalse(await new VaultSecretWaiter(broker).WaitAsync(token, "provider github", CancellationToken.None).WithinAsync(TestTime.Seconds(30)), "in CodeAlta Desktop its password is typed in Settings");

        var asked = new List<string>();
        string? typed = null;
        var waiter = new VaultSecretWaiter(broker, new VaultSecretWaiterOptions
        {
            AskPassword = (provider, _) =>
            {
                asked.Add(provider.Id);
                return ValueTask.FromResult(typed);
            },
        });
        Assert.IsFalse(await waiter.WaitAsync(token, "provider github", CancellationToken.None).WithinAsync(TestTime.Seconds(30)), "nobody typed it");
        typed = "not the password";
        Assert.IsFalse(await waiter.WaitAsync(token, "provider github", CancellationToken.None).WithinAsync(TestTime.Seconds(30)), "a wrong password leaves the vault locked");
        typed = "kdbx-password-0001";
        Assert.IsTrue(await waiter.WaitAsync(token, "provider github", CancellationToken.None).WithinAsync(TestTime.Seconds(30)));

        Assert.AreEqual("kp-password-value-111", broker.ReadBound("GITHUB_TOKEN", "provider github").Value);
        CollectionAssert.AreEqual(new[] { "kp", "kp", "kp" }, asked);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Verifier : IUserPresenceVerifier
    {
        public UserPresenceResult Answer { get; set; } = UserPresenceResult.Verified;

        public List<string> Messages { get; } = [];

        public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new UserPresenceAvailability(true, null));

        public ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.FromResult(Answer);
        }
    }
}
