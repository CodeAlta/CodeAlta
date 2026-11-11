using CodeAlta.Agent;
using CodeAlta.Browser.Tools;
using CodeAlta.Credentials;
using CodeAlta.Desktop;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopSignInsTests
{
    [TestMethod]
    public async Task A_sign_in_asks_the_card_once_for_a_site_allowed_for_30_days_and_lets_the_agent_choose_among_several()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-signins-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = root });
            broker.SignIns.Save(new SignIn("github", "GitHub", "personal", ["https://github.com"], "op://Private/GitHub/username", "op://Private/GitHub/password", null), "alice");
            List<AgentPermissionRequest> asked = [];
            var answer = AgentPermissionDecisionKind.AllowAlways;
            Task<AgentPermissionDecision> Ask(string session, AgentPermissionRequest request, CancellationToken token)
            {
                asked.Add(request);
                return Task.FromResult(answer == AgentPermissionDecisionKind.Deny ? AgentPermissionDecision.DeniedByUser() : new AgentPermissionDecision(answer));
            }

            using (var other = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://gist.github.com", null, CancellationToken.None))
            {
                Assert.AreEqual(LoginFillStatus.NoSignIn, other.Status, "a subdomain is another site");
            }

            Assert.IsEmpty(asked);
            using (var first = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", null, CancellationToken.None))
            {
                // No vault is configured here: the read fails after the card, with a message that names no value.
                Assert.AreEqual(LoginFillStatus.Unsupported, first.Status);
                StringAssert.Contains(first.Message, "personal");
            }

            var card = (AgentCredentialPermissionRequest)asked.Single();
            Assert.AreEqual((AgentCredentialPermissionRequest.SignInAction, "https://github.com", "With GitHub of personal", "session"), (card.Action, card.Subject, card.Detail, card.SessionId));
            Assert.IsTrue(broker.SignIns.IsGranted("github", "https://github.com"), "Allow for this site keeps a grant");
            using (await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", null, CancellationToken.None)) { }
            Assert.HasCount(1, asked, "a site allowed is not asked again");

            broker.SignIns.Save(new SignIn("work", "GitHub (work)", "personal", ["https://github.com"], "op://Work/GitHub/username", "op://Work/GitHub/password", null), "alice");
            using (var several = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", null, CancellationToken.None))
            {
                Assert.AreEqual(LoginFillStatus.Choose, several.Status);
                CollectionAssert.AreEquivalent(new[] { "github", "work" }, several.Choices.Select(static choice => choice.Id).ToArray());
            }

            answer = AgentPermissionDecisionKind.Deny;
            using (var denied = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", "work", CancellationToken.None))
            {
                Assert.AreEqual(LoginFillStatus.Denied, denied.Status);
            }

            Assert.IsFalse(broker.SignIns.IsGranted("work", "https://github.com"));
            using (var unknown = await DesktopSignIns.FillAsync(broker, Ask, "session", "https://github.com", "nothing", CancellationToken.None))
            {
                Assert.AreEqual(LoginFillStatus.NoSignIn, unknown.Status);
            }

            using var none = await DesktopSignIns.FillAsync(null, Ask, "session", "https://github.com", null, CancellationToken.None);
            Assert.AreEqual(LoginFillStatus.Unsupported, none.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task A_sign_in_the_user_changes_while_its_card_waits_is_not_read_nor_typed()
    {
        using var fixture = new Fixture();
        var github = fixture.Broker.SignIns.Save(GitHub(), "alice");
        var changes = new Func<SignIn, SignIn>[]
        {
            // Another item of the vault, a site that is no longer listed, another vault.
            signIn => signIn with { Password = "Work/GitHub/password" },
            signIn => signIn with { Sites = ["https://gitlab.com"] },
            signIn => signIn with { Provider = "other" },
        };
        foreach (var answer in new[] { AgentPermissionDecisionKind.AllowOnce, AgentPermissionDecisionKind.AllowAlways })
        {
            foreach (var change in changes)
            {
                fixture.Broker.SignIns.Save(github, "alice");
                Task<AgentPermissionDecision> Ask(string session, AgentPermissionRequest request, CancellationToken token)
                {
                    // The user edits the sign-in in Settings while the card of the session waits.
                    fixture.Broker.SignIns.Save(change(github), "alice");
                    return Task.FromResult(new AgentPermissionDecision(answer));
                }

                using var grant = await DesktopSignIns.FillAsync(fixture.Broker, Ask, "session", "https://github.com", null, CancellationToken.None);
                Assert.AreEqual(LoginFillStatus.NoSignIn, grant.Status, $"{answer}: {grant.Message}");
                StringAssert.Contains(grant.Message, "changed the sign-in GitHub");
                Assert.IsEmpty(fixture.Vault.Reads, "nothing of the vault is read for a sign-in the card did not show");
                Assert.IsFalse(fixture.Broker.SignIns.IsGranted("github", "https://github.com"), "nor is the site allowed for it");
            }
        }

        // Unchanged, it is read as the card showed it.
        fixture.Broker.SignIns.Save(github, "alice");
        using var granted = await DesktopSignIns.FillAsync(fixture.Broker, Allow, "session", "https://github.com", null, CancellationToken.None);
        Assert.AreEqual(LoginFillStatus.Granted, granted.Status);
        CollectionAssert.AreEqual(new[] { "Private/GitHub/username", "Private/GitHub/password", "Private/GitHub/otp" }, fixture.Vault.Reads.ToArray());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Broker.ReadSignInAsync(github, "https://gitlab.com"), "a site the sign-in does not list");
    }

    [TestMethod]
    public async Task A_one_time_code_is_read_again_when_its_field_is_reached_after_it_expired()
    {
        using var fixture = new Fixture();
        var github = fixture.Broker.SignIns.Save(GitHub(), "alice");
        var start = fixture.Clock.GetUtcNow();

        // A code with life left is the one read with the password.
        var code = new DesktopSignIns.OneTimeCode(fixture.Broker, github, "https://github.com", "111111", start + TimeSpan.FromSeconds(30), fixture.Clock);
        Assert.AreEqual("111111", await code.ReadAsync(CancellationToken.None));
        Assert.IsEmpty(fixture.Vault.Reads);

        // A slow page: the code expired before its field came. It is read again, masked first.
        fixture.Clock.Now += TimeSpan.FromSeconds(40);
        fixture.Vault.Codes.Enqueue(("222222", TimeSpan.FromSeconds(30)));
        Assert.AreEqual("222222", await code.ReadAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "Private/GitHub/otp" }, fixture.Vault.Reads.ToArray(), "the code only");
        CollectionAssert.Contains(fixture.Masked, "222222");

        // About to end: the code that comes after it is typed.
        fixture.Clock.Now += TimeSpan.FromSeconds(28);
        fixture.Vault.Codes.Enqueue(("333333", TimeSpan.FromSeconds(1)));
        fixture.Vault.Codes.Enqueue(("444444", TimeSpan.FromSeconds(30)));
        Assert.AreEqual("444444", await code.ReadAsync(CancellationToken.None));

        // A vault that does not say when a code ends: it is read again after a while.
        var unknown = new DesktopSignIns.OneTimeCode(fixture.Broker, github, "https://github.com", "555555", null, fixture.Clock);
        Assert.AreEqual("555555", await unknown.ReadAsync(CancellationToken.None));
        fixture.Clock.Now += DesktopSignIns.OneTimeCode.UnknownLife;
        fixture.Vault.Codes.Enqueue(("666666", TimeSpan.Zero));
        fixture.Vault.UnknownExpiry = true;
        Assert.AreEqual("666666", await unknown.ReadAsync(CancellationToken.None));

        // The user changed the sign-in since the card: no code is read nor typed.
        fixture.Broker.SignIns.Save(github with { Totp = "Work/GitHub/otp" }, "alice");
        fixture.Clock.Now += TimeSpan.FromMinutes(1);
        var reads = fixture.Vault.Reads.Count;
        Assert.IsNull(await code.ReadAsync(CancellationToken.None));
        Assert.HasCount(reads, fixture.Vault.Reads);
    }

    private static SignIn GitHub() => new("github", "GitHub", "fake", ["https://github.com"], "Private/GitHub/username", "Private/GitHub/password", "Private/GitHub/otp");

    private static Task<AgentPermissionDecision> Allow(string session, AgentPermissionRequest request, CancellationToken token)
        => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));

    // A broker with one vault of a plugin type, in memory, and a clock the test moves.
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-signins-" + Guid.NewGuid().ToString("N"))).FullName;

        public Fixture()
        {
            Vault = new FakeVault(Clock);
            File.WriteAllText(Path.Combine(_root, "config.toml"), "[credentials.fake]\ntype = \"acme.vault/secrets\"\n\n[credentials.other]\ntype = \"acme.vault/secrets\"\n");
            var contribution = new PluginCredentialProviderContribution { Type = "acme.vault/secrets", DisplayName = "Acme Vault", Create = _ => Vault };
            Broker = new CredentialBroker(new CredentialBrokerOptions
            {
                GlobalRoot = _root,
                Mask = Masked.Add,
                Time = Clock,
                Types = new CredentialProviderTypes(new VaultCommandRunner(), () => [contribution]),
            });
        }

        public ManualClock Clock { get; } = new(DateTimeOffset.Parse("2026-11-11T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

        public FakeVault Vault { get; }

        public List<string> Masked { get; } = [];

        public CredentialBroker Broker { get; }

        public void Dispose()
        {
            Broker.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // A vault that gives a value for each reference, and the one-time codes queued, each with its life from now.
    private sealed class FakeVault(TimeProvider time) : ICredentialProvider
    {
        public List<string> Reads { get; } = [];

        public Queue<(string Code, TimeSpan Life)> Codes { get; } = new();

        public bool UnknownExpiry { get; set; }

        public ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderStatus.Ready());

        public ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialProviderStatus.Ready());

        public ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
        {
            Reads.Add(reference);
            if (!reference.EndsWith("/otp", StringComparison.Ordinal)) return ValueTask.FromResult(new CredentialValue("value of " + reference));
            var (code, life) = Codes.Count > 0 ? Codes.Dequeue() : ("999999", TimeSpan.FromSeconds(30));
            return ValueTask.FromResult(new CredentialValue(code) { ExpiresAt = UnknownExpiry ? null : time.GetUtcNow() + life });
        }
    }
}
