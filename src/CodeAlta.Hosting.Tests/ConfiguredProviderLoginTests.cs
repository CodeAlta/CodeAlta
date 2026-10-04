using CodeAlta.Agent.Copilot;
using CodeAlta.Agent.Xai;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting.Tests;

// No network, listener or real credential: argument checks, an empty temporary state root and literal Codex operations.
[TestClass]
public sealed class ConfiguredProviderLoginTests
{
    private static readonly Uri Authorize = new("https://auth.example.test/authorize?state=1");

    [TestMethod]
    [DataRow("codex", "browser")]
    [DataRow("copilot", "device")]
    [DataRow("xai", "browser,device")]
    [DataRow("openai-chat", "")]
    [DataRow("anthropic", "")]
    [DataRow("Codex", "")]
    [DataRow(" xai", "")]
    [DataRow("", "")]
    [DataRow(null, "")]
    public void ModesAndSupport_FollowTheExactProviderType(string? providerType, string modes)
    {
        var expected = modes.Split(',', StringSplitOptions.RemoveEmptyEntries);

        CollectionAssert.AreEqual(expected, ConfiguredProviderLogin.GetLoginModes(providerType).ToArray());
        Assert.AreEqual(expected.Length > 0, ConfiguredProviderLogin.SupportsLogin(providerType));
    }

    [TestMethod]
    public async Task Operations_RejectMissingArgumentsBeforeAnyProviderWork()
    {
        var codex = Definition("codex");
        static ValueTask Prompt(ProviderLoginPrompt prompt, CancellationToken token) => throw new AssertFailedException("No prompt is expected.");

        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => ConfiguredProviderLogin.LoginAsync(null!, "root", "browser", Prompt, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ConfiguredProviderLogin.LoginAsync(codex, " ", "browser", Prompt, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => ConfiguredProviderLogin.LoginAsync(codex, "root", null!, Prompt, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => ConfiguredProviderLogin.LoginAsync(codex, "root", "browser", null!, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => ConfiguredProviderLogin.GetStatusAsync(null!, "root", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ConfiguredProviderLogin.GetStatusAsync(codex, "", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => ConfiguredProviderLogin.SignOutAsync(null!, "root", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ConfiguredProviderLogin.SignOutAsync(codex, " ", CancellationToken.None));
    }

    [TestMethod]
    [DataRow("openai-chat", "browser")]
    [DataRow("anthropic", "device")]
    [DataRow("Codex", "browser")]
    [DataRow(null, "browser")]
    [DataRow("codex", "device")]
    [DataRow("codex", "Browser")]
    [DataRow("copilot", "browser")]
    [DataRow("xai", "")]
    public async Task Login_RejectsAnUnsupportedTypeOrMode(string? providerType, string mode)
    {
        var failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() => ConfiguredProviderLogin.LoginAsync(Definition(providerType), "root", mode,
            static (_, _) => throw new AssertFailedException("No prompt is expected."), CancellationToken.None));

        Assert.AreEqual(ConfiguredProviderLogin.SupportsLogin(providerType) ? "mode" : "definition", failure.ParamName);
    }

    [TestMethod]
    [DataRow("openai-chat")]
    [DataRow("XAI")]
    [DataRow(null)]
    public async Task StatusAndSignOut_RejectATypeWithoutAccountSignIn(string? providerType)
    {
        var definition = Definition(providerType);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ConfiguredProviderLogin.GetStatusAsync(definition, "root", CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ConfiguredProviderLogin.SignOutAsync(definition, "root", CancellationToken.None));
    }

    [TestMethod]
    [DataRow("codex")]
    [DataRow("copilot")]
    [DataRow("xai")]
    public async Task StatusAndSignOut_OfAnEmptyStateRootAreSignedOut(string providerType)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-provider-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var definition = Definition(providerType);

            var status = await ConfiguredProviderLogin.GetStatusAsync(definition, root, CancellationToken.None);

            Assert.AreEqual(new ProviderLoginStatus(false, null, null, null), status);
            Assert.IsFalse(status.Usable);
            Assert.IsFalse(await ConfiguredProviderLogin.SignOutAsync(definition, root, CancellationToken.None), "Nothing was stored, so nothing is removed.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    [TestMethod]
    public void Status_ProjectsOnlyNeutralFacts()
    {
        var expiry = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        Assert.AreEqual(new ProviderLoginStatus(true, "Workspace", null, null), ConfiguredProviderLogin.FromCodex(new CodexAccountMetadata("Workspace", "subject", "client", true)));
        var declined = ConfiguredProviderLogin.FromCodex(new CodexAccountMetadata(" ", "subject", "client", false));
        Assert.IsTrue(declined.SignedIn);
        Assert.IsFalse(declined.Usable, "Like the TUI, a registration without plan usage is not a usable sign-in.");
        Assert.IsNull(declined.Account);
        Assert.AreEqual(ConfiguredProviderLogin.NoPlanUsageDetail, declined.Detail);
        Assert.AreEqual(new ProviderLoginStatus(true, "octo.example.test", "api.githubcopilot.com", expiry),
            ConfiguredProviderLogin.FromCopilot(new CopilotDirectLoginResult(new Uri("https://api.githubcopilot.com/"), expiry, " octo.example.test ")));
        Assert.AreEqual(new ProviderLoginStatus(true, null, "api.githubcopilot.com", null),
            ConfiguredProviderLogin.FromCopilot(new CopilotDirectLoginResult(new Uri("https://api.githubcopilot.com/"), null, null)));
        Assert.AreEqual(new ProviderLoginStatus(true, null, "api.x.ai", expiry),
            ConfiguredProviderLogin.FromXai(new XaiDirectLoginResult(new Uri("https://api.x.ai/v1"), expiry, "scope-is-not-reported")));
    }

    [TestMethod]
    public async Task CodexLogin_ForwardsTheAuthorizationAsABrowserPrompt()
    {
        var prompts = new List<ProviderLoginPrompt>();
        using var cancellation = new CancellationTokenSource();

        var status = await ConfiguredProviderLogin.LoginCodexAsync(
            (prompt, _) => { prompts.Add(prompt); return ValueTask.CompletedTask; },
            (report, _) =>
            {
                report(Authorize);
                return Task.FromResult(new CodexAccountMetadata("Workspace", "subject", "client", true));
            }, cancellation.Token);

        Assert.AreEqual(new ProviderLoginPrompt(Authorize, null, null), prompts.Single());
        Assert.AreEqual(new ProviderLoginStatus(true, "Workspace", null, null), status);
    }

    [TestMethod]
    public async Task CodexLogin_ReportsAPromptThatFailsAtOnceToTheProvider()
    {
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ConfiguredProviderLogin.LoginCodexAsync(
            static (_, _) => throw new InvalidDataException("prompt"),
            static (report, _) =>
            {
                report(Authorize); // Throws into the provider, which propagates it like any callback failure.
                throw new AssertFailedException("The report must not return.");
            }, CancellationToken.None));

        Assert.AreEqual("prompt", failure.Message);
    }

    [TestMethod]
    public async Task CodexLogin_APromptFailingLaterCancelsTheSignInAndIsTheReportedFailure()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var login = ConfiguredProviderLogin.LoginCodexAsync(
            async (_, _) =>
            {
                await release.Task;
                throw new InvalidDataException("late prompt failure");
            },
            async (report, token) =>
            {
                report(Authorize); // Returns although the prompt is still running.
                reported.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new AssertFailedException("The wait must be canceled.");
            }, CancellationToken.None);
        await reported.Task;
        Assert.IsFalse(login.IsCompleted);
        release.SetResult();

        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => login);
        Assert.AreEqual("late prompt failure", failure.Message);
    }

    [TestMethod]
    public async Task CodexLogin_JoinsAPromptThatOutlivesTheSignIn()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var login = ConfiguredProviderLogin.LoginCodexAsync(
            async (_, _) => await release.Task,
            static (report, _) =>
            {
                report(Authorize);
                return Task.FromResult(new CodexAccountMetadata(null, "subject", "client", true));
            }, CancellationToken.None);
        Assert.IsFalse(login.IsCompleted, "The prompt is joined before the result is returned.");
        release.SetResult();

        Assert.IsTrue((await login).SignedIn);
    }

    [TestMethod]
    public async Task CodexLogin_PropagatesCancellationAndTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var canceled = ConfiguredProviderLogin.LoginCodexAsync(static (_, _) => ValueTask.CompletedTask,
            static async (report, token) =>
            {
                report(Authorize);
                await Task.Delay(Timeout.Infinite, token);
                throw new AssertFailedException("The wait must be canceled.");
            }, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => ConfiguredProviderLogin.LoginCodexAsync(static (_, _) => ValueTask.CompletedTask,
            static (_, _) => Task.FromException<CodexAccountMetadata>(new TimeoutException()), CancellationToken.None));
    }

    private static CodeAltaProviderDocument Definition(string? providerType)
        => new() { ProviderKey = "configured", ProviderType = providerType };
}
