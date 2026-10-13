using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace CodeAlta.Desktop.Speech;

/// <summary>How the files of a speech model are given to the engine.</summary>
internal enum SpeechModelFamily
{
    /// <summary>A NeMo transducer, such as Parakeet TDT: encoder, decoder, joiner.</summary>
    NemoTransducer,

    /// <summary>Another transducer: encoder, decoder, joiner; the engine reads its kind in the encoder.</summary>
    Transducer,

    /// <summary>Moonshine with four files: preprocessor, encoder, uncached and cached decoder.</summary>
    MoonshineV1,

    /// <summary>Moonshine with two files: encoder and merged decoder.</summary>
    MoonshineV2,

    /// <summary>Whisper: encoder and decoder.</summary>
    Whisper,
}

/// <summary>One file to download, with what it is checked against.</summary>
/// <param name="Name">The file name, without a folder.</param>
/// <param name="Role">What the file is to the engine: <c>encoder</c>, <c>decoder</c>, <c>joiner</c>, <c>tokens</c>,
/// <c>preprocessor</c>, <c>uncachedDecoder</c>, <c>cachedDecoder</c>, <c>mergedDecoder</c>, or empty.</param>
/// <param name="Size">The exact size in bytes.</param>
/// <param name="Sha256">The SHA-256 of the content, in lower-case hexadecimal, when the source gives one.</param>
/// <param name="GitSha1">The git blob SHA-1 of the content, for a file the source keeps in git itself.</param>
internal sealed record SpeechFile(string Name, string Role, long Size, string? Sha256, string? GitSha1);

/// <summary>A speech model: where its files are, and what is shown about it.</summary>
/// <param name="Id">The name of its folder in the cache, and of a preset in the configuration.</param>
/// <param name="Name">The name shown.</param>
/// <param name="Author">Who made the model.</param>
/// <param name="Languages"><c>english</c>, <c>european</c> (25 languages), <c>multilingual</c>, or empty when unknown.</param>
/// <param name="License">The license shown, or empty when unknown.</param>
/// <param name="Repository">The Hugging Face repository, <c>owner/name</c>.</param>
/// <param name="Commit">The commit the files are read at.</param>
/// <param name="Folder">The folder of the files in the repository, empty for its root.</param>
/// <param name="Family">How the engine loads it.</param>
/// <param name="Files">The files.</param>
internal sealed record SpeechModelDefinition(string Id, string Name, string Author, string Languages, string License,
    string Repository, string Commit, string Folder, SpeechModelFamily Family, IReadOnlyList<SpeechFile> Files)
{
    /// <summary>Gets the size of the download.</summary>
    public long Size => Files.Sum(static file => file.Size);

    /// <summary>Gets the address a file is downloaded from.</summary>
    public Uri AddressOf(SpeechFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var path = Folder.Length == 0 ? file.Name : Folder + "/" + file.Name;
        return new($"https://huggingface.co/{Repository}/resolve/{Commit}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}");
    }
}

/// <summary>The engine for one platform: the package of its native libraries.</summary>
/// <param name="RuntimeIdentifier">The .NET runtime identifier.</param>
/// <param name="Size">The size of the package.</param>
/// <param name="Sha256">The SHA-256 of the package.</param>
/// <param name="Libraries">The libraries, the one the others need first.</param>
internal sealed record SpeechEngineDefinition(string RuntimeIdentifier, long Size, string Sha256, IReadOnlyList<string> Libraries)
{
    /// <summary>Gets the address of the package.</summary>
    public Uri Address
        => new($"https://api.nuget.org/v3-flatcontainer/org.k2fsa.sherpa.onnx.runtime.{RuntimeIdentifier}/{SpeechModelCatalog.EngineVersion}/org.k2fsa.sherpa.onnx.runtime.{RuntimeIdentifier}.{SpeechModelCatalog.EngineVersion}.nupkg");
}

/// <summary>
/// What speech to prompt can download: the preset models, the engine that runs them and the model that finds
/// where speech starts and ends. Every file is pinned by its commit or version and its hash.
/// </summary>
internal static class SpeechModelCatalog
{
    /// <summary>
    /// The version of sherpa-onnx: of the managed wrapper the project references and of the native libraries
    /// downloaded here. They are built from the same structures and change together.
    /// </summary>
    public const string EngineVersion = "1.13.8";

    /// <summary>Gets the preset models, the default first.</summary>
    public static IReadOnlyList<SpeechModelDefinition> Presets { get; } =
    [
        new("parakeet-tdt-0.6b-v3", "Parakeet TDT 0.6b v3", "NVIDIA", "european", "CC BY 4.0",
            "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8", "2bda32ec70b097a55adaa07d9a7173915b43cc78", "", SpeechModelFamily.NemoTransducer,
            [
                new("encoder.int8.onnx", "encoder", 652184281, "acfc2b4456377e15d04f0243af540b7fe7c992f8d898d751cf134c3a55fd2247", null),
                new("decoder.int8.onnx", "decoder", 11845275, "179e50c43d1a9de79c8a24149a2f9bac6eb5981823f2a2ed88d655b24248db4e", null),
                new("joiner.int8.onnx", "joiner", 6355277, "3164c13fc2821009440d20fcb5fdc78bff28b4db2f8d0f0b329101719c0948b3", null),
                new("tokens.txt", "tokens", 93939, null, "fc43e1c723e262df60b70e1919614417162d1fe2"),
            ]),
        new("parakeet-tdt-0.6b-v2", "Parakeet TDT 0.6b v2", "NVIDIA", "english", "CC BY 4.0",
            "csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "1ab9323565ddb038682214b292f588070a538ce2", "", SpeechModelFamily.NemoTransducer,
            [
                new("encoder.int8.onnx", "encoder", 652184296, "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab", null),
                new("decoder.int8.onnx", "decoder", 7257753, "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e", null),
                new("joiner.int8.onnx", "joiner", 1739080, "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2", null),
                new("tokens.txt", "tokens", 9384, null, "f0742785f6073e80b911964c455b05f3609bf23b"),
            ]),
        new("whisper-small", "Whisper small", "OpenAI", "multilingual", "MIT",
            "csukuangfj/sherpa-onnx-whisper-small", "8f3c18b358db4d1f2fc1eae49d75cd20989e4309", "", SpeechModelFamily.Whisper,
            [
                new("small-encoder.int8.onnx", "encoder", 112442483, "4cbe7b22fa9026b843b60a68640c747de05bafb1a11b57edc0e66c232d9f33a9", null),
                new("small-decoder.int8.onnx", "decoder", 262226114, "acad50b5c782696e91b55914cc5ab4f756f1532f76e22aa6fc615f39fb69a8ee", null),
                new("small-tokens.txt", "tokens", 816730, null, "a4edf0b719c10d28cd83f8c294449f99ff5d5dc0"),
            ]),
        new("moonshine-base-en", "Moonshine base", "Moonshine AI", "english", "MIT",
            "csukuangfj2/sherpa-onnx-moonshine-base-en-quantized-2026-02-27", "8f4d6c58c03d40bcea40043bb7120a878f2bbef6", "", SpeechModelFamily.MoonshineV2,
            [
                new("encoder_model.ort", "encoder", 31326816, "7c66495948d0d08ec1af454cd4b5514862ae6511e94712a60e6d83eaec8dc8cf", null),
                new("decoder_model_merged.ort", "mergedDecoder", 109424400, "d9d7b333af34bc552580576ddcf248a1c6c839e0d3b43b09afb9376ed009899d", null),
                new("tokens.txt", "tokens", 549350, null, "138e8d16af070f5c615e88cfc5432560c3cbf480"),
            ]),
        new("moonshine-tiny-en", "Moonshine tiny", "Moonshine AI", "english", "MIT",
            "csukuangfj2/sherpa-onnx-moonshine-tiny-en-quantized-2026-02-27", "d1e6c30921780b8508d04b492dfb3ce8a51605d4", "", SpeechModelFamily.MoonshineV2,
            [
                new("encoder_model.ort", "encoder", 13281600, "94e90a4654fc45cdfedb77c4c08e1739f48862998e58fada384b25118134f221", null),
                new("decoder_model_merged.ort", "mergedDecoder", 30412256, "cf524c4862d36e9e5ab032eddc73637efd822d70e868ac575cf1a46e1e4708a0", null),
                new("tokens.txt", "tokens", 549350, null, "138e8d16af070f5c615e88cfc5432560c3cbf480"),
            ]),
    ];

    /// <summary>Gets the model that finds where speech starts and ends (Silero VAD), as a one-file model.</summary>
    public static SpeechModelDefinition VoiceDetector { get; } = new("vad", "Silero VAD", "Silero", "", "MIT",
        "csukuangfj/vad", "fba88cd2e921609e7675c3aaf51e0b9b295da4bc", "", SpeechModelFamily.Transducer,
        [new("silero_vad.onnx", "", 1807522, "a35ebf52fd3ce5f1469b2a36158dba761bc47b973ea3382b3186ca15b1f5af28", null)]);

    private static readonly FrozenDictionary<string, SpeechEngineDefinition> Engines = new SpeechEngineDefinition[]
    {
        new("win-x64", 8535869, "2257c06893d1d90792ea3157606de33a51122d935b6913bed5442d2ffb9e9bd7", ["onnxruntime.dll", "sherpa-onnx-c-api.dll"]),
        new("win-arm64", 8142361, "592463d28f93f6e0946acedfb98b140730448c54016f54e2df3ed382637d9b33", ["onnxruntime.dll", "sherpa-onnx-c-api.dll"]),
        new("osx-x64", 11011142, "8b482b0d58ef651f1b97cd573112f95a0374a9db9bd010453030c999f757e14e", ["libonnxruntime.dylib", "libsherpa-onnx-c-api.dylib"]),
        new("osx-arm64", 9824946, "ce4c79d88f1a0aa0fe877f6acbfa4204842eeefc79750707b2fdd8b1e6a90bf8", ["libonnxruntime.dylib", "libsherpa-onnx-c-api.dylib"]),
        new("linux-x64", 10794309, "65cf697ca95abfbd01dde3b015ede032880ac5fccff931aa6737cfe542c04e76", ["libonnxruntime.so", "libsherpa-onnx-c-api.so"]),
        new("linux-arm64", 13638800, "c1fd7628eddb84837f459c95bd9c41a8c3dc22ef1001b18f04f6c573c1d1d054", ["libonnxruntime.so", "libsherpa-onnx-c-api.so"]),
    }.ToFrozenDictionary(static engine => engine.RuntimeIdentifier, StringComparer.Ordinal);

    /// <summary>Gets the runtime identifiers the engine exists for.</summary>
    public static IEnumerable<string> EngineRuntimeIdentifiers => Engines.Keys;

    /// <summary>Gets a preset by its id, or null.</summary>
    public static SpeechModelDefinition? FindPreset(string? id)
        => id is null ? null : Presets.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.Ordinal));

    /// <summary>Gets the engine of a platform, or null when sherpa-onnx has none for it.</summary>
    public static SpeechEngineDefinition? FindEngine(string? runtimeIdentifier)
        => runtimeIdentifier is not null && Engines.TryGetValue(runtimeIdentifier, out var engine) ? engine : null;

    /// <summary>Gets the runtime identifier of this process among those the engine exists for, or null.</summary>
    public static string? CurrentRuntimeIdentifier()
    {
        var system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null;
        var architecture = RuntimeInformation.ProcessArchitecture switch { Architecture.X64 => "x64", Architecture.Arm64 => "arm64", _ => null };
        // The engine is built against glibc: a musl system has no package.
        if (system is null || architecture is null || RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.Ordinal)) return null;
        return system + "-" + architecture;
    }
}
