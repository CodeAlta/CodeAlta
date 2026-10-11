using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Storage;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>Owned-host storage settings and metadata measurement. No RPC accepts a path or deletes data.</summary>
[NeoRpcService("storage", Version = 1)]
internal sealed class StorageService
{
    private readonly CodeAltaConfigStore? _config;
    private readonly ProfileStorageScanner? _scanner;
    private readonly string? _root;
    private readonly string? _epoch;
    private readonly IStartupScratchpadCleanup? _scratchpadCleanup;
    private readonly SessionRetentionService? _retention;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    internal StorageService() { }

    internal StorageService(CatalogOptions options, string epoch) : this(options, epoch, null) { }

    internal StorageService(CatalogOptions options, string epoch, IStartupScratchpadCleanup? scratchpadCleanup)
        : this(options, epoch, scratchpadCleanup, null) { }

    internal StorageService(CatalogOptions options, string epoch, IStartupScratchpadCleanup? scratchpadCleanup,
        SessionRetentionService? retention)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _config = new(options);
        _scanner = new(options);
        _root = Path.GetFullPath(options.GlobalRoot);
        _epoch = epoch;
        _scratchpadCleanup = scratchpadCleanup;
        _retention = retention;
    }

    /// <summary>Reads global policy separately from the potentially long filesystem scan.</summary>
    [NeoRpcMethod("readSettings")]
    public async Task<StorageSettingsResponse> ReadSettingsAsync(StorageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status, null, null, null);
        return await Task.Run(ReadSettings, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves choices for the next startup; never triggers a cleanup.</summary>
    [NeoRpcMethod("saveSettings")]
    public async Task<StorageSettingsResponse> SaveSettingsAsync(StorageSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status, null, null, null);
        if (request.Settings is not { } wanted) return new("invalid_request", _root, null, "Storage settings are required.");
        var retention = new SessionRetentionSettings(wanted.RetentionEnabled, wanted.RetentionAmount, wanted.RetentionUnit);
        if (retention.GetValidationError() is { } error || retention.Enabled && !retention.TryGetCutoff(DateTimeOffset.UtcNow, out _, out error))
            return new("invalid_request", _root, null, error);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _config!.SaveGlobalStorageSettings(new(wanted.ClearScratchpadOnStartup, retention));
                    return ReadSettings();
                }
                catch (Exception exception) when (IsConfigurationFailure(exception))
                {
                    return new StorageSettingsResponse("write_failed", _root, null, "The storage configuration could not be saved.");
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _settingsGate.Release(); }
    }

    /// <summary>Streams bounded metadata progress and a final measurement; closing it stops only this scan.</summary>
    [NeoRpcMethod("scan")]
    public NeoRpcChannel<StorageScanResponse> Scan(StorageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(ScanAsync(request, cancellationToken), DesktopJsonContext.Default.StorageScanResponse);
    }

    internal async IAsyncEnumerable<StorageScanResponse> ScanAsync(StorageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is { } status) { yield return new(status, null); yield break; }
        yield return new("running", null);
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var updates = Channel.CreateBounded<StorageScanResponse>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        var worker = ProduceScanAsync(updates.Writer, scanCancellation.Token);
        try
        {
            await foreach (var update in updates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            await scanCancellation.CancelAsync().ConfigureAwait(false);
            await worker.ConfigureAwait(false);
        }
    }

    private async Task ProduceScanAsync(ChannelWriter<StorageScanResponse> updates, CancellationToken cancellationToken)
    {
        try
        {
            var measured = await _scanner!.ScanAsync(new ScanProgress(updates), cancellationToken).ConfigureAwait(false);
            updates.TryWrite(new("ok", new(measured.RootPath, measured.TotalBytes, measured.CompletedAt,
                measured.Categories.Select(category => new StorageCategory(category.Id, category.Bytes, category.FileCount)).ToArray(),
                measured.SkippedLinks, measured.SkippedEntries, measured.IsPartial))
            { ProcessedFiles = measured.ProcessedFiles, ProcessedDirectories = measured.ProcessedDirectories });
            updates.TryComplete();
        }
        catch (Exception exception) { updates.TryComplete(exception); }
    }

    private sealed class ScanProgress(ChannelWriter<StorageScanResponse> updates) : IProgress<ProfileStorageProgress>
    {
        public void Report(ProfileStorageProgress value) => updates.TryWrite(new("running", null)
        { ProcessedFiles = value.ProcessedFiles, ProcessedDirectories = value.ProcessedDirectories });
    }

    /// <summary>Replays and follows the host-owned startup operation; closing the stream never cancels cleanup.</summary>
    [NeoRpcMethod("watchMaintenance")]
    public NeoRpcChannel<StorageMaintenance> WatchMaintenance(StorageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchMaintenanceAsync(request, cancellationToken), DesktopJsonContext.Default.StorageMaintenance);
    }

    internal async IAsyncEnumerable<StorageMaintenance> WatchMaintenanceAsync(StorageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null || _scratchpadCleanup is null) yield break;
        // Only the latest state matters. Subscribe replays atomically, closing the read/subscribe race.
        var snapshots = Channel.CreateBounded<ScratchpadCleanupSnapshot>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        using var subscription = _scratchpadCleanup.Subscribe(snapshot => snapshots.Writer.TryWrite(snapshot));
        await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return new(snapshot.State.ToString().ToLowerInvariant(), snapshot.RemovedFiles,
                snapshot.RemovedDirectories, snapshot.SkippedEntries, snapshot.Reasons, snapshot.StartedAt, snapshot.CompletedAt)
            { ProcessedFiles = snapshot.ProcessedFiles, ProcessedDirectories = snapshot.ProcessedDirectories };
    }

    /// <summary>Replays and follows retention outcomes without starting, retrying or canceling the host pass.</summary>
    [NeoRpcMethod("watchRetention")]
    public NeoRpcChannel<StorageRetentionMaintenance> WatchRetention(StorageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchRetentionAsync(request, cancellationToken), DesktopJsonContext.Default.StorageRetentionMaintenance);
    }

    internal async IAsyncEnumerable<StorageRetentionMaintenance> WatchRetentionAsync(StorageRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null || _retention is null) yield break;
        // Callbacks cannot block the worker. Each window holds only the latest bounded snapshot.
        var snapshots = Channel.CreateBounded<SessionRetentionStatus>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        using var subscription = _retention.Subscribe(snapshot => snapshots.Writer.TryWrite(snapshot));
        await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return new(snapshot.State, snapshot.Examined, snapshot.Deleted, snapshot.Skipped, snapshot.Reason);
    }

    private StorageSettingsResponse ReadSettings()
    {
        try
        {
            var settings = _config!.LoadGlobalStorageSettings();
            return new("ok", _root, new(settings.ClearScratchpadOnStartup, settings.SessionRetention.Enabled,
                settings.SessionRetention.Amount, settings.SessionRetention.Unit), null);
        }
        catch (Exception exception) when (IsConfigurationFailure(exception))
        {
            // Parser/provider exceptions can quote configuration contents. Do not send them to the renderer.
            return new("read_failed", _root, null, "The storage configuration could not be read. Automatic cleanup is skipped until it is valid.");
        }
    }

    private string? Refuse(string? epoch) => _config is null ? "unavailable" : epoch == _epoch ? null : "stale_epoch";
    private static bool IsConfigurationFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException;
}

internal sealed record StorageRequest(string? ExpectedEpoch);
internal sealed record StoragePreferences(bool ClearScratchpadOnStartup, bool RetentionEnabled, int RetentionAmount, string RetentionUnit);
internal sealed record StorageSettingsResponse(string Status, string? RootPath, StoragePreferences? Settings, string? Error);
internal sealed record StorageSaveRequest(string? ExpectedEpoch, StoragePreferences? Settings);
internal sealed record StorageCategory(string Id,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Bytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long FileCount);
internal sealed record StorageMeasurement(string RootPath,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long TotalBytes,
    DateTimeOffset CompletedAt, IReadOnlyList<StorageCategory> Categories,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long SkippedLinks,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long SkippedEntries,
    bool IsPartial);
internal sealed record StorageScanResponse(string Status, StorageMeasurement? Snapshot)
{
    [NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))]
    public long ProcessedFiles { get; init; }
    [NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))]
    public long ProcessedDirectories { get; init; }
}
internal sealed record StorageMaintenance(string State,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long RemovedFiles,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long RemovedDirectories,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long SkippedEntries,
    IReadOnlyList<string> Reasons, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt)
{
    [NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))]
    public long ProcessedFiles { get; init; }
    [NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))]
    public long ProcessedDirectories { get; init; }
}
internal sealed record StorageRetentionMaintenance(string State, int Examined, int Deleted, int Skipped, string? Reason);
