using CodeAlta.Catalog.Secrets;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

// The lock, the expiry and the creation of providers, with a provider of a plugin whose reads a test holds and releases.
[TestClass]
public sealed class CredentialBrokerLifecycleTests
{
    private const string Type = "test.vault/gated";

    [TestMethod]
    public async Task A_read_that_started_before_a_lock_drops_its_value()
    {
        using var fixture = new Fixture();
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.NextRead = gate;
        var reading = fixture.Broker.RefreshAsync();
        await WaitAsync(() => fixture.Provider.Reads == 1);

        fixture.Broker.Lock("gated");
        var locks = fixture.Provider.Locks;
        gate.SetResult("late-value-123");
        await reading;

        Assert.IsNull(fixture.Broker.ReadBound("TOKEN", null).Value, "the value read before the lock is not kept");
        Assert.AreEqual(0, fixture.Broker.GetProvider("gated")!.Held);
        Assert.IsGreaterThan(locks, fixture.Provider.Locks, "what the read put back in the provider is forgotten again");
    }

    [TestMethod]
    public async Task A_provider_the_user_locked_is_not_read_again_until_it_is_unlocked()
    {
        using var fixture = new Fixture();
        await fixture.Broker.RefreshAsync();
        Assert.AreEqual("gated-value-1", fixture.Broker.ReadBound("TOKEN", null).Value);

        fixture.Broker.Lock("gated");
        var reads = fixture.Provider.Reads;
        // A vault that needs nobody (pass, a keychain) answers ready to any check: a check or a read must not undo the lock.
        await fixture.Broker.TestProviderAsync("gated");
        await fixture.Broker.RefreshAsync();
        await fixture.Broker.ReadSecretAsync("TOKEN");
        Assert.AreEqual(reads, fixture.Provider.Reads);
        var locked = fixture.Broker.GetProvider("gated")!;
        Assert.AreEqual(CredentialProviderState.Locked, locked.Status.State);
        Assert.IsTrue(locked.LockedByUser);
        Assert.IsNull(fixture.Broker.ReadBound("TOKEN", null).Value);

        var view = await fixture.Broker.UnlockAsync("gated", "typed-password-42");
        Assert.AreEqual(CredentialProviderState.Ready, view.Status.State);
        Assert.IsTrue(view.Unlocked, "the window offers to lock what CodeAlta unlocked, even without a value");
        Assert.IsFalse(view.LockedByUser);
        Assert.Contains("typed-password-42", fixture.Masked, "the password is masked before the provider gets it");
        Assert.AreEqual("gated-value-1", fixture.Broker.ReadBound("TOKEN", null).Value);

        fixture.Broker.Lock("gated");
        Assert.IsFalse(fixture.Broker.GetProvider("gated")!.Unlocked);
    }

    [TestMethod]
    public async Task A_value_seen_expired_by_a_resolver_is_still_read_again()
    {
        using var fixture = new Fixture();
        fixture.Provider.ExpiresIn = TimeSpan.FromMinutes(1);
        await fixture.Broker.RefreshAsync();
        Assert.AreEqual(1, fixture.Provider.Reads);

        fixture.Time.Now += TimeSpan.FromMinutes(2);
        // The resolver comes first and forgets the expired value; the timer of the broker would find nothing left to read again.
        Assert.IsNull(fixture.Broker.ReadBound("TOKEN", null).Value);
        await WaitAsync(() => fixture.Broker.ReadBound("TOKEN", null).Value is not null);
        Assert.AreEqual(2, fixture.Provider.Reads);
    }

    [TestMethod]
    public async Task A_token_that_asks_before_use_waits_for_a_confirmation_and_its_rotation_creates_the_provider_again()
    {
        using var fixture = new Fixture(withToken: true);
        fixture.Store.SetSecret("CI_TOKEN", "ci-token-v1", "alice");
        fixture.Store.Bindings.SetAskBeforeUse("CI_TOKEN", SecretAskDurations.FifteenMinutes);

        var waiting = fixture.Broker.GetProvider("gated")!;
        Assert.AreEqual(CredentialProviderState.Unavailable, waiting.Status.State);
        StringAssert.Contains(waiting.Status.Message, "asks before use");
        Assert.AreEqual("credential provider gated", fixture.Broker.ListPending().Single().Use, "a use the user can confirm");
        Assert.IsEmpty(fixture.Tokens);
        fixture.Broker.GetProvider("gated");
        Assert.HasCount(1, fixture.Broker.ListPending(), "asking again does not add a use");

        Assert.AreEqual(UserPresenceResult.Verified, await fixture.Broker.ConfirmAsync("CI_TOKEN", "credential provider gated"));
        await WaitAsync(() => fixture.Broker.GetProvider("gated")!.Status.State == CredentialProviderState.Ready);
        CollectionAssert.AreEqual(new[] { "ci-token-v1" }, fixture.Tokens.ToArray());
        Assert.Contains("ci-token-v1", fixture.Masked);
        await WaitAsync(() => fixture.Broker.ReadBound("TOKEN", null).Value is not null);

        fixture.Store.RotateSecret("CI_TOKEN", "ci-token-v2", "alice");
        await WaitAsync(() => fixture.Broker.GetProvider("gated") is not null && fixture.Tokens.Count == 2 && fixture.Broker.ReadBound("TOKEN", null).Value is not null);
        Assert.AreEqual("ci-token-v2", fixture.Tokens[^1], "the provider is opened with the new token");
    }

    [TestMethod]
    public async Task What_a_vault_program_writes_on_its_error_output_reaches_the_user_only()
    {
        using var vault = new FakeVault("vaulttool");
        vault.WriteConfig($"[credentials.team]\ntype = \"command\"\nread = [\"{vault.TomlPath}\", \"fail\", \"sk-leaked-secret-999\"]\n"
            + $"status = [\"{vault.TomlPath}\", \"fail\", \"sk-status-leak-777\"]\n");
        new LocalSecretStore(vault.Profile).Bind("TEAM_TOKEN", "team", "deploy/prod#token", "deploy/prod#token", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        await broker.RefreshAsync();
        var status = broker.GetProvider("team")!.Status;
        Assert.DoesNotContain("sk-status-leak", status.Message!);
        StringAssert.Contains(status.Detail, "sk-status-leak-777");

        vault.WriteConfig($"[credentials.team]\ntype = \"command\"\nread = [\"{vault.TomlPath}\", \"fail\", \"sk-leaked-secret-999\"]\n");
        await broker.RefreshAsync();
        var availability = broker.GetAvailability("TEAM_TOKEN")!;
        Assert.AreEqual(SecretAvailabilityState.Unavailable, availability.State);
        Assert.DoesNotContain("sk-leaked", availability.Message!, "what an agent reads names no value a program wrote");
        StringAssert.Contains(availability.Detail, "sk-leaked-secret-999");
        Assert.DoesNotContain("sk-leaked", broker.ReadBound("TEAM_TOKEN", "the shell of a session").Problem!);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow + CodeAlta.Testing.TestTime.Seconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) Assert.Fail("It did not happen in time.");
            await Task.Delay(20);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture(bool withToken = false)
        {
            _root = Path.Combine(Path.GetTempPath(), "codealta-broker-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "config.toml"), $"[credentials.gated]\ntype = \"{Type}\"\n" + (withToken ? "token = \"${secret:CI_TOKEN}\"\n" : string.Empty));
            Store = new LocalSecretStore(_root);
            Store.Bind("TOKEN", "gated", "item#field", "item", "alice");
            var contribution = new PluginCredentialProviderContribution
            {
                Type = Type,
                DisplayName = "Gated",
                Settings = [new("token", "Token", PluginCredentialProviderSettingKind.Secret)],
                Create = context =>
                {
                    if (context.GetString("token") is { } token) Tokens.Add(token);
                    return Provider;
                },
            };
            Broker = new CredentialBroker(new CredentialBrokerOptions
            {
                GlobalRoot = _root,
                Types = new CredentialProviderTypes(new VaultCommandRunner(), () => [contribution]),
                Time = Time,
                Mask = value => { lock (Masked) Masked.Add(value); },
                CanPrompt = true,
                Verifier = new Verifier(),
            });
            Provider.Time = Time;
        }

        public ManualTime Time { get; } = new();

        public GatedProvider Provider { get; } = new();

        public LocalSecretStore Store { get; }

        public CredentialBroker Broker { get; }

        public List<string> Masked { get; } = [];

        public List<string> Tokens { get; } = [];

        public void Dispose()
        {
            Broker.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Cleaned by the system later.
            }
        }
    }

    // A vault that needs nobody and whose lock forgets nothing, as pass or a keychain.
    private sealed class GatedProvider : ICredentialProvider
    {
        private int _reads;
        private int _locks;

        public TaskCompletionSource<string>? NextRead { get; set; }

        public TimeSpan? ExpiresIn { get; set; }

        public ManualTime? Time { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public int Locks => Volatile.Read(ref _locks);

        public ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderStatus.Ready());

        public ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderStatus.Ready());

        public async ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            var gate = NextRead;
            NextRead = null;
            var value = gate is null ? "gated-value-1" : await gate.Task.WaitAsync(cancellationToken);
            return new CredentialValue(value) { ExpiresAt = ExpiresIn is { } after ? Time!.GetUtcNow() + after : null };
        }

        public void Lock() => Interlocked.Increment(ref _locks);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Verifier : IUserPresenceVerifier
    {
        public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new UserPresenceAvailability(true, null));

        public ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken) => ValueTask.FromResult(UserPresenceResult.Verified);
    }
}
