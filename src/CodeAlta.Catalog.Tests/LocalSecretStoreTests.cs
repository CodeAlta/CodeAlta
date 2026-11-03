using System.Text;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class LocalSecretStoreTests
{
    private const string Value = "sk-test-0123456789abcdef";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ASecret_IsListedByName_AndItsValueIsNeverInTheFile_WithTheFileKey()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);

        var set = store.SetSecret("GRAFANA_TOKEN", Value, "alex (tests)");

        Assert.AreEqual(("GRAFANA_TOKEN", 1, "alex (tests)", LocalSecretStore.MachineScope), (set.Name, set.Version, set.SetBy, set.Scope));
        var listed = store.ListSecrets().Single();
        Assert.AreEqual(("GRAFANA_TOKEN", 1), (listed.Name, listed.Version));
        Assert.AreEqual(SecretProtectionKinds.File, store.Protection);
        AssertNowhereInClear(root.Path);
        Assert.IsTrue(store.TryGetSecret("grafana_token", out var value), "A name is one name whatever its case, as an environment variable on Windows.");
        Assert.AreEqual(Value, value);
    }

    [TestMethod]
    public void AnotherStoreOnTheSameFolder_ReadsTheValueBack_AndFollowsItsChanges()
    {
        using var root = new TempRoot();
        var first = new LocalSecretStore(root.Path);
        var second = new LocalSecretStore(root.Path);
        Assert.IsNull(second.Protection, "A store without secret has no protection yet.");

        first.SetSecret("API_KEY", Value, "a");
        Assert.IsTrue(second.TryGetSecret("API_KEY", out var read), "The protection the store was made with is the one read back.");
        Assert.AreEqual(Value, read);
        Assert.AreEqual(OperatingSystem.IsWindows() ? SecretProtectionKinds.Dpapi : second.Protection, second.Protection);

        var before = second.ChangeStamp;
        first.RotateSecret("API_KEY", "sk-rotated-value", "b");
        Assert.AreNotEqual(before, second.ChangeStamp);
        Assert.IsTrue(second.TryGetSecret("API_KEY", out read));
        Assert.AreEqual("sk-rotated-value", read);
        Assert.AreEqual((2, "b"), (second.ListSecrets().Single().Version, second.ListSecrets().Single().SetBy));
        new LocalSecretStore(root.Path).Delete();
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void OnWindows_ValuesAreProtectedByDpapi()
    {
        using var root = new TempRoot();
        var store = new LocalSecretStore(root.Path);

        store.SetSecret("OPENAI_API_KEY", Value, "alex");

        Assert.AreEqual(SecretProtectionKinds.Dpapi, store.Protection);
        Assert.IsFalse(File.Exists(Path.Combine(store.FolderPath, "key")), "DPAPI needs no key of its own.");
        AssertNowhereInClear(root.Path);
        var protector = new DpapiSecretProtector();
        var protectedBytes = protector.Protect("hello"u8);
        Assert.AreEqual("hello", Encoding.UTF8.GetString(protector.Unprotect(protectedBytes)));
        Assert.ThrowsExactly<SecretStoreUnavailableException>(() => protector.Unprotect([1, 2, 3, 4]));
    }

    [TestMethod]
    public void Rotate_NeedsAnExistingSecret_SetAddsOrRenews_AndDeleteRemoves()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);

        Assert.ThrowsExactly<KeyNotFoundException>(() => store.RotateSecret("MISSING", Value, "a"));
        store.SetSecret("TOKEN", Value, "a");
        Assert.AreEqual(2, store.SetSecret("TOKEN", "another-value", "a").Version, "Setting an existing secret is a new version.");
        Assert.AreEqual(3, store.RotateSecret("TOKEN", "a-third-value", "a").Version);
        Assert.IsTrue(store.DeleteSecret("TOKEN"));
        Assert.IsFalse(store.DeleteSecret("TOKEN"));
        Assert.IsFalse(store.TryGetSecret("TOKEN", out _));
        Assert.AreEqual(0, store.ListSecrets().Count);
    }

    [TestMethod]
    public void Variables_AreReadable_AndVersionedOnlyWhenTheyChange()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);

        store.SetVariable("GRAFANA_URL", "https://grafana.example", "a");
        Assert.AreEqual(1, store.SetVariable("GRAFANA_URL", "https://grafana.example", "b").Version, "The same value is no change.");
        Assert.AreEqual(2, store.SetVariable("GRAFANA_URL", "https://grafana2.example", "b").Version);
        Assert.AreEqual("https://grafana2.example", store.ListVariables().Single().Value);
        Assert.IsTrue(store.TryGetVariable("GRAFANA_URL", out var value));
        Assert.AreEqual("https://grafana2.example", value);
        Assert.IsNull(store.Protection, "Variables are not protected: they need no key.");
        Assert.IsTrue(store.DeleteVariable("GRAFANA_URL"));
        Assert.IsFalse(store.TryGetVariable("GRAFANA_URL", out _));
    }

    [TestMethod]
    public void NamesAndValues_AreValidated()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);

        Assert.ThrowsExactly<ArgumentException>(() => store.SetSecret("1TOKEN", Value, "a"));
        Assert.ThrowsExactly<ArgumentException>(() => store.SetSecret("MY-TOKEN", Value, "a"));
        Assert.ThrowsExactly<ArgumentException>(() => store.SetSecret("TOKEN", string.Empty, "a"));
        Assert.ThrowsExactly<ArgumentException>(() => store.SetVariable("URL", new string('x', LocalSecretStore.MaximumValueLength + 1), "a"));
        Assert.IsTrue(LocalSecretStore.IsName("_A1"));
        Assert.IsFalse(LocalSecretStore.IsName(null));
    }

    [TestMethod]
    public void GetSecretValues_GivesEveryValue_ForMasking()
    {
        using var root = new TempRoot();
        var store = FileStore(root.Path);
        Assert.AreEqual(0, store.GetSecretValues().Count);

        store.SetSecret("A", "first-secret-value", "a");
        store.SetSecret("B", "second-secret-value", "a");

        CollectionAssert.AreEquivalent(new[] { "first-secret-value", "second-secret-value" }, store.GetSecretValues().ToArray());
    }

    [TestMethod]
    public void AValueProtectedByAnotherKey_IsAClearError_NeverAnotherValue()
    {
        using var root = new TempRoot();
        FileStore(root.Path).SetSecret("TOKEN", Value, "a");
        File.WriteAllText(Path.Combine(root.Path, "secrets", "key"), Convert.ToHexString(new byte[32]));

        var error = Assert.ThrowsExactly<SecretStoreUnavailableException>(() => FileStore(root.Path).TryGetSecret("TOKEN", out _));
        StringAssert.Contains(error.Message, "set it again");
        Assert.AreEqual(0, FileStore(root.Path).GetSecretValues().Count, "What cannot be read is not masked, and cannot leak.");
    }

    [TestMethod]
    public void ADamagedFile_IsReported()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(Path.Combine(root.Path, "secrets"));
        File.WriteAllText(Path.Combine(root.Path, "secrets", "store.json"), "{ not json");

        Assert.ThrowsExactly<InvalidDataException>(() => FileStore(root.Path).ListSecrets());
    }

    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public void OnUnix_TheFilesAreTheUsersAlone()
    {
        if (OperatingSystem.IsWindows()) return;
        using var root = new TempRoot();
        var store = FileStore(root.Path);
        store.SetSecret("TOKEN", Value, "a");

        const UnixFileMode userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.AreEqual(userOnly | UnixFileMode.UserExecute, File.GetUnixFileMode(store.FolderPath));
        Assert.AreEqual(userOnly, File.GetUnixFileMode(Path.Combine(store.FolderPath, "store.json")));
        Assert.AreEqual(userOnly, File.GetUnixFileMode(Path.Combine(store.FolderPath, "key")));
    }

    [TestMethod]
    public void TheStoreOfThisMachine_RoundTrips()
    {
        // The protection a real profile gets here: DPAPI on Windows, the Keychain or libsecret where they answer, else the file.
        using var root = new TempRoot();
        var store = new LocalSecretStore(root.Path);
        try
        {
            store.SetSecret("TOKEN", Value, "a");
            TestContext.WriteLine($"Protection: {store.Protection}");
            Assert.IsTrue(new LocalSecretStore(root.Path).TryGetSecret("TOKEN", out var value));
            Assert.AreEqual(Value, value);
        }
        finally
        {
            store.Delete();
        }

        Assert.IsFalse(Directory.Exists(store.FolderPath));
    }

    private static LocalSecretStore FileStore(string root)
        => new(root, new MasterKeySecretProtector(new FileSecretKeyStore(Path.Combine(root, "secrets", "key"))));

    private static void AssertNowhereInClear(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            Assert.IsFalse(File.ReadAllText(file).Contains(Value, StringComparison.Ordinal), $"{file} holds the value in clear.");
        }
    }

    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-secrets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
