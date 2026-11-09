using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace CodeAlta.ModelServers.Hardware;

/// <summary>Runs a short program and gives its output, or null when it is not there or does not answer in time.</summary>
/// <param name="fileName">The program.</param>
/// <param name="arguments">Its arguments.</param>
/// <param name="cancellationToken">Ends the wait.</param>
/// <returns>What it wrote on its standard output, or null.</returns>
public delegate Task<string?> ProgramRunner(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// Reads what a machine has for running models without any vendor SDK: the system's own records of its GPUs (the
/// registry on Windows, sysfs and the KFD topology on Linux, sysctl on macOS), the tools of the drivers when they are
/// there (<c>nvidia-smi</c>, <c>rocminfo</c>), and the libraries llama.cpp builds need. What it cannot read stays null.
/// </summary>
public sealed partial class HardwareProbe
{
    /// <summary>How long a tool of a driver may take to answer.</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(8);

    private readonly ProgramRunner _run;
    private readonly string _linuxRoot;

    /// <summary>Creates a probe of this machine.</summary>
    public HardwareProbe()
        : this(RunProgramAsync, "/") { }

    /// <summary>Creates a probe on given parts, for tests.</summary>
    /// <param name="run">Runs the tools of the drivers.</param>
    /// <param name="linuxRoot">The root the Linux files are read under (<c>/</c> on a machine).</param>
    public HardwareProbe(ProgramRunner run, string linuxRoot)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(linuxRoot);
        _run = run;
        _linuxRoot = linuxRoot;
    }

    /// <summary>Gets the system of this process: <c>windows</c>, <c>linux</c> or <c>macos</c>.</summary>
    public static string CurrentOs => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>Gets the architecture of this process: <c>x64</c> or <c>arm64</c>.</summary>
    public static string CurrentArchitecture => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    /// <summary>Reads the machine.</summary>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>What it has.</returns>
    public async Task<HardwareInventory> ProbeAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return await ProbeWindowsAsync(cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsMacOS()) return await ProbeMacAsync(cancellationToken).ConfigureAwait(false);
        return await ProbeLinuxAsync(CurrentArchitecture, ReadGlibc(), cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private async Task<HardwareInventory> ProbeWindowsAsync(CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var gpus = new List<GpuInfo>();
        try
        {
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in classKey?.GetSubKeyNames() ?? [])
            {
                if (!name.All(char.IsAsciiDigit)) continue;
                using var adapter = classKey!.OpenSubKey(name);
                if (adapter?.GetValue("DriverDesc") is not string description) continue;
                var deviceId = adapter.GetValue("MatchingDeviceId") as string;
                var pci = ParseWindowsPciId(deviceId);
                // The size of the memory is a QWORD here (or 4 bytes on older drivers), where WMI stops at 4 GB.
                long? memory = adapter.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long value => value,
                    byte[] { Length: 8 } bytes => BitConverter.ToInt64(bytes),
                    byte[] { Length: 4 } bytes => BitConverter.ToUInt32(bytes),
                    int value => (uint)value,
                    _ => null,
                };
                if (pci is null && memory is null) continue; // a virtual display adapter
                var vendor = VendorOf(pci, description);
                if (vendor == "other" && memory is null) continue;
                gpus.Add(new GpuInfo("", vendor, description.Trim(), memory, IsIntegrated(vendor, description, memory), vendor == "amd" ? AmdArchitectureOf(pci) : null, pci));
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            notes.Add($"The display adapters could not be read: {exception.Message}");
        }

        // A driver installed before keeps a record without the device it matched: the same GPU, named again.
        gpus.RemoveAll(gpu => gpu.PciId is null && gpus.Any(other => other.PciId is not null && string.Equals(other.Name, gpu.Name, StringComparison.OrdinalIgnoreCase)));
        var cuda = await ReadNvidiaAsync(gpus, cancellationToken).ConfigureAwait(false);
        var hipPath = Environment.GetEnvironmentVariable("HIP_PATH");
        string? hipVersion = null;
        var hipBlas = false;
        if (hipPath is { Length: > 0 } && Directory.Exists(hipPath))
        {
            hipVersion = Regex.Match(hipPath, @"(\d+\.\d+)").Value is { Length: > 0 } v ? v : null;
            hipBlas = File.Exists(Path.Combine(hipPath, "bin", "hipblas.dll"));
        }
        else
        {
            hipPath = null;
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var vulkan = File.Exists(Path.Combine(system, "vulkan-1.dll"));
        return new HardwareInventory("windows", CurrentArchitecture, ReadWindowsCpuName(), ReadMemory(), Order(gpus),
            new MachineLibraries(cuda, hipVersion, hipPath, hipBlas, vulkan, null), notes);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadWindowsCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    private static long? ReadMemory()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : null;
    }

    /// <summary>Reads a Linux machine under the root of this probe.</summary>
    /// <param name="architecture">Its architecture.</param>
    /// <param name="glibc">Its glibc version, or null.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>What it has.</returns>
    internal async Task<HardwareInventory> ProbeLinuxAsync(string architecture, string? glibc, CancellationToken cancellationToken)
    {
        var notes = new List<string>();
        var gpus = new List<GpuInfo>();
        var drm = Rooted("/sys/class/drm");
        var kfd = ReadKfdArchitectures();
        var rocmNames = await ReadRocmNamesAsync(cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(drm))
        {
            foreach (var card in Directory.EnumerateDirectories(drm, "card*").Where(static path => Regex.IsMatch(Path.GetFileName(path), @"^card\d+$")).Order(StringComparer.Ordinal))
            {
                var device = Path.Combine(card, "device");
                var vendorId = ReadHex(Path.Combine(device, "vendor"));
                var deviceId = ReadHex(Path.Combine(device, "device"));
                if (vendorId is null) continue;
                var pci = $"{vendorId}:{deviceId}";
                var vendor = VendorOf(pci, "");
                long? memory = ReadLong(Path.Combine(device, "mem_info_vram_total"));
                string? architectureName = null;
                if (vendor == "amd" && deviceId is not null && kfd.TryGetValue(deviceId, out var found)) architectureName = found.Dequeue();
                architectureName ??= vendor == "amd" ? AmdArchitectureOf(pci) : null;
                var name = ReadText(Path.Combine(device, "product_name"));
                if (name is null && architectureName is not null && rocmNames.TryGetValue(architectureName, out var names) && names.Count > 0) name = names.Dequeue();
                name ??= vendor switch { "amd" => $"AMD GPU {architectureName ?? pci}", "nvidia" => "NVIDIA GPU", "intel" => "Intel GPU", _ => $"GPU {pci}" };
                gpus.Add(new GpuInfo("", vendor, name, memory, IsIntegrated(vendor, name, memory), architectureName, pci));
            }
        }
        else
        {
            notes.Add("/sys/class/drm is not there: the GPUs come from the tools of their drivers only.");
        }

        var cuda = await ReadNvidiaAsync(gpus, cancellationToken).ConfigureAwait(false);
        var (rocmVersion, rocmPath) = ReadLinuxRocm();
        var libraries = new[] { "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib" };
        var hipBlas = rocmPath is not null && File.Exists(Path.Combine(Rooted(rocmPath), "lib", "libhipblas.so.3"));
        var vulkan = libraries.Any(folder => File.Exists(Path.Combine(Rooted(folder), "libvulkan.so.1")));
        return new HardwareInventory("linux", architecture, ReadLinuxCpuName(), ReadLinuxMemory() ?? ReadMemory(), Order(gpus),
            new MachineLibraries(cuda, rocmVersion, rocmPath, hipBlas, vulkan, glibc), notes);
    }

    private async Task<HardwareInventory> ProbeMacAsync(CancellationToken cancellationToken)
    {
        var cpu = (await _run("sysctl", ["-n", "machdep.cpu.brand_string"], cancellationToken).ConfigureAwait(false))?.Trim();
        var memoryText = (await _run("sysctl", ["-n", "hw.memsize"], cancellationToken).ConfigureAwait(false))?.Trim();
        long? memory = long.TryParse(memoryText, CultureInfo.InvariantCulture, out var bytes) ? bytes : ReadMemory();
        var gpus = new List<GpuInfo>();
        if (CurrentArchitecture == "arm64" && cpu is { Length: > 0 })
        {
            // The GPU of Apple Silicon shares the memory of the machine; Metal can use most of it.
            gpus.Add(new GpuInfo("gpu0", "apple", cpu, memory, true, cpu.Replace("Apple ", "", StringComparison.Ordinal).ToLowerInvariant(), null));
        }

        return new HardwareInventory("macos", CurrentArchitecture, cpu, memory, gpus, new MachineLibraries(null, null, null, false, false, null), []);
    }

    private async Task<string?> ReadNvidiaAsync(List<GpuInfo> gpus, CancellationToken cancellationToken)
    {
        if (!gpus.Any(static gpu => gpu.Vendor == "nvidia") && OperatingSystem.IsWindows()) return null;
        var header = await _run("nvidia-smi", [], cancellationToken).ConfigureAwait(false);
        if (header is null) return null;
        var cuda = CudaVersionOf(header);
        var query = await _run("nvidia-smi", ["--query-gpu=name,memory.total,compute_cap", "--format=csv,noheader,nounits"], cancellationToken).ConfigureAwait(false);
        var index = 0;
        foreach (var line in (query ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3) continue;
            long? memory = long.TryParse(parts[1], CultureInfo.InvariantCulture, out var mib) ? mib * 1024 * 1024 : null;
            var architecture = parts[2].Replace(".", "", StringComparison.Ordinal) is { Length: > 0 } cc && cc.All(char.IsAsciiDigit) ? "sm_" + cc : null;
            // The n-th NVIDIA GPU of the system is the n-th of nvidia-smi, which orders them by PCI bus as both do.
            var known = gpus.Where(static gpu => gpu.Vendor == "nvidia").ElementAtOrDefault(index);
            if (known is null) gpus.Add(new GpuInfo("", "nvidia", parts[0], memory, false, architecture, null));
            else gpus[gpus.IndexOf(known)] = known with { Name = parts[0], MemoryBytes = memory ?? known.MemoryBytes, Architecture = architecture };
            index++;
        }

        return cuda;
    }

    private Dictionary<string, Queue<string>> ReadKfdArchitectures()
    {
        var result = new Dictionary<string, Queue<string>>(StringComparer.OrdinalIgnoreCase);
        var nodes = Rooted("/sys/class/kfd/kfd/topology/nodes");
        if (!Directory.Exists(nodes)) return result;
        foreach (var node in Directory.EnumerateDirectories(nodes).OrderBy(static path => int.TryParse(Path.GetFileName(path), out var n) ? n : int.MaxValue))
        {
            var properties = ReadText(Path.Combine(node, "properties"));
            if (properties is null) continue;
            var values = properties.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Split(' ', 2))
                .Where(static parts => parts.Length == 2)
                .GroupBy(static parts => parts[0])
                .ToDictionary(static group => group.Key, static group => group.First()[1].Trim(), StringComparer.Ordinal);
            if (!values.TryGetValue("gfx_target_version", out var target) || !int.TryParse(target, CultureInfo.InvariantCulture, out var version) || version == 0) continue;
            if (!values.TryGetValue("device_id", out var device) || !int.TryParse(device, CultureInfo.InvariantCulture, out var deviceId)) continue;
            var key = deviceId.ToString("x4", CultureInfo.InvariantCulture);
            if (!result.TryGetValue(key, out var queue)) result[key] = queue = new Queue<string>();
            queue.Enqueue(GfxName(version));
        }

        return result;
    }

    /// <summary>The gfx name of a KFD target version: 120001 is gfx1201, 90010 is gfx90a, 110501 is gfx1151.</summary>
    /// <param name="version">The <c>gfx_target_version</c> of the KFD topology.</param>
    /// <returns>Its gfx name.</returns>
    internal static string GfxName(int version)
    {
        var major = version / 10000;
        var minor = version / 100 % 100;
        var step = version % 100;
        return string.Create(CultureInfo.InvariantCulture, $"gfx{major}{minor}{step:x}");
    }

    private async Task<Dictionary<string, Queue<string>>> ReadRocmNamesAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        var output = await _run("rocminfo", [], cancellationToken).ConfigureAwait(false);
        if (output is null) return result;
        string? name = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Name:", StringComparison.Ordinal)) name = line[5..].Trim();
            else if (line.StartsWith("Marketing Name:", StringComparison.Ordinal) && name is { } gfx && gfx.StartsWith("gfx", StringComparison.Ordinal))
            {
                if (!result.TryGetValue(gfx, out var queue)) result[gfx] = queue = new Queue<string>();
                queue.Enqueue(line[15..].Trim());
            }
        }

        return result;
    }

    private (string? Version, string? Path) ReadLinuxRocm()
    {
        var opt = Rooted("/opt");
        if (!Directory.Exists(opt)) return (null, null);
        var best = Directory.EnumerateDirectories(opt, "rocm*")
            .Select(folder => (Folder: folder, Version: ReadText(Path.Combine(folder, ".info", "version")) ?? Regex.Match(Path.GetFileName(folder), @"\d+(\.\d+)+").Value))
            .Where(static entry => entry.Version is { Length: > 0 })
            .OrderByDescending(static entry => File.Exists(Path.Combine(entry.Folder, "lib", "libamdhip64.so.7")))
            .ThenByDescending(static entry => ParseVersion(entry.Version!))
            .FirstOrDefault();
        if (best.Folder is null) return (null, null);
        var version = best.Version!.Split('-')[0].Trim();
        return (version, "/" + Path.GetRelativePath(_linuxRoot, best.Folder).Replace('\\', '/'));
    }

    private string? ReadLinuxCpuName()
        => ReadText(Rooted("/proc/cpuinfo"))?.Split('\n').FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2) is [_, var name] ? name.Trim() : null;

    private long? ReadLinuxMemory()
        => ReadText(Rooted("/proc/meminfo"))?.Split('\n').FirstOrDefault(static line => line.StartsWith("MemTotal:", StringComparison.Ordinal)) is { } line
            && long.TryParse(Regex.Match(line, @"\d+").Value, CultureInfo.InvariantCulture, out var kb) ? kb * 1024 : null;

    private string Rooted(string path) => Path.Combine(_linuxRoot, path.TrimStart('/'));

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? ReadHex(string path)
        => ReadText(path) is { } text && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..].ToLowerInvariant() : null;

    private static long? ReadLong(string path) => long.TryParse(ReadText(path), CultureInfo.InvariantCulture, out var value) ? value : null;

    private static Version ParseVersion(string text) => Version.TryParse(Regex.Match(text, @"\d+(\.\d+)*").Value, out var version) ? version : new Version(0, 0);

    private static string? ParseWindowsPciId(string? matchingDeviceId)
    {
        if (matchingDeviceId is null) return null;
        var match = Regex.Match(matchingDeviceId, @"VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        return match.Success ? $"{match.Groups[1].Value.ToLowerInvariant()}:{match.Groups[2].Value.ToLowerInvariant()}" : null;
    }

    private static string VendorOf(string? pci, string name)
    {
        var vendor = pci?.Split(':')[0];
        if (vendor == "10de" || name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "nvidia";
        if (vendor == "1002" || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || name.StartsWith("AMD", StringComparison.OrdinalIgnoreCase)) return "amd";
        if (vendor == "8086" || name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "intel";
        return "other";
    }

    // The GPU of a processor has a small carve-out of memory, or a name without a model: "AMD Radeon(TM) Graphics",
    // "Intel(R) UHD Graphics". Only the order of the GPUs depends on it.
    private static bool IsIntegrated(string vendor, string name, long? memory)
        => vendor == "intel" && !name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
           || vendor == "amd" && Regex.IsMatch(name, @"^AMD Radeon(\(TM\))? Graphics$|^AMD GPU gfx(103[56]|11[05]\w)", RegexOptions.IgnoreCase)
           || memory is > 0 and <= 2L * 1024 * 1024 * 1024 && vendor != "nvidia";

    /// <summary>The gfx of an AMD GPU from its PCI ids, for the common cards; null for the others (Windows has no KFD).</summary>
    /// <param name="pci">Its PCI ids, <c>1002:7551</c>.</param>
    /// <returns>Its gfx name, or null.</returns>
    internal static string? AmdArchitectureOf(string? pci) => pci?.Split(':') is [_, var device] ? device switch
    {
        "7550" or "7551" => "gfx1201",
        "7590" => "gfx1200",
        "744c" or "7448" or "745e" => "gfx1100",
        "747e" or "7470" => "gfx1101",
        "7480" or "7483" or "7489" => "gfx1102",
        "73bf" or "73a5" or "73a3" => "gfx1030",
        "73df" => "gfx1031",
        "73ff" or "73ef" => "gfx1032",
        "1586" => "gfx1151",
        "150e" => "gfx1150",
        "164e" => "gfx1036",
        "15bf" or "15c8" => "gfx1103",
        _ => null,
    } : null;

    private static List<GpuInfo> Order(List<GpuInfo> gpus)
        => gpus.OrderBy(static gpu => gpu.Integrated).ThenByDescending(static gpu => gpu.MemoryBytes ?? 0)
            .Select(static (gpu, index) => gpu with { Id = "gpu" + index.ToString(CultureInfo.InvariantCulture) }).ToList();

    private static string? ReadGlibc()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try { return Marshal.PtrToStringUTF8(GnuGetLibcVersion()); }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException) { return null; }
    }

    [LibraryImport("libc", EntryPoint = "gnu_get_libc_version")]
    private static partial nint GnuGetLibcVersion();

    /// <summary>The CUDA version the driver supports, from the header of <c>nvidia-smi</c>: <c>CUDA Version: 13.0</c>, or <c>CUDA UMD Version: 13.3</c> since the 610 drivers.</summary>
    /// <param name="header">What <c>nvidia-smi</c> printed.</param>
    /// <returns>The version, or null.</returns>
    internal static string? CudaVersionOf(string header) => CudaVersionPattern().Match(header) is { Success: true } match ? match.Groups[1].Value : null;

    [GeneratedRegex(@"CUDA (?:UMD |Driver )?Version:\s*(\d+\.\d+)")]
    private static partial Regex CudaVersionPattern();

    /// <summary>Runs a program of the machine with <see cref="ToolTimeout"/>.</summary>
    /// <param name="fileName">The program.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="cancellationToken">Ends the wait.</param>
    /// <returns>Its output, or null when it is missing, fails or does not answer in time.</returns>
    public static async Task<string?> RunProgramAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ToolTimeout);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }

            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
