using CodeAlta.Catalog;
using CodeAlta.Desktop.Speech;
using CodeAlta.NativeEngines;
using CodeAlta.Plugin.Pseudonymizer;
using CodeAlta.Pseudonymization.Ner;
using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// What the window gives the Pseudonymizer plugin to run its models: they are kept in <c>cache/pseudonymizer/models</c>,
/// run on the ONNX Runtime that speech and search share (<see cref="SherpaRuntime"/>), in a worker process of this executable.
/// </summary>
internal sealed class DesktopPseudonymizerModels : IPseudonymizerModelHost
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.Pseudonymizer");

    private readonly CatalogOptions _catalog;
    private readonly SherpaRuntime _runtime;

    public DesktopPseudonymizerModels(CatalogOptions catalog, SherpaRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(runtime);
        _catalog = catalog;
        _runtime = runtime;
    }

    /// <inheritdoc />
    public string ModelsRoot => Path.Combine(_catalog.CacheRoot, "pseudonymizer", "models");

    /// <inheritdoc />
    public string? OnnxRuntimeLibrary() => _runtime.OnnxRuntimeLibrary();

    /// <inheritdoc />
    public Task InstallOnnxRuntimeAsync(CancellationToken cancellationToken) => _runtime.InstallAsync(progress: null, cancellationToken);

    /// <inheritdoc />
    public IWorkerProcess StartWorker()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_catalog.CacheRoot, "pseudonymizer")).FullName;
        return WorkerProcess.Start(NerWorker.Option, folder, static line => Logger.Info($"pseudonymizer worker: {line}"));
    }
}
