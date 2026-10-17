using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Speech;
using CodeAlta.Desktop.Speech;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Speech to prompt: its settings, the speech models in the cache and their download, and dictation. The page
/// records the microphone and sends the audio here; a local model, in a worker process, turns it into text.
/// Nothing is downloaded before <c>install</c> is called.
/// </summary>
[NeoRpcService("speech", Version = 1)]
internal sealed class SpeechService
{
    /// <summary>How long the worker keeps a model loaded without a dictation.</summary>
    internal static readonly TimeSpan IdleTime = TimeSpan.FromMinutes(5);

    // A chunk is a tenth of a second of 16 kHz 16-bit audio: 3 200 bytes. Ten seconds in one call is not a chunk.
    private const int MaximumChunkCharacters = 430_000;

    private readonly CodeAltaConfigStore? _config;
    private readonly SpeechInstaller? _installer;
    private readonly SpeechWorkerClient? _worker;
    private readonly string? _epoch;
    private readonly SemaphoreSlim _settingsGate = new(1, 1);

    /// <summary>Creates the service of a window that has no speech to prompt.</summary>
    internal SpeechService() { }

    /// <summary>Creates the service on the network and on worker processes of this executable.</summary>
    internal SpeechService(CatalogOptions options, string epoch)
        : this(options, epoch, null, SpeechWorkerProcess.Start, TimeProvider.System, SpeechModelCatalog.CurrentRuntimeIdentifier()) { }

    /// <summary>Creates the service on given parts.</summary>
    /// <param name="options">The catalog, for the configuration and the cache folder.</param>
    /// <param name="epoch">The epoch of the host.</param>
    /// <param name="handler">The handler of the downloads, or null for the network.</param>
    /// <param name="start">Starts a speech worker in a folder.</param>
    /// <param name="time">The clock of the idle time of the worker.</param>
    /// <param name="runtimeIdentifier">The platform the engine is installed for, or null when it has none.</param>
    internal SpeechService(CatalogOptions options, string epoch, HttpMessageHandler? handler, Func<string, ISpeechWorkerProcess> start,
        TimeProvider time, string? runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _config = new(options);
        _epoch = epoch;
        _worker = new(start, time, IdleTime);
        _installer = new(options.SpeechRoot, options.RuntimeRoot, handler, SpeechModelCatalog.FindEngine(runtimeIdentifier), SpeechModelCatalog.VoiceDetector)
        {
            // A model the engine refuses is known when its download ends, not at the first dictation.
            Validate = (model, cancellationToken) => _worker.ProbeAsync(_installer!.LoadFor(model), cancellationToken),
        };
    }

    /// <summary>Gets the installer, for tests.</summary>
    internal SpeechInstaller? Installer => _installer;

    /// <summary>
    /// Gets whether the page may use the microphone now: speech to prompt exists here and is turned on. Read
    /// each time, so that turning the feature off closes the microphone at once.
    /// </summary>
    internal bool IsMicrophoneAllowed()
    {
        if (_config is null || _installer is not { IsSupported: true }) return false;
        try { return _config.LoadGlobalSpeechSettings().Enabled; }
        catch (Exception exception) when (IsConfigurationFailure(exception)) { return false; }
    }

    /// <summary>Reads the settings and the models: the presets, and what is in the cache.</summary>
    [NeoRpcMethod("read")]
    public async Task<SpeechListingResponse> ReadAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return SpeechListingResponse.Refused(status);
        return await Task.Run(() => Listing("ok", null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves the settings. A model that is no longer the chosen one leaves the memory.</summary>
    [NeoRpcMethod("saveSettings")]
    public async Task<SpeechListingResponse> SaveSettingsAsync(SpeechSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return SpeechListingResponse.Refused(status);
        var model = request.Model?.Trim() ?? "";
        if (SpeechSettings.GetModelError(model) is not null || SpeechModelSource.ModelIdOf(model) is not { } id)
            return await Task.Run(() => Listing("invalid_request", "The model is not a preset or a Hugging Face repository address."), cancellationToken).ConfigureAwait(false);
        if (request.Send is not null && SpeechSettings.GetSendError(request.Send) is not null)
            return await Task.Run(() => Listing("invalid_request", "The send mode is manual or on_stop."), cancellationToken).ConfigureAwait(false);
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await Task.Run(() =>
            {
                try
                {
                    // A request that does not name the send mode keeps the one that is saved.
                    _config!.SaveGlobalSpeechSettings(new(request.Enabled, model, request.Send ?? ReadSettings()?.Send ?? SpeechSettings.ManualSend));
                    return true;
                }
                catch (Exception exception) when (IsConfigurationFailure(exception))
                {
                    return false;
                }
            }, cancellationToken).ConfigureAwait(false);
            if (!saved) return Listing("write_failed", "The speech settings could not be saved.");
            if (!request.Enabled || _installer!.Find(id)?.Directory != _worker!.ModelDirectory) await _worker!.UnloadAsync().ConfigureAwait(false);
            return await Task.Run(() => Listing("ok", null), cancellationToken).ConfigureAwait(false);
        }
        finally { _settingsGate.Release(); }
    }

    /// <summary>Starts the download of a model, the chosen one when none is named. It goes on when the page looks away.</summary>
    [NeoRpcMethod("install")]
    public async Task<SpeechStatusResponse> InstallAsync(SpeechModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status);
        return await Task.Run(() =>
        {
            var model = request.Model ?? ReadSettings()?.Model;
            if (model is null || SpeechModelSource.ModelIdOf(model) is null) return new SpeechStatusResponse("invalid_request");
            return new SpeechStatusResponse(!_installer!.IsSupported ? "unsupported" : _installer.TryStart(model) ? "ok" : "busy");
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the running download. What it wrote stays, and the next one goes on from it.</summary>
    [NeoRpcMethod("cancelInstall")]
    public SpeechStatusResponse CancelInstall(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status);
        _installer!.Cancel();
        return new("ok");
    }

    /// <summary>Removes a model from the cache.</summary>
    [NeoRpcMethod("delete")]
    public async Task<SpeechListingResponse> DeleteAsync(SpeechModelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return SpeechListingResponse.Refused(status);
        if (_installer!.Find(request.Model) is not { } installed) return await Task.Run(() => Listing("ok", null), cancellationToken).ConfigureAwait(false);
        // The worker holds the files of the model it loaded.
        if (installed.Directory == _worker!.ModelDirectory) await _worker.UnloadAsync().ConfigureAwait(false);
        return await Task.Run(() => _installer.Delete(installed.Manifest.Id) ? Listing("ok", null) : Listing("write_failed", "The model could not be removed."), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replays and follows the download. Closing the stream never stops it.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<SpeechInstallState> Watch(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.SpeechInstallState);
    }

    internal async IAsyncEnumerable<SpeechInstallState> WatchAsync(SpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null) yield break;
        // Only the latest state matters: the bytes received so far, or how it ended.
        var snapshots = Channel.CreateBounded<SpeechInstallSnapshot>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        using var subscription = _installer!.Subscribe(snapshot => snapshots.Writer.TryWrite(snapshot));
        await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return new(snapshot.State, snapshot.ModelId, snapshot.ModelName, snapshot.ReceivedBytes, snapshot.TotalBytes, snapshot.Error);
    }

    /// <summary>Starts a dictation with the chosen model, when it is in the cache.</summary>
    [NeoRpcMethod("start")]
    public async Task<SpeechStartResponse> StartAsync(SpeechRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status, null);
        return await Task.Run(() =>
        {
            if (!_installer!.IsSupported) return new SpeechStartResponse("unsupported", null);
            if (ReadSettings() is not { } settings) return new("read_failed", null);
            if (!settings.Enabled) return new("disabled", null);
            var id = SpeechModelSource.ModelIdOf(settings.Model);
            if (_installer.Current is { IsRunning: true } running && running.ModelId == id) return new("installing", null);
            if (!_installer.IsEngineInstalled || _installer.Find(id) is not { } installed) return new("model_missing", null);
            return new SpeechStartResponse("ok", _worker!.Start(_installer.LoadFor(installed)).Id);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gives the dictation its next chunk of audio: 16 kHz mono 16-bit samples in Base64, numbered from zero.</summary>
    [NeoRpcMethod("append")]
    public SpeechStatusResponse Append(SpeechAudioRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status);
        if (request.Id is null || request.Base64 is not { Length: > 0 and <= MaximumChunkCharacters } text || request.Sequence < 0) return new("invalid_request");
        var chunk = new byte[text.Length / 4 * 3];
        if (!Convert.TryFromBase64String(text, chunk, out var length)) return new("invalid_request");
        return new(_worker!.Append(request.Id, request.Sequence, chunk.AsSpan(0, length).ToArray()) ? "ok" : "unknown");
    }

    /// <summary>
    /// Follows what a dictation reads as. Each item is the whole text so far, so an item the page did not get
    /// loses nothing; the stream ends with the dictation.
    /// </summary>
    [NeoRpcMethod("listen")]
    public NeoRpcChannel<SpeechTranscript> Listen(SpeechDictationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(ListenAsync(request, cancellationToken), DesktopJsonContext.Default.SpeechTranscript);
    }

    internal async IAsyncEnumerable<SpeechTranscript> ListenAsync(SpeechDictationRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request.ExpectedEpoch) is not null || _worker!.Find(request.Id) is not { } dictation) yield break;
        var snapshots = Channel.CreateBounded<SpeechTranscriptSnapshot>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        using var subscription = dictation.Subscribe(snapshot =>
        {
            snapshots.Writer.TryWrite(snapshot);
            if (snapshot.IsOver) snapshots.Writer.TryComplete();
        });
        await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return new(snapshot.State, snapshot.FinalText, snapshot.PartialText, snapshot.Error);
    }

    /// <summary>Ends a dictation and gives its whole text; canceled, what was still being spoken is dropped.</summary>
    [NeoRpcMethod("stop")]
    public async Task<SpeechStopResponse> StopAsync(SpeechStopRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } status) return new(status, "", null);
        if (request.Id is null || await _worker!.StopAsync(request.Id, request.Cancel, cancellationToken).ConfigureAwait(false) is not { } last) return new("unknown", "", null);
        return new(last.State == "failed" ? "failed" : "ok", last.FinalText, last.Error);
    }

    /// <summary>Stops the download and the worker, and waits for both.</summary>
    internal async Task CloseAsync()
    {
        if (_installer is null) return;
        await _installer.CloseAsync().ConfigureAwait(false);
        await _worker!.CloseAsync().ConfigureAwait(false);
    }

    private SpeechSettings? ReadSettings()
    {
        try { return _config!.LoadGlobalSpeechSettings(); }
        catch (Exception exception) when (IsConfigurationFailure(exception)) { return null; }
    }

    private SpeechListingResponse Listing(string status, string? error)
    {
        var settings = ReadSettings();
        if (settings is null && status == "ok") (status, error) = ("read_failed", "The speech settings could not be read.");
        var installed = _installer!.List();
        var models = new List<SpeechModelInfo>();
        foreach (var preset in SpeechModelCatalog.Presets)
        {
            var found = installed.FirstOrDefault(model => model.Manifest.Id == preset.Id);
            models.Add(new(preset.Id, preset.Id, preset.Name, preset.Author, preset.Languages, preset.License, found?.Bytes ?? preset.Size, found is not null, true));
        }

        foreach (var model in installed.Where(static model => SpeechModelCatalog.FindPreset(model.Manifest.Id) is null))
        {
            var manifest = model.Manifest;
            models.Add(new(manifest.Id, manifest.Source, manifest.Name, manifest.Author, manifest.Languages, manifest.License, model.Bytes, true, false));
        }

        // A custom address that is chosen and not downloaded yet: known by its repository only.
        var chosen = settings?.Model ?? SpeechSettings.DefaultModel;
        var chosenId = SpeechModelSource.ModelIdOf(chosen);
        if (chosenId is not null && models.All(model => model.Id != chosenId) && SpeechModelSource.TryParse(chosen, out var repository))
            models.Add(new(chosenId, chosen, repository.Repository, repository.Repository.Split('/')[0], "", "", 0, false, false));
        return new(status, _installer.IsSupported, settings?.Enabled ?? false, chosen, chosenId, models, _installer.Root, error) { Send = settings?.Send ?? SpeechSettings.ManualSend };
    }

    private string? Refuse(string? epoch) => _config is null ? "unavailable" : epoch == _epoch ? null : "stale_epoch";

    private static bool IsConfigurationFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException;
}

internal sealed record SpeechRequest(string? ExpectedEpoch);
internal sealed record SpeechSaveRequest(string? ExpectedEpoch, bool Enabled, string? Model)
{
    /// <summary>Gets what is done with the prompt when a dictation stops, <c>manual</c> or <c>on_stop</c>; null keeps what is saved.</summary>
    public string? Send { get; init; }
}
/// <param name="ExpectedEpoch">The epoch of the host.</param>
/// <param name="Model">For <c>install</c>, a preset id or an address, or null for the chosen model; for <c>delete</c>, the id of a model.</param>
internal sealed record SpeechModelRequest(string? ExpectedEpoch, string? Model);
internal sealed record SpeechAudioRequest(string? ExpectedEpoch, string? Id, int Sequence, string? Base64);
internal sealed record SpeechDictationRequest(string? ExpectedEpoch, string? Id);
internal sealed record SpeechStopRequest(string? ExpectedEpoch, string? Id, bool Cancel);
internal sealed record SpeechStatusResponse(string Status);
internal sealed record SpeechStartResponse(string Status, string? Id);
internal sealed record SpeechStopResponse(string Status, string Text, string? Error);
internal sealed record SpeechTranscript(string State, string FinalText, string PartialText, string? Error);

/// <param name="Id">The id of the model: the name of its folder in the cache.</param>
/// <param name="Value">What the configuration holds to choose it: a preset id, or the address of a custom model.</param>
/// <param name="Name">The name shown.</param>
/// <param name="Author">Who made the model.</param>
/// <param name="Languages"><c>english</c>, <c>european</c>, <c>multilingual</c> or empty.</param>
/// <param name="License">The license shown, or empty.</param>
/// <param name="Bytes">What it takes on disk, or the size of its download; zero when not known yet.</param>
/// <param name="Installed">Whether it is in the cache.</param>
/// <param name="Preset">Whether it is one of the models CodeAlta proposes.</param>
internal sealed record SpeechModelInfo(string Id, string Value, string Name, string Author, string Languages, string License,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Bytes, bool Installed, bool Preset);

/// <param name="Status"><c>ok</c>, <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>, <c>read_failed</c> or <c>write_failed</c>.</param>
/// <param name="Supported">Whether the engine exists for this platform.</param>
/// <param name="Enabled">Whether speech to prompt is turned on.</param>
/// <param name="Model">The configuration value of the chosen model.</param>
/// <param name="ModelId">The id of the chosen model.</param>
/// <param name="Models">The presets, then the other models in the cache, then a chosen address not downloaded yet.</param>
/// <param name="RootPath">The folder of the cache.</param>
/// <param name="Error">What to tell the user when the status is a failure.</param>
internal sealed record SpeechListingResponse(string Status, bool Supported, bool Enabled, string Model, string? ModelId,
    IReadOnlyList<SpeechModelInfo> Models, string? RootPath, string? Error)
{
    /// <summary>Gets what is done with the prompt when a dictation stops: <c>manual</c> (it stays for the user to send) or <c>on_stop</c> (it is sent).</summary>
    public string Send { get; init; } = SpeechSettings.ManualSend;

    internal static SpeechListingResponse Refused(string status) => new(status, false, false, SpeechSettings.DefaultModel, null, [], null, null);
}

internal sealed record SpeechInstallState(string State, string? ModelId, string? ModelName,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long ReceivedBytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long TotalBytes, string? Error);
