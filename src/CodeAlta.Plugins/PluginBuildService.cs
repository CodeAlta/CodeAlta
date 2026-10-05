using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes a plugin build request.
/// </summary>
public sealed record PluginBuildRequest
{
    /// <summary>Gets the source plugin package to build.</summary>
    public required SourcePluginPackage Package { get; init; }

    /// <summary>Gets a value indicating whether the build should bypass the up-to-date manifest fast path.</summary>
    public bool ForceRebuild { get; init; }
}

/// <summary>
/// Builds source plugin packages.
/// </summary>
public interface IPluginBuildService
{
    /// <summary>
    /// Builds a source plugin package.
    /// </summary>
    /// <param name="request">The build request.</param>
    /// <param name="cancellationToken">A token to cancel the build.</param>
    /// <returns>The build result.</returns>
    ValueTask<PluginBuildResult> BuildAsync(PluginBuildRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// Describes a diagnostic raised during plugin build.
/// </summary>
public sealed record PluginBuildDiagnostic
{
    /// <summary>Gets the diagnostic severity.</summary>
    public required PluginDiagnosticSeverity Severity { get; init; }

    /// <summary>Gets the diagnostic code, when available.</summary>
    public string? Code { get; init; }

    /// <summary>Gets the diagnostic message.</summary>
    public required string Message { get; init; }

    /// <summary>Gets the source file associated with the diagnostic, when available.</summary>
    public string? File { get; init; }

    /// <summary>Gets the 1-based line number, when available.</summary>
    public int LineNumber { get; init; }

    /// <summary>Gets the 1-based column number, when available.</summary>
    public int ColumnNumber { get; init; }

    /// <summary>Gets the project file associated with the diagnostic, when available.</summary>
    public string? ProjectFile { get; init; }
}

/// <summary>
/// Describes one target output item discovered during plugin build.
/// </summary>
public sealed record PluginBuildTargetOutput
{
    /// <summary>Gets the target name.</summary>
    public required string TargetName { get; init; }

    /// <summary>Gets the task item item-spec.</summary>
    public required string ItemSpec { get; init; }

    /// <summary>Gets the task item metadata.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Describes a plugin build result.
/// </summary>
public sealed record PluginBuildResult
{
    /// <summary>Gets the source plugin package.</summary>
    public required SourcePluginPackage Package { get; init; }

    /// <summary>Gets a value indicating whether the plugin was skipped because it was up to date.</summary>
    public bool IsUpToDate { get; init; }

    /// <summary>Gets a value indicating whether the build succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets the process exit code, when a build process was started.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets the output assembly path, when resolved from build output or the manifest fast path.</summary>
    public string? OutputAssemblyPath { get; init; }

    /// <summary>Gets build diagnostics.</summary>
    public IReadOnlyList<PluginBuildDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets discovered target outputs.</summary>
    public IReadOnlyList<PluginBuildTargetOutput> TargetOutputs { get; init; } = [];

    /// <summary>Gets stdout captured for troubleshooting only.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Gets stderr captured for troubleshooting only.</summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>Gets runtime diagnostics raised by build orchestration.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> RuntimeDiagnostics { get; init; } = [];

    /// <summary>
    /// Creates a structured build summary suitable for plugin status descriptors and management UI.
    /// </summary>
    /// <returns>The build summary.</returns>
    public PluginBuildSummary CreateSummary()
        => new()
        {
            PackageId = Package.PackageId,
            Succeeded = Succeeded,
            IsUpToDate = IsUpToDate,
            ExitCode = ExitCode,
            OutputAssemblyPath = OutputAssemblyPath,
            DiagnosticCount = Diagnostics.Count,
            WarningCount = Diagnostics.Count(static diagnostic => diagnostic.Severity == PluginDiagnosticSeverity.Warning),
            ErrorCount = Diagnostics.Count(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error) +
                RuntimeDiagnostics.Count(static diagnostic => diagnostic.Severity >= PluginDiagnosticSeverity.Error),
            RuntimeDiagnostics = RuntimeDiagnostics,
            BuildDiagnostics = Diagnostics,
            StandardOutputTail = GetTail(StandardOutput),
            StandardErrorTail = GetTail(StandardError),
        };

    private static string GetTail(string text, int maximumLength = 4096)
        => text.Length <= maximumLength ? text : text[^maximumLength..];
}

/// <summary>
/// Describes the structured diagnostic/log summary from the last plugin build.
/// </summary>
public sealed record PluginBuildSummary
{
    /// <summary>Gets the package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>Gets a value indicating whether the build succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets a value indicating whether the build used the up-to-date fast path.</summary>
    public bool IsUpToDate { get; init; }

    /// <summary>Gets the process exit code, when a process ran.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets the output assembly path, when known.</summary>
    public string? OutputAssemblyPath { get; init; }

    /// <summary>Gets the build diagnostic count.</summary>
    public int DiagnosticCount { get; init; }

    /// <summary>Gets the warning count.</summary>
    public int WarningCount { get; init; }

    /// <summary>Gets the error count including runtime diagnostics.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Gets runtime diagnostics from build orchestration.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> RuntimeDiagnostics { get; init; } = [];

    /// <summary>Gets build diagnostics.</summary>
    public IReadOnlyList<PluginBuildDiagnostic> BuildDiagnostics { get; init; } = [];

    /// <summary>Gets stdout tail for troubleshooting only.</summary>
    public string StandardOutputTail { get; init; } = string.Empty;

    /// <summary>Gets stderr tail for troubleshooting only.</summary>
    public string StandardErrorTail { get; init; } = string.Empty;
}

/// <summary>
/// Runs <c>dotnet build plugin.cs</c> for source plugin packages and resolves the output from CodeAlta-generated build output.
/// </summary>
public sealed class PluginBuildService : IPluginBuildService
{
    private readonly PluginBuildManifestStore? _manifestStore;
    private readonly PluginBuildLockService? _buildLockService;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginBuildService"/> class.
    /// </summary>
    /// <param name="manifestStore">The optional manifest store used for fast-path checks.</param>
    /// <param name="buildLockService">The optional per-package build lock service.</param>
    public PluginBuildService(PluginBuildManifestStore? manifestStore = null, PluginBuildLockService? buildLockService = null)
    {
        _manifestStore = manifestStore;
        _buildLockService = buildLockService;
    }

    /// <summary>
    /// Builds a source plugin package.
    /// </summary>
    /// <param name="request">The build request.</param>
    /// <param name="cancellationToken">A token to cancel the build.</param>
    /// <returns>The build result.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is <see langword="null"/>.</exception>
    public async ValueTask<PluginBuildResult> BuildAsync(PluginBuildRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var package = request.Package ?? throw new ArgumentException("The build request package is required.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.ForceRebuild && _manifestStore is not null)
        {
            var manifestResult = await _manifestStore.TryGetUpToDateManifestAsync(package, cancellationToken).ConfigureAwait(false);
            if (manifestResult.IsUpToDate)
            {
                return new PluginBuildResult
                {
                    Package = package,
                    IsUpToDate = true,
                    Succeeded = true,
                    OutputAssemblyPath = manifestResult.Manifest!.OutputAssemblyPath,
                };
            }
        }

        await using var packageBuildLock = _buildLockService is null
            ? NoOpAsyncDisposable.Instance
            : await _buildLockService.AcquireAsync(package, cancellationToken).ConfigureAwait(false);

        if (!request.ForceRebuild && _manifestStore is not null)
        {
            var manifestResult = await _manifestStore.TryGetUpToDateManifestAsync(package, cancellationToken).ConfigureAwait(false);
            if (manifestResult.IsUpToDate)
            {
                return new PluginBuildResult
                {
                    Package = package,
                    IsUpToDate = true,
                    Succeeded = true,
                    OutputAssemblyPath = manifestResult.Manifest!.OutputAssemblyPath,
                };
            }
        }

        var result = await RunBuildProcessAsync(package, CreateFileBuildStartInfo(package), cancellationToken).ConfigureAwait(false);

        if (result.Succeeded && _manifestStore is not null)
        {
            await _manifestStore.WriteManifestAsync(result, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    private static async ValueTask<PluginBuildResult> RunBuildProcessAsync(
        SourcePluginPackage package,
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<PluginBuildDiagnostic>();
        var targetOutputs = new List<PluginBuildTargetOutput>();
        var runtimeDiagnostics = new List<PluginRuntimeDiagnostic>
        {
            PluginRuntimeDiagnostic.Info(
            PluginRuntimeDiagnosticSource.Build,
            "Plugin build started.",
            package.PackageId,
            package.EntryFilePath),
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();
        int? exitCode = null;
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                outputBuilder.AppendLine(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                errorBuilder.AppendLine(args.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            exitCode = process.ExitCode;
            runtimeDiagnostics.Add(new PluginRuntimeDiagnostic
            {
                Severity = exitCode == 0 ? PluginDiagnosticSeverity.Info : PluginDiagnosticSeverity.Error,
                Source = PluginRuntimeDiagnosticSource.Build,
                Message = exitCode == 0 ? "Plugin build finished."
                    : FindFirstBuildError(outputBuilder.ToString()) is { } firstError ? $"Plugin build failed: {firstError}" : "Plugin build failed.",
                PackageId = package.PackageId,
                Path = package.EntryFilePath,
            });
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            runtimeDiagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Build,
                $"Failed to start plugin build: {ex.Message}",
                package.PackageId,
                package.EntryFilePath,
                ex));
            return new PluginBuildResult
            {
                Package = package,
                Succeeded = false,
                RuntimeDiagnostics = runtimeDiagnostics,
                StandardOutput = outputBuilder.ToString(),
                StandardError = errorBuilder.ToString(),
            };
        }
        finally
        {
            process.Dispose();
        }

        var standardOutput = outputBuilder.ToString();
        var standardError = errorBuilder.ToString();
        AddTargetPathMessages(targetOutputs, standardOutput);
        if (exitCode != 0 && LooksLikeFileBasedBuildIsUnsupported(standardOutput, standardError))
        {
            runtimeDiagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Build,
                "The selected .NET SDK did not recognize `dotnet build plugin.cs` as a file-based C# build. Ensure the plugin root contains CodeAlta's generated global.json selecting a supported .NET 10 SDK, install a supported SDK if needed, or start CodeAlta with --plugin-safe-mode / --no-plugins to bypass dynamic source plugins.",
                package.PackageId,
                package.EntryFilePath));
        }

        var outputAssemblyPath = PluginBuildOutputResolver.ResolveOutputAssembly(targetOutputs, package.Root.RootPath);
        if (exitCode == 0 && outputAssemblyPath is null)
        {
            runtimeDiagnostics.Add(PluginRuntimeDiagnostic.Error(
                PluginRuntimeDiagnosticSource.Build,
                "Plugin build succeeded, but the output assembly could not be resolved from the CodeAltaPluginTargetPath build output.",
                package.PackageId,
                package.EntryFilePath));
        }

        var succeeded = exitCode == 0 && outputAssemblyPath is not null;
        return new PluginBuildResult
        {
            Package = package,
            Succeeded = succeeded,
            ExitCode = exitCode,
            OutputAssemblyPath = outputAssemblyPath,
            Diagnostics = diagnostics,
            TargetOutputs = targetOutputs,
            RuntimeDiagnostics = runtimeDiagnostics,
            StandardOutput = standardOutput,
            StandardError = standardError,
        };
    }

    internal static ProcessStartInfo CreateFileBuildStartInfo(SourcePluginPackage package)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = package.PackageDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // A host without a console (the desktop) must not get a console window for the build.
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(Path.GetRelativePath(package.PackageDirectory, package.EntryFilePath));
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("-v:minimal");
        return startInfo;
    }

    private const string TargetPathMessagePrefix = "CodeAltaPluginTargetPath=";

    private static void AddTargetPathMessages(List<PluginBuildTargetOutput> targetOutputs, string standardOutput)
    {
        using var reader = new StringReader(standardOutput);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(TargetPathMessagePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            targetOutputs.Add(new PluginBuildTargetOutput
            {
                TargetName = "CodeAltaPluginTargetPath",
                ItemSpec = trimmed[TargetPathMessagePrefix.Length..],
            });
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// Finds the first compiler or MSBuild error in build output, as <c>plugin.cs(6,27): error CS0103: …</c>:
    /// the file without its folder, and without the project that MSBuild appends.
    /// </summary>
    /// <param name="output">The standard output of the build.</param>
    /// <returns>The error line, or null when the output has none.</returns>
    internal static string? FindFirstBuildError(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var at = line.IndexOf(": error ", StringComparison.Ordinal);
            if (at <= 0) continue;
            if (line.EndsWith(']') && line.LastIndexOf(" [", StringComparison.Ordinal) is var project && project > at) line = line[..project];
            var location = line[..at];
            var separator = location.LastIndexOfAny(['\\', '/']);
            return string.Concat(location.AsSpan(separator + 1), line.AsSpan(at));
        }

        return null;
    }

    private static bool LooksLikeFileBasedBuildIsUnsupported(string standardOutput, string standardError)
    {
        var text = standardOutput + standardError;
        return text.Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("Data at the root level is invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// Resolves plugin output assemblies from CodeAlta-generated target output messages.
/// </summary>
public static class PluginBuildOutputResolver
{
    /// <summary>
    /// Resolves a single plugin output assembly from build target outputs.
    /// </summary>
    /// <param name="targetOutputs">Target outputs captured from the deterministic <c>CodeAltaPluginTargetPath</c> build message.</param>
    /// <param name="pluginRoot">The plugin root used to resolve relative item specs.</param>
    /// <returns>The single resolved output assembly path, or <see langword="null"/> when none or multiple assemblies are found.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="targetOutputs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="pluginRoot"/> is empty.</exception>
    public static string? ResolveOutputAssembly(IReadOnlyList<PluginBuildTargetOutput> targetOutputs, string pluginRoot)
    {
        ArgumentNullException.ThrowIfNull(targetOutputs);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginRoot);
        var buildOutputs = ResolveOutputs(targetOutputs, "Build", pluginRoot);
        if (buildOutputs.Length == 1)
        {
            return buildOutputs[0];
        }

        if (buildOutputs.Length > 1)
        {
            return null;
        }

        var fallbackOutputs = ResolveOutputs(targetOutputs, "CodeAltaPluginTargetPath", pluginRoot);
        return fallbackOutputs.Length == 1 ? fallbackOutputs[0] : null;
    }

    private static string[] ResolveOutputs(IReadOnlyList<PluginBuildTargetOutput> targetOutputs, string targetName, string pluginRoot)
        => targetOutputs
            .Where(output => string.Equals(output.TargetName, targetName, StringComparison.OrdinalIgnoreCase))
            .Select(output => ResolvePath(output.ItemSpec, pluginRoot))
            .Where(static path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            .Distinct(GetPathComparer())
            .ToArray();

    private static string ResolvePath(string path, string basePath)
        => Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(basePath, path));

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// Provides cooperative per-package build locks to avoid duplicate concurrent plugin builds across CodeAlta processes.
/// </summary>
public sealed class PluginBuildLockService
{
    private readonly string _lockRoot;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginBuildLockService"/> class.
    /// </summary>
    /// <param name="lockRoot">The CodeAlta-owned lock root.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="lockRoot"/> is empty.</exception>
    public PluginBuildLockService(string lockRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockRoot);
        _lockRoot = PluginRuntimePathService.NormalizeDirectory(lockRoot);
    }

    /// <summary>
    /// Acquires a package build lock, waiting until competing processes release it or cancellation is requested.
    /// </summary>
    /// <param name="package">The package to lock.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async disposable lock handle.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="package"/> is <see langword="null"/>.</exception>
    public async ValueTask<IAsyncDisposable> AcquireAsync(SourcePluginPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var path = GetLockPath(package);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
                var marker = Encoding.UTF8.GetBytes($"{Environment.ProcessId}:{DateTimeOffset.UtcNow:O}");
                await stream.WriteAsync(marker, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Position = 0;
                return new FileStreamAsyncDisposable(stream);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Gets the lock path for a package.
    /// </summary>
    /// <param name="package">The package.</param>
    /// <returns>The lock path.</returns>
    public string GetLockPath(SourcePluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var scope = package.Root.Scope.ToString().ToLowerInvariant();
        var rootHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(package.Root.RootPath))).ToLowerInvariant();
        return Path.Combine(_lockRoot, "plugins", "build-locks", scope, rootHash, SanitizeFileName(package.PackageId) + ".lock");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }
}

internal sealed class FileStreamAsyncDisposable : IAsyncDisposable
{
    private readonly FileStream _stream;

    public FileStreamAsyncDisposable(FileStream stream)
        => _stream = stream;

    public async ValueTask DisposeAsync()
        => await _stream.DisposeAsync().ConfigureAwait(false);
}

internal sealed class NoOpAsyncDisposable : IAsyncDisposable
{
    public static NoOpAsyncDisposable Instance { get; } = new();

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;
}

/// <summary>
/// Describes a plugin build manifest persisted for fast up-to-date checks.
/// </summary>
public sealed record PluginBuildManifest
{
    /// <summary>Gets the manifest schema version.</summary>
    public int Version { get; init; } = 1;

    /// <summary>Gets the package id.</summary>
    public required string PackageId { get; init; }

    /// <summary>Gets the plugin scope.</summary>
    public required PluginScope Scope { get; init; }

    /// <summary>Gets the entry source file path.</summary>
    public required string EntryFilePath { get; init; }

    /// <summary>Gets the output assembly path.</summary>
    public required string OutputAssemblyPath { get; init; }

    /// <summary>Gets the target framework.</summary>
    public string TargetFramework { get; init; } = "net10.0";

    /// <summary>Gets the CodeAlta build identity.</summary>
    public required string CodeAltaBuildIdentity { get; init; }

    /// <summary>Gets the selected SDK identity.</summary>
    public required string SdkIdentity { get; init; }

    /// <summary>Gets source input hashes.</summary>
    public IReadOnlyDictionary<string, string> SourceInputHashes { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets direct file-based package directives observed in plugin source inputs.</summary>
    public IReadOnlyList<string> PackageDirectives { get; init; } = [];

    /// <summary>Gets generated build-file hashes.</summary>
    public IReadOnlyDictionary<string, string> GeneratedFileHashes { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the manifest creation timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Describes an up-to-date manifest lookup result.
/// </summary>
public sealed record PluginBuildManifestLookupResult
{
    /// <summary>Gets a value indicating whether the manifest is up to date.</summary>
    public bool IsUpToDate { get; init; }

    /// <summary>Gets the manifest, when one was found and read.</summary>
    public PluginBuildManifest? Manifest { get; init; }

    /// <summary>Gets the reason the manifest was not up to date.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Stores plugin build manifests used by the build fast path.
/// </summary>
public sealed class PluginBuildManifestStore
{
    private readonly string _cacheRoot;
    private readonly string _codeAltaBuildIdentity;
    private readonly string _sdkIdentity;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginBuildManifestStore"/> class.
    /// </summary>
    /// <param name="cacheRoot">The CodeAlta-owned cache root.</param>
    /// <param name="codeAltaBuildIdentity">The CodeAlta build identity.</param>
    /// <param name="sdkIdentity">The selected SDK identity.</param>
    /// <exception cref="ArgumentException">Thrown when an argument is empty.</exception>
    public PluginBuildManifestStore(string cacheRoot, string codeAltaBuildIdentity, string sdkIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeAltaBuildIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(sdkIdentity);
        _cacheRoot = PluginRuntimePathService.NormalizeDirectory(cacheRoot);
        _codeAltaBuildIdentity = codeAltaBuildIdentity;
        _sdkIdentity = sdkIdentity;
    }

    /// <summary>
    /// Attempts to read an up-to-date manifest for a source plugin package.
    /// </summary>
    /// <param name="package">The source plugin package.</param>
    /// <param name="cancellationToken">A token to cancel I/O.</param>
    /// <returns>The lookup result.</returns>
    public async ValueTask<PluginBuildManifestLookupResult> TryGetUpToDateManifestAsync(SourcePluginPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var path = GetManifestPath(package);
        if (!File.Exists(path))
        {
            return new PluginBuildManifestLookupResult { Reason = "Manifest does not exist." };
        }

        var manifest = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), PluginRuntimeJsonContext.Default.PluginBuildManifest);
        if (manifest is null)
        {
            return new PluginBuildManifestLookupResult { Reason = "Manifest could not be read." };
        }

        if (!File.Exists(manifest.OutputAssemblyPath))
        {
            return new PluginBuildManifestLookupResult { Manifest = manifest, Reason = "Output assembly does not exist." };
        }

        if (!string.Equals(manifest.CodeAltaBuildIdentity, _codeAltaBuildIdentity, StringComparison.Ordinal) ||
            !string.Equals(manifest.SdkIdentity, _sdkIdentity, StringComparison.Ordinal))
        {
            return new PluginBuildManifestLookupResult { Manifest = manifest, Reason = "Build identity changed." };
        }

        var currentSourceHashes = ComputeSourceInputHashes(package);
        if (!DictionaryEquals(manifest.SourceInputHashes, currentSourceHashes))
        {
            return new PluginBuildManifestLookupResult { Manifest = manifest, Reason = "Source inputs changed." };
        }

        var currentGeneratedHashes = PluginRootBuildFileGenerator.ComputeGeneratedFileHashes(package.Root.RootPath);
        if (!DictionaryEquals(manifest.GeneratedFileHashes, currentGeneratedHashes))
        {
            return new PluginBuildManifestLookupResult { Manifest = manifest, Reason = "Generated build files changed." };
        }

        return new PluginBuildManifestLookupResult
        {
            IsUpToDate = true,
            Manifest = manifest,
        };
    }

    /// <summary>
    /// Writes a manifest from a successful plugin build result.
    /// </summary>
    /// <param name="result">The successful build result.</param>
    /// <param name="cancellationToken">A token to cancel I/O.</param>
    /// <returns>A task representing asynchronous manifest persistence.</returns>
    public async ValueTask WriteManifestAsync(PluginBuildResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.OutputAssemblyPath))
        {
            throw new ArgumentException("Only successful plugin build results with an output assembly can be persisted.", nameof(result));
        }

        var manifest = new PluginBuildManifest
        {
            PackageId = result.Package.PackageId,
            Scope = result.Package.Root.Scope,
            EntryFilePath = result.Package.EntryFilePath,
            OutputAssemblyPath = result.OutputAssemblyPath!,
            CodeAltaBuildIdentity = _codeAltaBuildIdentity,
            SdkIdentity = _sdkIdentity,
            SourceInputHashes = ComputeSourceInputHashes(result.Package),
            PackageDirectives = DiscoverPackageDirectives(result.Package),
            GeneratedFileHashes = PluginRootBuildFileGenerator.ComputeGeneratedFileHashes(result.Package.Root.RootPath),
        };

        var path = GetManifestPath(result.Package);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, PluginRuntimeJsonContext.Default.PluginBuildManifest), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets the manifest path for a source plugin package.
    /// </summary>
    /// <param name="package">The package.</param>
    /// <returns>The manifest path.</returns>
    public string GetManifestPath(SourcePluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var scope = package.Root.Scope.ToString().ToLowerInvariant();
        var rootHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(package.Root.RootPath))).ToLowerInvariant();
        return Path.Combine(_cacheRoot, "plugins", "build", scope, rootHash, package.PackageId, "manifest.json");
    }

    private static IReadOnlyDictionary<string, string> ComputeSourceInputHashes(SourcePluginPackage package)
    {
        var hashes = new Dictionary<string, string>(GetPathComparer());
        foreach (var sourcePath in DiscoverSourceInputs(package))
        {
            hashes[Path.GetRelativePath(package.PackageDirectory, sourcePath)] = HashFile(sourcePath);
        }

        return hashes;
    }

    private static IReadOnlyList<string> DiscoverPackageDirectives(SourcePluginPackage package)
        => DiscoverSourceInputs(package)
            .SelectMany(File.ReadLines)
            .Select(static line => line.Trim())
            .Where(static line => line.StartsWith("#:package ", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static directive => directive, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<string> DiscoverSourceInputs(SourcePluginPackage package)
    {
        var sourcePaths = new List<string>();
        var seenPaths = new HashSet<string>(GetPathComparer());
        Add(package.EntryFilePath);
        return sourcePaths;

        void Add(string sourcePath)
        {
            var fullPath = Path.GetFullPath(sourcePath);
            if (!seenPaths.Add(fullPath) || !File.Exists(fullPath))
            {
                return;
            }

            sourcePaths.Add(fullPath);
            foreach (var includePath in ReadIncludeDirectives(fullPath))
            {
                Add(Path.IsPathRooted(includePath)
                    ? includePath
                    : Path.Combine(Path.GetDirectoryName(fullPath)!, includePath));
            }
        }
    }

    private static IEnumerable<string> ReadIncludeDirectives(string sourcePath)
    {
        foreach (var line in File.ReadLines(sourcePath))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("#:include ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var includePath = trimmed["#:include ".Length..].Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(includePath))
            {
                yield return includePath;
            }
        }
    }

    private static string HashFile(string path)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static bool DictionaryEquals(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static StringComparer GetPathComparer()
        => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

[JsonSerializable(typeof(PluginBuildManifest))]
internal sealed partial class PluginRuntimeJsonContext : JsonSerializerContext
{
}
