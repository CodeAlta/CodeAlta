using CodeAlta.Fleet.Update;
using CodeAlta.Server.Hosting;
using CodeAlta.Server.Storage;
using CodeAlta.Server.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Server.Tests;

/// <summary>
/// Backups and the secrets the server uses itself (<c>doc/fleet/server.md</c>, restore and upgrades): a backup taken since
/// the server seals them holds none in clear; one an older version took may, and the server says so at its start and
/// deletes them on its owner's word, never the one a version on trial rolls back to.
/// </summary>
[TestClass]
public sealed class ServerBackupTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ABackupOfAServerThatSealsHoldsNoSecretInClear()
    {
        using var root = new TempDirectory();
        var paths = new ServerPaths(root.Path);
        await using (var store = new SqliteCoordinatorStore(new SqliteCoordinatorStoreOptions
                     {
                         DatabasePath = paths.DatabasePath, TimeProvider = new TestClock(Start), Sealer = ServerSecretSealer.Create(paths.KeysRoot),
                     }))
        {
            await store.InitializeAsync();
            await SetUpAsync(store);
            Assert.IsTrue(await store.SetSignInSettingsAsync(new SignInSettingsRecord("org", "{}", "client-secret-in-backup", 1, Start), Audit()));
            var running = Path.Combine(paths.BackupsRoot, "running.sqlite3");
            await store.BackupAsync(running);
            Assert.IsFalse(ServerFixture.DatabaseFilesHold(running, "client-secret-in-backup"), "the copy a running server takes");
        }

        var stopped = ServerUpdates.BackupStopped(paths, UpdateVersion.Parse("4.0.1"))!;
        Assert.IsFalse(ServerFixture.DatabaseFilesHold(stopped, "client-secret-in-backup"), "the copy taken before a switch");
        Assert.AreEqual(0, ServerBackups.FindUnsealed(paths).Count);
    }

    [TestMethod]
    public async Task TheServerWarnsOfBackupsAnOlderVersionTook_AndDeletesThemOnItsOwnersWord_ButNotTheOneOfATrial()
    {
        var token = TestContext.CancellationToken;
        using var root = new TempDirectory();
        var paths = new ServerPaths(root.Path);
        Directory.CreateDirectory(paths.BackupsRoot);
        var older = Path.Combine(paths.BackupsRoot, "before-4.0.0-3.9.0-20261101-120000.sqlite3");
        var trial = Path.Combine(paths.BackupsRoot, "before-4.0.1-4.0.0-20261102-120000.sqlite3");
        var empty = Path.Combine(paths.BackupsRoot, "before-3.9.0-3.8.0-20261001-120000.sqlite3");
        await OlderBackupAsync(older, "old-client-secret", "{\"x-api-key\":\"old-header\"}");
        await OlderBackupAsync(trial, "trial-client-secret", null);
        await OlderBackupAsync(empty, null, null);

        CollectionAssert.AreEquivalent(new[] { older, trial }, ServerBackups.FindUnsealed(paths).ToArray(), "a backup without a secret is not flagged");

        // The start says it, once, with what to do.
        var output = new StringWriter();
        var error = new StringWriter();
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ServerApplication(TextWriter.Synchronized(output), TextWriter.Synchronized(error))
        {
            Started = (_, stop) => started.TrySetResult(stop),
            ConfigureServer = static options => new CoordinatorServerOptions { DataRoot = options.DataRoot, Urls = options.Urls, ConfigureLogging = static _ => { } },
        };
        var running = server.RunAsync(["run", "--data-root", root.Path, "--urls", "http://127.0.0.1:0"]);
        var stop = await started.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
        stop();
        await running.WaitAsync(TimeSpan.FromSeconds(30), token);
        var said = error.ToString();
        Assert.AreEqual(1, said.Split("2 backups").Length - 1, said);
        StringAssert.Contains(said, "altaserver backups --delete-unsealed");

        // The version on trial rolls back to its backup: it stays until the trial ends.
        Directory.CreateDirectory(paths.VersionsRoot);
        File.WriteAllText(Path.Combine(paths.VersionsRoot, "state.json"),
            $"{{\"trial\":{{\"version\":\"4.0.1\",\"selectedAt\":\"2026-11-04T12:00:00+00:00\",\"starts\":1,\"backup\":\"{System.Text.Json.JsonEncodedText.Encode(trial)}\"}}}}");
        Assert.AreEqual(trial, new UpdateVersionStore(paths.VersionsRoot).Read().Trial?.Backup);

        // altaserver backups lists them; --delete-unsealed deletes those it may.
        var tool = new StringWriter();
        var toolError = new StringWriter();
        var app = new ServerApplication(TextWriter.Synchronized(tool), TextWriter.Synchronized(toolError));
        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["backups", "--data-root", root.Path]), toolError.ToString());
        StringAssert.Contains(tool.ToString(), Path.GetFileName(older) + ": secrets in clear");
        StringAssert.Contains(tool.ToString(), Path.GetFileName(trial) + ": secrets in clear, kept while 4.0.1 is on trial");
        StringAssert.Contains(tool.ToString(), Path.GetFileName(empty) + ": no secret");
        Assert.AreEqual(ServerExitCodes.Success, await app.RunAsync(["backups", "--delete-unsealed", "--data-root", root.Path]), toolError.ToString());
        Assert.IsFalse(File.Exists(older));
        Assert.IsTrue(File.Exists(trial), "the rollback of the trial needs it");
        Assert.IsTrue(File.Exists(empty));
    }

    [TestMethod]
    public async Task NoBackupIsDeleted_WhileTheStateOfTheUpdatesDoesNotRead()
    {
        using var root = new TempDirectory();
        var paths = new ServerPaths(root.Path);
        Directory.CreateDirectory(paths.BackupsRoot);
        var trial = Path.Combine(paths.BackupsRoot, "before-4.0.1-4.0.0-20261102-120000.sqlite3");
        await OlderBackupAsync(trial, "trial-client-secret", null);

        // A state that is there but does not read may name a trial that rolls back to this copy: nothing is deleted.
        Directory.CreateDirectory(paths.VersionsRoot);
        File.WriteAllText(Path.Combine(paths.VersionsRoot, "state.json"), "{\"trial\":{\"version\":");
        var tool = new StringWriter();
        var toolError = new StringWriter();
        var app = new ServerApplication(TextWriter.Synchronized(tool), TextWriter.Synchronized(toolError));
        Assert.AreEqual(ServerExitCodes.Failure, await app.RunAsync(["backups", "--delete-unsealed", "--data-root", root.Path]), tool.ToString());
        Assert.IsTrue(File.Exists(trial), "the rollback of a trial may need it");
        StringAssert.Contains(toolError.ToString(), "state.json");
        Assert.ThrowsExactly<UpdateException>(() => ServerBackups.DeleteUnsealed(paths));
    }

    // A database as a version before the sealing kept it: the columns in clear, holding what it was given.
    private static async Task OlderBackupAsync(string path, string? clientSecret, string? headers)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE sign_in_settings (organization_id TEXT, settings TEXT, company_client_secret TEXT NULL, version INTEGER, updated_at INTEGER);" +
            "CREATE TABLE telemetry_settings (organization_id TEXT, server_headers TEXT NULL);" +
            "INSERT INTO sign_in_settings VALUES ('org', '{}', $secret, 1, 0); INSERT INTO telemetry_settings VALUES ('org', $headers);";
        command.Parameters.AddWithValue("$secret", (object?)clientSecret ?? DBNull.Value);
        command.Parameters.AddWithValue("$headers", (object?)headers ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetUpAsync(ICoordinatorStore store)
    {
        var organization = new OrganizationRecord("org", "Home", Start);
        var person = new PersonRecord("alex", organization.Id, "Alex", PersonRoles.Owner, Start);
        var device = new DeviceRecord("desktop", organization.Id, "Desktop", CodeAlta.Fleet.Protocol.FleetDeviceKinds.Controller, "key", "fp", person.Id, DeviceStatuses.Approved,
            "desktop", null, Start, Start, null, 1);
        var entry = new DeviceListEntryRecord(organization.Id, 1, device.Id, CodeAlta.Fleet.Protocol.FleetDeviceListOperations.Add, device.Id, "{}", "sig", "hash-1", Start);
        Assert.IsTrue(await store.CreateOrganizationAsync(organization, person, device, entry, Audit()));
    }

    private static AuditEntry Audit() => new("test", AuditEntry.Success, "alex", "desktop", null, "127.0.0.1", Guid.NewGuid().ToString("N"));
}
