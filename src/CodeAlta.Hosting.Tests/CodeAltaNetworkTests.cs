using CodeAlta.Catalog;
using CodeAlta.Catalog.Network;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Hosting.Tests;

/// <summary>What an application of CodeAlta reads at its start to apply its proxy: the table of the profile and the password of its secret.</summary>
[TestClass]
public sealed class CodeAltaNetworkTests
{
    [TestMethod]
    public void Read_OfAProfileWithoutTable_IsTheSystemMode()
    {
        var root = NewRoot();
        try
        {
            Assert.AreEqual((ProxySettings.Default, (string?)null, (string?)null), CodeAltaNetwork.Read(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Read_OfATableThatIsNotValid_KeepsTheSystemMode_AndSaysWhy()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config.toml"), "[network.proxy]\nmode = \"tunnel\"\n");
            var (settings, password, error) = CodeAltaNetwork.Read(root);
            Assert.AreEqual(ProxySettings.Default, settings);
            Assert.IsNull(password);
            StringAssert.Contains(error, "The proxy settings could not be read");
            StringAssert.Contains(error, "The proxy mode must be");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Read_GivesThePasswordOfTheSecret_AndSaysWhenItIsNotSet()
    {
        var root = NewRoot();
        try
        {
            new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root }).SaveGlobalProxySettings(new ProxySettings(ProxyMode.Manual, "http://proxy.corp:8080", [],
                ProxyAuthentication.Password, "jdoe", ProxySettings.DefaultPasswordSecret));
            var missing = CodeAltaNetwork.Read(root);
            Assert.AreEqual(ProxyMode.Manual, missing.Settings.Mode, "the proxy is still used: it answers 407, which says what is wrong");
            Assert.IsNull(missing.Password);
            StringAssert.Contains(missing.Error, "NETWORK_PROXY_PASSWORD");

            // A store with a key in a file: a test never touches the Keychain of the user.
            var store = new LocalSecretStore(root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "key"))));
            store.SetSecret(ProxySettings.DefaultPasswordSecret, "s3cret", "test");
            var read = CodeAltaNetwork.Read(root);
            Assert.AreEqual(("s3cret", (string?)null), (read.Password, read.Error));
            Assert.IsFalse(File.ReadAllText(Path.Combine(root, "config.toml")).Contains("s3cret", StringComparison.Ordinal), "the password is never in the configuration");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Read_OfAStoreThatCannotBeUsed_KeepsTheManualProxyWithoutPassword_AndSaysWhatToDo()
    {
        var root = NewRoot();
        try
        {
            var settings = new ProxySettings(ProxyMode.Manual, "http://proxy.corp:8080", [], ProxyAuthentication.Password, "jdoe", ProxySettings.DefaultPasswordSecret);
            var protector = new SwitchingProtector();
            var store = new LocalSecretStore(root, protector);
            store.SetSecret(ProxySettings.DefaultPasswordSecret, "s3cret", "test");
            // The Keychain is locked, its key is gone, or the value was protected by another key: the start goes on.
            protector.Available = false;
            var (applied, password, error) = CodeAltaNetwork.ReadPassword(new LocalSecretStore(root, protector), settings);
            Assert.AreEqual(ProxyMode.Manual, applied.Mode, "requests go through the proxy and get its 407, never around it");
            Assert.IsNull(password);
            StringAssert.Contains(error, "could not be read from the secret NETWORK_PROXY_PASSWORD");
            StringAssert.Contains(error, "Settings > Network");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void ReadPassword_IsNotAskedOutsideTheManualPasswordMode()
    {
        var settings = new ProxySettings(ProxyMode.System, "http://proxy.corp:8080", [], ProxyAuthentication.Password, "jdoe", "MISSING_SECRET");
        Assert.AreEqual((settings, (string?)null, (string?)null), CodeAltaNetwork.ReadPassword("/nowhere", settings));
    }

    // A protection whose store goes away.
    private sealed class SwitchingProtector : ISecretProtector
    {
        public bool Available { get; set; } = true;

        public string Kind => SecretProtectionKinds.File;

        public byte[] Protect(ReadOnlySpan<byte> value) => Available ? [.. value.ToArray().Reverse()] : throw new SecretStoreUnavailableException("The Keychain is locked.");

        public byte[] Unprotect(ReadOnlySpan<byte> protectedValue)
            => Available ? [.. protectedValue.ToArray().Reverse()] : throw new SecretStoreUnavailableException("The Keychain is locked.");
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
