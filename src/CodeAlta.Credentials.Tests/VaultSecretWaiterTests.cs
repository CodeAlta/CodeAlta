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
