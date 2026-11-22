using System.Collections.Concurrent;
using System.Net.Http.Headers;
using CodeAlta.NativeEngines;
using CodeAlta.Pseudonymization;
using CodeAlta.Pseudonymization.Ner;

namespace CodeAlta.Plugin.Pseudonymizer;

/// <summary>
/// What the application gives the plugin to run its models: where they are kept, the ONNX Runtime that speech and search
/// share (downloaded when none of them has it yet, and again when a version of the application runs on another one), and
/// the worker process the models run in.
/// </summary>
public interface IPseudonymizerModelHost
{
    /// <summary>Gets the folder the models are kept in.</summary>
    string ModelsRoot { get; }

    /// <summary>Gets the full path of the ONNX Runtime library when it is installed; null otherwise.</summary>
    /// <returns>The path, or null.</returns>
    string? OnnxRuntimeLibrary();

    /// <summary>Downloads the ONNX Runtime; does nothing when it is there.</summary>
    /// <param name="cancellationToken">Stops the download.</param>
    /// <returns>The download.</returns>
    /// <exception cref="PinnedDownloadException">The download failed.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no ONNX Runtime.</exception>
    Task InstallOnnxRuntimeAsync(CancellationToken cancellationToken);

    /// <summary>Starts a worker process for the models (<see cref="NerWorker.Option"/>).</summary>
    /// <returns>The worker.</returns>
    IWorkerProcess StartWorker();
}

/// <summary>What a model that finds names runs with, and a download brings.</summary>
internal enum NerModelPart
{
    /// <summary>The files of the model.</summary>
    Model,

    /// <summary>The ONNX Runtime the model runs on.</summary>
    Runtime,
}

/// <summary>Thrown when a model cannot run for want of a part a download brings.</summary>
internal sealed class NerModelMissingException(NerModelPart missing, string message) : InvalidOperationException(message)
{
    /// <summary>Gets what is missing.</summary>
    public NerModelPart Missing { get; } = missing;
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

    /// <summary>Gets whether the ONNX Runtime the models run on is installed.</summary>
    public bool HasRuntime => _host.OnnxRuntimeLibrary() is not null;

    /// <summary>Downloads the ONNX Runtime the models run on; does nothing when it is there.</summary>
    /// <exception cref="PinnedDownloadException">The download failed.</exception>
    /// <exception cref="PlatformNotSupportedException">This platform has no ONNX Runtime.</exception>
    public Task InstallRuntimeAsync(CancellationToken cancellationToken) => _host.InstallOnnxRuntimeAsync(cancellationToken);

    /// <summary>
    /// Removes a model of the catalog that is there and not whole (a file cut short or missing), before it is downloaded
    /// again: the download takes a folder that has its marker for an installation, and would leave a damaged one as it is.
    /// </summary>
    /// <exception cref="IOException">The damaged model cannot be removed.</exception>
    /// <exception cref="UnauthorizedAccessException">The damaged model cannot be removed.</exception>
    public void RemoveDamaged(NerModelPreset preset)
    {
        var directory = DirectoryOf(preset);
        if (IsInstalled(preset) || !Directory.Exists(directory)) return;
        Unload(directory);
        Directory.Delete(directory, recursive: true);
    }

    /// <summary>Downloads a model of the catalog; does nothing when it is there.</summary>
    /// <exception cref="PinnedDownloadException">The download failed.</exception>
    public async Task InstallAsync(NerModelPreset preset, IPinnedDownloadProgress progress, CancellationToken cancellationToken)
    {
        if (IsInstalled(preset)) return;
        using var downloads = new PinnedDownloader(null, new ProductInfoHeaderValue("CodeAlta", typeof(NerModels).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        Directory.CreateDirectory(_host.ModelsRoot);
        var marker = System.Text.Encoding.UTF8.GetBytes($"{{\"id\":\"{preset.Id}\",\"folder\":\"{preset.FolderName}\"}}");
        await downloads.InstallFolderAsync(DirectoryOf(preset), Marker, [.. preset.Files.Select(static file => (file.Pinned, file.Address))],
            (directory, _) => WriteDescriptionAsync(directory, preset), marker, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the recognizer of a model: one of the catalog by its id, or the folder of a model of the user. A model that is
    /// not installed, or does not load, fails: the request sends nothing.
    /// </summary>
    /// <exception cref="NerModelMissingException">The model is not downloaded, or the ONNX Runtime that runs it is not.</exception>
    /// <exception cref="InvalidOperationException">The model cannot be run.</exception>
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
            if (!IsInstalled(preset)) throw new NerModelMissingException(NerModelPart.Model, $"The model {preset.Name} is not downloaded.");
        }

        lock (_gate)
        {
            if (_loaded.TryGetValue(directory, out var loaded)) return loaded.Recognizer;
            var runtime = _host.OnnxRuntimeLibrary() ?? throw new NerModelMissingException(NerModelPart.Runtime, "The ONNX Runtime is not installed.");
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
    // a model of the user.
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
