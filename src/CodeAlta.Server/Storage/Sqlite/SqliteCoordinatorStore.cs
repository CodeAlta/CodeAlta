using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using CodeAlta.Fleet.Protocol;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Storage.Sqlite;

/// <summary>The options of a <see cref="SqliteCoordinatorStore"/>.</summary>
public sealed class SqliteCoordinatorStoreOptions
{
    /// <summary>Gets the path of the database file.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Gets whether the store only reads (a command such as <c>altaserver status</c> beside a running server).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Gets the clock of the times the store records (audit records).</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Gets how many writes may wait in the queue before a writer waits for room.</summary>
    public int WriteQueueCapacity { get; init; } = 1024;

    /// <summary>
    /// Gets what is told of each operation once it ended: its name (such as <c>PublishSessionFacts</c>), how long it took
    /// (for a write, its wait in the queue included) and whether it failed; null for nothing.
    /// </summary>
    public Action<string, TimeSpan, bool>? ObserveOperation { get; init; }
}

/// <summary>
/// The SQLite store of the coordinator: write-ahead log, one writer through a queue the store owns (as the application
/// database of CodeAlta), numbered SQL migrations embedded in the assembly.
/// </summary>
/// <remarks>
/// One connection writes, on one loop, each operation in its own <c>BEGIN IMMEDIATE</c> transaction, so no write meets
/// another and none needs a retry; reads open pooled connections of their own and never wait for the writer. WAL does
/// not work on a network file system: the database belongs on a local disk.
/// </remarks>
public sealed class SqliteCoordinatorStore : ICoordinatorStore
{
    private const string MigrationResourcePrefix = "CodeAlta.Server.Storage.Sqlite.Migrations.";
    private const string FactsStream = "facts";
    private const string InventoryStream = "inventory";

    private readonly SqliteCoordinatorStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly string _writeConnectionString;
    private readonly string _readConnectionString;
    private readonly Channel<WriteItem> _writes;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private SqliteConnection? _writer;
    private Task? _writeLoop;
    private bool _readyReadOnly;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="SqliteCoordinatorStore"/> class; the file is opened by <see cref="InitializeAsync"/>.</summary>
    /// <param name="options">The options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The path of the database is empty.</exception>
    public SqliteCoordinatorStore(SqliteCoordinatorStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        _options = options;
        _time = options.TimeProvider;
        DatabasePath = Path.GetFullPath(options.DatabasePath);
        _writeConnectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        _readConnectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ToString();
        _writes = Channel.CreateBounded<WriteItem>(new BoundedChannelOptions(Math.Max(1, options.WriteQueueCapacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>Gets the full path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets how many writes wait in the queue of the writer now.</summary>
    public int WriteQueueDepth => _writes.Reader.Count;

    /// <summary>Gets the numbers and names of the migrations this version of the store has, in order.</summary>
    /// <returns>The migrations.</returns>
    public static IReadOnlyList<(int Version, string Name)> GetMigrations()
        => LoadMigrations().Select(static migration => (migration.Version, migration.Name)).ToArray();

    /// <inheritdoc />
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_writeLoop is not null || _readyReadOnly) return;
            if (_options.ReadOnly)
            {
                if (!File.Exists(DatabasePath)) throw new FileNotFoundException("The database of the server does not exist.", DatabasePath);
                _readyReadOnly = true;
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            var writer = new SqliteConnection(_writeConnectionString);
            await writer.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // WAL is a property of the file: set once, it stays. synchronous=NORMAL is safe with WAL (a power cut
                // may lose the last commits, never corrupt the file) and is what the plan measured.
                Execute(writer, null, "PRAGMA journal_mode=WAL;");
                Execute(writer, null, "PRAGMA synchronous=NORMAL;");
                Execute(writer, null, "PRAGMA foreign_keys=ON;");
                Execute(writer, null, "PRAGMA busy_timeout=5000;");
                Migrate(writer);
            }
            catch
            {
                await writer.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _writer = writer;
            _writeLoop = Task.Factory.StartNew(WriteLoopAsync, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Copies the database into a new file with SQLite's backup API: a consistent copy of the last committed state, taken
    /// while the server runs (the writer goes on; the copy is what was committed when it started).
    /// </summary>
    /// <param name="destinationPath">The file to write; it must not exist.</param>
    /// <param name="cancellationToken">Cancels the wait for the copy to start.</param>
    /// <returns>A task that completes once the copy is written.</returns>
    /// <exception cref="IOException">The destination exists.</exception>
    public async Task BackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (File.Exists(destinationPath)) throw new IOException($"The backup '{destinationPath}' exists already.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await Task.Run(() =>
        {
            using var source = new SqliteConnection(_readConnectionString);
            source.Open();
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
            destination.Open();
            source.BackupDatabase(destination);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
        => ReadAsync(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations'";
            if (command.ExecuteScalar() is null) return 0;
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask<OrganizationRecord?> GetOrganizationAsync(CancellationToken cancellationToken = default)
        => ReadAsync(static connection => ReadOrganization(connection, null), cancellationToken);

    /// <inheritdoc />
    public ValueTask<bool> CreateOrganizationAsync(OrganizationRecord organization, PersonRecord owner, DeviceRecord device, DeviceListEntryRecord firstEntry,
        AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(firstEntry);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            if (ReadOrganization(connection, transaction) is not null) return false;
            Execute(connection, transaction, "INSERT INTO organizations (id, name, created_at) VALUES ($id, $name, $created)",
                ("$id", organization.Id), ("$name", organization.Name), ("$created", ToTime(organization.CreatedAt)));
            InsertPerson(connection, transaction, owner);
            InsertDevice(connection, transaction, device);
            InsertDeviceListEntry(connection, transaction, firstEntry);
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PersonRecord?> GetPersonAsync(string personId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        return ReadAsync(connection => QuerySingle(connection, null, PersonColumns + " FROM people WHERE id = $id", ReadPerson, ("$id", personId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PersonRecord>> ListPeopleAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, PersonColumns + " FROM people WHERE organization_id = $org ORDER BY created_at, id", ReadPerson,
            ("$org", organizationId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        return ReadAsync(connection => ReadDevice(connection, null, deviceId), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DeviceRecord>> ListDevicesAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, DeviceColumns + " FROM devices WHERE organization_id = $org ORDER BY created_at, id", ReadDeviceRow,
            ("$org", organizationId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DeviceListEntryRecord>> ListDeviceListAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, DeviceListColumns + " FROM device_list WHERE organization_id = $org ORDER BY seq", ReadDeviceListEntry,
            ("$org", organizationId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DeviceListEntryRecord?> GetLastDeviceListEntryAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => ReadLastDeviceListEntry(connection, null, organizationId), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask CreateEnrollmentTokenAsync(EnrollmentTokenRecord token, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(audit);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction,
                "INSERT INTO enrollment_tokens (id, organization_id, token_hash, label, owner_person_id, created_by_device_id, created_at, expires_at, used_at, used_by_device_id) " +
                "VALUES ($id, $org, $hash, $label, $owner, $by, $created, $expires, NULL, NULL)",
                ("$id", token.Id), ("$org", token.OrganizationId), ("$hash", token.TokenHash), ("$label", token.Label), ("$owner", token.OwnerPersonId),
                ("$by", token.CreatedByDeviceId), ("$created", ToTime(token.CreatedAt)), ("$expires", ToTime(token.ExpiresAt)));
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<(EnrollmentOutcome Outcome, DeviceRecord? Device)> EnrollDeviceAsync(string tokenHash, DeviceRecord device, DateTimeOffset now, AuditEntry audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenHash);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync<(EnrollmentOutcome, DeviceRecord?)>((connection, transaction) =>
        {
            var token = QuerySingle(connection, transaction,
                "SELECT id, organization_id, owner_person_id, created_by_device_id, expires_at, used_at FROM enrollment_tokens WHERE token_hash = $hash",
                static reader => (Id: reader.GetString(0), Organization: reader.GetString(1), Owner: GetNullableString(reader, 2), CreatedBy: reader.GetString(3),
                    ExpiresAt: reader.GetInt64(4), UsedAt: reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5)),
                ("$hash", tokenHash));
            if (token.Id is null) return (EnrollmentOutcome.TokenUnknown, null);
            if (token.UsedAt is not null) return (EnrollmentOutcome.TokenUsed, null);
            if (token.ExpiresAt <= ToTime(now)) return (EnrollmentOutcome.TokenExpired, null);
            if (ReadDevice(connection, transaction, device.Id) is not null || CountWhere(connection, transaction, "devices", "fingerprint", device.Fingerprint) > 0)
            {
                return (EnrollmentOutcome.DeviceExists, null);
            }

            var registered = device with
            {
                OrganizationId = token.Organization,
                OwnerPersonId = token.Owner,
                Status = DeviceStatuses.Pending,
                EnrolledByDeviceId = token.CreatedBy,
                EnrollmentTokenId = token.Id,
                CreatedAt = now,
                ApprovedAt = null,
                RevokedAt = null,
                Version = 1,
            };
            // The token is used only if nobody used it in between; under the one writer this always holds, but the
            // condition keeps it true for a store with concurrent writers (PostgreSQL).
            var used = Execute(connection, transaction,
                "UPDATE enrollment_tokens SET used_at = $now, used_by_device_id = $device WHERE id = $id AND used_at IS NULL",
                ("$now", ToTime(now)), ("$device", registered.Id), ("$id", token.Id));
            if (used != 1) return (EnrollmentOutcome.TokenUsed, null);
            InsertDevice(connection, transaction, registered);
            AppendAudit(connection, transaction, audit);
            return (EnrollmentOutcome.Enrolled, registered);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<(DeviceListAppendOutcome Outcome, DeviceRecord? Device)> AppendDeviceListEntryAsync(DeviceListEntryRecord entry, string expectedStatus, string newStatus,
        string? ownerPersonId, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(expectedStatus);
        ArgumentException.ThrowIfNullOrEmpty(newStatus);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync<(DeviceListAppendOutcome, DeviceRecord?)>((connection, transaction) =>
        {
            var last = ReadLastDeviceListEntry(connection, transaction, entry.OrganizationId);
            if (last is null || entry.Sequence != last.Sequence + 1) return (DeviceListAppendOutcome.Conflict, null);

            var device = ReadDevice(connection, transaction, entry.DeviceId);
            if (device is null || device.Status != expectedStatus) return (DeviceListAppendOutcome.DeviceStateChanged, device);

            var now = entry.RecordedAt;
            var changed = device with
            {
                Status = newStatus,
                OwnerPersonId = ownerPersonId ?? device.OwnerPersonId,
                ApprovedAt = newStatus == DeviceStatuses.Approved ? now : device.ApprovedAt,
                RevokedAt = newStatus == DeviceStatuses.Revoked ? now : device.RevokedAt,
                Version = device.Version + 1,
            };
            Execute(connection, transaction,
                "UPDATE devices SET status = $status, owner_person_id = $owner, approved_at = $approved, revoked_at = $revoked, version = $version WHERE id = $id",
                ("$status", changed.Status), ("$owner", changed.OwnerPersonId), ("$approved", ToNullableTime(changed.ApprovedAt)),
                ("$revoked", ToNullableTime(changed.RevokedAt)), ("$version", changed.Version), ("$id", changed.Id));
            InsertDeviceListEntry(connection, transaction, entry);
            AppendAudit(connection, transaction, audit);
            return (DeviceListAppendOutcome.Appended, changed);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<DeviceRecord?> RefusePendingDeviceAsync(string deviceId, DateTimeOffset now, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync<DeviceRecord?>((connection, transaction) =>
        {
            var device = ReadDevice(connection, transaction, deviceId);
            if (device is null || device.Status != DeviceStatuses.Pending) return null;
            var changed = device with { Status = DeviceStatuses.Revoked, RevokedAt = now, Version = device.Version + 1 };
            Execute(connection, transaction, "UPDATE devices SET status = $status, revoked_at = $revoked, version = $version WHERE id = $id",
                ("$status", changed.Status), ("$revoked", ToTime(now)), ("$version", changed.Version), ("$id", deviceId));
            AppendAudit(connection, transaction, audit);
            return changed;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<InventoryPublication> PublishInventoryAsync(MachineRecord machine, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return WriteAsync((connection, transaction) =>
        {
            var cursor = ReadCursor(connection, transaction, machine.DeviceId, InventoryStream);
            if (machine.InventorySequence <= cursor) return new InventoryPublication(cursor, false);
            Execute(connection, transaction,
                "INSERT INTO machines (device_id, organization_id, inventory, inventory_seq, updated_at) VALUES ($device, $org, $inventory, $seq, $updated) " +
                "ON CONFLICT (device_id) DO UPDATE SET inventory = excluded.inventory, inventory_seq = excluded.inventory_seq, updated_at = excluded.updated_at",
                ("$device", machine.DeviceId), ("$org", machine.OrganizationId), ("$inventory", machine.Inventory), ("$seq", machine.InventorySequence),
                ("$updated", ToTime(machine.UpdatedAt)));
            WriteCursor(connection, transaction, machine.DeviceId, InventoryStream, machine.InventorySequence);
            return new InventoryPublication(machine.InventorySequence, true);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<SessionFactsPublication> PublishSessionFactsAsync(string deviceId, long sequence, IReadOnlyList<SessionRecord> facts, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(facts);
        return WriteAsync((connection, transaction) =>
        {
            var outcomes = new List<(SessionRecord, SessionFactOutcome)>(facts.Count);
            foreach (var fact in facts)
            {
                var held = QuerySingle(connection, transaction, SessionColumns + " FROM sessions WHERE id = $id", ReadSession, ("$id", fact.Id));
                if (held is not null)
                {
                    if (fact.Epoch < held.Epoch || (fact.Epoch == held.Epoch && !string.Equals(fact.MachineId, held.MachineId, StringComparison.Ordinal)))
                    {
                        outcomes.Add((held, SessionFactOutcome.Stale));
                        continue;
                    }

                    if (fact.Epoch == held.Epoch && fact.Version <= held.Version)
                    {
                        outcomes.Add((held, SessionFactOutcome.Unchanged));
                        continue;
                    }
                }

                Execute(connection, transaction,
                    "INSERT INTO sessions (id, organization_id, machine_id, epoch, version, state, created_at, updated_at, provider_type, model, turns, tool_calls, " +
                    "input_tokens, output_tokens, sealed_metadata, received_at) VALUES ($id, $org, $machine, $epoch, $version, $state, $created, $updated, $provider, " +
                    "$model, $turns, $tools, $input, $output, $sealed, $received) " +
                    "ON CONFLICT (id) DO UPDATE SET machine_id = excluded.machine_id, epoch = excluded.epoch, version = excluded.version, state = excluded.state, " +
                    "created_at = excluded.created_at, updated_at = excluded.updated_at, provider_type = excluded.provider_type, model = excluded.model, " +
                    "turns = excluded.turns, tool_calls = excluded.tool_calls, input_tokens = excluded.input_tokens, output_tokens = excluded.output_tokens, " +
                    "sealed_metadata = excluded.sealed_metadata, received_at = excluded.received_at " +
                    "WHERE excluded.epoch > sessions.epoch OR (excluded.epoch = sessions.epoch AND excluded.version > sessions.version)",
                    ("$id", fact.Id), ("$org", fact.OrganizationId), ("$machine", fact.MachineId), ("$epoch", fact.Epoch), ("$version", fact.Version),
                    ("$state", fact.State), ("$created", ToTime(fact.CreatedAt)), ("$updated", ToTime(fact.UpdatedAt)), ("$provider", fact.ProviderType),
                    ("$model", fact.Model), ("$turns", fact.Turns), ("$tools", fact.ToolCalls), ("$input", fact.InputTokens), ("$output", fact.OutputTokens),
                    ("$sealed", fact.SealedMetadata), ("$received", ToTime(fact.ReceivedAt)));
                outcomes.Add((fact, SessionFactOutcome.Applied));
            }

            var cursor = ReadCursor(connection, transaction, deviceId, FactsStream);
            if (sequence > cursor)
            {
                WriteCursor(connection, transaction, deviceId, FactsStream, sequence);
                cursor = sequence;
            }

            return new SessionFactsPublication(cursor, outcomes);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SessionRecord>> WithdrawSessionFactsAsync(string deviceId, IReadOnlyList<string> sessionIds, AuditEntry audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        ArgumentNullException.ThrowIfNull(sessionIds);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync<IReadOnlyList<SessionRecord>>((connection, transaction) =>
        {
            var removed = new List<SessionRecord>();
            foreach (var id in sessionIds.Distinct(StringComparer.Ordinal))
            {
                var held = QuerySingle(connection, transaction, SessionColumns + " FROM sessions WHERE id = $id AND machine_id = $device", ReadSession, ("$id", id),
                    ("$device", deviceId));
                if (held is not null
                    && Execute(connection, transaction, "DELETE FROM sessions WHERE id = $id AND machine_id = $device", ("$id", id), ("$device", deviceId)) == 1)
                {
                    removed.Add(held);
                }
            }

            if (removed.Count > 0)
            {
                AppendAudit(connection, transaction, audit with { Details = IdsDetails("sessions", [.. removed.Select(static session => session.Id)]) });
            }

            return removed;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<FleetPublicationCursors> GetPublicationCursorsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        return ReadAsync(connection => new FleetPublicationCursors(ReadCursor(connection, null, deviceId, FactsStream), ReadCursor(connection, null, deviceId, InventoryStream)),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MachineRecord>> ListMachinesAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null,
            "SELECT device_id, organization_id, inventory, inventory_seq, updated_at FROM machines WHERE organization_id = $org ORDER BY device_id",
            static reader => new MachineRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), FromTime(reader.GetInt64(4))),
            ("$org", organizationId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SessionRecord>> ListSessionsAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QueryList(connection, null, SessionColumns + " FROM sessions WHERE organization_id = $org ORDER BY updated_at DESC, id", ReadSession,
            ("$org", organizationId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> CreateMetadataKeyAsync(MetadataKeyRecord key, IReadOnlyList<MetadataKeyWrapRecord> wraps, AuditEntry audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(wraps);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            using (var exists = Create(connection, transaction, "SELECT COUNT(*) FROM metadata_keys WHERE (person_id = $person AND epoch = $epoch) OR id = $id",
                ("$person", key.PersonId), ("$epoch", key.Epoch), ("$id", key.Id)))
            {
                if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return false;
            }

            Execute(connection, transaction,
                "INSERT INTO metadata_keys (id, organization_id, person_id, epoch, created_by_device_id, created_at) VALUES ($id, $org, $person, $epoch, $by, $created)",
                ("$id", key.Id), ("$org", key.OrganizationId), ("$person", key.PersonId), ("$epoch", key.Epoch), ("$by", key.CreatedByDeviceId),
                ("$created", ToTime(key.CreatedAt)));
            foreach (var wrap in wraps) InsertWrap(connection, transaction, wrap);
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> AddMetadataKeyWrapsAsync(IReadOnlyList<MetadataKeyWrapRecord> wraps, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(wraps);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync<IReadOnlyList<string>>((connection, transaction) =>
        {
            var added = new List<string>();
            foreach (var wrap in wraps)
            {
                if (InsertWrap(connection, transaction, wrap) == 1) added.Add(wrap.DeviceId);
            }

            if (added.Count > 0) AppendAudit(connection, transaction, audit with { Details = IdsDetails("devices", added) });
            return added;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<MetadataKeyWithWraps>> ListMetadataKeysAsync(string personId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(personId);
        return ReadAsync<IReadOnlyList<MetadataKeyWithWraps>>(connection =>
        {
            var keys = QueryList(connection, null,
                "SELECT id, organization_id, person_id, epoch, created_by_device_id, created_at FROM metadata_keys WHERE person_id = $person ORDER BY epoch",
                static reader => new MetadataKeyRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4),
                    FromTime(reader.GetInt64(5))), ("$person", personId));
            return [.. keys.Select(key => new MetadataKeyWithWraps(key, QueryList(connection, null,
                "SELECT key_id, device_id, wrapped, wrapped_by_device_id, created_at FROM metadata_key_wraps WHERE key_id = $key ORDER BY created_at, device_id",
                static reader => new MetadataKeyWrapRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), FromTime(reader.GetInt64(4))),
                ("$key", key.Id))))];
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<TelemetrySettingsRecord?> GetTelemetrySettingsAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QuerySingle(connection, null,
            "SELECT organization_id, seq, payload, signature, signer_device_id, server_headers, updated_at FROM telemetry_settings WHERE organization_id = $org",
            static reader => new TelemetrySettingsRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                GetNullableString(reader, 5), FromTime(reader.GetInt64(6))), ("$org", organizationId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> SetTelemetrySettingsAsync(TelemetrySettingsRecord settings, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            // A setting replaces only an older one: two owners that sign at once do not undo each other silently.
            var changed = Execute(connection, transaction,
                "INSERT INTO telemetry_settings (organization_id, seq, payload, signature, signer_device_id, server_headers, updated_at) " +
                "VALUES ($org, $seq, $payload, $signature, $signer, $headers, $at) " +
                "ON CONFLICT (organization_id) DO UPDATE SET seq = excluded.seq, payload = excluded.payload, signature = excluded.signature, " +
                "signer_device_id = excluded.signer_device_id, server_headers = excluded.server_headers, updated_at = excluded.updated_at " +
                "WHERE telemetry_settings.seq < excluded.seq",
                ("$org", settings.OrganizationId), ("$seq", settings.Sequence), ("$payload", settings.Payload), ("$signature", settings.Signature),
                ("$signer", settings.SignerDeviceId), ("$headers", settings.ServerHeaders), ("$at", ToTime(settings.UpdatedAt)));
            if (changed == 0) return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    private const string CampaignColumns = "SELECT id, organization_id, seq, automatic, target_version, feed, rollback, not_before, time_zone, payload, signature, " +
        "created_by_device_id, state, server_state, server_message, created_at, updated_at";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UpdateCampaignRecord>> ListUpdateCampaignsAsync(string organizationId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync(connection => QueryList(connection, null, CampaignColumns + " FROM update_campaigns WHERE organization_id = $org ORDER BY created_at DESC, id DESC LIMIT $limit",
            ReadCampaign, ("$org", organizationId), ("$limit", limit)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<UpdateCampaignRecord?> GetUpdateCampaignAsync(string campaignId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(campaignId);
        return ReadAsync(connection => QuerySingle(connection, null, CampaignColumns + " FROM update_campaigns WHERE id = $id", ReadCampaign, ("$id", campaignId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<UpdateCampaignMachineRecord>> ListUpdateCampaignMachinesAsync(string campaignId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(campaignId);
        return ReadAsync(connection => QueryList(connection, null,
            "SELECT campaign_id, device_id, state, running, message, instructed_at, updated_at FROM update_campaign_machines WHERE campaign_id = $id ORDER BY device_id",
            static reader => new UpdateCampaignMachineRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), GetNullableString(reader, 3), GetNullableString(reader, 4),
                GetNullableTime(reader, 5), FromTime(reader.GetInt64(6))), ("$id", campaignId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<long> GetLastUpdateSequenceAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection =>
        {
            using var command = Create(connection, null, "SELECT COALESCE(MAX(seq), 0) FROM update_campaigns WHERE organization_id = $org AND automatic = 0", ("$org", organizationId));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> CreateUpdateCampaignAsync(UpdateCampaignRecord campaign, IReadOnlyList<UpdateCampaignMachineRecord> machines, AuditEntry? audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(machines);
        return WriteAsync((connection, transaction) =>
        {
            if (!campaign.Automatic)
            {
                using var exists = Create(connection, transaction, "SELECT COUNT(*) FROM update_campaigns WHERE organization_id = $org AND automatic = 0 AND seq = $seq",
                    ("$org", campaign.OrganizationId), ("$seq", campaign.Sequence));
                if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) > 0) return false;
            }

            Execute(connection, transaction,
                "INSERT INTO update_campaigns (id, organization_id, seq, automatic, target_version, feed, rollback, not_before, time_zone, payload, signature, " +
                "created_by_device_id, state, server_state, server_message, created_at, updated_at) VALUES ($id, $org, $seq, $automatic, $version, $feed, $rollback, " +
                "$notBefore, $zone, $payload, $sig, $by, $state, $serverState, $serverMessage, $created, $updated)",
                ("$id", campaign.Id), ("$org", campaign.OrganizationId), ("$seq", campaign.Sequence), ("$automatic", campaign.Automatic ? 1 : 0),
                ("$version", campaign.TargetVersion), ("$feed", campaign.Feed), ("$rollback", campaign.Rollback ? 1 : 0), ("$notBefore", ToNullableTime(campaign.NotBefore)),
                ("$zone", campaign.TimeZone), ("$payload", campaign.Payload), ("$sig", campaign.Signature), ("$by", campaign.CreatedByDeviceId), ("$state", campaign.State),
                ("$serverState", campaign.ServerState), ("$serverMessage", campaign.ServerMessage), ("$created", ToTime(campaign.CreatedAt)), ("$updated", ToTime(campaign.UpdatedAt)));
            foreach (var machine in machines)
            {
                // A machine an older campaign still waits for is this campaign's now.
                Execute(connection, transaction,
                    "UPDATE update_campaign_machines SET state = 'superseded', updated_at = $at WHERE device_id = $device AND campaign_id <> $id " +
                    "AND state IN ('pending', 'instructed', 'waiting', 'downloading', 'switching') " +
                    "AND campaign_id IN (SELECT id FROM update_campaigns WHERE organization_id = $org)",
                    ("$at", ToTime(campaign.CreatedAt)), ("$device", machine.DeviceId), ("$id", campaign.Id), ("$org", campaign.OrganizationId));
                WriteCampaignMachine(connection, transaction, machine);
            }

            if (audit is not null) AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SaveUpdateCampaignAsync(UpdateCampaignRecord campaign, IReadOnlyList<UpdateCampaignMachineRecord> machines, AuditEntry? audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(machines);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            Execute(connection, transaction,
                "UPDATE update_campaigns SET state = $state, server_state = $serverState, server_message = $serverMessage, updated_at = $updated WHERE id = $id",
                ("$state", campaign.State), ("$serverState", campaign.ServerState), ("$serverMessage", campaign.ServerMessage), ("$updated", ToTime(campaign.UpdatedAt)),
                ("$id", campaign.Id));
            foreach (var machine in machines) WriteCampaignMachine(connection, transaction, machine);
            if (audit is not null) AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<UpdatePolicyRecord?> GetUpdatePolicyAsync(string organizationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return ReadAsync(connection => QuerySingle(connection, null,
            "SELECT organization_id, seq, payload, signature, signer_device_id, last_check_at, last_check_message, updated_at FROM update_policies WHERE organization_id = $org",
            static reader => new UpdatePolicyRecord(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                GetNullableTime(reader, 5), GetNullableString(reader, 6), FromTime(reader.GetInt64(7))), ("$org", organizationId)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> SetUpdatePolicyAsync(UpdatePolicyRecord policy, AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(audit);
        return WriteAsync((connection, transaction) =>
        {
            // A new policy forgets the last check: an automatic one reads its feed at once.
            var changed = Execute(connection, transaction,
                "INSERT INTO update_policies (organization_id, seq, payload, signature, signer_device_id, last_check_at, last_check_message, updated_at) " +
                "VALUES ($org, $seq, $payload, $signature, $signer, NULL, NULL, $at) " +
                "ON CONFLICT (organization_id) DO UPDATE SET seq = excluded.seq, payload = excluded.payload, signature = excluded.signature, " +
                "signer_device_id = excluded.signer_device_id, last_check_at = NULL, last_check_message = NULL, updated_at = excluded.updated_at " +
                "WHERE update_policies.seq < excluded.seq",
                ("$org", policy.OrganizationId), ("$seq", policy.Sequence), ("$payload", policy.Payload), ("$signature", policy.Signature),
                ("$signer", policy.SignerDeviceId), ("$at", ToTime(policy.UpdatedAt)));
            if (changed == 0) return false;
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask SetUpdatePolicyCheckAsync(string organizationId, DateTimeOffset at, string? message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(organizationId);
        return new ValueTask(WriteAsync((connection, transaction) => Execute(connection, transaction,
            "UPDATE update_policies SET last_check_at = $at, last_check_message = $message WHERE organization_id = $org",
            ("$at", ToTime(at)), ("$message", message), ("$org", organizationId)), cancellationToken).AsTask());
    }

    private static void WriteCampaignMachine(SqliteConnection connection, SqliteTransaction transaction, UpdateCampaignMachineRecord machine)
        => Execute(connection, transaction,
            "INSERT INTO update_campaign_machines (campaign_id, device_id, state, running, message, instructed_at, updated_at) " +
            "VALUES ($campaign, $device, $state, $running, $message, $instructed, $updated) ON CONFLICT (campaign_id, device_id) DO UPDATE SET " +
            "state = excluded.state, running = excluded.running, message = excluded.message, instructed_at = excluded.instructed_at, updated_at = excluded.updated_at",
            ("$campaign", machine.CampaignId), ("$device", machine.DeviceId), ("$state", machine.State), ("$running", machine.Running), ("$message", machine.Message),
            ("$instructed", ToNullableTime(machine.InstructedAt)), ("$updated", ToTime(machine.UpdatedAt)));

    private static UpdateCampaignRecord ReadCampaign(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3) != 0, reader.GetString(4), reader.GetString(5), reader.GetInt64(6) != 0,
            GetNullableTime(reader, 7), GetNullableString(reader, 8), reader.GetString(9), reader.GetString(10), GetNullableString(reader, 11), reader.GetString(12),
            reader.GetString(13), GetNullableString(reader, 14), FromTime(reader.GetInt64(15)), FromTime(reader.GetInt64(16)));

    /// <inheritdoc />
    public ValueTask AppendAuditAsync(AuditEntry audit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return new ValueTask(WriteAsync((connection, transaction) =>
        {
            AppendAudit(connection, transaction, audit);
            return true;
        }, cancellationToken).AsTask());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AuditRecord>> ListAuditAsync(long afterSequence, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        return ReadAsync(connection => QueryList(connection, null, AuditColumns + " FROM audit WHERE seq > $after ORDER BY seq LIMIT $limit", ReadAudit,
            ("$after", afterSequence), ("$limit", limit)), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<AuditVerification> VerifyAuditAsync(CancellationToken cancellationToken = default)
        => ReadAsync(static connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = AuditColumns + " FROM audit ORDER BY seq";
            using var reader = command.ExecuteReader();
            return AuditChain.Verify(ReadAll(reader));

            static IEnumerable<AuditRecord> ReadAll(SqliteDataReader reader)
            {
                while (reader.Read()) yield return ReadAudit(reader);
            }
        }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _writes.Writer.TryComplete();
        if (_writeLoop is not null)
        {
            await _writeLoop.ConfigureAwait(false);
        }

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        // Pooled read connections keep the file open (on Windows, a temporary folder could not be deleted).
        using (var pooled = new SqliteConnection(_readConnectionString))
        {
            SqliteConnection.ClearPool(pooled);
        }

        _initGate.Dispose();
    }

    private ValueTask<T> ReadAsync<T>(Func<SqliteConnection, T> read, CancellationToken cancellationToken, [CallerMemberName] string operation = "")
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_writeLoop is null && !_readyReadOnly) throw new InvalidOperationException("The store is not initialized.");
        var started = Stopwatch.GetTimestamp();
        var failed = true;
        try
        {
            using var connection = new SqliteConnection(_readConnectionString);
            connection.Open();
            var result = read(connection);
            failed = false;
            return ValueTask.FromResult(result);
        }
        finally
        {
            Observe(operation, started, failed);
        }
    }

    private async ValueTask<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken,
        [CallerMemberName] string operation = "")
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_options.ReadOnly) throw new InvalidOperationException("The store was opened to read only.");
        if (_writeLoop is null) throw new InvalidOperationException("The store is not initialized.");
        var item = new WriteItem<T>(write, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        var failed = true;
        try
        {
            try
            {
                await _writes.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException exception)
            {
                throw new ObjectDisposedException(nameof(SqliteCoordinatorStore), exception);
            }

            var result = await item.Task.ConfigureAwait(false);
            failed = false;
            return result;
        }
        finally
        {
            Observe(operation, started, failed);
        }
    }

    // The name of the operation without its Async suffix, and the time since it started.
    private void Observe(string operation, long started, bool failed)
    {
        if (_options.ObserveOperation is not { } observe) return;
        var name = operation.EndsWith("Async", StringComparison.Ordinal) ? operation[..^"Async".Length] : operation;
        observe(name, Stopwatch.GetElapsedTime(started), failed);
    }

    private async Task WriteLoopAsync()
    {
        var connection = _writer!;
        await foreach (var item in _writes.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            item.Run(connection);
        }
    }

    private void Migrate(SqliteConnection connection)
    {
        Execute(connection, null, "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)");
        long applied;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations";
            applied = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var migrations = LoadMigrations();
        if (migrations.Count > 0 && applied > migrations[^1].Version)
        {
            throw new InvalidOperationException(
                $"The database '{DatabasePath}' is at version {applied} of the schema, newer than this server ({migrations[^1].Version}): run the newer server or restore the backup taken before its upgrade.");
        }

        foreach (var migration in migrations)
        {
            if (migration.Version <= applied) continue;
            // Each migration in its transaction: a failed one leaves the previous version in place.
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, migration.Sql);
            Execute(connection, transaction, "INSERT INTO schema_migrations (version, name, applied_at) VALUES ($version, $name, $at)",
                ("$version", migration.Version), ("$name", migration.Name), ("$at", ToTime(_time.GetUtcNow())));
            transaction.Commit();
        }
    }

    private static List<(int Version, string Name, string Sql)> LoadMigrations()
    {
        var assembly = typeof(SqliteCoordinatorStore).Assembly;
        var migrations = new List<(int Version, string Name, string Sql)>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(MigrationResourcePrefix, StringComparison.Ordinal) || !resource.EndsWith(".sql", StringComparison.Ordinal)) continue;
            var name = resource[MigrationResourcePrefix.Length..^".sql".Length];
            var separator = name.IndexOf('_', StringComparison.Ordinal);
            if (separator <= 0 || !int.TryParse(name.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException($"The migration '{resource}' is not named '<number>_<name>.sql'.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            migrations.Add((version, name[(separator + 1)..], reader.ReadToEnd()));
        }

        migrations.Sort(static (left, right) => left.Version.CompareTo(right.Version));
        for (var index = 0; index < migrations.Count; index++)
        {
            if (migrations[index].Version != index + 1) throw new InvalidOperationException($"The migrations are not numbered from 1 without gaps (at {migrations[index].Version}).");
        }

        return migrations;
    }

    private void AppendAudit(SqliteConnection connection, SqliteTransaction transaction, AuditEntry entry)
    {
        var last = QuerySingle(connection, transaction, "SELECT seq, hash FROM audit ORDER BY seq DESC LIMIT 1",
            static reader => (Sequence: reader.GetInt64(0), Hash: reader.GetString(1)));
        var previous = last.Hash ?? string.Empty;
        var sequence = last.Sequence + 1;
        // The chain hashes milliseconds: the stored time is the hashed time.
        var recordedAt = FromTime(ToTime(_time.GetUtcNow()));
        var hash = AuditChain.ComputeHash(previous, sequence, recordedAt, entry);
        Execute(connection, transaction,
            "INSERT INTO audit (seq, recorded_at, actor_person_id, actor_device_id, action, target, result, source_address, request_id, details, previous_hash, hash) " +
            "VALUES ($seq, $at, $person, $device, $action, $target, $result, $source, $request, $details, $previous, $hash)",
            ("$seq", sequence), ("$at", ToTime(recordedAt)), ("$person", entry.ActorPersonId), ("$device", entry.ActorDeviceId), ("$action", entry.Action),
            ("$target", entry.Target), ("$result", entry.Result), ("$source", entry.SourceAddress), ("$request", entry.RequestId), ("$details", entry.Details),
            ("$previous", previous), ("$hash", hash));
    }

    private const string PersonColumns = "SELECT id, organization_id, display_name, role, created_at";
    private const string DeviceColumns =
        "SELECT id, organization_id, name, kind, public_key, fingerprint, owner_person_id, status, enrolled_by_device_id, enrollment_token_id, created_at, approved_at, revoked_at, version";
    private const string DeviceListColumns = "SELECT organization_id, seq, device_id, operation, signer_device_id, payload, signature, hash, recorded_at";
    private const string SessionColumns =
        "SELECT id, organization_id, machine_id, epoch, version, state, created_at, updated_at, provider_type, model, turns, tool_calls, input_tokens, output_tokens, sealed_metadata, received_at";
    private const string AuditColumns =
        "SELECT seq, recorded_at, actor_person_id, actor_device_id, action, target, result, source_address, request_id, details, previous_hash, hash";

    private static OrganizationRecord? ReadOrganization(SqliteConnection connection, SqliteTransaction? transaction)
        => QuerySingle(connection, transaction, "SELECT id, name, created_at FROM organizations ORDER BY created_at LIMIT 1",
            static reader => new OrganizationRecord(reader.GetString(0), reader.GetString(1), FromTime(reader.GetInt64(2))));

    private static PersonRecord ReadPerson(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), FromTime(reader.GetInt64(4)));

    private static DeviceRecord? ReadDevice(SqliteConnection connection, SqliteTransaction? transaction, string deviceId)
        => QuerySingle(connection, transaction, DeviceColumns + " FROM devices WHERE id = $id", ReadDeviceRow, ("$id", deviceId));

    private static DeviceRecord ReadDeviceRow(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            GetNullableString(reader, 6), reader.GetString(7), GetNullableString(reader, 8), GetNullableString(reader, 9), FromTime(reader.GetInt64(10)),
            GetNullableTime(reader, 11), GetNullableTime(reader, 12), reader.GetInt64(13));

    private static DeviceListEntryRecord? ReadLastDeviceListEntry(SqliteConnection connection, SqliteTransaction? transaction, string organizationId)
        => QuerySingle(connection, transaction, DeviceListColumns + " FROM device_list WHERE organization_id = $org ORDER BY seq DESC LIMIT 1", ReadDeviceListEntry,
            ("$org", organizationId));

    private static DeviceListEntryRecord ReadDeviceListEntry(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), FromTime(reader.GetInt64(8)));

    private static SessionRecord ReadSession(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5), FromTime(reader.GetInt64(6)),
            FromTime(reader.GetInt64(7)), GetNullableString(reader, 8), GetNullableString(reader, 9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12),
            reader.GetInt64(13), GetNullableString(reader, 14), FromTime(reader.GetInt64(15)));

    private static AuditRecord ReadAudit(SqliteDataReader reader)
        => new(reader.GetInt64(0), FromTime(reader.GetInt64(1)),
            new AuditEntry(reader.GetString(4), reader.GetString(6), GetNullableString(reader, 2), GetNullableString(reader, 3), GetNullableString(reader, 5),
                GetNullableString(reader, 7), GetNullableString(reader, 8), GetNullableString(reader, 9)),
            reader.GetString(10), reader.GetString(11));

    private static void InsertPerson(SqliteConnection connection, SqliteTransaction transaction, PersonRecord person)
        => Execute(connection, transaction, "INSERT INTO people (id, organization_id, display_name, role, created_at) VALUES ($id, $org, $name, $role, $created)",
            ("$id", person.Id), ("$org", person.OrganizationId), ("$name", person.DisplayName), ("$role", person.Role), ("$created", ToTime(person.CreatedAt)));

    private static void InsertDevice(SqliteConnection connection, SqliteTransaction transaction, DeviceRecord device)
        => Execute(connection, transaction,
            "INSERT INTO devices (id, organization_id, name, kind, public_key, fingerprint, owner_person_id, status, enrolled_by_device_id, enrollment_token_id, " +
            "created_at, approved_at, revoked_at, version) VALUES ($id, $org, $name, $kind, $key, $fingerprint, $owner, $status, $by, $token, $created, $approved, $revoked, $version)",
            ("$id", device.Id), ("$org", device.OrganizationId), ("$name", device.Name), ("$kind", device.Kind), ("$key", device.PublicKey),
            ("$fingerprint", device.Fingerprint), ("$owner", device.OwnerPersonId), ("$status", device.Status), ("$by", device.EnrolledByDeviceId),
            ("$token", device.EnrollmentTokenId), ("$created", ToTime(device.CreatedAt)), ("$approved", ToNullableTime(device.ApprovedAt)),
            ("$revoked", ToNullableTime(device.RevokedAt)), ("$version", device.Version));

    private static void InsertDeviceListEntry(SqliteConnection connection, SqliteTransaction transaction, DeviceListEntryRecord entry)
        => Execute(connection, transaction,
            "INSERT INTO device_list (organization_id, seq, device_id, operation, signer_device_id, payload, signature, hash, recorded_at) " +
            "VALUES ($org, $seq, $device, $op, $signer, $payload, $sig, $hash, $at)",
            ("$org", entry.OrganizationId), ("$seq", entry.Sequence), ("$device", entry.DeviceId), ("$op", entry.Operation), ("$signer", entry.SignerDeviceId),
            ("$payload", entry.Payload), ("$sig", entry.Signature), ("$hash", entry.Hash), ("$at", ToTime(entry.RecordedAt)));

    private static long ReadCursor(SqliteConnection connection, SqliteTransaction? transaction, string deviceId, string stream)
    {
        using var command = Create(connection, transaction, "SELECT seq FROM publication_cursors WHERE device_id = $device AND stream = $stream",
            ("$device", deviceId), ("$stream", stream));
        return command.ExecuteScalar() is long value ? value : 0;
    }

    private static void WriteCursor(SqliteConnection connection, SqliteTransaction transaction, string deviceId, string stream, long sequence)
        => Execute(connection, transaction,
            "INSERT INTO publication_cursors (device_id, stream, seq) VALUES ($device, $stream, $seq) ON CONFLICT (device_id, stream) DO UPDATE SET seq = excluded.seq",
            ("$device", deviceId), ("$stream", stream), ("$seq", sequence));

    private static long CountWhere(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string value)
    {
        using var command = Create(connection, transaction, $"SELECT COUNT(*) FROM {table} WHERE {column} = $value", ("$value", value));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static T? QuerySingle<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? read(reader) : default;
    }

    private static IReadOnlyList<T> QueryList<T>(SqliteConnection connection, SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Create(connection, transaction, sql, parameters);
        using var reader = command.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(read(reader));
        return list;
    }

    private static SqliteCommand Create(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableTime(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : FromTime(reader.GetInt64(ordinal));

    private static long ToTime(DateTimeOffset time) => time.ToUnixTimeMilliseconds();

    private static object? ToNullableTime(DateTimeOffset? time) => time?.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromTime(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    // A wrap is never replaced: a device keeps the copy it has. Gives 1 when added.
    private static int InsertWrap(SqliteConnection connection, SqliteTransaction transaction, MetadataKeyWrapRecord wrap)
        => Execute(connection, transaction,
            "INSERT INTO metadata_key_wraps (key_id, device_id, wrapped, wrapped_by_device_id, created_at) VALUES ($key, $device, $wrapped, $by, $created) " +
            "ON CONFLICT (key_id, device_id) DO NOTHING",
            ("$key", wrap.KeyId), ("$device", wrap.DeviceId), ("$wrapped", wrap.Wrapped), ("$by", wrap.WrappedByDeviceId), ("$created", ToTime(wrap.CreatedAt)));

    // {"<name>":[...]}: ids only, never content.
    private static string IdsDetails(string name, IReadOnlyList<string> ids)
    {
        using var buffer = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray(name);
            foreach (var id in ids) writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private abstract class WriteItem
    {
        public abstract void Run(SqliteConnection connection);
    }

    private sealed class WriteItem<T>(Func<SqliteConnection, SqliteTransaction, T> write, CancellationToken cancellationToken) : WriteItem
    {
        private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public override void Run(SqliteConnection connection)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                using var transaction = connection.BeginTransaction();
                var result = write(connection, transaction);
                transaction.Commit();
                _completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                // The transaction rolled back with its disposal: nothing of a failed write is kept.
                _completion.TrySetException(exception);
            }
        }
    }
}
