using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The shell of a session that attaches waits for the user's answer to a secret that asks before use: it starts with the value
/// once the use is confirmed, and without it, named in what the session is told, once the use is denied or not answered in time.
/// </summary>
[TestClass]
public sealed class ShellSecretConfirmationsTests
{
    private const string Value = "local-deploy-value-1";
    private const string RegistryValue = "local-registry-value-1";

    [TestMethod]
    public async Task TheShell_WaitsForTheConfirmation_ThenGetsTheValue_InEachVariableThatNamesIt()
    {
        using var fixture = await Fixture.CreateAsync(SecretAskDurations.EachUse);

        var attaching = fixture.AttachAsync("session-1");
        var waiting = await fixture.AwaitedAsync("shell of the session session-1");
        Assert.IsNotNull(waiting.AwaitedUntil, "the window shows that the shell waits for the answer");
        Assert.IsFalse(attaching.IsCompleted, "nothing is given before the user confirms");

        Assert.AreEqual(UserPresenceResult.Verified, await fixture.Broker.ConfirmAsync("DEPLOY", "shell of the session session-1"));
        var identity = await attaching.WithinAsync(TestTime.Seconds(30));

        Assert.AreEqual(Value, identity.Environment.Get("DEPLOY_TOKEN"));
        Assert.AreEqual(Value, identity.Environment.Get("DEPLOY_TOKEN_AGAIN"), "one environment is one use, whatever the variables that name the secret");
        Assert.IsEmpty(identity.SecretProblems);
        Assert.IsEmpty(fixture.Broker.ListPending());
        StringAssert.Contains(fixture.Verifier.Messages.Single(), "shell of the session session-1");

        // A confirmation of each use is used once: the next attachment asks again.
        var again = fixture.AttachAsync("session-1");
        await fixture.AwaitedAsync("shell of the session session-1");
        fixture.Broker.Decline("DEPLOY", "shell of the session session-1");
        Assert.IsNull((await again.WithinAsync(TestTime.Seconds(30))).Environment.Get("DEPLOY_TOKEN"));
    }

    [TestMethod]
    public async Task ASecretConfirmedAheadForEachUse_StaysInTheShell_WhenAnotherIsConfirmedWhileItWaits()
    {
        using var fixture = await Fixture.CreateAsync(SecretAskDurations.FifteenMinutes, second: true);
        // REGISTRY is confirmed ahead for one use of the shell of the session; DEPLOY waits for the user.
        Assert.AreEqual(UserPresenceResult.Verified, await fixture.Broker.ConfirmAsync("REGISTRY", "shell of the session session-7"));

        var attaching = fixture.AttachAsync("session-7");
        await fixture.AwaitedAsync("shell of the session session-7");
        await fixture.Broker.ConfirmAsync("DEPLOY", "shell of the session session-7");
        var identity = await attaching.WithinAsync(TestTime.Seconds(30));

        Assert.AreEqual(RegistryValue, identity.Environment.Get("REGISTRY_TOKEN"), "the first build took its confirmation of each use, and the second keeps what it read");
        Assert.AreEqual(Value, identity.Environment.Get("DEPLOY_TOKEN"));
        Assert.IsEmpty(identity.SecretProblems);
        Assert.IsEmpty(fixture.Broker.ListPending(), "nothing asks again");
    }

    [TestMethod]
    public async Task ADeniedUse_IsNotAskedAgain_WhenAnotherIsConfirmedWhileItWaits()
    {
        using var fixture = await Fixture.CreateAsync(SecretAskDurations.FifteenMinutes, second: true);

        var attaching = fixture.AttachAsync("session-8");
        await fixture.AwaitedAsync("shell of the session session-8");
        fixture.Broker.Decline("REGISTRY", "shell of the session session-8");
        await fixture.Broker.ConfirmAsync("DEPLOY", "shell of the session session-8");
        var identity = await attaching.WithinAsync(TestTime.Seconds(30));

        Assert.AreEqual(Value, identity.Environment.Get("DEPLOY_TOKEN"));
        Assert.IsNull(identity.Environment.Get("REGISTRY_TOKEN"));
        StringAssert.Contains(identity.SecretProblems.Single(), "REGISTRY: its use was denied");
        Assert.IsEmpty(fixture.Broker.ListPending(), "the denied use does not wait again for a shell that started");
    }

    [TestMethod]
    public async Task ADeniedUse_StartsTheShellWithoutTheSecret_AndTheSessionIsTold()
    {
        using var fixture = await Fixture.CreateAsync(SecretAskDurations.FifteenMinutes);

        var attaching = fixture.AttachAsync("session-2");
        await fixture.AwaitedAsync("shell of the session session-2");
        fixture.Broker.Decline("DEPLOY", "shell of the session session-2");
        var identity = await attaching.WithinAsync(TestTime.Seconds(30));

        Assert.IsNull(identity.Environment.Get("DEPLOY_TOKEN"));
        StringAssert.Contains(identity.SecretProblems.Single(), "started without the secret DEPLOY: its use was denied");
        Assert.AreEqual("refused", fixture.Broker.ListUses().First().Outcome);
        Assert.IsEmpty(fixture.Verifier.Messages, "a denial asks the system nothing");
    }

    [TestMethod]
    public async Task AUseNotAnsweredInTime_StopsWaiting_AndTheShellStartsWithoutIt()
    {
        using var fixture = await Fixture.CreateAsync(SecretAskDurations.FifteenMinutes, TimeSpan.FromMilliseconds(300));

        var identity = await fixture.AttachAsync("session-3").WithinAsync(TestTime.Seconds(30));

        Assert.IsNull(identity.Environment.Get("DEPLOY_TOKEN"));
        StringAssert.Contains(identity.SecretProblems.Single(), "was not confirmed within");
        Assert.IsEmpty(fixture.Broker.ListPending(), "a use nothing waits for any more is not asked about");
        Assert.AreEqual("expired", fixture.Broker.ListUses().First().Outcome);
    }

    [TestMethod]
    public async Task AConfirmationThatLasts_IsNotAskedAgain_ByTheSameSession_ButAnotherSessionAsksForItsOwn()
    {
        using var fixture = await Fixture.CreateAsync(SecretAskDurations.FifteenMinutes);

        var first = fixture.AttachAsync("session-4");
        await fixture.AwaitedAsync("shell of the session session-4");
        await fixture.Broker.ConfirmAsync("DEPLOY", "shell of the session session-4");
        Assert.AreEqual(Value, (await first.WithinAsync(TestTime.Seconds(30))).Environment.Get("DEPLOY_TOKEN"));

        var again = await fixture.AttachAsync("session-4").WithinAsync(TestTime.Seconds(30));
        Assert.AreEqual(Value, again.Environment.Get("DEPLOY_TOKEN"), "the same session within the time the confirmation lasts");

        var other = fixture.AttachAsync("session-5");
        await fixture.AwaitedAsync("shell of the session session-5");
        Assert.IsFalse(other.IsCompleted, "a confirmation for one session never lets another have the secret");
        fixture.Broker.Decline("DEPLOY", null);
        Assert.IsNull((await other.WithinAsync(TestTime.Seconds(30))).Environment.Get("DEPLOY_TOKEN"));
    }

    [TestMethod]
    public async Task WhereNobodyCanConfirm_TheShellStartsAtOnce_AndSaysWhy()
    {
        using var fixture = await Fixture.CreateAsync(SecretAskDurations.FifteenMinutes, canPrompt: false);

        var identity = await fixture.AttachAsync("session-6").WithinAsync(TestTime.Seconds(30));

        Assert.IsNull(identity.Environment.Get("DEPLOY_TOKEN"));
        StringAssert.Contains(identity.SecretProblems.Single(), "it cannot be confirmed here now");
    }

    [TestMethod]
    public void TheShellOfASession_IsKnownByItsUse()
    {
        Assert.IsTrue(CodeAltaHost.IsShellUse("shell of the session s1", out var session));
        Assert.AreEqual("s1", session);
        Assert.IsTrue(CodeAltaHost.IsShellUse("shell of a session", out session));
        Assert.IsNull(session);
        Assert.IsFalse(CodeAltaHost.IsShellUse("provider anthropic", out session));
        Assert.IsFalse(CodeAltaHost.IsShellUse(null, out _));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp;
        private readonly SessionIdentityResolver _resolver;
        private readonly string _spaceId;

        private Fixture(TempDirectory temp, CredentialBroker broker, Verifier verifier, SessionIdentityResolver resolver, string spaceId)
        {
            _temp = temp;
            Broker = broker;
            Verifier = verifier;
            _resolver = resolver;
            _spaceId = spaceId;
        }

        public CredentialBroker Broker { get; }

        public Verifier Verifier { get; }

        public static async Task<Fixture> CreateAsync(string askBeforeUse, TimeSpan? timeout = null, bool canPrompt = true, bool second = false)
        {
            var temp = new TempDirectory();
            var options = new CatalogOptions { GlobalRoot = Path.Combine(temp.Path, "global") };
            Directory.CreateDirectory(options.GlobalRoot);
            var store = new LocalSecretStore(options.GlobalRoot);
            store.SetSecret("DEPLOY", Value, "alice");
            store.Bindings.SetAskBeforeUse("DEPLOY", askBeforeUse);
            // A second secret, confirmed for each use, in a variable of its own.
            if (second)
            {
                store.SetSecret("REGISTRY", RegistryValue, "alice");
                store.Bindings.SetAskBeforeUse("REGISTRY", SecretAskDurations.EachUse);
            }

            var verifier = new Verifier();
            var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = options.GlobalRoot, Verifier = verifier, CanPrompt = canPrompt });
            var spaces = new SpaceCatalog(new ProjectCatalog(options));
            var space = (await spaces.CreateAsync("Deploy")).Space!;
            Assert.IsTrue((await spaces.UpdateSettingsAsync(space.Id, _ => new SpaceSettings(Identity: new SpaceIdentity(Cli:
                [new("deploy", [new("DEPLOY_TOKEN", "${secret:DEPLOY}"), new("DEPLOY_TOKEN_AGAIN", "${secret:DEPLOY}")]),
                 new("registry", [new("REGISTRY_TOKEN", "${secret:REGISTRY}")])])))).Succeeded);
            // As the host builds the environment of a shell.
            var confirmations = new CodeAltaHost.ShellSecretConfirmations(broker, broker, timeout ?? TimeSpan.FromMinutes(2));
            var resolver = new SessionIdentityResolver(spaces, (identity, scope) => SpaceIdentityEnvironment.Create(identity,
                    SecretReferenceResolver.ForScope(options.GlobalRoot, scope, shell: true, null, null, confirmations.SecretsOf(scope), CodeAltaHost.ShellUseOf(scope)).Read,
                    temp.Path, OperatingSystem.IsWindows()), null, null)
            {
                Confirmations = confirmations,
            };
            return new Fixture(temp, broker, verifier, resolver, space.Id);
        }

        public Task<SessionIdentity> AttachAsync(string sessionId) => _resolver.ResolveForAttachmentAsync(_spaceId, null, sessionId, null, CancellationToken.None);

        // The use once something waits for its answer, as the prompt of the window sees it.
        public async Task<PendingSecretUse> AwaitedAsync(string use)
        {
            var until = DateTime.UtcNow + TestTime.Seconds(30);
            while (DateTime.UtcNow < until)
            {
                if (Broker.ListPending().FirstOrDefault(pending => pending.Use == use && pending.AwaitedUntil is not null) is { } found) return found;
                await Task.Delay(10);
            }

            Assert.Fail($"Nothing waited for the use '{use}'.");
            return null!;
        }

        public void Dispose()
        {
            Broker.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-shell-secrets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }

    private sealed class Verifier : IUserPresenceVerifier
    {
        public List<string> Messages { get; } = [];

        public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new UserPresenceAvailability(true, null));

        public ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.FromResult(UserPresenceResult.Verified);
        }
    }
}
