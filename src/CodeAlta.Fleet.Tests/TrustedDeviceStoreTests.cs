using System.Security.Cryptography;
using CodeAlta.Catalog;
using CodeAlta.Fleet.Identity;

namespace CodeAlta.Fleet.Tests;

[TestClass]
public sealed class TrustedDeviceStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void AddedDevicesArePersisted()
    {
        using var folder = new TestFolder();
        var clock = new ManualTimeProvider(Now);
        var store = new TrustedDeviceStore(folder.Path, clock);
        var laptop = NewFingerprint();
        var phone = NewFingerprint();

        var added = store.Add("  Laptop  ", laptop, DevicePermissions.View | DevicePermissions.Drive);
        clock.Advance(TimeSpan.FromMinutes(1));
        store.Add("Phone", phone, DevicePermissions.View);

        Assert.AreEqual(DeviceIdentity.DeviceIdOf(laptop), added.DeviceId);
        Assert.AreEqual("Laptop", added.Name);
        Assert.AreEqual(Now, added.PairedAt);

        var reopened = new TrustedDeviceStore(folder.Path, clock);
        var devices = reopened.List();
        Assert.HasCount(2, devices);
        Assert.AreEqual(added, devices[0]);
        Assert.AreEqual("Phone", devices[1].Name);
        Assert.AreEqual(added, reopened.FindTrusted(laptop));
        Assert.IsNull(reopened.FindTrusted(NewFingerprint()));
        Assert.AreEqual(Path.Combine(folder.Path, TrustedDeviceStore.FileName), reopened.FilePath);
        Assert.HasCount(1, Directory.GetFiles(folder.Path), "No staging file is left behind.");
    }

    [TestMethod]
    public void RevokedDeviceStaysRefused()
    {
        using var folder = new TestFolder();
        var clock = new ManualTimeProvider(Now);
        var store = new TrustedDeviceStore(folder.Path, clock);
        var laptop = NewFingerprint();
        var device = store.Add("Laptop", laptop, DevicePermissions.View);

        clock.Advance(TimeSpan.FromHours(1));
        Assert.IsTrue(store.Revoke(device.DeviceId));
        Assert.IsFalse(store.Revoke(device.DeviceId));
        Assert.IsFalse(store.Revoke("unknown"));

        var reopened = new TrustedDeviceStore(folder.Path, clock);
        Assert.IsNull(reopened.FindTrusted(laptop));
        Assert.IsTrue(reopened.IsRevoked(laptop));
        Assert.IsEmpty(reopened.List());
        var listed = reopened.List(includeRevoked: true).Single();
        Assert.AreEqual(Now.AddHours(1), listed.RevokedAt);
        Assert.IsTrue(listed.IsRevoked);

        var paired = reopened.Add("Laptop again", laptop, DevicePermissions.View);
        Assert.IsFalse(reopened.IsRevoked(laptop));
        Assert.AreEqual(paired, reopened.FindTrusted(laptop));
        Assert.HasCount(1, reopened.List(includeRevoked: true));
    }

    [TestMethod]
    public void StoreLivesInTheFleetFolderOfTheInstance()
    {
        using var folder = new TestFolder();
        var options = new CatalogOptions { GlobalRoot = folder.Path, StateRoot = Path.Combine(folder.Path, "dev") };

        var store = new TrustedDeviceStore(options);

        Assert.AreEqual(Path.Combine(folder.Path, "dev", "fleet", TrustedDeviceStore.FileName), store.FilePath);
    }

    [TestMethod]
    public void InvalidInputsAndDamagedFilesAreRefused()
    {
        using var folder = new TestFolder();
        var store = new TrustedDeviceStore(folder.Path, TimeProvider.System);

        Assert.ThrowsExactly<ArgumentException>(() => store.Add(" ", NewFingerprint(), DevicePermissions.View));
        Assert.ThrowsExactly<ArgumentException>(() => store.Add(new string('x', 65), NewFingerprint(), DevicePermissions.View));
        Assert.ThrowsExactly<ArgumentException>(() => store.Add("bad\nname", NewFingerprint(), DevicePermissions.View));
        Assert.ThrowsExactly<ArgumentException>(() => store.Add("Laptop", default, DevicePermissions.View));

        File.WriteAllText(store.FilePath, "{ \"version\": 1, \"devices\": [ { \"deviceId\": \"x\", \"name\": \"Laptop\", \"fingerprint\": \"bad\" } ] }");
        Assert.ThrowsExactly<InvalidDataException>(() => new TrustedDeviceStore(folder.Path, TimeProvider.System));

        File.WriteAllText(store.FilePath, "not json");
        Assert.ThrowsExactly<InvalidDataException>(() => new TrustedDeviceStore(folder.Path, TimeProvider.System));
    }

    private static DeviceFingerprint NewFingerprint() => DeviceFingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
}
