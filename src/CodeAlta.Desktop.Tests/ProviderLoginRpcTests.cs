using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Hosting;

namespace CodeAlta.Desktop.Tests;

/// <summary>Literal sign-in callbacks only: no network, browser or stored credential.</summary>
[TestClass]
public sealed class ProviderLoginRpcTests
{
    private const string Epoch = "epoch-1";
    private static readonly Uri Authorize = new("https://auth.example.test/authorize?state=1");
    private static readonly DateTimeOffset Expiry = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [TestMethod]
    public async Task WithoutAnOwnedHost_EveryOperationIsUnavailable()
    {
        var service = new ProviderLoginService();

        Assert.AreEqual("unavailable", (await service.StatusAsync(new(Epoch, "codex"), CancellationToken.None)).Status);
        Assert.AreEqual("unavailable", (await service.LogoutAsync(new(Epoch, "codex"), CancellationToken.None)).Status);
        AssertFailed("unavailable", (await Events(service, new(Epoch, "codex", null))).Single());
        await service.CloseAsync();
    }

    [TestMethod]
    public async Task Status_ReportsSupportedUnsupportedUnknownAndStaleRequests()
    {
        var fake = new Fake { Status = (_, _) => Task.FromResult(new ProviderLoginStatus(true, "Workspace", "api.example.test", Expiry)) };
        var service = fake.Service();

        var codex = await service.StatusAsync(new(Epoch, " Codex "), CancellationToken.None);
        Assert.AreEqual(new ProviderLoginStatusResponse("ok", "codex", true, codex.Modes, true, "Workspace", "api.example.test", Expiry), codex);
        CollectionAssert.AreEqual(new[] { "browser" }, codex.Modes);
        CollectionAssert.AreEqual(new[] { "browser", "device" }, (await service.StatusAsync(new(Epoch, "xai"), CancellationToken.None)).Modes);

        var local = await service.StatusAsync(new(Epoch, "local"), CancellationToken.None);
        Assert.AreEqual("ok", local.Status);
        Assert.IsFalse(local.Supported, "An API-key provider has no account sign-in.");
        Assert.AreEqual(0, local.Modes.Length);
        Assert.IsFalse(local.SignedIn);

        Assert.AreEqual("unknown_provider", (await service.StatusAsync(new(Epoch, "missing"), CancellationToken.None)).Status);
        Assert.AreEqual("invalid", (await service.StatusAsync(new(Epoch, "bad key"), CancellationToken.None)).Status);
        Assert.AreEqual("invalid", (await service.StatusAsync(new(Epoch, null), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.StatusAsync(new("another", "codex"), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.StatusAsync(new(null, "codex"), CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Status_ReportsUnreadableConfigurationOrCredentialsByCodeOnly()
    {
        var fake = new Fake { Status = static (_, _) => Task.FromException<ProviderLoginStatus>(new IOException("C:\\secret\\path")) };
        var unreadable = await fake.Service().StatusAsync(new(Epoch, "codex"), CancellationToken.None);
        Assert.AreEqual("read_failed", unreadable.Status);
        Assert.IsTrue(unreadable.Supported);
        Assert.IsFalse(unreadable.ToString().Contains("secret", StringComparison.Ordinal));

        fake.Load = static () => throw new InvalidDataException("secret");
        Assert.AreEqual("config_invalid", (await fake.Service().StatusAsync(new(Epoch, "codex"), CancellationToken.None)).Status);
        fake.Load = static () => throw new UnauthorizedAccessException("secret");
        Assert.AreEqual("read_failed", (await fake.Service().StatusAsync(new(Epoch, "codex"), CancellationToken.None)).Status);
        AssertFailed("login_failed", (await Events(fake.Service(), new(Epoch, "codex", null))).Single(), "read_failed");
    }

    [TestMethod]
    public async Task Login_EmitsThePromptThenCompletesAndEnablesADisabledProvider()
    {
        var fake = new Fake();
        string? mode = null;
        fake.Login = async (definition, requested, prompt, token) =>
        {
            mode = requested;
            Assert.AreEqual("codex", definition.ProviderKey);
            await prompt(new(Authorize, "ABCD-1234", Expiry), token);
            return new(true, "Workspace", "api.example.test", Expiry);
        };

        var events = await Events(fake.Service(), new(Epoch, "codex", null));

        Assert.AreEqual("browser", mode, "A blank mode selects the provider's first mode.");
        Assert.AreEqual(2, events.Count);
        Assert.AreEqual(new ProviderLoginEvent("prompt", Authorize.AbsoluteUri, "ABCD-1234", Expiry, true, false, null, null, null), events[0]);
        Assert.AreEqual(new ProviderLoginEvent("completed", null, null, Expiry, false, true, "Workspace", "api.example.test", null), events[1]);
        CollectionAssert.AreEqual(new[] { Authorize }, fake.Opened);
        CollectionAssert.AreEqual(new[] { "codex" }, fake.Enabled, "The disabled provider is enabled after its sign-in.");
    }

    [TestMethod]
    public async Task Login_OpensOnlyHttpsAddressesAndLeavesAnEnabledOrUnusableProviderAlone()
    {
        var fake = new Fake { BrowserOpens = false };
        fake.Login = static async (_, _, prompt, token) =>
        {
            await prompt(new(new Uri("http://127.0.0.1:1455/authorize"), null, null), token);
            await prompt(new(Authorize, null, null), token);
            await prompt(new(new Uri("https://auth.example.test/" + new string('a', ProviderLoginService.MaximumUrlLength)), null, null), token);
            for (var index = 0; index < ProviderLoginService.MaximumPrompts; index++) await prompt(new(Authorize, null, null), token);
            return new(true, null, null, null);
        };

        var events = await Events(fake.Service(), new(Epoch, "xai", " Device "));

        Assert.AreEqual(ProviderLoginService.MaximumPrompts + 1, events.Count, "Prompts beyond the limit are dropped.");
        Assert.AreEqual("http://127.0.0.1:1455/authorize", events[0].Url);
        Assert.IsFalse(events.Take(events.Count - 1).Any(static item => item.BrowserOpened), "Nothing opened: http is never tried and the opener failed.");
        Assert.IsNull(events[2].Url, "An address beyond the limit is not sent.");
        Assert.AreEqual(ProviderLoginService.MaximumPrompts - 1, fake.Opened.Count, "Only https addresses reach the opener.");
        Assert.AreEqual("completed", events[^1].Kind);
        Assert.AreEqual(0, fake.Enabled.Count, "An enabled provider is not rewritten.");

        // Like the TUI, a ChatGPT sign-in that declined plan usage does not enable the provider.
        fake.Login = static (_, _, _, _) => Task.FromResult(new ProviderLoginStatus(true, null, ConfiguredProviderLogin.NoPlanUsageDetail, null) { Usable = false });
        var declined = (await Events(fake.Service(), new(Epoch, "codex", "browser"))).Single();
        Assert.AreEqual(new ProviderLoginEvent("completed", null, null, null, false, true, null, ConfiguredProviderLogin.NoPlanUsageDetail, null), declined);
        Assert.AreEqual(0, fake.Enabled.Count);
    }

    [TestMethod]
    public async Task Login_ReportsFailuresByCodeWithoutExceptionText()
    {
        var fake = new Fake { Login = static (_, _, _, _) => throw new InvalidOperationException("token=secret-value") };
        var service = fake.Service();

        var failed = (await Events(service, new(Epoch, "codex", "browser"))).Single();
        AssertFailed("login_failed", failed, nameof(InvalidOperationException));
        Assert.IsFalse(failed.ToString().Contains("secret-value", StringComparison.Ordinal));
        Assert.AreEqual(0, fake.Enabled.Count);

        fake.Login = static (_, _, _, _) => Task.FromException<ProviderLoginStatus>(new TimeoutException("secret-value"));
        AssertFailed("timeout", (await Events(service, new(Epoch, "codex", "browser"))).Single());
        fake.Login = static (_, _, _, _) => Task.FromException<ProviderLoginStatus>(new OperationCanceledException());
        AssertFailed("canceled", (await Events(service, new(Epoch, "codex", "browser"))).Single());

        fake.Login = static (_, _, _, _) => throw new AssertFailedException("A refused request must not start a sign-in.");
        AssertFailed("unsupported", (await Events(service, new(Epoch, "local", null))).Single());
        AssertFailed("unsupported", (await Events(service, new(Epoch, "codex", "device"))).Single());
        AssertFailed("unknown_provider", (await Events(service, new(Epoch, "missing", null))).Single());
        AssertFailed("invalid", (await Events(service, new(Epoch, "", null))).Single());
        AssertFailed("stale_epoch", (await Events(service, new("another", "codex", null))).Single());
        AssertFailed("stale_epoch", (await Events(service, null)).Single());
    }

    [TestMethod]
    public async Task Login_RefusesASecondSignInAndASignOutWhileOneRuns()
    {
        var fake = new Fake();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Login = async (_, _, prompt, token) =>
        {
            await prompt(new(Authorize, null, null), token);
            await release.Task.WaitAsync(token);
            return new(true, null, null, null);
        };
        var service = fake.Service();

        await using var first = service.LoginAsync(new(Epoch, "xai", "browser"), CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await first.MoveNextAsync());
        Assert.AreEqual("prompt", first.Current.Kind);

        AssertFailed("busy", (await Events(service, new(Epoch, "codex", null))).Single());
        Assert.AreEqual(new ProviderLogoutResponse("busy", "xai", false), await service.LogoutAsync(new(Epoch, "xai"), CancellationToken.None));
        Assert.AreEqual(0, fake.SignedOut.Count);

        release.SetResult();
        Assert.IsTrue(await first.MoveNextAsync());
        Assert.AreEqual("completed", first.Current.Kind);
        Assert.IsFalse(await first.MoveNextAsync());

        fake.Login = static (_, _, _, _) => Task.FromResult(new ProviderLoginStatus(true, null, null, null));
        Assert.AreEqual("completed", (await Events(service, new(Epoch, "codex", null))).Single().Kind, "The slot is free once the sign-in settled.");
    }

    [TestMethod]
    public async Task Login_IsCanceledWhenItsChannelIsClosed()
    {
        var fake = new Fake();
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Login = async (_, _, prompt, token) =>
        {
            await prompt(new(Authorize, null, null), token);
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { canceled.SetResult(); throw; }
            throw new AssertFailedException("The wait must be canceled.");
        };
        var service = fake.Service();

        var channel = service.LoginAsync(new(Epoch, "codex", "browser"), CancellationToken.None).GetAsyncEnumerator();
        Assert.IsTrue(await channel.MoveNextAsync());
        await channel.DisposeAsync(); // What closing the RPC channel does to the enumeration.

        Assert.IsTrue(canceled.Task.IsCompletedSuccessfully, "Closing joins the canceled sign-in.");
        Assert.AreEqual(0, fake.Enabled.Count);
        fake.Login = static (_, _, _, _) => Task.FromResult(new ProviderLoginStatus(true, null, null, null));
        Assert.AreEqual("completed", (await Events(service, new(Epoch, "xai", null))).Single().Kind);
    }

    [TestMethod]
    public async Task Login_IsCanceledByItsRequestTokenAndByClosingTheService()
    {
        var fake = new Fake { Login = static async (_, _, _, token) => { await Task.Delay(Timeout.Infinite, token); return new(true, null, null, null); } };
        var service = fake.Service();
        using var request = new CancellationTokenSource();

        var events = Events(service, new(Epoch, "codex", "browser"), request.Token);
        request.Cancel();
        AssertFailed("canceled", (await events).Single());

        var running = Events(service, new(Epoch, "codex", "browser"));
        await service.CloseAsync();
        AssertFailed("canceled", (await running).Single());
        AssertFailed("unavailable", (await Events(service, new(Epoch, "codex", "browser"))).Single());
        await service.CloseAsync();
    }

    [TestMethod]
    public async Task Logout_RemovesTheCredentialAndReportsRefusalsAndFailuresByCode()
    {
        var fake = new Fake();
        var service = fake.Service();

        Assert.AreEqual(new ProviderLogoutResponse("ok", "codex", true), await service.LogoutAsync(new(Epoch, "codex"), CancellationToken.None));
        CollectionAssert.AreEqual(new[] { "codex" }, fake.SignedOut);
        Assert.AreEqual(new ProviderLogoutResponse("unsupported", "local", false), await service.LogoutAsync(new(Epoch, "local"), CancellationToken.None));
        Assert.AreEqual("unknown_provider", (await service.LogoutAsync(new(Epoch, "missing"), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await service.LogoutAsync(new("another", "codex"), CancellationToken.None)).Status);

        fake.SignOut = static (_, _) => Task.FromException<bool>(new IOException("C:\\secret\\path"));
        var failed = await service.LogoutAsync(new(Epoch, "xai"), CancellationToken.None);
        Assert.AreEqual(new ProviderLogoutResponse("logout_failed", "xai", false), failed);
    }

    [TestMethod]
    public async Task OwnedService_ReadsTheConfigurationAndEnablesThroughTheConfigurationService()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-provider-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var registry = new ModelProviderRegistry();
        try
        {
            var options = new CatalogOptions { GlobalRoot = root };
            File.WriteAllText(options.ConfigPath, """
                [providers.codex]
                enabled = false
                type = "codex"

                [providers.local]
                type = "openai-chat"
                api_key = "secret-value"
                api_url = "http://127.0.0.1:9999/v1"
                """);
            var store = new CodeAltaConfigStore(options);
            var configuration = new GlobalConfigService(store, registry, root, Epoch);
            var service = new ProviderLoginService(store, configuration, root, Epoch);

            // The stored state of an empty root is read locally: no sign-in exists.
            var codex = await service.StatusAsync(new(Epoch, "codex"), CancellationToken.None);
            Assert.AreEqual(new ProviderLoginStatusResponse("ok", "codex", true, codex.Modes, false, null, null, null), codex);
            Assert.IsFalse((await service.StatusAsync(new(Epoch, "local"), CancellationToken.None)).Supported);
            Assert.AreEqual(new ProviderLogoutResponse("ok", "codex", false), await service.LogoutAsync(new(Epoch, "codex"), CancellationToken.None));

            // What a completed sign-in does to a disabled provider.
            Assert.AreEqual("invalid", configuration.EnableProvider("missing").Status);
            var enabled = configuration.EnableProvider("codex");
            Assert.AreEqual("ok", enabled.Status, enabled.Message);
            var listed = configuration.Providers(new(Epoch));
            Assert.AreEqual(enabled.Revision, listed.Revision);
            Assert.IsTrue(listed.Providers.Single(static provider => provider.Key == "codex").Enabled);
            StringAssert.Contains(File.ReadAllText(options.ConfigPath), "secret-value", "Other providers keep their settings.");
            CollectionAssert.Contains(registry.ListProviders(includeDisabled: true).Select(static provider => provider.ProviderId.Value).ToArray(), "local");

            var before = File.ReadAllText(options.ConfigPath);
            Assert.AreEqual("ok", configuration.EnableProvider("local").Status);
            Assert.AreEqual(before, File.ReadAllText(options.ConfigPath), "An enabled provider is not rewritten.");
            Assert.AreEqual("unavailable", new GlobalConfigService().EnableProvider("codex").Status);
        }
        finally
        {
            await registry.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    private static void AssertFailed(string code, ProviderLoginEvent item, string? detail = null)
        => Assert.AreEqual(new ProviderLoginEvent("failed", null, null, null, false, false, null, detail, code), item);

    private static async Task<List<ProviderLoginEvent>> Events(ProviderLoginService service, ProviderLoginRequest? request,
        CancellationToken cancellationToken = default)
    {
        var events = new List<ProviderLoginEvent>();
        await foreach (var item in service.LoginAsync(request, cancellationToken)) events.Add(item);
        return events;
    }

    // One disabled Codex provider, one enabled xAI provider and one API-key provider, behind literal operations.
    private sealed class Fake
    {
        public Func<IReadOnlyList<CodeAltaProviderDocument>> Load { get; set; } = static () =>
        [
            new() { ProviderKey = "codex", ProviderType = "codex", Enabled = false },
            new() { ProviderKey = "xai", ProviderType = "xai", Enabled = true },
            new() { ProviderKey = "local", ProviderType = "openai-chat", Enabled = true },
        ];

        public Func<CodeAltaProviderDocument, string, Func<ProviderLoginPrompt, CancellationToken, ValueTask>, CancellationToken, Task<ProviderLoginStatus>> Login { get; set; }
            = static (_, _, _, _) => throw new AssertFailedException("No sign-in is expected.");

        public Func<CodeAltaProviderDocument, CancellationToken, Task<ProviderLoginStatus>> Status { get; set; }
            = static (_, _) => Task.FromResult(new ProviderLoginStatus(false, null, null, null));

        public Func<CodeAltaProviderDocument, CancellationToken, Task<bool>>? SignOut { get; set; }

        public bool BrowserOpens { get; set; } = true;
        public List<Uri> Opened { get; } = [];
        public List<string> Enabled { get; } = [];
        public List<string> SignedOut { get; } = [];

        public ProviderLoginService Service() => new(new ProviderLoginOperations(
            () => Load(),
            (definition, mode, prompt, token) => Login(definition, mode, prompt, token),
            (definition, token) => Status(definition, token),
            (definition, token) =>
            {
                if (SignOut is not null) return SignOut(definition, token);
                SignedOut.Add(definition.ProviderKey);
                return Task.FromResult(true);
            },
            key => { Enabled.Add(key); return true; },
            uri => { Opened.Add(uri); return BrowserOpens; }), Epoch);
    }
}
