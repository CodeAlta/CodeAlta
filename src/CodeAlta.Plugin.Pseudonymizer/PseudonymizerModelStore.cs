using System.Globalization;
using CodeAlta.NativeEngines;
using CodeAlta.Pseudonymization;

namespace CodeAlta.Plugin.Pseudonymizer;

/// <summary>
/// The languages a secure project chooses for its documents: each one finds names with a model of its own, which is
/// downloaded for the projects that use it only. <see cref="Identifiers"/> finds identifiers alone and needs no model.
/// </summary>
public static class PseudonymizerLanguages
{
    /// <summary>French documents: CamemBERT-NER.</summary>
    public const string French = "french";

    /// <summary>English documents: BERT-base-NER.</summary>
    public const string English = "english";

    /// <summary>Documents in several languages: BERT multilingual NER, ten languages among which French and English.</summary>
    public const string Multilingual = "multilingual";

    /// <summary>Identifiers only (emails, phone numbers, IBAN, NIR...): no model, nothing to download, names are not found.</summary>
    public const string Identifiers = "identifiers";

    /// <summary>A model of the user, given by its folder (the setting <c>custom_model</c>).</summary>
    public const string Custom = "custom";

    /// <summary>The language of a project that names none: it covers French and English.</summary>
    public const string Default = Multilingual;

    /// <summary>Gets the languages the window offers, in their order.</summary>
    public static IReadOnlyList<string> Offered { get; } = [French, English, Multilingual, Identifiers];

    /// <summary>Gets every language the setting takes.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Offered, Custom];

    /// <summary>
    /// Creates the recognizers of the identifiers a language reads: those of France for French, of the United Kingdom
    /// and of the United States for English, and both for any other choice; with those every country shares.
    /// </summary>
    /// <param name="language">The language of the documents.</param>
    /// <returns>A new set of recognizers.</returns>
    internal static List<PiiRecognizer> IdentifierRecognizers(string? language) => language switch
    {
        French => PiiRecognizerSets.French(),
        English => PiiRecognizerSets.English(),
        _ => PiiRecognizerSets.All(),
    };

    /// <summary>Gets the size of the model of a language in megabytes, as the window and the messages say it; 0 without one.</summary>
    /// <param name="language">The language.</param>
    /// <returns>The megabytes, rounded.</returns>
    public static int MegabytesOf(string? language) => Megabytes(PresetOf(language)?.Size ?? 0);

    /// <summary>Converts bytes to megabytes, rounded, as the window and the messages say a size.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The megabytes.</returns>
    public static int Megabytes(long bytes) => (int)Math.Round(bytes / (1024d * 1024d));

    // The model of the catalog a language runs; null for none or for the model of the user.
    internal static NerModelPreset? PresetOf(string? language) => language switch
    {
        French => NerModelCatalog.Find(NerModelCatalog.CamembertId),
        English => NerModelCatalog.Find(NerModelCatalog.EnglishId),
        Multilingual => NerModelCatalog.Find(NerModelCatalog.MultilingualId),
        _ => null,
    };
}

/// <summary>A language of the documents of a secure project, as the window shows it.</summary>
/// <param name="Id">One of <see cref="PseudonymizerLanguages.Offered"/>.</param>
/// <param name="Model">The name of the model that finds names; null for identifiers only.</param>
/// <param name="Size">The bytes of its download; 0 without a model.</param>
/// <param name="Installed">Whether its model is there: downloaded whole, or no model needed.</param>
public sealed record PseudonymizerLanguage(string Id, string? Model, long Size, bool Installed)
{
    /// <summary>Gets the size of its download in megabytes, as the window says it.</summary>
    public int SizeMegabytes => PseudonymizerLanguages.Megabytes(Size);
}

/// <summary>The states of the download of a model.</summary>
public enum PseudonymizerDownloadState
{
    /// <summary>Nothing was downloaded since the application started.</summary>
    Idle,

    /// <summary>A model is being downloaded.</summary>
    Downloading,

    /// <summary>The last download ended with the model installed.</summary>
    Done,

    /// <summary>The last download failed.</summary>
    Failed,
}

/// <summary>Where the download of a model is.</summary>
/// <param name="State">Its state.</param>
/// <param name="Language">The language whose model is or was downloaded; null when idle.</param>
/// <param name="Received">The bytes received.</param>
/// <param name="Total">The bytes of the whole download: those of the model.</param>
/// <param name="Error">Why it failed, for <c>failed</c>.</param>
public sealed record PseudonymizerDownload(PseudonymizerDownloadState State, string? Language, long Received, long Total, string? Error)
{
    /// <summary>Gets the state of a store that downloads nothing.</summary>
    public static PseudonymizerDownload Idle { get; } = new(PseudonymizerDownloadState.Idle, null, 0, 0, null);

    /// <summary>Gets the percent received, below 100 while it runs.</summary>
    public int Percent => Total > 0 ? (int)Math.Min(99, Received * 100 / Total) : 0;
}

/// <summary>
/// The models that find names, as the application keeps them: which language is ready, the download of one at a time
/// (the others wait their turn), the download of the ONNX Runtime they run on, the deletion of a model, and the
/// recognizers that run them. The plugin and the window share one store, so both see the same downloads.
/// </summary>
public sealed class PseudonymizerModelStore : IDisposable
{
    private readonly NerModels _models;
    private readonly Lock _gate = new();
    private readonly Queue<string> _waiting = new();
    private readonly CancellationTokenSource _stopping = new();
    private PseudonymizerDownload _download = PseudonymizerDownload.Idle;
    private bool _running;

    /// <summary>Creates the store over what keeps and runs the models.</summary>
    /// <param name="host">Where the models are kept and run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    public PseudonymizerModelStore(IPseudonymizerModelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _models = new NerModels(host);
    }

    /// <summary>Raised when a download starts, moves by a percent, ends, or a model is deleted; on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the download that runs or that last ended.</summary>
    public PseudonymizerDownload Download
    {
        get
        {
            lock (_gate) return _download;
        }
    }

    /// <summary>Gets the languages the window offers, with their model and whether it is downloaded.</summary>
    public IReadOnlyList<PseudonymizerLanguage> Languages
        => [.. PseudonymizerLanguages.Offered.Select(language => PseudonymizerLanguages.PresetOf(language) is { } preset
            ? new PseudonymizerLanguage(language, preset.Name, preset.Size, _models.IsInstalled(preset))
            : new PseudonymizerLanguage(language, null, 0, true))];

    /// <summary>Gets whether the model of a language is there: it is downloaded, or the language needs none.</summary>
    /// <param name="language">The language.</param>
    /// <returns>Whether it is ready; true for identifiers only and for the model of the user. The ONNX Runtime that runs it may still be missing.</returns>
    public bool IsReady(string? language) => PseudonymizerLanguages.PresetOf(language) is not { } preset || _models.IsInstalled(preset);

    /// <summary>
    /// Gets whether models are there without the ONNX Runtime that runs them: a version of the application that runs on
    /// another ONNX Runtime finds the models an earlier one downloaded, and not their runtime.
    /// </summary>
    public bool NeedsRuntime => !_models.HasRuntime && NerModelCatalog.Presets.Any(_models.IsInstalled);

    /// <summary>Downloads the ONNX Runtime the models run on, a few megabytes; does nothing when it is there.</summary>
    /// <returns>Null once the runtime is there; otherwise why it could not be downloaded.</returns>
    public async Task<string?> InstallRuntimeAsync()
    {
        try
        {
            await _models.InstallRuntimeAsync(_stopping.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsDownloadFailure(exception))
        {
            return FailureOf(exception);
        }
    }

    /// <summary>Downloads the model of a language, after the one that runs; does nothing when it is there or on its way.</summary>
    /// <param name="language">The language.</param>
    /// <returns>Whether a download of it runs or waits now.</returns>
    public bool Install(string? language)
    {
        if (PseudonymizerLanguages.PresetOf(language) is not { } preset || _models.IsInstalled(preset)) return false;
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested) return false;
            if (IsBusy(language!)) return true;
            if (_running)
            {
                _waiting.Enqueue(language!);
                return true;
            }

            // It downloads from now on, not once its task runs: who asked reads the state right after this.
            _running = true;
            _download = Downloading(language!);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() => DownloadAsync(language!));
        return true;
    }

    /// <summary>Deletes the model of a language from this computer; a project that uses it downloads it again.</summary>
    /// <param name="language">The language.</param>
    /// <returns>Whether it was deleted; false when it is not there or is being downloaded.</returns>
    /// <exception cref="IOException">A file of the model cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">A file of the model cannot be deleted.</exception>
    public bool Delete(string? language)
    {
        if (PseudonymizerLanguages.PresetOf(language) is not { } preset) return false;
        lock (_gate)
        {
            if (IsBusy(language!)) return false;
        }

        var directory = _models.DirectoryOf(preset);
        if (!Directory.Exists(directory)) return false;
        _models.Unload(directory);
        Directory.Delete(directory, recursive: true);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Gets the recognizer that finds names in a language; null for identifiers only. A model that is not downloaded, or
    /// does not load, fails: the request sends nothing.
    /// </summary>
    /// <param name="language">The language.</param>
    /// <param name="customFolder">The folder of the model of the user, for <see cref="PseudonymizerLanguages.Custom"/>.</param>
    /// <returns>The recognizer, or null.</returns>
    /// <exception cref="InvalidOperationException">The model is not downloaded, or cannot be run.</exception>
    internal PiiRecognizer? Recognizer(string language, string? customFolder) => language switch
    {
        PseudonymizerLanguages.Identifiers => null,
        PseudonymizerLanguages.Custom => _models.Recognizer(NerModelCatalog.CustomId, customFolder),
        _ => _models.Recognizer((PseudonymizerLanguages.PresetOf(language) ?? throw new InvalidOperationException($"'{language}' is not a language of the Pseudonymizer.")).Id, null),
    };

    // Whether the model of a language is being downloaded or waits its turn; under the gate.
    private bool IsBusy(string language)
        => _download is { State: PseudonymizerDownloadState.Downloading } running && running.Language == language || _waiting.Contains(language);

    private static PseudonymizerDownload Downloading(string language)
        => new(PseudonymizerDownloadState.Downloading, language, 0, PseudonymizerLanguages.PresetOf(language)!.Size, null);

    // Downloads a language, whose download the state already says, then the waiting ones one after the other, and stops.
    private async Task DownloadAsync(string language)
    {
        while (true)
        {
            string? failure;
            try
            {
                var preset = PseudonymizerLanguages.PresetOf(language)!;
                _models.RemoveDamaged(preset);
                // The runtime first: a model is not downloaded for a computer that cannot run it.
                failure = await InstallRuntimeAsync().ConfigureAwait(false);
                if (failure is null) await _models.InstallAsync(preset, new Progress(this), _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsDownloadFailure(exception))
            {
                failure = FailureOf(exception);
            }

            var ended = new PseudonymizerDownload(failure is null ? PseudonymizerDownloadState.Done : PseudonymizerDownloadState.Failed, language, 0, 0, failure);

            bool last;
            lock (_gate)
            {
                last = !_waiting.TryDequeue(out var next) || _stopping.IsCancellationRequested;
                if (last) _running = false;
                else language = next!;
                _download = last ? ended : Downloading(language);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            if (last) return;
        }
    }

    private static bool IsDownloadFailure(Exception exception)
        => exception is PinnedDownloadException or IOException or UnauthorizedAccessException or InvalidDataException or PlatformNotSupportedException or OperationCanceledException or HttpRequestException;

    // Why a download failed, as the window and the messages say it: the code of a download, or the message of the system.
    private static string FailureOf(Exception exception) => exception is PinnedDownloadException download ? download.Code : exception.Message;

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate) _waiting.Clear();
        _stopping.Cancel();
        _models.Dispose();
    }

    // Counts the bytes received; the store says it changed at each percent.
    private sealed class Progress(PseudonymizerModelStore owner) : IPinnedDownloadProgress
    {
        private int _percent = -1;

        public void Received(long bytes)
        {
            PseudonymizerDownload state;
            lock (owner._gate) owner._download = state = owner._download with { Received = owner._download.Received + bytes };

            if (Interlocked.Exchange(ref _percent, state.Percent) != state.Percent) owner.Changed?.Invoke(owner, EventArgs.Empty);
        }

        public void Checking(PinnedFile file)
        {
        }
    }
}
