using System.Net;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Network;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Desktop.Rpc;

namespace CodeAlta.Desktop.Tests;

/// <summary>The page's side of the proxy: its settings, their save and application, and the check of the connection.</summary>
[TestClass]
public sealed class NetworkRpcTests
{
    private const string Epoch = "host";

    [TestMethod]
    public async Task Unavailable_AndStaleEpoch_AreRefused()
    {
        Assert.AreEqual("unavailable", (await new NetworkService().ReadAsync(new(Epoch), CancellationToken.None)).Status);
        using var fixture = new Fixture();
        Assert.AreEqual("stale_epoch", (await fixture.Service.ReadAsync(new("other"), CancellationToken.None)).Status);
        Assert.AreEqual("stale_epoch", (await fixture.Service.CheckAsync(new("other", null, "none", null, null, null, null, null), CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Read_OfANewProfile_IsTheSystemMode_AndSaysWhereTheSystemGoes()
    {
        using var fixture = new Fixture { System = new FixedProxy(new Uri("http://env-proxy:3128")) };
        var listing = await fixture.Service.ReadAsync(new(Epoch), CancellationToken.None);
        Assert.AreEqual(("ok", "system", "system", "http://env-proxy:3128", false, false),
            (listing.Status, listing.Mode, listing.AppliedMode, listing.SystemProxy, listing.PasswordSet, listing.Pending));
    }

    [TestMethod]
    public async Task Save_KeepsThePasswordAsASecret_WritesTheTable_AndAppliesIt()
    {
        using var fixture = new Fixture();
        var saved = await fixture.Service.SaveAsync(new(Epoch, "manual", " http://proxy.corp:8080 ", ["*.corp.example, 10.0.0.0/8", "intranet"], "password", "jdoe", "s3cret"),
            CancellationToken.None);

        Assert.AreEqual("ok", saved.Status, saved.Message);
        Assert.AreEqual(1, fixture.Applied, "a save applies the settings at once");
        Assert.AreEqual(1, fixture.SecretChanges, "the masking learns the password");
        Assert.AreEqual(("manual", "http://proxy.corp:8080", "password", "jdoe", true), (saved.Mode, saved.Url, saved.Authentication, saved.User, saved.PasswordSet));
        CollectionAssert.AreEqual(new[] { "*.corp.example", "10.0.0.0/8", "intranet" }, saved.Bypass);
        Assert.IsTrue(fixture.Secrets.TryGetSecret(ProxySettings.DefaultPasswordSecret, out var password));
        Assert.AreEqual("s3cret", password);
        var text = File.ReadAllText(Path.Combine(fixture.Root, "config.toml"));
        Assert.IsFalse(text.Contains("s3cret", StringComparison.Ordinal), "never a password in the configuration");
        StringAssert.Contains(text, "password_secret = \"NETWORK_PROXY_PASSWORD\"");

        // A save without a password keeps the one kept.
        var again = await fixture.Service.SaveAsync(new(Epoch, "manual", "http://proxy.corp:8080", [], "password", "jdoe2", null), CancellationToken.None);
        Assert.AreEqual(("ok", "jdoe2", true, 1), (again.Status, again.User, again.PasswordSet, fixture.SecretChanges));
    }

    [TestMethod]
    public async Task Save_RefusesWhatIsNotAProxy_AndChangesNothing()
    {
        using var fixture = new Fixture();
        var noAddress = await fixture.Service.SaveAsync(new(Epoch, "manual", "", [], "none", null, null), CancellationToken.None);
        Assert.AreEqual("invalid", noAddress.Status);
        StringAssert.Contains(noAddress.Message, "needs the address");
        Assert.AreEqual("invalid", (await fixture.Service.SaveAsync(new(Epoch, "manual", "http://proxy:8080", ["not valid!"], "none", null, null), CancellationToken.None)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.SaveAsync(new(Epoch, "tunnel", null, null, null, null, null), CancellationToken.None)).Status);
        var noPassword = await fixture.Service.SaveAsync(new(Epoch, "manual", "http://proxy:8080", [], "password", "jdoe", null), CancellationToken.None);
        Assert.AreEqual("invalid", noPassword.Status);
        StringAssert.Contains(noPassword.Message, "needs a password");
        Assert.AreEqual(0, fixture.Applied);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "config.toml")));
    }

    [TestMethod]
    public async Task Check_RequestsThroughTheFormAsItIs_AndSaysHowItWent()
    {
        using var fixture = new Fixture();
        await fixture.Service.SaveAsync(new(Epoch, "manual", "http://proxy.corp:8080", [], "password", "jdoe", "kept-password"), CancellationToken.None);
        fixture.Answer = HttpStatusCode.NoContent;

        var through = await fixture.Service.CheckAsync(new(Epoch, null, "manual", "http://proxy.corp:8080", [], "password", "jdoe", null), CancellationToken.None);
        Assert.AreEqual(("ok", true, 204, "http://proxy.corp:8080", "https://www.codealta.io/"), (through.Status, through.Reached, through.HttpStatus, through.Via, through.Target));
        Assert.AreEqual(new Uri("https://www.codealta.io/"), fixture.Requested);
        // The password kept is tried when the page types none.
        Assert.AreEqual("kept-password", fixture.Proxy!.Credentials!.GetCredential(new Uri("http://proxy.corp:8080/"), "Basic")!.Password);

        // Settings not saved: a direct connection, to another address.
        var direct = await fixture.Service.CheckAsync(new(Epoch, "https://api.anthropic.com", "none", null, null, null, null, null), CancellationToken.None);
        Assert.AreEqual((true, (string?)null, "https://api.anthropic.com/"), (direct.Reached, direct.Via, direct.Target));
        Assert.IsNull(fixture.Proxy);

        fixture.Answer = HttpStatusCode.ProxyAuthenticationRequired;
        var refused = await fixture.Service.CheckAsync(new(Epoch, null, "manual", "http://proxy.corp:8080", [], "password", "jdoe", "wrong"), CancellationToken.None);
        Assert.AreEqual((false, 407), (refused.Reached, refused.HttpStatus));
        Assert.AreEqual("wrong", fixture.Proxy!.Credentials!.GetCredential(new Uri("http://proxy.corp:8080/"), "Basic")!.Password, "a password typed is the one tried");

        fixture.Failure = new HttpRequestException("No such host is known. (proxy.corp:8080)");
        var unreachable = await fixture.Service.CheckAsync(new(Epoch, null, "manual", "http://proxy.corp:8080", [], "none", null, null), CancellationToken.None);
        Assert.AreEqual(("ok", false), (unreachable.Status, unreachable.Reached));
        StringAssert.Contains(unreachable.Message, "No such host is known");

        Assert.AreEqual("invalid", (await fixture.Service.CheckAsync(new(Epoch, "ftp://x", "none", null, null, null, null, null), CancellationToken.None)).Status);
        Assert.AreEqual("invalid", (await fixture.Service.CheckAsync(new(Epoch, null, "manual", null, null, null, null, null), CancellationToken.None)).Status);
    }

    [TestMethod]
    public async Task Read_SaysWhenTheFileHoldsSettingsThatDoNotRun()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Root, "config.toml"), "[network.proxy]\nmode = \"none\"\n");
        Assert.IsTrue((await fixture.Service.ReadAsync(new(Epoch), CancellationToken.None)).Pending);
        File.WriteAllText(Path.Combine(fixture.Root, "config.toml"), "[network.proxy]\nmode = \"bad\"\n");
        Assert.AreEqual("config_invalid", (await fixture.Service.ReadAsync(new(Epoch), CancellationToken.None)).Status);
    }

    private sealed class Fixture : IDisposable
    {
        private NetworkService? _service;

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "codealta-network-rpc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            // A key in a file: a test never touches the Keychain of the user.
            Secrets = new LocalSecretStore(Root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(Root, "secrets", "key"))));
        }

        public string Root { get; }

        public LocalSecretStore Secrets { get; }

        public IWebProxy? System { get; init; }

        public int Applied { get; private set; }

        public int SecretChanges { get; private set; }

        public ProxySettings Running { get; private set; } = ProxySettings.Default;

        public HttpStatusCode Answer { get; set; } = HttpStatusCode.OK;

        public Exception? Failure { get; set; }

        public IWebProxy? Proxy { get; private set; }

        public Uri? Requested { get; private set; }

        public NetworkService Service => _service ??= new NetworkService(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Root }), Secrets, () =>
        {
            Applied++;
            Running = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Root }).LoadGlobalProxySettings();
            return null;
        }, () => Running, System, Epoch, proxy =>
        {
            Proxy = proxy;
            return new Answering(this);
        }) { SecretChanged = () => SecretChanges++ };

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private sealed class Answering(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                fixture.Requested = request.RequestUri;
                if (fixture.Failure is { } failure) throw failure;
                return Task.FromResult(new HttpResponseMessage(fixture.Answer));
            }
        }
    }

    private sealed class FixedProxy(Uri address) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => address;

        public bool IsBypassed(Uri host) => false;
    }
}
