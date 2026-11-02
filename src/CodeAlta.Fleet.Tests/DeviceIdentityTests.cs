using System.Security.Cryptography;
using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class DeviceIdentityTests
{
    [TestMethod]
    public void KeyIsCreatedOnceAndReloadedIdentically()
    {
        using var folder = new TestFolder();
        var fleetRoot = Path.Combine(folder.Path, "fleet");
        byte[] firstKey;
        string firstId;
        DeviceFingerprint firstFingerprint;
        DateTimeOffset firstCreatedAt;
        byte[] signature;
        var data = "signed by the device"u8.ToArray();
        using (var first = DeviceIdentity.LoadOrCreate(fleetRoot))
        {
            firstKey = first.ExportSubjectPublicKeyInfo();
            firstId = first.DeviceId;
            firstFingerprint = first.Fingerprint;
            firstCreatedAt = first.CreatedAt;
            signature = first.SignData(data);
            Assert.AreEqual(Path.Combine(Path.GetFullPath(fleetRoot), DeviceIdentity.KeyFileName), first.KeyPath);
        }

        var writtenAt = File.GetLastWriteTimeUtc(Path.Combine(fleetRoot, DeviceIdentity.KeyFileName));
        using var second = DeviceIdentity.LoadOrCreate(fleetRoot);
        CollectionAssert.AreEqual(firstKey, second.ExportSubjectPublicKeyInfo());
        Assert.AreEqual(firstId, second.DeviceId);
        Assert.AreEqual(firstFingerprint, second.Fingerprint);
        Assert.AreEqual(firstCreatedAt, second.CreatedAt);
        Assert.AreEqual(writtenAt, File.GetLastWriteTimeUtc(second.KeyPath));
        Assert.HasCount(1, Directory.GetFiles(fleetRoot));

        using var publicKey = ECDsa.Create();
        publicKey.ImportSubjectPublicKeyInfo(firstKey, out _);
        Assert.IsTrue(publicKey.VerifyData(data, second.SignData(data), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.IsTrue(publicKey.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [TestMethod]
    public void PrivateKeyIsProtectedAtRest()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path);
        using var document = JsonDocument.Parse(File.ReadAllBytes(identity.KeyPath));
        var stored = Convert.FromBase64String(document.RootElement.GetProperty("privateKey").GetString()!);

        if (OperatingSystem.IsWindows())
        {
            Assert.AreEqual("dpapi-current-user", document.RootElement.GetProperty("protection").GetString());
            using var probe = ECDsa.Create();
            Assert.ThrowsExactly<CryptographicException>(() => probe.ImportPkcs8PrivateKey(stored, out _));

            var privateKey = WindowsDataProtection.Unprotect(stored);
            probe.ImportPkcs8PrivateKey(privateKey, out _);
            CollectionAssert.AreEqual(identity.ExportSubjectPublicKeyInfo(), probe.ExportSubjectPublicKeyInfo());
            CollectionAssert.AreEqual(privateKey, WindowsDataProtection.Unprotect(WindowsDataProtection.Protect(privateKey)));
        }
        else
        {
            Assert.AreEqual("owner-only-file", document.RootElement.GetProperty("protection").GetString());
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(identity.KeyPath));
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder.Path));
        }
    }

    [TestMethod]
    public void EachStateRootHasItsOwnDevice()
    {
        using var folder = new TestFolder();
        var normal = new CatalogOptions { GlobalRoot = folder.Path };
        var developer = new CatalogOptions { GlobalRoot = folder.Path, StateRoot = Path.Combine(folder.Path, "dev") };

        using var normalIdentity = DeviceIdentity.LoadOrCreate(normal);
        using var developerIdentity = DeviceIdentity.LoadOrCreate(developer);

        Assert.AreEqual(Path.Combine(folder.Path, "fleet", DeviceIdentity.KeyFileName), normalIdentity.KeyPath);
        Assert.AreEqual(Path.Combine(folder.Path, "dev", "fleet", DeviceIdentity.KeyFileName), developerIdentity.KeyPath);
        Assert.AreNotEqual(normalIdentity.Fingerprint, developerIdentity.Fingerprint);
    }

    [TestMethod]
    public void DeviceIdDerivesFromTheKey()
    {
        using var folder = new TestFolder();
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path);

        Assert.AreEqual(DeviceIdentity.DeviceIdOf(identity.Fingerprint), identity.DeviceId);
        Assert.IsTrue(DeviceIdentity.IsValidDeviceId(identity.DeviceId));
        Assert.AreEqual(26, identity.DeviceId.Length);
        Assert.IsFalse(DeviceIdentity.IsValidDeviceId(identity.DeviceId.ToUpperInvariant()));
        Assert.IsFalse(DeviceIdentity.IsValidDeviceId(identity.DeviceId[..25]));
    }

    [TestMethod]
    public void DamagedKeyFileIsRefusedNotReplaced()
    {
        using var folder = new TestFolder();
        string path;
        using (var identity = DeviceIdentity.LoadOrCreate(folder.Path))
        {
            path = identity.KeyPath;
        }

        File.WriteAllText(path, "{ \"version\": 1, \"algorithm\": \"ecdsa-p256\" ");
        Assert.ThrowsExactly<InvalidDataException>(() => DeviceIdentity.LoadOrCreate(folder.Path));

        File.WriteAllText(path, "{ \"version\": 1, \"algorithm\": \"ecdsa-p256\", \"protection\": \"" + (OperatingSystem.IsWindows() ? "dpapi-current-user" : "owner-only-file")
            + "\", \"privateKey\": \"AAAA\", \"publicKey\": \"AAAA\" }");
        Assert.ThrowsExactly<InvalidDataException>(() => DeviceIdentity.LoadOrCreate(folder.Path));
    }

    [TestMethod]
    public void TlsCertificateCarriesTheDeviceKey()
    {
        using var folder = new TestFolder();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        using var identity = DeviceIdentity.LoadOrCreate(folder.Path, clock);
        using var certificate = identity.CreateTlsCertificate();

        Assert.IsTrue(certificate.HasPrivateKey);
        Assert.AreEqual(identity.Fingerprint, DeviceFingerprint.FromCertificate(certificate));
        Assert.AreEqual($"CN=CodeAlta device {identity.DeviceId}", certificate.Subject);
        Assert.IsLessThan(clock.Now.UtcDateTime, certificate.NotBefore.ToUniversalTime());
        Assert.IsGreaterThan(clock.Now.AddMonths(11).UtcDateTime, certificate.NotAfter.ToUniversalTime());
    }

    [TestMethod]
    public void DisposedIdentityRefusesToSign()
    {
        using var folder = new TestFolder();
        var identity = DeviceIdentity.LoadOrCreate(folder.Path);
        identity.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => identity.SignData([1, 2, 3]));
    }
}
