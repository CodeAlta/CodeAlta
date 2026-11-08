using System.Globalization;

namespace CodeAlta.ModelServers.LlamaCpp;

/// <summary>
/// Chooses the llama.cpp builds of a machine from its GPUs and libraries: hard rules first (system, glibc, the CUDA
/// version the driver supports, the gfx targets of the ROCm archives, the libraries they need), each with a verdict a
/// person reads. The probe of the unpacked build (<c>--list-devices</c>) has the last word.
/// </summary>
public static class BuildSelector
{
    // The gfx targets the official ROCm archives are built for (release.yml of llama.cpp).
    private static readonly HashSet<string> WindowsRocmTargets = new(StringComparer.Ordinal)
    {
        "gfx1010", "gfx1011", "gfx1012", "gfx1030", "gfx1031", "gfx1032", "gfx1033", "gfx1034", "gfx1035", "gfx1036",
        "gfx1100", "gfx1101", "gfx1102", "gfx1103", "gfx1150", "gfx1151", "gfx1152", "gfx1153", "gfx1200", "gfx1201",
    };

    private static readonly HashSet<string> LinuxRocmTargets = new(StringComparer.Ordinal)
    {
        "gfx908", "gfx90a", "gfx942", "gfx950", "gfx1010", "gfx1011", "gfx1012", "gfx1030", "gfx1031", "gfx1032", "gfx1033", "gfx1034",
        "gfx1035", "gfx1036", "gfx1100", "gfx1101", "gfx1102", "gfx1103", "gfx1150", "gfx1151", "gfx1152", "gfx1200", "gfx1201",
    };

    /// <summary>Says what each backend of a release would do on a machine, the best first.</summary>
    /// <param name="hardware">The machine.</param>
    /// <param name="release">The release, or null to judge by the machine alone.</param>
    /// <returns>A candidate per backend.</returns>
    public static IReadOnlyList<BuildCandidate> Candidates(HardwareInventory hardware, LlamaRelease? release)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        var result = new List<BuildCandidate>();
        var assets = release?.Assets.Where(asset => asset.Os == hardware.Os && asset.Arch == hardware.Architecture && !asset.IsCudaRuntime).ToList() ?? [];
        var nvidia = hardware.Gpus.Where(static gpu => gpu.Vendor == "nvidia").ToList();
        var amd = hardware.Gpus.Where(static gpu => gpu.Vendor == "amd" && !gpu.Integrated).ToList();
        var discrete = hardware.Gpus.Where(static gpu => !gpu.Integrated).ToList();
        var glibc = ParseVersion(hardware.Libraries.Glibc);

        if (hardware.Os == "macos")
        {
            var metal = hardware.Architecture == "arm64";
            result.Add(Candidate("metal", Asset(assets, "metal", release), metal ? "fits" : "noGpu",
                metal ? "Metal runs on the GPU of Apple Silicon." : "An Intel Mac runs llama.cpp on its CPU only.", 100, hardware.Gpus, release));
        }

        if (nvidia.Count > 0)
        {
            var driver = ParseVersion(hardware.Libraries.CudaDriverVersion);
            var cudaAssets = assets.Where(static asset => asset.Backend == "cuda").ToList();
            var oldArchitecture = nvidia.Any(static gpu => gpu.Architecture is { } sm && int.TryParse(sm.AsSpan(3), CultureInfo.InvariantCulture, out var cc) && cc < 75);
            // A driver runs every build of its major CUDA version or older (minor version compatibility).
            var fitting = cudaAssets.Where(asset => driver is not null && ParseVersion(asset.BackendVersion) is { } v && v.Major <= driver.Major)
                .Where(asset => !oldArchitecture || ParseVersion(asset.BackendVersion)?.Major < 13)
                .OrderByDescending(static asset => ParseVersion(asset.BackendVersion)).FirstOrDefault();
            if (driver is null)
                result.Add(new("cuda", null, "driverTooOld", "No NVIDIA driver answered (nvidia-smi): install or update the driver of the GPU.", 0, Ids(nvidia)));
            else if (release is not null && cudaAssets.Count == 0)
                result.Add(new("cuda", null, "notInRelease", $"{release.Tag} has no CUDA build for this system.", 0, Ids(nvidia)));
            else if (release is not null && fitting is null)
                result.Add(new("cuda", null, "driverTooOld", $"The driver supports CUDA {hardware.Libraries.CudaDriverVersion}, older than the CUDA builds of {release.Tag}: update the driver.", 0, Ids(nvidia)));
            else if (hardware.Os == "linux" && glibc is not null && glibc < new Version(2, 38))
                result.Add(new("cuda", fitting?.Name, "systemTooOld", $"The CUDA builds need glibc 2.38; this system has {hardware.Libraries.Glibc}.", 0, Ids(nvidia)));
            else
                result.Add(new("cuda", fitting?.Name, "fits", $"CUDA {fitting?.BackendVersion ?? hardware.Libraries.CudaDriverVersion} on {Names(nvidia)}.", 100, Ids(nvidia)));
        }

        if (amd.Count > 0)
        {
            var targets = hardware.Os == "windows" ? WindowsRocmTargets : LinuxRocmTargets;
            var supported = amd.Where(gpu => gpu.Architecture is { } gfx && targets.Contains(gfx)).ToList();
            var rocm = Asset(assets, "rocm", release);
            if (release is not null && rocm is null)
                result.Add(new("rocm", null, "notInRelease", $"{release.Tag} has no ROCm build for this system.", 0, Ids(amd)));
            else if (supported.Count == 0)
                result.Add(new("rocm", rocm, "unsupportedGpu", amd.Any(static gpu => gpu.Architecture is null)
                    ? $"The gfx of {Names(amd)} is not known here, so the ROCm build is not chosen by itself."
                    : $"The ROCm build has no code for {string.Join(", ", amd.Select(static gpu => gpu.Architecture))}.", 0, Ids(amd)));
            else if (hardware.Os == "linux" && ParseVersion(hardware.Libraries.RocmVersion) is not { Major: >= 7 })
                result.Add(new("rocm", rocm, "libraryMissing", hardware.Libraries.RocmVersion is null
                    ? "The ROCm build needs ROCm 7 on the system (/opt/rocm)."
                    : $"The ROCm build needs ROCm 7; this system has {hardware.Libraries.RocmVersion}.", 0, Ids(supported)));
            else if (hardware.Os == "linux" && glibc is not null && glibc < new Version(2, 38))
                result.Add(new("rocm", rocm, "systemTooOld", $"The ROCm build needs glibc 2.38; this system has {hardware.Libraries.Glibc}.", 0, Ids(supported)));
            else if (!hardware.Libraries.HipBlas)
                result.Add(new("rocm", rocm, "libraryMissing", hardware.Os == "windows"
                    ? "The ROCm build needs hipblas.dll of the AMD HIP SDK: install the HIP SDK."
                    : "The ROCm build needs libhipblas.so.3 of ROCm.", 0, Ids(supported)));
            else
                result.Add(new("rocm", rocm, "fits", $"ROCm {hardware.Libraries.RocmVersion} on {Names(supported)}.", 90, Ids(supported)));
        }

        if (hardware.Os != "macos")
        {
            var vulkan = Asset(assets, "vulkan", release);
            if (release is not null && vulkan is null)
                result.Add(new("vulkan", null, "notInRelease", $"{release.Tag} has no Vulkan build for this system.", 0, Ids(hardware.Gpus)));
            else if (hardware.Gpus.Count == 0)
                result.Add(new("vulkan", vulkan, "noGpu", "No GPU was found.", 0, []));
            else if (!hardware.Libraries.Vulkan)
                result.Add(new("vulkan", vulkan, "libraryMissing", "The Vulkan loader is not there: install the driver of the GPU (or Mesa on Linux).", 0, Ids(hardware.Gpus)));
            else if (hardware.Os == "linux" && glibc is not null && glibc < new Version(2, 34))
                result.Add(new("vulkan", vulkan, "systemTooOld", $"The Vulkan build needs glibc 2.34; this system has {hardware.Libraries.Glibc}.", 0, Ids(hardware.Gpus)));
            else
                result.Add(new("vulkan", vulkan, "fits", $"Vulkan on {Names(discrete.Count > 0 ? discrete : hardware.Gpus)}.", discrete.Count > 0 ? 70 : 30,
                    Ids(discrete.Count > 0 ? discrete : hardware.Gpus)));
        }

        var cpu = Asset(assets, "cpu", release);
        if (hardware.Os != "macos" || hardware.Architecture == "x64")
            result.Add(new("cpu", cpu, release is not null && cpu is null ? "notInRelease" : "fits", "The processor alone: the slowest, for small models.", 0, []));
        return result.OrderByDescending(static candidate => candidate.Verdict == "fits").ThenByDescending(static candidate => candidate.Score).ToList();
    }

    /// <summary>The builds CodeAlta installs on a machine by itself: the best fitting one per family of GPUs, and Vulkan beside ROCm as its fallback.</summary>
    /// <param name="hardware">The machine.</param>
    /// <param name="candidates">The candidates of the machine.</param>
    /// <returns>The backends to install.</returns>
    public static IReadOnlyList<string> Recommended(HardwareInventory hardware, IReadOnlyList<BuildCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(candidates);
        var fits = candidates.Where(static candidate => candidate.Verdict == "fits").Select(static candidate => candidate.Backend).ToHashSet(StringComparer.Ordinal);
        var chosen = new[] { "metal", "cuda", "rocm" }.Where(fits.Contains).ToList();
        // AMD and Intel GPUs without ROCm, and ROCm's own fallback, run on Vulkan.
        var otherDiscrete = hardware.Gpus.Any(static gpu => !gpu.Integrated && gpu.Vendor is "amd" or "intel");
        if (fits.Contains("vulkan") && (otherDiscrete || chosen.Count == 0)) chosen.Add("vulkan");
        if (chosen.Count == 0) chosen.Add("cpu");
        return chosen;
    }

    /// <summary>The archives a backend of a release installs on a machine: the build, and its CUDA runtime for CUDA.</summary>
    /// <param name="release">The release.</param>
    /// <param name="os">The system.</param>
    /// <param name="arch">The architecture.</param>
    /// <param name="backend">The backend.</param>
    /// <param name="candidate">The candidate that chose the archive, or null for the newest of the backend.</param>
    /// <returns>The archives, the build first; empty when the release has none.</returns>
    public static IReadOnlyList<LlamaAsset> AssetsFor(LlamaRelease release, string os, string arch, string backend, BuildCandidate? candidate)
    {
        ArgumentNullException.ThrowIfNull(release);
        var build = release.Assets.FirstOrDefault(asset => asset.Name == candidate?.Asset)
            ?? release.Assets.Where(asset => asset.Os == os && asset.Arch == arch && asset.Backend == backend && !asset.IsCudaRuntime)
                .OrderByDescending(static asset => ParseVersion(asset.BackendVersion)).FirstOrDefault();
        if (build is null) return [];
        if (backend != "cuda") return [build];
        var runtime = release.Assets.FirstOrDefault(asset => asset.IsCudaRuntime && asset.Os == os && asset.Arch == arch && asset.BackendVersion == build.BackendVersion);
        return runtime is null ? [build] : [build, runtime];
    }

    private static string? Asset(List<LlamaAsset> assets, string backend, LlamaRelease? release)
        => assets.Where(asset => asset.Backend == backend).OrderByDescending(static asset => ParseVersion(asset.BackendVersion)).FirstOrDefault()?.Name;

    private static BuildCandidate Candidate(string backend, string? asset, string verdict, string message, int score, IReadOnlyList<GpuInfo> gpus, LlamaRelease? release)
        => new(backend, asset, release is not null && asset is null ? "notInRelease" : verdict, message, score, Ids(gpus));

    private static List<string> Ids(IEnumerable<GpuInfo> gpus) => gpus.Select(static gpu => gpu.Id).ToList();

    private static string Names(IEnumerable<GpuInfo> gpus)
        => string.Join(", ", gpus.GroupBy(static gpu => gpu.Name).Select(static group => group.Count() > 1 ? $"{group.Count()}× {group.Key}" : group.Key));

    /// <summary>Reads a version such as <c>13.4</c> or <c>2.39</c>.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The version, or null.</returns>
    internal static Version? ParseVersion(string? text)
    {
        if (text is null) return null;
        var digits = new string(text.TakeWhile(static c => char.IsAsciiDigit(c) || c == '.').ToArray()).Trim('.');
        if (digits.Length == 0) return null;
        if (!digits.Contains('.', StringComparison.Ordinal)) digits += ".0";
        return Version.TryParse(digits, out var version) ? version : null;
    }
}
