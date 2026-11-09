using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Core.IO;
using CodeAlta.ModelServers.Downloads;

namespace CodeAlta.ModelServers.LlamaCpp;

/// <summary>What the store of builds keeps: what is installed, and which build each backend runs.</summary>
public sealed record BuildState
{
    /// <summary>Gets the builds installed.</summary>
    public List<InstalledBuild> Installed { get; init; } = [];

    /// <summary>Gets the build each backend runs.</summary>
    public Dictionary<string, string> Current { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Gets the build each backend goes back to.</summary>
    public Dictionary<string, string> Previous { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Gets the builds that failed their test here, as <c>tag/backend</c>.</summary>
    public List<string> Bad { get; init; } = [];
}

/// <summary>What a build said when it was probed.</summary>
/// <param name="VersionLine">What <c>--version</c> printed.</param>
/// <param name="Build">The build number it says.</param>
/// <param name="Devices">The devices it sees.</param>
public sealed record BuildProbe(string? VersionLine, int? Build, IReadOnlyList<LlamaDevice> Devices);

/// <summary>
/// The llama.cpp builds of a machine, side by side under <c>builds/&lt;tag&gt;/&lt;backend&gt;/</c>: installed whole (the server is a
/// small launcher beside its libraries), never over a build a server runs (Windows locks its DLLs), the current and
/// the previous build of each backend kept, and the others removed.
/// </summary>
public sealed partial class LlamaBuilds
{
    /// <summary>How long a probe of a build may take.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);

    /// <summary>The oldest build offered: it fixed a bypass of the fix of the GGUF parser (CVE-2026-27940).</summary>
    public const int MinimumBuild = 8146;

    private readonly string _root;
    private readonly HttpClient _http;
    private readonly Lock _gate = new();

    /// <summary>Creates the store of builds.</summary>
    /// <param name="root">The folder of the builds.</param>
    /// <param name="http">The client of the downloads.</param>
    public LlamaBuilds(string root, HttpClient http)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(http);
        _root = Path.GetFullPath(root);
        _http = http;
    }

    /// <summary>Gets the folder of the builds.</summary>
    public string Root => _root;

    /// <summary>Reads what is installed.</summary>
    /// <returns>The state.</returns>
    public BuildState Read()
    {
        lock (_gate)
        {
            return ReadState();
        }
    }

    /// <summary>Lists the builds installed, with which ones are current, previous and bad.</summary>
    /// <returns>The builds, newest first.</returns>
    public IReadOnlyList<InstalledBuild> List()
    {
        var state = Read();
        return state.Installed.Where(static build => Directory.Exists(build.Folder))
            .Select(build => build with
            {
                Current = state.Current.GetValueOrDefault(build.Backend) == build.Tag,
                Previous = state.Previous.GetValueOrDefault(build.Backend) == build.Tag,
                Bad = state.Bad.Contains(build.Tag + "/" + build.Backend),
            })
            .OrderBy(static build => build.Backend, StringComparer.Ordinal).ThenByDescending(static build => build.Build).ToList();
    }

    /// <summary>The build a backend runs, or a named one.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="tag">A build, or null for the current one.</param>
    /// <returns>The build, or null when none is installed.</returns>
    public InstalledBuild? Find(string backend, string? tag)
    {
        var builds = List();
        return tag is null ? builds.FirstOrDefault(build => build.Backend == backend && build.Current)
            : builds.FirstOrDefault(build => build.Backend == backend && build.Tag == tag);
    }

    /// <summary>The program of the server of a build.</summary>
    /// <param name="build">The build.</param>
    /// <returns>The path of <c>llama-server</c>.</returns>
    public static string ServerProgram(InstalledBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        return Path.Combine(build.Folder, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
    }

    /// <summary>Downloads, checks, unpacks and probes a build; it becomes the current build of its backend unless told otherwise.</summary>
    /// <param name="release">The release.</param>
    /// <param name="backend">The backend.</param>
    /// <param name="assets">Its archives (the build, and the CUDA runtime for CUDA).</param>
    /// <param name="makeCurrent">Whether it becomes the current build of its backend.</param>
    /// <param name="progress">Called with the bytes received.</param>
    /// <param name="cancellationToken">Stops; what is downloaded stays for the next attempt.</param>
    /// <returns>The build installed.</returns>
    /// <exception cref="ModelHostException">The build is too old, its download failed, or its probe saw no device of its backend.</exception>
    public async Task<InstalledBuild> InstallAsync(LlamaRelease release, string backend, IReadOnlyList<LlamaAsset> assets, bool makeCurrent, Action<long> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(assets);
        // Lemonade numbers its builds itself: the llama.cpp build it carries is the one its probe tells.
        var lemonade = release.Source == "lemonade";
        if (!lemonade && release.Build < MinimumBuild) throw new ModelHostException("too_old", $"{release.Tag} is older than b{MinimumBuild}, which fixed a flaw of the GGUF parser.");
        if (assets.Count == 0) throw new ModelHostException("not_found", $"{release.Tag} has no {backend} build for this machine.");
        if (assets.Any(static asset => asset.Sha256 is null))
            throw new ModelHostException("checksum", $"{release.Tag} publishes no SHA-256 for its archives: it is not installed.");
        var folder = Path.Combine(_root, release.Tag, backend);
        if (List().FirstOrDefault(build => build.Tag == release.Tag && build.Backend == backend) is { } existing && File.Exists(ServerProgram(existing)))
        {
            if (makeCurrent) MakeCurrent(backend, release.Tag);
            return List().First(build => build.Tag == release.Tag && build.Backend == backend);
        }

        var partial = folder + ".partial";
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        var downloader = new RangeDownloader(_http);
        foreach (var asset in assets)
        {
            var archive = Path.Combine(downloads, asset.Name);
            if (!File.Exists(archive) || new FileInfo(archive).Length != asset.Size)
            {
                var part = archive + ".part";
                try
                {
                    await downloader.DownloadAsync(_ => Task.FromResult(new DownloadSource(new Uri(asset.Url), null)), part, asset.Size, asset.Sha256, 2, progress, null,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (DownloadException exception)
                {
                    throw new ModelHostException(exception.Code, $"{asset.Name}: {exception.Message}");
                }

                File.Move(part, archive, overwrite: true);
            }
        }

        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);
        Directory.CreateDirectory(partial);
        foreach (var asset in assets) Extract(Path.Combine(downloads, asset.Name), partial);
        Flatten(partial);
        var probe = await ProbeAsync(partial, cancellationToken).ConfigureAwait(false);
        if (lemonade && probe.Build is < MinimumBuild)
        {
            Directory.Delete(partial, recursive: true);
            throw new ModelHostException("too_old", $"{release.Tag} carries llama.cpp b{probe.Build}, older than b{MinimumBuild}, which fixed a flaw of the GGUF parser.");
        }

        if (backend is not ("cpu" or "metal") && !probe.Devices.Any(device => DeviceBackend(device.Name) == backend))
        {
            var seen = probe.Devices.Count == 0 ? "no device" : string.Join(", ", probe.Devices.Select(static device => $"{device.Name} ({device.Description})"));
            Directory.Delete(partial, recursive: true);
            throw new ModelHostException("no_device", $"The {backend} build of {release.Tag} sees {seen} on this machine: it is not installed.");
        }

        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.Move(partial, folder);
        foreach (var asset in assets) TryDelete(Path.Combine(downloads, asset.Name));
        var build = new InstalledBuild(release.Tag, lemonade ? probe.Build ?? 0 : release.Build, backend, assets[0].BackendVersion, folder, FolderSize(folder), DateTimeOffset.UtcNow,
            probe.Devices, probe.VersionLine, false, false, false, release.Source);
        lock (_gate)
        {
            var state = ReadState();
            state.Installed.RemoveAll(existingBuild => existingBuild.Tag == build.Tag && existingBuild.Backend == backend);
            state.Installed.Add(build);
            WriteState(state);
        }

        if (makeCurrent) MakeCurrent(backend, release.Tag);
        return List().First(installed => installed.Tag == release.Tag && installed.Backend == backend);
    }

    /// <summary>Adds a folder of the person's own build of llama.cpp, which is never updated.</summary>
    /// <param name="folder">The folder of <c>llama-server</c>.</param>
    /// <param name="backend">Its backend.</param>
    /// <param name="cancellationToken">Ends the probe.</param>
    /// <returns>The build.</returns>
    /// <exception cref="ModelHostException">The folder has no llama-server that answers.</exception>
    public async Task<InstalledBuild> AddOwnAsync(string folder, string backend, CancellationToken cancellationToken)
    {
        folder = Path.GetFullPath(folder);
        var probe = await ProbeAsync(folder, cancellationToken).ConfigureAwait(false);
        var tag = "own-" + (probe.Build?.ToString(CultureInfo.InvariantCulture) ?? "0");
        var build = new InstalledBuild(tag, probe.Build ?? 0, backend, null, folder, 0, DateTimeOffset.UtcNow, probe.Devices, probe.VersionLine, false, false, false, "own");
        lock (_gate)
        {
            var state = ReadState();
            state.Installed.RemoveAll(existing => existing.Tag == tag && existing.Backend == backend);
            state.Installed.Add(build);
            WriteState(state);
        }

        return build;
    }

    /// <summary>Makes a build the one a backend runs; the build it replaces is kept to go back to.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="tag">The build.</param>
    public void MakeCurrent(string backend, string tag)
    {
        lock (_gate)
        {
            var state = ReadState();
            // A build that failed its test is no way back: a rollback leaves none rather than offer it.
            if (state.Current.GetValueOrDefault(backend) is { } current && current != tag)
            {
                if (state.Bad.Contains(current + "/" + backend)) state.Previous.Remove(backend);
                else state.Previous[backend] = current;
            }

            state.Current[backend] = tag;
            if (state.Previous.GetValueOrDefault(backend) == tag) state.Previous.Remove(backend);
            WriteState(state);
        }
    }

    /// <summary>Marks a build bad on this machine: an update does not take it again.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="tag">The build.</param>
    public void MarkBad(string backend, string tag)
    {
        lock (_gate)
        {
            var state = ReadState();
            var key = tag + "/" + backend;
            if (!state.Bad.Contains(key)) state.Bad.Add(key);
            WriteState(state);
        }
    }

    /// <summary>Whether a build failed its test here.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="tag">The build.</param>
    /// <returns>Whether it did.</returns>
    public bool IsBad(string backend, string tag) => Read().Bad.Contains(tag + "/" + backend);

    /// <summary>Removes the builds that are neither current, previous nor in use.</summary>
    /// <param name="inUse">The folders servers run now.</param>
    /// <returns>The bytes freed.</returns>
    public long Prune(IReadOnlySet<string> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        long freed = 0;
        foreach (var build in List().Where(build => !build.Current && !build.Previous && build.Source != "own" && !inUse.Contains(build.Folder)))
            freed += Remove(build.Backend, build.Tag);
        return freed;
    }

    /// <summary>Removes one build.</summary>
    /// <param name="backend">The backend.</param>
    /// <param name="tag">The build.</param>
    /// <returns>The bytes freed.</returns>
    /// <exception cref="ModelHostException">It is in use.</exception>
    public long Remove(string backend, string tag)
    {
        lock (_gate)
        {
            var state = ReadState();
            var build = state.Installed.FirstOrDefault(existing => existing.Backend == backend && existing.Tag == tag);
            if (build is null) return 0;
            long freed = 0;
            if (build.Source != "own" && Directory.Exists(build.Folder))
            {
                freed = FolderSize(build.Folder);
                try { Directory.Delete(build.Folder, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new ModelHostException("in_use", $"{tag} ({backend}) is in use: stop its servers first.");
                }

                var parent = Path.GetDirectoryName(build.Folder)!;
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }

            state.Installed.Remove(build);
            if (state.Current.GetValueOrDefault(backend) == tag)
            {
                state.Current.Remove(backend);
                if (state.Previous.Remove(backend, out var previous)) state.Current[backend] = previous;
            }

            if (state.Previous.GetValueOrDefault(backend) == tag) state.Previous.Remove(backend);
            WriteState(state);
            return freed;
        }
    }

    /// <summary>Asks a build what it is and which devices it sees, in a child process with <see cref="ProbeTimeout"/>.</summary>
    /// <param name="folder">The folder of the build.</param>
    /// <param name="cancellationToken">Ends the probe.</param>
    /// <returns>What it said.</returns>
    /// <exception cref="ModelHostException">It has no llama-server, or it does not answer.</exception>
    public static async Task<BuildProbe> ProbeAsync(string folder, CancellationToken cancellationToken)
    {
        var program = Path.Combine(folder, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        if (!File.Exists(program)) throw new ModelHostException("not_found", $"There is no llama-server in {folder}.");
        var (_, versionError) = await RunAsync(program, ["--version"], cancellationToken).ConfigureAwait(false);
        var (devices, _) = await RunAsync(program, ["--list-devices"], cancellationToken).ConfigureAwait(false);
        var line = versionError.Split('\n').Select(static text => text.Trim()).FirstOrDefault(static text => text.StartsWith("version:", StringComparison.Ordinal));
        int? number = line is not null && BuildNumberPattern().Match(line) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        return new BuildProbe(line, number, ParseDevices(devices));
    }

    /// <summary>
    /// Measures how a model fits the devices of a build with its <c>llama-fit-params</c>, which reads the model without
    /// loading its weights: the context and the layers that fit, then what each device takes.
    /// </summary>
    /// <param name="build">The build.</param>
    /// <param name="modelPath">The main file of the model.</param>
    /// <param name="devices">The devices, as a server that names none uses them; empty for the build's choice.</param>
    /// <param name="at">When.</param>
    /// <param name="cancellationToken">Ends the measure.</param>
    /// <returns>The measure; null when the build has no llama-fit-params.</returns>
    /// <exception cref="ModelHostException">It does not start or does not answer in time.</exception>
    public static async Task<MeasuredFit?> MeasureFitAsync(InstalledBuild build, string modelPath, IReadOnlyList<string> devices, DateTimeOffset at, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(devices);
        var program = Path.Combine(build.Folder, OperatingSystem.IsWindows() ? "llama-fit-params.exe" : "llama-fit-params");
        if (!File.Exists(program)) return null;
        string[] arguments = devices.Count > 0 ? ["-m", modelPath, "--device", string.Join(',', devices)] : ["-m", modelPath];
        var (fitted, fittedLog) = await RunAsync(program, arguments, cancellationToken).ConfigureAwait(false);
        // The memory is that of the context it fitted: without it, -fitp counts the whole context the model was trained for.
        var context = ParseFit(build, fitted, "", at).Context;
        string[] memoryArguments = context is { } tokens ? [.. arguments, "-c", tokens.ToString(CultureInfo.InvariantCulture), "-fitp", "on"] : [.. arguments, "-fitp", "on"];
        var (memory, _) = await RunAsync(program, memoryArguments, cancellationToken).ConfigureAwait(false);
        var measure = ParseFit(build, fitted, memory, at);
        // Nothing printed: the reason is the last error of its log (a file it cannot read, an architecture this build does not know).
        return measure.Arguments.Length > 0 || measure.Devices.Count > 0 ? measure
            : measure with { Problem = fittedLog.Split('\n').Select(static line => line.Trim()).LastOrDefault(static line => line.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? "llama-fit-params printed nothing." };
    }

    /// <summary>Reads what <c>llama-fit-params</c> printed: <c>-c 40960 -ngl -1</c>, and with <c>-fitp on</c> a line per device of MiB (<c>CUDA0 1050 4480 352</c>).</summary>
    /// <param name="build">The build that measured.</param>
    /// <param name="fitted">The arguments it printed.</param>
    /// <param name="memory">The memory it printed.</param>
    /// <param name="at">When.</param>
    /// <returns>The measure.</returns>
    public static MeasuredFit ParseFit(InstalledBuild build, string fitted, string memory, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(build);
        var line = (fitted ?? "").Split('\n').Select(static text => text.Trim()).LastOrDefault(static text => text.StartsWith('-')) ?? "";
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? Value(params string[] names)
        {
            var index = Array.FindIndex(words, word => names.Contains(word, StringComparer.Ordinal));
            return index >= 0 && index + 1 < words.Length && int.TryParse(words[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        const long MiB = 1024 * 1024;
        var devices = (memory ?? "").Split('\n').Select(static text => text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(static parts => parts.Length == 4 && parts.Skip(1).All(static part => long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            .Select(static parts => new DeviceMemory(parts[0], long.Parse(parts[1], CultureInfo.InvariantCulture) * MiB, long.Parse(parts[2], CultureInfo.InvariantCulture) * MiB,
                long.Parse(parts[3], CultureInfo.InvariantCulture) * MiB)).ToList();
        return new MeasuredFit(build.Backend, build.Tag, Value("-c", "--ctx-size"), Value("-ngl", "--n-gpu-layers"), line, devices, at);
    }

    /// <summary>Reads what <c>--list-devices</c> printed: <c>  CUDA0: NVIDIA GeForce RTX 4070 (12281 MiB, 11106 MiB free)</c>.</summary>
    /// <param name="output">The output.</param>
    /// <returns>The devices.</returns>
    public static IReadOnlyList<LlamaDevice> ParseDevices(string output)
        => (output ?? "").Split('\n').Select(static line => DevicePattern().Match(line.TrimEnd('\r'))).Where(static match => match.Success)
            .Select(static match => new LlamaDevice(match.Groups["name"].Value, match.Groups["description"].Value.Trim(),
                long.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture), long.Parse(match.Groups["free"].Value, CultureInfo.InvariantCulture), null))
            .ToList();

    /// <summary>
    /// The devices a server that names none runs on: those of the build that are not the GPU of the processor, which shares
    /// the memory of the system (ROCm names it after the processor, Vulkan as the integrated GPU of the inventory). Empty when
    /// all are, or when none is: llama.cpp then chooses.
    /// </summary>
    /// <param name="build">The build.</param>
    /// <param name="hardware">The GPUs of the machine.</param>
    /// <returns>The names of the devices, or empty.</returns>
    public static IReadOnlyList<string> DiscreteDevices(InstalledBuild build, HardwareInventory hardware)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(hardware);
        static string Normalize(string name) => string.Join(' ', name.Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
        var integrated = hardware.Gpus.Where(static gpu => gpu.Integrated).Select(gpu => Normalize(gpu.Name)).ToHashSet(StringComparer.Ordinal);
        var discreteNames = hardware.Gpus.Where(static gpu => !gpu.Integrated).Select(gpu => Normalize(gpu.Name)).ToHashSet(StringComparer.Ordinal);
        bool IsIntegrated(LlamaDevice device)
        {
            var name = Normalize(device.Description);
            return device.Name.StartsWith("CPU", StringComparison.OrdinalIgnoreCase) || ProcessorPattern().IsMatch(device.Description)
                || integrated.Contains(name) && !discreteNames.Contains(name);
        }

        var discrete = build.Devices.Where(device => !IsIntegrated(device)).Select(static device => device.Name).ToList();
        return discrete.Count > 0 && discrete.Count < build.Devices.Count ? discrete : [];
    }

    [GeneratedRegex(@"\b(Ryzen|Core\(TM\)|Core Ultra|Processor)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessorPattern();

    /// <summary>The backend of a device name: <c>CUDA0</c> is <c>cuda</c>, <c>ROCm1</c> is <c>rocm</c>, <c>Vulkan2</c> is <c>vulkan</c>.</summary>
    /// <param name="device">The device.</param>
    /// <returns>Its backend.</returns>
    public static string DeviceBackend(string device)
    {
        var name = device.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').ToLowerInvariant();
        return name switch { "mtl" or "metal" => "metal", "sycl" => "sycl", _ => name };
    }

    private static async Task<(string Output, string Error)> RunAsync(string program, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(program)! };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new ModelHostException("cannot_start", $"{program} does not start on this machine: {exception.Message}");
        }

        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new ModelHostException("timeout", $"{Path.GetFileName(program)} did not answer within {ProbeTimeout.TotalSeconds:0} s.");
        }
    }

    private static void Extract(string archive, string target)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, target, overwriteFiles: true);
            return;
        }

        using var file = File.OpenRead(archive);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, target, overwriteFiles: true);
    }

    // An archive whose whole content is one folder (llama-b12110/) is unpacked from inside it.
    private static void Flatten(string folder)
    {
        while (Directory.GetFiles(folder).Length == 0 && Directory.GetDirectories(folder) is [var only])
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(only))
            {
                var destination = Path.Combine(folder, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, destination);
                else File.Move(entry, destination);
            }

            Directory.Delete(only);
        }
    }

    private static long FolderSize(string folder)
        => Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(static path => new FileInfo(path)).Where(static info => info.LinkTarget is null).Sum(static info => info.Length) : 0;

    private BuildState ReadState()
    {
        var path = Path.Combine(_root, "state.json");
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize(File.ReadAllText(path), ModelServersJsonContext.Default.BuildState) ?? new BuildState();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new BuildState();
    }

    private void WriteState(BuildState state)
    {
        Directory.CreateDirectory(_root);
        SafeFile.WriteAllText(Path.Combine(_root, "state.json"), JsonSerializer.Serialize(state, ModelServersJsonContext.Default.BuildState));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"^\s+(?<name>[A-Za-z][A-Za-z0-9_]*\d+):\s+(?<description>.+?)\s+\((?<total>\d+) MiB, (?<free>\d+) MiB free\)\s*$")]
    private static partial Regex DevicePattern();

    [GeneratedRegex(@"build (\d+)")]
    private static partial Regex BuildNumberPattern();
}