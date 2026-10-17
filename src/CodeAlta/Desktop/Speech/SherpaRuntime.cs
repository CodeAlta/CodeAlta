using System.IO.Compression;
using CodeAlta.NativeEngines;

namespace CodeAlta.Desktop.Speech;

/// <summary>
/// The native libraries of sherpa-onnx (its C API and the ONNX Runtime it ships) in the shared cache, one folder
/// per version and platform: <c>cache/runtime/sherpa-onnx/&lt;version&gt;/&lt;rid&gt;</c>. Speech loads both libraries;
/// search loads the ONNX Runtime alone, so both map the same file.
/// </summary>
internal static class SherpaRuntime
{
    /// <summary>The file whose presence says the folder is complete.</summary>
    public const string Marker = "engine.json";

    /// <summary>Gets the folder of an engine under the runtime root.</summary>
    public static string DirectoryOf(string runtimeRoot, SpeechEngineDefinition engine)
        => Path.Combine(runtimeRoot, "sherpa-onnx", SpeechModelCatalog.EngineVersion, engine.RuntimeIdentifier);

    /// <summary>Gets the folder speech used before the runtime was shared: <c>speech/runtime/&lt;version&gt;/&lt;rid&gt;</c>.</summary>
    public static string LegacyDirectoryOf(string speechRoot, SpeechEngineDefinition engine)
        => Path.Combine(speechRoot, "runtime", SpeechModelCatalog.EngineVersion, engine.RuntimeIdentifier);

    /// <summary>Gets whether a folder holds the engine.</summary>
    public static bool IsInstalled(string directory, SpeechEngineDefinition engine)
        => File.Exists(Path.Combine(directory, Marker)) && engine.Libraries.All(library => File.Exists(Path.Combine(directory, library)));

    /// <summary>Gets the path of the ONNX Runtime library of a folder: the first library, loaded before the C API.</summary>
    public static string OnnxRuntimePathOf(string directory, SpeechEngineDefinition engine) => Path.Combine(directory, engine.Libraries[0]);

    /// <summary>
    /// Gets the folder that holds the engine now: the shared one, after moving there what speech downloaded
    /// before; the old folder when the move failed (a worker still holds its files); the shared one when neither
    /// holds it.
    /// </summary>
    public static string Resolve(string runtimeRoot, string speechRoot, SpeechEngineDefinition engine)
    {
        var shared = DirectoryOf(runtimeRoot, engine);
        if (IsInstalled(shared, engine)) return shared;
        var legacy = LegacyDirectoryOf(speechRoot, engine);
        if (!IsInstalled(legacy, engine)) return shared;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
            if (Directory.Exists(shared)) Directory.Delete(shared, recursive: true);
            Directory.Move(legacy, shared);
            return shared;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SpeechLog.Info($"The speech engine stays in its old folder for now: {exception.Message}");
            return legacy;
        }
    }

    /// <summary>Downloads the package of the engine and unpacks its libraries into the shared folder.</summary>
    /// <exception cref="PinnedDownloadException">The download failed.</exception>
    public static Task InstallAsync(PinnedDownloader downloads, string runtimeRoot, SpeechEngineDefinition engine, IPinnedDownloadProgress progress,
        Action? installing, CancellationToken cancellationToken)
    {
        var package = new PinnedFile("engine.nupkg", engine.Size, engine.Sha256);
        return downloads.InstallFolderAsync(DirectoryOf(runtimeRoot, engine), Marker, [(package, engine.Address)], (directory, _) =>
        {
            installing?.Invoke();
            Extract(directory, package, engine);
            return Task.CompletedTask;
        }, "{}"u8.ToArray(), progress, cancellationToken);
    }

    private static void Extract(string directory, PinnedFile package, SpeechEngineDefinition engine)
    {
        var path = Path.Combine(directory, package.Name);
        using (var archive = ZipFile.OpenRead(path))
        {
            foreach (var library in engine.Libraries)
            {
                var entry = archive.GetEntry($"runtimes/{engine.RuntimeIdentifier}/native/{library}") ?? throw new PinnedDownloadException("checksum");
                entry.ExtractToFile(Path.Combine(directory, library), overwrite: true);
            }
        }

        File.Delete(path);
    }
}
