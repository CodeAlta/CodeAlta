using CodeAlta.Catalog;
using CodeAlta.Desktop.Speech;
using CodeAlta.NativeEngines;
using CodeAlta.Plugin.Pseudonymizer;
using CodeAlta.Pseudonymization.Ner;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// What the window gives the Pseudonymizer plugin to run its models: they are kept in <c>cache/pseudonymizer/models</c>,
/// run on the ONNX Runtime that speech and search share (<c>cache/runtime</c>), in a worker process of this executable.
/// </summary>
internal sealed class DesktopPseudonymizerModels : IPseudonymizerModelHost
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Pseudonymizer");

    private readonly CatalogOptions _catalog;
    private readonly SpeechEngineDefinition? _engine = SpeechModelCatalog.FindEngine(SpeechModelCatalog.CurrentRuntimeIdentifier());

    public DesktopPseudonymizerModels(CatalogOptions catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <inheritdoc />
    public string ModelsRoot => Path.Combine(_catalog.CacheRoot, "pseudonymizer", "models");

    /// <inheritdoc />
    public string? OnnxRuntimeLibrary()
    {
        if (_engine is null) return null;
        var directory = SherpaRuntime.Resolve(_catalog.RuntimeRoot, _catalog.SpeechRoot, _engine);
        return SherpaRuntime.IsInstalled(directory, _engine) ? SherpaRuntime.OnnxRuntimePathOf(directory, _engine) : null;
    }

    /// <inheritdoc />
    public Task InstallOnnxRuntimeAsync(PinnedDownloader downloads, IPinnedDownloadProgress progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(progress);
        if (_engine is null) throw new PlatformNotSupportedException("This platform has no ONNX Runtime for the models of the Pseudonymizer.");
        return OnnxRuntimeLibrary() is not null ? Task.CompletedTask
            : SherpaRuntime.InstallAsync(downloads, _catalog.RuntimeRoot, _engine, progress, installing: null, cancellationToken);
    }

    /// <inheritdoc />
    public IWorkerProcess StartWorker()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_catalog.CacheRoot, "pseudonymizer")).FullName;
        return WorkerProcess.Start(NerWorker.Option, folder, static line => Logger.Info($"pseudonymizer worker: {line}"));
    }
}
