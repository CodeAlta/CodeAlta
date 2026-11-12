using System.Text.Json;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Notifications;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The prompt of a secret that asks before use: the window is told the uses that wait as they come (who asks, whether something
/// waits for the answer, read again at each notice of the shell), and a system notification tells a user who is not in front of the window; never with a value.
/// </summary>
[TestClass]
public sealed class DesktopSecretPromptsTests
{
    private const string Epoch = "epoch-1";
    private const string Value = "local-deploy-value-1";

    [TestMethod]
    public async Task TheWindow_ReadsTheUsesThatWait_WhoAsks_AndWhatWaitsForTheAnswer()
    {
        using var profile = new Profile();
        using var broker = profile.Broker();
        var service = new SecretsService(new LocalSecretStore(profile.Root), Epoch) { Credentials = broker };
        var notices = 0;
        broker.PendingUsesChanged += (_, _) => Interlocked.Increment(ref notices);

        var empty = await service.PendingAsync(new SecretsRequest(Epoch), CancellationToken.None);
        Assert.AreEqual(("ok", 0, true), (empty.Status, empty.Pending.Length, empty.CanConfirm));

        // The shell of a session asks: the window prompts once something waits for the answer; a provider asks at once.
        broker.AuthorizeUse("DEPLOY", "shell of the session s1");
        var shell = (await service.PendingAsync(new SecretsRequest(Epoch), CancellationToken.None)).Pending.Single();
        Assert.AreEqual(("s1", false, (string?)null), (shell.SessionId, shell.Prompt, shell.AwaitedUntil));
        var waiting = broker.WaitForConfirmationAsync("DEPLOY", "shell of the session s1", TimeSpan.FromMinutes(2));
        var awaited = (await service.PendingAsync(new SecretsRequest(Epoch), CancellationToken.None)).Pending.Single();
        Assert.AreEqual(true, awaited.Prompt);
        Assert.IsNotNull(awaited.AwaitedUntil);

        broker.AuthorizeUse("DEPLOY", "provider anthropic");
        var both = await service.PendingAsync(new SecretsRequest(Epoch), CancellationToken.None);
        Assert.AreEqual((null, true), (both.Pending[1].SessionId, both.Pending[1].Prompt));
        Assert.IsFalse(JsonSerializer.Serialize(both, DesktopJsonContext.Default.SecretPendingEvent).Contains(Value, StringComparison.Ordinal), "never a value");

        Assert.AreEqual("ok", (await service.ConfirmUseAsync(new SecretUseRequest(Epoch, "DEPLOY", "shell of the session s1"), CancellationToken.None)).Status);
        Assert.AreEqual(SecretConfirmationOutcome.Confirmed, await waiting.WithinAsync(TestTime.Seconds(30)));
        Assert.AreEqual("ok", service.DeclineUse(new SecretUseRequest(Epoch, "DEPLOY", "provider anthropic")).Status);
        Assert.IsEmpty((await service.PendingAsync(new SecretsRequest(Epoch), CancellationToken.None)).Pending);
        Assert.IsGreaterThanOrEqualTo(4, Volatile.Read(ref notices), "the page is told as the uses start and stop waiting");

        // A page of another host gets nothing.
        var stale = await service.PendingAsync(new SecretsRequest("another"), CancellationToken.None);
        Assert.AreEqual(("stale_epoch", 0), (stale.Status, stale.Pending.Length));
    }

    [TestMethod]
    public async Task AUserNotInFrontOfTheWindow_IsNotified_OnceForEachUse_AndTheNotificationGoesWithTheUse()
    {
        var surface = new FakeSurface { Sessions = [new("s1", "project", "Fix the parser", true, 0, false)] };
        var pending = new List<PendingSecretUse>
        {
            new("DEPLOY", "shell of the session s1", DateTimeOffset.UtcNow) { AwaitedUntil = DateTimeOffset.UtcNow.AddMinutes(2) },
            new("OLD", "shell of the session gone", DateTimeOffset.UtcNow),
        };
        using var prompts = new DesktopSecretPrompts(surface, () => pending);

        await prompts.RefreshAsync();
        await prompts.RefreshAsync();

        var shown = surface.Shown.Single();
        Assert.AreEqual(("Use the secret DEPLOY?", "s1"), (shown.Title, shown.ActivationData));
        StringAssert.Contains(shown.Body, "The shell of the session “Fix the parser” asks for it.");
        StringAssert.StartsWith(shown.Id, DesktopSecretPrompts.NotificationPrefix);
        Assert.AreEqual(1, surface.Attention);
        Assert.IsTrue(DesktopSecretPrompts.IsPrompt(new(shown.Id, null, shown.ActivationData, false), out var session));
        Assert.AreEqual("s1", session);
        Assert.IsFalse(DesktopSecretPrompts.IsPrompt(new(shown.Id, null, shown.ActivationData, Dismissed: true), out _));
        Assert.IsFalse(DesktopSecretPrompts.IsPrompt(new("codealta.session.s1", null, "s1", false), out _));

        pending.Clear();
        await prompts.RefreshAsync();
        CollectionAssert.AreEqual(new[] { shown.Id }, surface.Removed);
    }

    [TestMethod]
    public async Task TheSecretsOneSessionStartAsksFor_AreOneNotification_AndOneAnswer()
    {
        using var profile = new Profile();
        var store = new LocalSecretStore(profile.Root);
        store.SetSecret("REGISTRY", "local-registry-value-1", "alice");
        store.Bindings.SetAskBeforeUse("REGISTRY", SecretAskDurations.FifteenMinutes);
        using var broker = profile.Broker();
        var service = new SecretsService(store, Epoch) { Credentials = broker };
        const string use = "shell of the session s1";
        // The window watches the uses as the session starts: they are asked for, then start waiting together.
        var surface = new FakeSurface { Sessions = [new("s1", "project", "Fix the parser", true, 0, false)] };
        using var prompts = new DesktopSecretPrompts(surface, broker);
        foreach (var name in (string[])["DEPLOY", "REGISTRY"]) broker.AuthorizeUse(name, use);
        var waits = broker.WaitForConfirmationsAsync(["DEPLOY", "REGISTRY"], use, TimeSpan.FromMinutes(2));

        // A user who is not in front of the window gets one notification for the start of the session, which names both.
        var deadline = DateTimeOffset.UtcNow + TestTime.Seconds(30);
        while (surface.Shown.Count == 0)
        {
            Assert.IsTrue(DateTimeOffset.UtcNow < deadline, "The user was not told.");
            await Task.Delay(10);
        }

        await prompts.RefreshAsync();
        var shown = surface.Shown.Single();
        Assert.AreEqual(("Use 2 secrets?", "s1"), (shown.Title, shown.ActivationData));
        StringAssert.Contains(shown.Body, "The shell of the session “Fix the parser” asks for DEPLOY, REGISTRY.");

        // One Confirm of the window answers both, with one verification of the user.
        Assert.AreEqual("ok", (await service.ConfirmUseAsync(new SecretUseRequest(Epoch, "DEPLOY", use) { Names = ["DEPLOY", "REGISTRY"] }, CancellationToken.None)).Status);
        CollectionAssert.AreEqual(new[] { SecretConfirmationOutcome.Confirmed, SecretConfirmationOutcome.Confirmed }, (await waits.WithinAsync(TestTime.Seconds(30))).ToArray());
        await prompts.RefreshAsync();
        CollectionAssert.AreEqual(new[] { shown.Id }, surface.Removed, "the notification goes once nothing of it waits");

        // And one Deny refuses both.
        foreach (var name in (string[])["DEPLOY", "REGISTRY"]) broker.AuthorizeUse(name, "shell of the session s2");
        Assert.AreEqual("ok", service.DeclineUse(new SecretUseRequest(Epoch, "DEPLOY", "shell of the session s2") { Names = ["DEPLOY", "REGISTRY"] }).Status);
        Assert.IsEmpty(broker.ListPending());
    }

    [TestMethod]
    public async Task AUserInFrontOfTheWindow_SeesItsPrompt_AndTheSettingOfTheAlertsIsFollowed()
    {
        var surface = new FakeSurface { Foreground = true };
        var pending = new List<PendingSecretUse> { new("DEPLOY", "provider anthropic", DateTimeOffset.UtcNow) };
        using var prompts = new DesktopSecretPrompts(surface, () => pending);

        await prompts.RefreshAsync();
        Assert.AreEqual((0, 0), (surface.Shown.Count, surface.Attention), "the window shows its prompt");

        surface.Foreground = false;
        surface.Mode = DesktopTurnAlertMode.Mark;
        pending.Add(new("OTHER", "MCP server grafana", DateTimeOffset.UtcNow));
        await prompts.RefreshAsync();
        Assert.AreEqual((0, 1), (surface.Shown.Count, surface.Attention), "the mark of the application alone");

        surface.Mode = DesktopTurnAlertMode.Off;
        pending.Add(new("THIRD", null, DateTimeOffset.UtcNow));
        await prompts.RefreshAsync();
        Assert.AreEqual((0, 1), (surface.Shown.Count, surface.Attention));
        Assert.AreEqual("a use nobody named", DesktopSecretPrompts.WhoAsks(null, null));
        Assert.AreEqual("the shell of a session", DesktopSecretPrompts.WhoAsks("shell of a session", null));
        Assert.AreEqual("the provider anthropic", DesktopSecretPrompts.WhoAsks("provider anthropic", null));
    }

    private sealed class Profile : IDisposable
    {
        public Profile()
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-secret-prompts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var store = new LocalSecretStore(Root);
            store.SetSecret("DEPLOY", Value, "alice");
            store.Bindings.SetAskBeforeUse("DEPLOY", SecretAskDurations.FifteenMinutes);
        }

        public string Root { get; }

        public CredentialBroker Broker() => new(new CredentialBrokerOptions { GlobalRoot = Root, Verifier = new Verifier(), CanPrompt = true });

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup for temporary test files.
            }
        }
    }

    private sealed class Verifier : IUserPresenceVerifier
    {
        public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new UserPresenceAvailability(true, null));

        public ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken) => ValueTask.FromResult(UserPresenceResult.Verified);
    }

    private sealed class FakeSurface : IDesktopTurnAlertSurface
    {
        public DesktopTurnAlertMode Mode { get; set; } = DesktopTurnAlertMode.All;
        public bool Foreground { get; set; }
        public IReadOnlyList<SessionRuntimeOverview> Sessions { get; set; } = [];
        public List<NeoNotificationRequest> Shown { get; } = [];
        public List<string> Removed { get; } = [];
        public int Attention { get; private set; }

        public bool IsForeground => Foreground;
        public IReadOnlyList<SessionRuntimeOverview> ReadSessions() => Sessions;
        public ValueTask<IReadOnlyCollection<string>> ReadWaitingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyCollection<string>>([]);

        public ValueTask<NeoDesktopStatus> NotifyAsync(NeoNotificationRequest request, CancellationToken cancellationToken)
        {
            Shown.Add(request);
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }

        public ValueTask<NeoDesktopStatus> RemoveNotificationAsync(string id, CancellationToken cancellationToken)
        {
            Removed.Add(id);
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }

        public ValueTask<NeoDesktopStatus> SetBadgeAsync(string? badge, CancellationToken cancellationToken) => ValueTask.FromResult(NeoDesktopStatus.Success);

        public ValueTask<NeoDesktopStatus> RequestAttentionAsync(CancellationToken cancellationToken)
        {
            Attention++;
            return ValueTask.FromResult(NeoDesktopStatus.Success);
        }

        public void Warn(string message) => Assert.Fail(message);
    }
}
