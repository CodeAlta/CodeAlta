using System.Collections.Concurrent;
using System.Net.Http.Headers;
using CodeAlta.NativeEngines;
using CodeAlta.Pseudonymization;
using CodeAlta.Pseudonymization.Ner;

namespace CodeAlta.Plugin.Pseudonymizer;

/// <summary>
/// What the application gives the plugin to run its models: where they are kept, the ONNX Runtime that speech and search
/// share (downloaded when none of them has it yet), and the worker process the models run in.
/// </summary>
public interface IPseudonymizerModelHost
{
    /// <summary>Gets the folder the models are kept in.</summary>
    string ModelsRoot { get; }

    /// <summary>Gets the full path of the ONNX Runtime library when it is installed; null otherwise.</summary>
    /// <returns>The path, or null.</returns>
    string? OnnxRuntimeLibrary();

    /// <summary>Downloads the ONNX Runtime; does nothing when it is there.</summary>
    /// <param name="downloads">What downloads it.</param>
    /// <param name="progress">Where the progress goes.</param>
    /// <param name="cancellationToken">Stops the download.</param>
    /// <returns>The download.</returns>
    /// <exception cref="PinnedDownloadException">The download failed.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no ONNX Runtime.</exception>
    Task InstallOnnxRuntimeAsync(PinnedDownloader downloads, IPinnedDownloadProgress progress, CancellationToken cancellationToken);

    /// <summary>Starts a worker process for the models (<see cref="NerWorker.Option"/>).</summary>
    /// <returns>The worker.</returns>
    IWorkerProcess StartWorker();
}

/// <summary>
/// The models that find names: those of <see cref="NerModelCatalog"/>, downloaded on demand, and a model of the user given
/// by its folder. A model in use runs in a worker process of its own, which all the sessions share.
/// </summary>
internal sealed class NerModels : IDisposable
{
    private const string Marker = "model.json";

    private readonly IPseudonymizerModelHost _host;
    private readonly ConcurrentDictionary<string, (NerWorkerClient Client, TokenClassificationRecognizer Recognizer)> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public NerModels(IPseudonymizerModelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
    }

    /// <summary>Gets the folder of a model of the catalog.</summary>
    public string DirectoryOf(NerModelPreset preset) => Path.Combine(_host.ModelsRoot, preset.FolderName);

    /// <summary>Gets whether a model of the catalog is downloaded whole.</summary>
    public bool IsInstalled(NerModelPreset preset)
    {
        var directory = DirectoryOf(preset);
        return File.Exists(Path.Combine(directory, Marker))
            && preset.Files.All(file => new FileInfo(Path.Combine(directory, file.Name)) is { Exists: true } info && info.Length == file.Size);
    }

    /// <summary>
    /// Downloads the ONNX Runtime and models of the catalog that are not there yet. A model that is there and not whole (a
    /// file cut short or missing) is removed first, and downloaded again.
    /// </summary>
    /// <exception cref="PinnedDownloadException">A download failed.</exception>
    /// <exception cref="IOException">A damaged model cannot be removed.</exception>
    /// <exception cref="UnauthorizedAccessException">A damaged model cannot be removed.</exception>
    public async Task InstallAsync(IReadOnlyList<NerModelPreset> presets, IPinnedDownloadProgress progress, CancellationToken cancellationToken)
    {
        // The download takes a folder that has its marker for an installation, and would leave a damaged one as it is.
        foreach (var preset in presets)
        {
            var directory = DirectoryOf(preset);
            if (IsInstalled(preset) || !Directory.Exists(directory)) continue;
            Unload(directory);
            Directory.Delete(directory, recursive: true);
        }

        using var downloads = new PinnedDownloader(null, new ProductInfoHeaderValue("CodeAlta", typeof(NerModels).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        if (_host.OnnxRuntimeLibrary() is null) await _host.InstallOnnxRuntimeAsync(downloads, progress, cancellationToken).ConfigureAwait(false);
        foreach (var preset in presets)
        {
            if (IsInstalled(preset)) continue;
            Directory.CreateDirectory(_host.ModelsRoot);
            var marker = System.Text.Encoding.UTF8.GetBytes($"{{\"id\":\"{preset.Id}\",\"folder\":\"{preset.FolderName}\"}}");
            await downloads.InstallFolderAsync(DirectoryOf(preset), Marker, [.. preset.Files.Select(static file => (file.Pinned, file.Address))],
                (directory, _) => WriteDescriptionAsync(directory, preset), marker, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the recognizer of a model: one of the catalog by its id, or the folder of a model of the user. A model that is
    /// not installed, or does not load, fails: the request sends nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The model is not installed, or cannot be run.</exception>
    public PiiRecognizer Recognizer(string id, string? customFolder)
    {
        string directory;
        if (string.Equals(id, NerModelCatalog.CustomId, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(customFolder) || !Directory.Exists(customFolder))
                throw new InvalidOperationException("The folder of the custom model is not set, or does not exist.");
            directory = Path.GetFullPath(customFolder);
        }
        else
        {
            var preset = NerModelCatalog.Find(id) ?? throw new InvalidOperationException($"'{id}' is not a model of the Pseudonymizer.");
            directory = DirectoryOf(preset);
            // A loaded model answers each request without its files being checked again: deleting it unloads it first.
            if (_loaded.TryGetValue(directory, out var running)) return running.Recognizer;
            if (!IsInstalled(preset)) throw new InvalidOperationException($"The model {preset.Name} is not downloaded.");
        }

        lock (_gate)
        {
            if (_loaded.TryGetValue(directory, out var loaded)) return loaded.Recognizer;
            // The description of a model of the catalog is the one of this version: the file a previous version wrote
            // beside it (with other kinds, other labels) is written again before this process and its worker read it.
            if (NerModelCatalog.Find(id) is { } catalogModel)
            {
                try
                {
                    catalogModel.Description.Save(directory);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException($"The description of the model {catalogModel.Name} could not be written: {exception.Message}", exception);
                }
            }

            var runtime = _host.OnnxRuntimeLibrary() ?? throw new InvalidOperationException("The ONNX Runtime is not installed.");
            NerModelDescription description;
            NerTokenizer tokenizer;
            try
            {
                description = NerModelDescription.Load(directory);
                tokenizer = NerTokenizer.Load(directory, description.Tokenizer);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidOperationException(exception.Message, exception);
            }

            var client = new NerWorkerClient(_host.StartWorker, new NerWorkerLoad(directory, runtime, Math.Clamp(Environment.ProcessorCount / 2, 1, 4)));
            var recognizer = new TokenClassificationRecognizer(description, tokenizer, client);
            _loaded[directory] = (client, recognizer);
            return recognizer;
        }
    }

    /// <summary>Stops the worker of the model of a folder, before its files are deleted.</summary>
    public void Unload(string directory)
    {
        lock (_gate)
        {
            if (_loaded.TryRemove(directory, out var loaded)) loaded.Client.Dispose();
        }
    }

    // The description of a model of the catalog is written beside its files: the worker reads it as it reads the one of
    // a model of the user. Each load writes it again (Recognizer).
    private static Task WriteDescriptionAsync(string directory, NerModelPreset preset)
    {
        preset.Description.Save(directory);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var (client, _) in _loaded.Values) client.Dispose();
        _loaded.Clear();
    }
}
