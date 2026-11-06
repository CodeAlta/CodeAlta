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
    public void ATrustTakenBack_LeavesTheDevicesAsTheyWere()
    {
        using var folder = new TestFolder();
        var clock = new ManualTimeProvider(Now);
        var store = new TrustedDeviceStore(folder.Path, clock);
        var laptop = NewFingerprint();
        var desk = NewFingerprint();
        var kept = store.Add("Laptop", laptop, DevicePermissions.View | DevicePermissions.Move);

        // A device trusted for a pairing that did not happen is forgotten, as if it never paired.
        var added = store.Add("Desk", desk, DevicePermissions.View);
        Assert.IsTrue(store.Remove(added.DeviceId));
        Assert.IsFalse(store.Remove(added.DeviceId));
        Assert.IsNull(store.FindTrusted(desk));
        Assert.IsFalse(store.IsRevoked(desk));

        // One that was revoked before is revoked again, with what it had.
        Assert.IsTrue(store.Revoke(kept.DeviceId));
        var revoked = store.List(includeRevoked: true).Single();
        store.Add("Laptop", laptop, DevicePermissions.View);
        store.Restore(revoked);
        var reopened = new TrustedDeviceStore(folder.Path, clock);
        Assert.AreEqual(revoked, reopened.List(includeRevoked: true).Single());
        Assert.IsTrue(reopened.IsRevoked(laptop));
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

    [TestMethod]
    public void SetPermissions_ChangesATrustedDevice_AndNeverARevokedOne()
    {
        using var folder = new TestFolder();
        var store = new TrustedDeviceStore(folder.Path, new ManualTimeProvider(Now));
        var laptop = store.Add("Laptop", NewFingerprint(), DevicePermissions.View | DevicePermissions.Drive);
        var phone = store.Add("Phone", NewFingerprint(), DevicePermissions.View);

        var changed = store.SetPermissions(laptop.DeviceId, DevicePermissions.View | DevicePermissions.Move);
        Assert.AreEqual(DevicePermissions.View | DevicePermissions.Move, changed!.Permissions);
        Assert.AreEqual(laptop.PairedAt, changed.PairedAt);
        Assert.AreEqual(changed, new TrustedDeviceStore(folder.Path, TimeProvider.System).List().First());

        store.Revoke(phone.DeviceId);
        Assert.IsNull(store.SetPermissions(phone.DeviceId, DevicePermissions.View));
        Assert.IsNull(store.SetPermissions("unknown", DevicePermissions.View));
    }

    [TestMethod]
    public void Match_FindsADeviceByIdNameOrIdPrefix()
    {
        using var folder = new TestFolder();
        var store = new TrustedDeviceStore(folder.Path, new ManualTimeProvider(Now));
        var laptop = store.Add("Laptop", NewFingerprint(), DevicePermissions.View);
        var phone = store.Add("Phone", NewFingerprint(), DevicePermissions.View);
        store.Revoke(phone.DeviceId);

        Assert.AreEqual(laptop.DeviceId, store.Match("laptop", includeRevoked: false).Single().DeviceId);
        Assert.AreEqual(laptop.DeviceId, store.Match(laptop.DeviceId.ToUpperInvariant(), includeRevoked: false).Single().DeviceId);
        Assert.AreEqual(laptop.DeviceId, store.Match(laptop.DeviceId[..6], includeRevoked: false).Single().DeviceId);
        Assert.IsEmpty(store.Match(laptop.DeviceId[..3], includeRevoked: false), "A prefix has four characters at least.");
        Assert.IsEmpty(store.Match("Phone", includeRevoked: false));
        Assert.AreEqual(phone.DeviceId, store.Match("Phone", includeRevoked: true).Single().DeviceId);
        Assert.IsEmpty(store.Match("  ", includeRevoked: true));
    }

    [TestMethod]
    public void PermissionNames_ReadAndWriteTheNamesOfTheApi()
    {
        Assert.IsTrue(DevicePermissionNames.TryParse(["view,Drive", " move "], out var permissions, out var unknown));
        Assert.AreEqual(DevicePermissions.View | DevicePermissions.Drive | DevicePermissions.Move, permissions);
        Assert.IsNull(unknown);
        CollectionAssert.AreEqual(new[] { "view", "drive", "move" }, DevicePermissionNames.Format(permissions).ToArray());
        Assert.IsFalse(DevicePermissionNames.TryParse(["view,fly"], out _, out unknown));
        Assert.AreEqual("fly", unknown);
        Assert.IsTrue(DevicePermissionNames.TryParse([], out permissions, out _));
        Assert.AreEqual(DevicePermissions.None, permissions);
    }

    private static DeviceFingerprint NewFingerprint() => DeviceFingerprint.FromBytes(RandomNumberGenerator.GetBytes(32));
}
