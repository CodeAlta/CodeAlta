using System.Reflection;
using System.Text;

namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ConfigRecoveryServiceTests
{
    private string _root = null!;
    private string ConfigPath => Path.Combine(_root, "config.toml");

    [TestInitialize]
    public void Initialize() => _root = Directory.CreateTempSubdirectory("codealta-recovery-store-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    [DataRow(65001, false)]
    [DataRow(65001, true)]
    [DataRow(1200, true)]
    [DataRow(1201, true)]
    [DataRow(12000, true)]
    [DataRow(12001, true)]
    public void Save_PreservesCompleteTextAndFormat(int codePage, bool bom)
    {
        var encoding = codePage == 65001 ? new UTF8Encoding(bom) : Encoding.GetEncoding(codePage);
        const string original = "# café\r\n[unknown]\nvalue = 'unchanged'\r\n";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(original)).ToArray();
        File.WriteAllBytes(ConfigPath, bytes);
        var service = Create();
        Assert.IsTrue(service.Reload());
        Assert.AreEqual(original, service.Snapshot!.Text);
        Assert.IsTrue(service.Save(original));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(ConfigPath));
        Assert.IsTrue(service.Save(original + "# next"));
        Assert.AreEqual(original + "# next", service.Snapshot!.Text);
        Assert.AreEqual(new TextFileCodec().Load(ConfigPath).Revision, service.Snapshot.Revision);
    }

    [TestMethod]
    [DataRow("@")]
    [DataRow("[providers.fixture]\ntype = 'unsupported-provider-type'")]
    public void Save_InvalidContentAndUnknownBaselineRefuseMutation(string invalid)
    {
        File.WriteAllText(ConfigPath, "# original");
        var service = Create();
        Assert.IsFalse(service.Save("# blind"));
        Assert.IsTrue(service.Reload());
        var baseline = service.Snapshot;
        Assert.IsFalse(service.Save(invalid));
        Assert.AreSame(baseline, service.Snapshot);
        Assert.AreEqual("# original", File.ReadAllText(ConfigPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Save_ConflictOrDeletionRequiresExplicitReload(bool delete)
    {
        File.WriteAllText(ConfigPath, "@");
        var service = Create();
        Assert.IsTrue(service.Reload());
        Assert.IsFalse(service.IsReady);
        var baseline = service.Snapshot;
        if (delete) File.Delete(ConfigPath);
        else File.WriteAllText(ConfigPath, "# external");
        Assert.IsFalse(service.Save("# repaired"));
        Assert.AreSame(baseline, service.Snapshot);
        Assert.IsFalse(service.Save("# retry"));
        Assert.AreEqual(!delete, File.Exists(ConfigPath));
        Assert.IsTrue(service.Reload());
        Assert.IsTrue(service.Save("# reapplied"));
    }

    [TestMethod]
    public void Reload_StrictDecodeFailureInvalidatesPreviousBaselineUntilReload()
    {
        File.WriteAllText(ConfigPath, "# original");
        var service = Create();
        Assert.IsTrue(service.Reload());
        File.WriteAllBytes(ConfigPath, [0xff, 0xfe, 0x00]);
        Assert.IsFalse(service.Reload());
        Assert.IsNull(service.Snapshot);
        Assert.IsNotNull(service.Failure);
        File.WriteAllText(ConfigPath, "# original");
        Assert.IsFalse(service.Save("# blind retry"));
        Assert.IsTrue(service.Reload());
        Assert.IsTrue(service.Save("# repaired"));
    }

    [TestMethod]
    public void Reload_ReadAndCreateFailuresRemainUnsavable()
    {
        Directory.CreateDirectory(ConfigPath);
        var service = Create();
        Assert.IsFalse(service.Reload());
        Assert.IsNull(service.Snapshot);
        Assert.IsFalse(service.Save("# unsafe"));
        Directory.Delete(ConfigPath);
        var blockedRoot = Path.Combine(_root, "blocked");
        File.WriteAllText(blockedRoot, "block parent creation");
        var blocked = new ConfigRecoveryService(blockedRoot, new TextFileCodec());
        Assert.IsFalse(blocked.Reload());
        Assert.IsFalse(blocked.IsReady);
        Assert.IsFalse(blocked.Save("# unsafe"));
        File.Delete(blockedRoot);
        Assert.IsTrue(blocked.Reload());
        Assert.IsTrue(blocked.IsReady);
    }

    [TestMethod]
    public void Save_ReadOnlyFailureRetainsBaselineAndCanRetry()
    {
        File.WriteAllText(ConfigPath, "# original");
        var service = Create();
        Assert.IsTrue(service.Reload());
        var baseline = service.Snapshot;
        File.SetAttributes(ConfigPath, FileAttributes.ReadOnly);
        try
        {
            Assert.IsFalse(service.Save("# edited"));
            Assert.AreSame(baseline, service.Snapshot);
            Assert.AreEqual("# original", File.ReadAllText(ConfigPath));
        }
        finally { File.SetAttributes(ConfigPath, FileAttributes.Normal); }
        Assert.IsTrue(service.Save("# edited"));
        Assert.IsNull(service.Failure);
    }

    [TestMethod]
    public void Reload_MissingNestedRootCreatesValidatedDefaults()
    {
        var service = new ConfigRecoveryService(Path.Combine(_root, "nested", "global"), new TextFileCodec());
        Assert.IsTrue(service.Reload());
        Assert.IsTrue(service.CreatedDefault);
        Assert.IsTrue(service.IsReady);
        Assert.IsNotNull(service.Snapshot);
        Assert.IsFalse(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.GetDirectoryName(service.ConfigPath)! }).EnsureGlobalConfigExists());
    }

    [TestMethod]
    [DataRow("# competing valid", true)]
    [DataRow("@", false)]
    public async Task FirstRun_CompetingCreatorIsNeverOverwrittenAndIsValidated(string content, bool valid)
    {
        var codec = new TextFileCodec();
        var gate = (SemaphoreSlim)typeof(TextFileCodec).GetField("_saveGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(codec)!;
        await gate.WaitAsync();
        var service = Create(codec);
        // Reload reaches the shared save gate synchronously after its missing-file read.
        var reload = service.ReloadAsync();
        Assert.IsFalse(reload.IsCompleted);
        File.WriteAllText(ConfigPath, content);
        gate.Release();
        Assert.IsTrue(await reload);
        Assert.AreEqual(content, File.ReadAllText(ConfigPath));
        Assert.AreEqual(content, service.Snapshot!.Text);
        Assert.AreEqual(valid, service.IsReady);
        Assert.IsFalse(service.CreatedDefault);
    }

    private ConfigRecoveryService Create() => Create(new TextFileCodec());
    private ConfigRecoveryService Create(TextFileCodec codec) => new(_root, codec);
}
