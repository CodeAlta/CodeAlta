using CodeAlta.Fleet.Protocol;
using CodeAlta.Server.Storage;
using CodeAlta.Server.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Tests;

[TestClass]
public sealed class SqliteCoordinatorStoreTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static async Task<SqliteCoordinatorStore> OpenAsync(TempDirectory root, TestClock? clock = null)
    {
        var store = new SqliteCoordinatorStore(new SqliteCoordinatorStoreOptions
        {
            DatabasePath = Path.Combine(root.Path, "test.sqlite3"),
            TimeProvider = clock ?? new TestClock(Start),
        });
        await store.InitializeAsync();
        return store;
    }

    private static async Task<(OrganizationRecord Organization, DeviceRecord Owner)> SetUpAsync(ICoordinatorStore store)
    {
        var organization = new OrganizationRecord("org", "Home", Start);
        var person = new PersonRecord("alex", organization.Id, "Alex", PersonRoles.Owner, Start);
        var device = new DeviceRecord("desktop", organization.Id, "Desktop", FleetDeviceKinds.Controller, "key-desktop", "fp-desktop", person.Id, DeviceStatuses.Approved,
            "desktop", null, Start, Start, null, 1);
        var entry = new DeviceListEntryRecord(organization.Id, 1, device.Id, FleetDeviceListOperations.Add, device.Id, "{}", "sig", "hash-1", Start);
        Assert.IsTrue(await store.CreateOrganizationAsync(organization, person, device, entry, Audit("server.setup")));
        return (organization, device);
    }

    private static AuditEntry Audit(string action, string? target = null) => new(action, AuditEntry.Success, "alex", "desktop", target, "127.0.0.1", Guid.NewGuid().ToString("N"));

    private static SessionRecord Fact(string id, string machine, long epoch, long version, string state = "running")
        => new(id, "org", machine, epoch, version, state, Start, Start.AddMinutes(version), "anthropic", "claude-opus-5-5", version, 2 * version, 100, 50, "sealed:" + version, Start);

    private static DeviceRecord Node(string id) => new(id, "org", id, FleetDeviceKinds.Node, "key-" + id, "fp-" + id, "alex", DeviceStatuses.Approved, "desktop", null, Start, Start, null, 1);

    [TestMethod]
    public async Task MigrationsRunFromAnEmptyFileAndOnceOnly()
    {
        using var root = new TempDirectory();
        var migrations = SqliteCoordinatorStore.GetMigrations();
        Assert.AreEqual(1, migrations[0].Version);
        await using (var store = await OpenAsync(root))
        {
            Assert.AreEqual(migrations[^1].Version, await store.GetSchemaVersionAsync());
            Assert.IsNull(await store.GetOrganizationAsync());
        }

        // Opened again: nothing runs twice, and the data stays.
        await using (var store = await OpenAsync(root))
        {
            Assert.AreEqual(migrations[^1].Version, await store.GetSchemaVersionAsync());
        }

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root.Path, "test.sqlite3")};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        var tables = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        }

        CollectionAssert.IsSubsetOf(new[] { "audit", "device_list", "devices", "enrollment_tokens", "machines", "organizations", "people", "schema_migrations", "sessions" }, tables);
        command.CommandText = "PRAGMA journal_mode";
        Assert.AreEqual("wal", (string?)await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM schema_migrations";
        Assert.AreEqual((long)migrations.Count, (long)(await command.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task ADatabaseNewerThanTheServerIsRefused()
    {
        using var root = new TempDirectory();
        await using (var store = await OpenAsync(root))
        {
        }

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root.Path, "test.sqlite3")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO schema_migrations (version, name, applied_at) VALUES (999, 'future', 0)";
            await command.ExecuteNonQueryAsync();
        }

        var newer = new SqliteCoordinatorStore(new SqliteCoordinatorStoreOptions { DatabasePath = Path.Combine(root.Path, "test.sqlite3") });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await newer.InitializeAsync());
        await newer.DisposeAsync();
    }

    [TestMethod]
    public async Task OneOrganizationPerServer()
    {
        using var root = new TempDirectory();
        await using var store = await OpenAsync(root);
        await SetUpAsync(store);
        var second = new OrganizationRecord("org2", "Other", Start);
        Assert.IsFalse(await store.CreateOrganizationAsync(second, new PersonRecord("p2", "org2", "P", PersonRoles.Owner, Start),
            new DeviceRecord("d2", "org2", "D", FleetDeviceKinds.Controller, "k2", "f2", "p2", DeviceStatuses.Approved, null, null, Start, Start, null, 1),
            new DeviceListEntryRecord("org2", 1, "d2", FleetDeviceListOperations.Add, "d2", "{}", "s", "h", Start), Audit("server.setup")));
        Assert.AreEqual("org", (await store.GetOrganizationAsync())!.Id);
        Assert.AreEqual(1, (await store.ListAuditAsync(0, 10)).Count, "A refused setup writes nothing, not even its audit record.");
    }

    [TestMethod]
    public async Task SessionFactsKeepTheHighestVersionAndRefuseStaleEpochs()
    {
        using var root = new TempDirectory();
        await using var store = await OpenAsync(root);
        await SetUpAsync(store);
        Assert.AreEqual(EnrollmentOutcome.TokenUnknown, (await store.EnrollDeviceAsync("none", Node("mac"), Start, Audit("device.enroll"))).Outcome);
        await InsertDevicesAsync(root, Node("mac"), Node("pc"));

        var first = await store.PublishSessionFactsAsync("mac", 1, [Fact("s1", "mac", 1, 3), Fact("s2", "mac", 1, 1)]);
        Assert.AreEqual(1, first.Ack);
        CollectionAssert.AreEqual(new[] { SessionFactOutcome.Applied, SessionFactOutcome.Applied }, first.Outcomes.Select(static o => o.Outcome).ToArray());

        // The same batch again is a no-op, and so is an older version.
        var again = await store.PublishSessionFactsAsync("mac", 1, [Fact("s1", "mac", 1, 3), Fact("s2", "mac", 1, 1)]);
        Assert.AreEqual(1, again.Ack);
        Assert.IsTrue(again.Outcomes.All(static o => o.Outcome == SessionFactOutcome.Unchanged));
        var older = await store.PublishSessionFactsAsync("mac", 2, [Fact("s1", "mac", 1, 2, "idle")]);
        Assert.AreEqual(SessionFactOutcome.Unchanged, older.Outcomes[0].Outcome);
        Assert.AreEqual(2, older.Ack);
        Assert.AreEqual(3, (await store.ListSessionsAsync("org")).Single(static s => s.Id == "s1").Version);

        // A newer version replaces it.
        var newer = await store.PublishSessionFactsAsync("mac", 3, [Fact("s1", "mac", 1, 4, "idle")]);
        Assert.AreEqual(SessionFactOutcome.Applied, newer.Outcomes[0].Outcome);
        var held = (await store.ListSessionsAsync("org")).Single(static s => s.Id == "s1");
        Assert.AreEqual(("idle", 4L, "sealed:4"), (held.State, held.Version, held.SealedMetadata));

        // Another machine at the same epoch is fenced; at a newer epoch (a move), it owns the session.
        Assert.AreEqual(SessionFactOutcome.Stale, (await store.PublishSessionFactsAsync("pc", 1, [Fact("s1", "pc", 1, 9)])).Outcomes[0].Outcome);
        Assert.AreEqual(SessionFactOutcome.Applied, (await store.PublishSessionFactsAsync("pc", 2, [Fact("s1", "pc", 2, 1)])).Outcomes[0].Outcome);
        // The old owner, at its old epoch, is stale from now on, whatever its version.
        Assert.AreEqual(SessionFactOutcome.Stale, (await store.PublishSessionFactsAsync("mac", 4, [Fact("s1", "mac", 1, 50)])).Outcomes[0].Outcome);
        held = (await store.ListSessionsAsync("org")).Single(static s => s.Id == "s1");
        Assert.AreEqual(("pc", 2L, 1L), (held.MachineId, held.Epoch, held.Version));

        var cursors = await store.GetPublicationCursorsAsync("mac");
        Assert.AreEqual(4, cursors.Facts);
        Assert.AreEqual(0, cursors.Inventory);
    }

    [TestMethod]
    public async Task AnEnrollmentTokenWorksOnceAndUntilItExpires()
    {
        using var root = new TempDirectory();
        await using var store = await OpenAsync(root);
        var (organization, owner) = await SetUpAsync(store);
        await store.CreateEnrollmentTokenAsync(new EnrollmentTokenRecord("t1", organization.Id, "hash-1", "mac", owner.OwnerPersonId, owner.Id, Start, Start.AddMinutes(10), null, null),
            Audit("enrollment_token.create", "t1"));
        await store.CreateEnrollmentTokenAsync(new EnrollmentTokenRecord("t2", organization.Id, "hash-2", null, owner.OwnerPersonId, owner.Id, Start, Start.AddMinutes(10), null, null),
            Audit("enrollment_token.create", "t2"));

        // Two machines race with one token: one enrolls, the other is refused.
        var race = await Task.WhenAll(
            store.EnrollDeviceAsync("hash-1", Node("mac") with { OrganizationId = string.Empty, Status = DeviceStatuses.Approved }, Start.AddMinutes(1), Audit("device.enroll")).AsTask(),
            store.EnrollDeviceAsync("hash-1", Node("pc"), Start.AddMinutes(1), Audit("device.enroll")).AsTask());
        Assert.AreEqual(1, race.Count(static r => r.Outcome == EnrollmentOutcome.Enrolled));
        Assert.AreEqual(1, race.Count(static r => r.Outcome == EnrollmentOutcome.TokenUsed));
        var enrolled = race.Single(static r => r.Outcome == EnrollmentOutcome.Enrolled).Device!;
        Assert.AreEqual((organization.Id, DeviceStatuses.Pending, owner.Id, "t1", owner.OwnerPersonId),
            (enrolled.OrganizationId, enrolled.Status, enrolled.EnrolledByDeviceId, enrolled.EnrollmentTokenId, enrolled.OwnerPersonId));

        Assert.AreEqual(EnrollmentOutcome.TokenExpired, (await store.EnrollDeviceAsync("hash-2", Node("other"), Start.AddMinutes(10), Audit("device.enroll"))).Outcome);
        Assert.AreEqual(EnrollmentOutcome.DeviceExists, (await store.EnrollDeviceAsync("hash-2", Node(enrolled.Id), Start.AddMinutes(5), Audit("device.enroll"))).Outcome);
    }

    [TestMethod]
    public async Task TheDeviceListGrowsOnlyFromItsLastEntry()
    {
        using var root = new TempDirectory();
        await using var store = await OpenAsync(root);
        var (organization, owner) = await SetUpAsync(store);
        await store.CreateEnrollmentTokenAsync(new EnrollmentTokenRecord("t1", organization.Id, "hash-1", null, owner.OwnerPersonId, owner.Id, Start, Start.AddHours(1), null, null),
            Audit("enrollment_token.create"));
        var (_, pending) = await store.EnrollDeviceAsync("hash-1", Node("mac"), Start, Audit("device.enroll"));

        var behind = new DeviceListEntryRecord(organization.Id, 1, pending!.Id, FleetDeviceListOperations.Add, owner.Id, "{}", "s", "h", Start);
        Assert.AreEqual(DeviceListAppendOutcome.Conflict, (await store.AppendDeviceListEntryAsync(behind, DeviceStatuses.Pending, DeviceStatuses.Approved, null, Audit("device.approve"))).Outcome);

        var next = behind with { Sequence = 2, Hash = "h2" };
        var (outcome, approved) = await store.AppendDeviceListEntryAsync(next, DeviceStatuses.Pending, DeviceStatuses.Approved, owner.OwnerPersonId, Audit("device.approve"));
        Assert.AreEqual(DeviceListAppendOutcome.Appended, outcome);
        Assert.AreEqual((DeviceStatuses.Approved, 2L), (approved!.Status, approved.Version));
        Assert.AreEqual(DeviceListAppendOutcome.DeviceStateChanged,
            (await store.AppendDeviceListEntryAsync(next with { Sequence = 3 }, DeviceStatuses.Pending, DeviceStatuses.Approved, null, Audit("device.approve"))).Outcome);
        Assert.AreEqual(2, (await store.ListDeviceListAsync(organization.Id)).Count);
    }

    [TestMethod]
    public async Task TheAuditLogIsChainedAndATamperedRecordBreaksIt()
    {
        using var root = new TempDirectory();
        var clock = new TestClock(Start);
        await using (var store = await OpenAsync(root, clock))
        {
            await SetUpAsync(store);
            for (var index = 0; index < 5; index++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                await store.AppendAuditAsync(Audit("test.action", "target-" + index) with { Details = "{\"n\":" + index + "}" });
            }

            var records = await store.ListAuditAsync(0, 100);
            Assert.AreEqual(6, records.Count);
            Assert.AreEqual(string.Empty, records[0].PreviousHash);
            for (var index = 1; index < records.Count; index++) Assert.AreEqual(records[index - 1].Hash, records[index].PreviousHash);
            Assert.AreEqual(new AuditVerification(true, 6, null), await store.VerifyAuditAsync());
            Assert.AreEqual(2, (await store.ListAuditAsync(4, 100)).Count);
        }

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root.Path, "test.sqlite3")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE audit SET target = 'someone-else' WHERE seq = 3";
            await command.ExecuteNonQueryAsync();
        }

        await using (var store = await OpenAsync(root, clock))
        {
            Assert.AreEqual(new AuditVerification(false, 3, 3), await store.VerifyAuditAsync());
        }

        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(root.Path, "test.sqlite3")};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE audit SET target = 'target-1' WHERE seq = 3; DELETE FROM audit WHERE seq = 4";
            await command.ExecuteNonQueryAsync();
        }

        await using (var store = await OpenAsync(root, clock))
        {
            var verification = await store.VerifyAuditAsync();
            Assert.IsFalse(verification.IsValid);
            Assert.AreEqual(5, verification.BrokenAt, "A removed record breaks the chain at the record after it.");
        }
    }

    [TestMethod]
    public async Task InventoryKeepsTheNewestSequence()
    {
        using var root = new TempDirectory();
        await using var store = await OpenAsync(root);
        await SetUpAsync(store);
        await InsertDevicesAsync(root, Node("mac"));
        Assert.AreEqual(new InventoryPublication(2, true), await store.PublishInventoryAsync(new MachineRecord("mac", "org", "{\"name\":\"b\",\"os\":\"macos\"}", 2, Start)));
        Assert.AreEqual(new InventoryPublication(2, false), await store.PublishInventoryAsync(new MachineRecord("mac", "org", "{\"name\":\"a\",\"os\":\"macos\"}", 1, Start)));
        Assert.AreEqual("{\"name\":\"b\",\"os\":\"macos\"}", (await store.ListMachinesAsync("org")).Single().Inventory);
        Assert.AreEqual(2, (await store.GetPublicationCursorsAsync("mac")).Inventory);
    }

    private static async Task InsertDevicesAsync(TempDirectory root, params DeviceRecord[] devices)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(root.Path, "test.sqlite3")};Pooling=False");
        await connection.OpenAsync();
        foreach (var device in devices)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO devices (id, organization_id, name, kind, public_key, fingerprint, owner_person_id, status, created_at, version) " +
                "VALUES ($id, 'org', $id, 'node', $key, $fp, 'alex', 'approved', 0, 1)";
            command.Parameters.AddWithValue("$id", device.Id);
            command.Parameters.AddWithValue("$key", device.PublicKey);
            command.Parameters.AddWithValue("$fp", device.Fingerprint);
            await command.ExecuteNonQueryAsync();
        }
    }
}
