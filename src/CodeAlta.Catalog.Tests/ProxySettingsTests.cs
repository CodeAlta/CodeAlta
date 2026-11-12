using System.Net;
using CodeAlta.Catalog.Network;

namespace CodeAlta.Catalog.Tests;

/// <summary>The proxy of <c>[network.proxy]</c>: its table, its bypass list, and what it does to the HTTP clients and the environment.</summary>
[TestClass]
public sealed class ProxySettingsTests
{
    private static readonly ProxySettings Manual = new(ProxyMode.Manual, "http://proxy.corp:8080", ["*.corp.example", "10.0.0.0/8", "intranet:8443"],
        ProxyAuthentication.None, null, null);

    [TestMethod]
    public void Table_IsReadWrittenAndValidated_ThroughTheStore()
    {
        var root = NewRoot();
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            Assert.AreEqual(ProxySettings.Default, store.LoadGlobalProxySettings(), "no file: as the system says");

            var settings = Manual with { Authentication = ProxyAuthentication.Password, User = @"CORP\jdoe", PasswordSecret = ProxySettings.DefaultPasswordSecret };
            store.SaveGlobalProxySettings(settings);
            var text = File.ReadAllText(Path.Combine(root, "config.toml"));
            StringAssert.Contains(text, "[network.proxy]");
            StringAssert.Contains(text, "mode = \"manual\"");
            StringAssert.Contains(text, "url = \"http://proxy.corp:8080\"");
            StringAssert.Contains(text, "password_secret = \"NETWORK_PROXY_PASSWORD\"");
            var read = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }).LoadGlobalProxySettings();
            Assert.AreEqual(ProxyMode.Manual, read.Mode);
            CollectionAssert.AreEqual(Manual.Bypass.ToArray(), read.Bypass.ToArray());
            Assert.AreEqual((ProxyAuthentication.Password, @"CORP\jdoe", "NETWORK_PROXY_PASSWORD"), (read.Authentication, read.User, read.PasswordSecret));
            Assert.IsTrue(store.ValidateEffectiveGlobalConfig().IsValid);

            // The defaults are not written: a return to the system mode with nothing else leaves no table.
            store.SaveGlobalProxySettings(ProxySettings.Default);
            Assert.IsFalse(File.ReadAllText(Path.Combine(root, "config.toml")).Contains("network", StringComparison.Ordinal));

            // The address is kept in the system mode, so that a return to the manual one finds it.
            store.SaveGlobalProxySettings(Manual with { Mode = ProxyMode.System });
            Assert.AreEqual("http://proxy.corp:8080", store.LoadGlobalProxySettings().Url);
            Assert.IsFalse(File.ReadAllText(Path.Combine(root, "config.toml")).Contains("mode", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void InvalidTable_IsReported_AndTheStartKeepsTheSystemMode()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config.toml"), "[network.proxy]\nmode = \"manual\"\nurl = \"http://user:secret@proxy:8080\"\n");
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            Assert.ThrowsExactly<InvalidDataException>(() => store.LoadGlobalProxySettings());
            Assert.IsFalse(store.ValidateEffectiveGlobalConfig().IsValid, "the file is not valid");
            Assert.IsFalse(store.TryLoadGlobalProxySettings(out var settings, out var error));
            Assert.AreEqual(ProxySettings.Default, settings);
            StringAssert.Contains(error, "must not hold a user name or a password");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("manual", null, "A manual proxy needs the address")]
    [DataRow("direct", "http://proxy:8080", "The proxy mode must be")]
    [DataRow("manual", "ftp://proxy:21", "must be http://host:port")]
    [DataRow("manual", "http://proxy:8080/path", "must be http://host:port")]
    [DataRow("manual", "http://jdoe:pa55@proxy:8080", "must not hold a user name or a password")]
    public void Read_RefusesWhatIsNotAProxy(string mode, string? url, string expected)
    {
        var error = Assert.ThrowsExactly<InvalidDataException>(() => ProxySettings.Read(new CodeAltaProxySettingsDocument { Mode = mode, Url = url }));
        StringAssert.Contains(error.Message, expected);
    }

    [TestMethod]
    public void Read_ChecksTheBypassListAndThePassword()
    {
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => ProxySettings.Read(new CodeAltaProxySettingsDocument
        {
            Mode = "manual", Url = "http://proxy:8080", NoProxy = ["ok.example", "not a host"],
        })).Message, "'not a host'");
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => ProxySettings.Read(new CodeAltaProxySettingsDocument
        {
            Mode = "manual", Url = "http://proxy:8080", Authentication = "password", User = "jdoe",
        })).Message, "needs a user name and the secret");
        StringAssert.Contains(Assert.ThrowsExactly<InvalidDataException>(() => ProxySettings.Read(new CodeAltaProxySettingsDocument
        {
            Url = "http://proxy:8080", Authentication = "password", User = "jdoe", PasswordSecret = "not-a-name",
        })).Message, "secret name");
        Assert.AreEqual("http://proxy.corp:3128/", ProxySettings.Read(new CodeAltaProxySettingsDocument { Mode = "MANUAL", Url = "proxy.corp:3128" }).ProxyUri!.AbsoluteUri,
            "an address without a scheme is http");
    }

    [TestMethod]
    [DataRow("localhost", true)]
    [DataRow("app.localhost", true)]
    [DataRow("127.0.0.1", true)]
    [DataRow("127.8.9.10", true)]
    [DataRow("[::1]", true)]
    [DataRow("corp.example", true)]
    [DataRow("build.corp.example", true)]
    [DataRow("notcorp.example", false)]
    [DataRow("10.20.30.40", true)]
    [DataRow("11.0.0.1", false)]
    [DataRow("intranet", true)]
    [DataRow("api.anthropic.com", false)]
    public void BypassList_CoversNamesSubdomainsRangesAndThisComputer(string host, bool bypassed)
        => Assert.AreEqual(bypassed, new ProxyBypassList(Manual.Bypass).Matches(host));

    [TestMethod]
    public void BypassList_TakesWhatNoProxyUsuallyHolds()
    {
        var list = new ProxyBypassList([".example.org", "192.168.1.10/24", "fd00::/8", "*"]);
        Assert.IsTrue(list.Matches("anything.anywhere"), "* is every host");
        var ranges = new ProxyBypassList(["192.168.1.10/24", "fd00::/8", ".example.org"]);
        Assert.IsTrue(ranges.Matches("192.168.1.200"), "the bits past the prefix are dropped");
        Assert.IsFalse(ranges.Matches("192.168.2.1"));
        Assert.IsTrue(ranges.Matches("[fd12::1]"));
        Assert.IsTrue(ranges.Matches("example.org"));
        Assert.IsTrue(ranges.Matches("www.example.org"));
        Assert.IsNull(ProxyBypassList.GetEntryError("[::1]:8080"));
        Assert.IsNotNull(ProxyBypassList.GetEntryError("a b"));
        Assert.IsNotNull(ProxyBypassList.GetEntryError("10.0.0.0/99"));
        Assert.IsNotNull(ProxyBypassList.GetEntryError(""));
    }

    [TestMethod]
    [DataRow("*.corp.example", ".corp.example")]
    [DataRow(".corp.example", ".corp.example")]
    [DataRow("corp.example", "corp.example")]
    [DataRow("corp.example.", "corp.example")]
    [DataRow("intranet:8443", "intranet")]
    [DataRow("10.1.2.3:8080", "10.1.2.3")]
    [DataRow("[fd00::1]:8443", "fd00::1")]
    [DataRow("192.168.1.10/24", "192.168.1.0/24")]
    [DataRow("10.0.0.0/8", "10.0.0.0/8")]
    [DataRow("*", "*")]
    [DataRow("not a host", null)]
    public void TheBypassList_IsWrittenAsCurlGitPythonAndNodeReadNoProxy(string entry, string? expected)
        => Assert.AreEqual(expected, ProxyBypassList.ToEnvironmentEntry(entry));

    [TestMethod]
    public void ParseBypass_ReadsWhatAUserTypes()
        => CollectionAssert.AreEqual(new[] { "a.example", "10.0.0.0/8", "b.example" },
            ProxySettings.ParseBypass(" a.example, 10.0.0.0/8;\nb.example  A.EXAMPLE ").ToArray());

    [TestMethod]
    public void ManualMode_SendsTheClientsThroughTheProxy_AndSetsTheEnvironmentOfThePrograms()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HTTPS_PROXY"] = "http://env-proxy:3128", ["ALL_PROXY"] = "socks5://x:1080" };
        var system = new FixedProxy(new Uri("http://env-proxy:3128"));
        var proxy = new NetworkProxy(system, name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);

        // Before anything is applied: as the system says.
        Assert.AreEqual(new Uri("http://env-proxy:3128"), proxy.GetProxy(new Uri("https://api.anthropic.com/")));

        proxy.Apply(Manual with { Authentication = ProxyAuthentication.Password, User = "j doe", PasswordSecret = "NETWORK_PROXY_PASSWORD" }, "p@ss:word");
        Assert.AreEqual(new Uri("http://proxy.corp:8080/"), proxy.GetProxy(new Uri("https://api.anthropic.com/")));
        Assert.IsFalse(proxy.IsBypassed(new Uri("https://api.anthropic.com/")));
        Assert.IsTrue(proxy.IsBypassed(new Uri("https://git.corp.example/")));
        Assert.IsNull(proxy.GetProxy(new Uri("http://127.0.0.1:2582/mcp")), "this computer is never served by the proxy");
        var credential = proxy.Credentials!.GetCredential(new Uri("http://proxy.corp:8080/"), "Basic")!;
        Assert.AreEqual(("j doe", "p@ss:word"), (credential.UserName, credential.Password));

        // The programs read their proxy from both cases; the password is in the address, escaped.
        Assert.AreEqual("http://j%20doe:p%40ss%3Aword@proxy.corp:8080", environment["HTTPS_PROXY"]);
        Assert.AreEqual(environment["HTTPS_PROXY"], environment["http_proxy"]);
        Assert.AreEqual(".corp.example,10.0.0.0/8,intranet,localhost,127.0.0.1,::1", environment["NO_PROXY"]);
        Assert.AreEqual(environment["NO_PROXY"], environment["no_proxy"]);
        Assert.IsNull(environment["ALL_PROXY"], "the manual proxy is the whole configuration");

        // A program whose environment read the variables of the user again gets them too.
        var terminal = new Dictionary<string, string>(StringComparer.Ordinal) { ["HTTPS_PROXY"] = "http://registry-proxy:1", ["all_proxy"] = "x" };
        proxy.ApplyTo(terminal);
        Assert.AreEqual(environment["HTTPS_PROXY"], terminal["HTTPS_PROXY"]);
        Assert.IsFalse(terminal.ContainsKey("all_proxy"));
    }

    [TestMethod]
    public void NoneMode_GoesDirectly_AndRemovesEveryProxyVariable()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HTTP_PROXY"] = "http://env-proxy:3128", ["no_proxy"] = "x" };
        var proxy = new NetworkProxy(new FixedProxy(new Uri("http://env-proxy:3128")), name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);
        proxy.Apply(ProxySettings.Default with { Mode = ProxyMode.None }, null);
        Assert.IsNull(proxy.GetProxy(new Uri("https://api.openai.com/")));
        Assert.IsTrue(proxy.IsBypassed(new Uri("https://api.openai.com/")));
        Assert.IsNull(proxy.Credentials!.GetCredential(new Uri("http://env-proxy:3128/"), "Basic"));
        foreach (var name in NetworkProxy.Variables) Assert.IsNull(environment.GetValueOrDefault(name), name);
    }

    [TestMethod]
    public void SystemMode_GivesBackWhatTheProcessStartedWith()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["HTTPS_PROXY"] = "http://env-proxy:3128", ["NO_PROXY"] = "corp.example" };
        var system = new FixedProxy(new Uri("http://env-proxy:3128")) { Credentials = new NetworkCredential("system", "credential") };
        var proxy = new NetworkProxy(system, name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);
        var credentials = proxy.Credentials;
        proxy.Apply(Manual, null);
        Assert.AreEqual("http://proxy.corp:8080", environment["HTTPS_PROXY"]);

        proxy.Apply(ProxySettings.Default, null);
        Assert.AreEqual("http://env-proxy:3128", environment["HTTPS_PROXY"]);
        Assert.AreEqual("corp.example", environment["NO_PROXY"]);
        Assert.IsNull(environment["HTTP_PROXY"]);
        Assert.AreEqual(new Uri("http://env-proxy:3128"), proxy.GetProxy(new Uri("https://api.anthropic.com/")));
        // The clients keep the credentials they read first: the same instance gives those of the mode that applies.
        Assert.AreSame(credentials, proxy.Credentials);
        Assert.AreEqual("system", proxy.Credentials!.GetCredential(new Uri("http://env-proxy:3128/"), "Basic")!.UserName);
        var terminal = new Dictionary<string, string>(StringComparer.Ordinal) { ["HTTPS_PROXY"] = "http://registry-proxy:1" };
        proxy.ApplyTo(terminal);
        Assert.AreEqual("http://registry-proxy:1", terminal["HTTPS_PROXY"], "the system mode leaves an environment as it is");
    }

    [TestMethod]
    public void AccountAuthentication_GivesTheCredentialsOfTheProcess_AndNoPasswordToThePrograms()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var proxy = new NetworkProxy(null, name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);
        proxy.Apply(Manual with { Authentication = ProxyAuthentication.Account }, null);
        Assert.AreSame(CredentialCache.DefaultNetworkCredentials, proxy.Credentials!.GetCredential(new Uri("http://proxy.corp:8080/"), "Negotiate"));
        Assert.AreEqual("http://proxy.corp:8080", environment["HTTP_PROXY"]);
    }

    [TestMethod]
    public void TheSystemMode_LeavesTheProxyOfDotNetAsTheDefault_AndTheOthersInstallTheirs()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var system = new FixedProxy(new Uri("http://env-proxy:3128"));
        IWebProxy? current = system;
        var proxy = new NetworkProxy(system, name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value, value => current = value);

        // A start in the system mode: the proxy of .NET stays, with what only it does (the PAC failover of Windows).
        proxy.Apply(ProxySettings.Default, null);
        Assert.AreSame(system, current);
        Assert.IsFalse(proxy.RestartAdvised);

        // Leaving it while CodeAlta runs: the clients created since keep the proxy of .NET until a restart.
        proxy.Apply(Manual, null);
        Assert.AreSame(proxy, current);
        Assert.IsTrue(proxy.RestartAdvised);
        proxy.Apply(ProxySettings.Default, null);
        Assert.AreSame(system, current, "back to the system mode, the proxy of .NET again");
        Assert.IsTrue(proxy.RestartAdvised, "the clients created in the manual mode keep this instance, without the PAC failover");

        // A start in the manual mode: no client ever had the proxy of .NET.
        IWebProxy? other = system;
        var manual = new NetworkProxy(system, name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value, value => other = value);
        manual.Apply(Manual with { Mode = ProxyMode.None }, null);
        Assert.AreSame(manual, other);
        Assert.IsFalse(manual.RestartAdvised);
        manual.Apply(Manual, null);
        Assert.IsFalse(manual.RestartAdvised, "from one mode of this instance to another, every client follows");
        // To the system mode: the clients created in the none and manual modes keep this instance until a restart.
        manual.Apply(ProxySettings.Default, null);
        Assert.AreSame(system, other);
        Assert.IsTrue(manual.RestartAdvised);
    }

    [TestMethod]
    public void TheCredentials_OfAProxy_AreGivenToThatProxyAlone()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var proxy = new NetworkProxy(null, name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);
        var credentials = proxy.Credentials!;
        var first = new Uri("http://proxy-a.corp:8080/");
        proxy.Apply(Manual with { Url = "http://proxy-a.corp:8080", Authentication = ProxyAuthentication.Password, User = "a", PasswordSecret = "S" }, "password-a");
        Assert.AreEqual("password-a", credentials.GetCredential(first, "Basic")!.Password);

        // A request that went to proxy A gets its 407 after the settings moved to proxy B: it is given nothing.
        proxy.Apply(Manual with { Url = "http://proxy-b.corp:3128", Authentication = ProxyAuthentication.Password, User = "b", PasswordSecret = "S" }, "password-b");
        Assert.IsNull(credentials.GetCredential(first, "Basic"));
        Assert.IsNull(proxy.Upstream.Credentials!.GetCredential(first, "Basic"), "nor the sandbox that chained to A");
        Assert.AreEqual("password-b", credentials.GetCredential(new Uri("http://PROXY-B.corp:3128/"), "Basic")!.Password);
        Assert.IsNull(credentials.GetCredential(new Uri("http://proxy-b.corp:8080/"), "Basic"), "another port is another proxy");

        var created = NetworkProxy.Create(Manual with { Authentication = ProxyAuthentication.Password, User = "c", PasswordSecret = "S" }, "password-c", null)!;
        Assert.IsNull(created.Credentials!.GetCredential(first, "Basic"));
        Assert.AreEqual("password-c", created.Credentials!.GetCredential(new Uri("http://proxy.corp:8080/"), "Basic")!.Password);
    }

    [TestMethod]
    public void TheAddressShown_NeverHoldsTheUserNameAndPasswordOfAProxy()
    {
        Assert.AreEqual("http://env-proxy:3128", NetworkProxy.DisplayAddress(new Uri("http://jdoe:p%40ss@env-proxy:3128/")));
        Assert.AreEqual("https://[fd00::1]:8443", NetworkProxy.DisplayAddress(new Uri("https://u:p@[fd00::1]:8443")));
        Assert.AreEqual("The proxy tunnel request to proxy 'http://env-proxy:3128/' failed.",
            NetworkProxy.RedactCredentials("The proxy tunnel request to proxy 'http://jdoe:p%40ss@env-proxy:3128/' failed."));
        Assert.AreEqual("Refused *** and ***.", NetworkProxy.RedactCredentials("Refused p@ss and p%40ss.", "p@ss"));
        Assert.IsNull(NetworkProxy.RedactCredentials(null));
    }

    [TestMethod]
    public void Upstream_OfTheSandbox_IsTheManualProxyAlone()
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal);
        var proxy = new NetworkProxy(new FixedProxy(new Uri("http://env-proxy:3128")), name => environment.GetValueOrDefault(name), (name, value) => environment[name] = value);
        var target = new Uri("https://api.anthropic.com/");
        // The system mode: a sandbox connects directly, as it always did.
        Assert.IsTrue(proxy.Upstream.IsBypassed(target));
        Assert.IsNull(proxy.Upstream.GetProxy(target));

        proxy.Apply(Manual with { Authentication = ProxyAuthentication.Password, User = "jdoe", PasswordSecret = "S" }, "pw");
        Assert.AreEqual(new Uri("http://proxy.corp:8080/"), proxy.Upstream.GetProxy(target));
        Assert.IsTrue(proxy.Upstream.IsBypassed(new Uri("https://git.corp.example/")));
        Assert.AreEqual("pw", proxy.Upstream.Credentials!.GetCredential(new Uri("http://proxy.corp:8080/"), "Basic")!.Password);

        proxy.Apply(ProxySettings.Default with { Mode = ProxyMode.None }, null);
        Assert.IsNull(proxy.Upstream.GetProxy(target));
    }

    [TestMethod]
    public void Create_GivesTheProxyOfSettingsNotApplied()
    {
        var system = new FixedProxy(new Uri("http://env-proxy:3128"));
        Assert.AreSame(system, NetworkProxy.Create(ProxySettings.Default, null, system));
        Assert.IsNull(NetworkProxy.Create(ProxySettings.Default with { Mode = ProxyMode.None }, null, system));
        var manual = NetworkProxy.Create(Manual with { Authentication = ProxyAuthentication.Password, User = @"CORP\jdoe", PasswordSecret = "S" }, "pw", system)!;
        Assert.AreEqual(new Uri("http://proxy.corp:8080/"), manual.GetProxy(new Uri("https://www.codealta.io/")));
        Assert.IsNull(manual.GetProxy(new Uri("http://localhost:5000/")));
        var credential = manual.Credentials!.GetCredential(new Uri("http://proxy.corp:8080/"), "NTLM")!;
        Assert.AreEqual(("jdoe", "CORP", "pw"), (credential.UserName, credential.Domain, credential.Password), "DOMAIN\\user signs in to a domain");
        Assert.ThrowsExactly<ArgumentException>(() => NetworkProxy.Create(ProxySettings.Default with { Mode = ProxyMode.Manual }, null, system));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FixedProxy(Uri address) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => address;

        public bool IsBypassed(Uri host) => false;
    }
}
