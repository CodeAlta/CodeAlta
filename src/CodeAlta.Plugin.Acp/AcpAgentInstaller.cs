using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Acp;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Acp;

/// <summary>What starts an agent installed from the registry.</summary>
/// <param name="Command">The executable: a full path, or a package runner on <c>PATH</c>.</param>
/// <param name="Arguments">Its arguments.</param>
/// <param name="Environment">The environment variables the registry sets for it.</param>
/// <param name="Distribution">How it runs: <c>binary</c>, <c>npx</c> or <c>uvx</c>.</param>
internal sealed record AcpInstalledAgent(string Command, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string Distribution);

/// <summary>The marker of a folder an agent was installed in: it is complete once the marker is written.</summary>
internal sealed class AcpInstallMarker
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

/// <summary>
/// Installs the agents of the ACP registry: a binary is downloaded into the cache shared by the normal and the
/// developer instance (<c>~/.alta/cache/acp/agents/&lt;id&gt;/&lt;version&gt;/&lt;target&gt;</c>); an agent that runs
/// through <c>npx</c> or <c>uvx</c> needs nothing installed, the runner fetches it at its first start.
/// </summary>
internal sealed class AcpAgentInstaller(string root, HttpClient httpClient)
{
    private const string MarkerFile = "acp-install.json";

    /// <summary>Gets what an entry of the registry runs with on this computer, or why it cannot run here.</summary>
    public static (PluginProviderCatalogInstallKind Kind, string Distribution, string? Unavailable) Describe(AcpRegistryAgentManifest manifest)
    {
        string? target;
        try
        {
            target = AcpInstallResolver.GetCurrentTargetId();
        }
        catch (PlatformNotSupportedException)
        {
            target = null;
        }

        if (target is not null && manifest.Distribution.Binary?.ContainsKey(target) == true)
        {
            return (PluginProviderCatalogInstallKind.Download, "binary", null);
        }

        if (manifest.Distribution.Npx is not null)
        {
            return (PluginProviderCatalogInstallKind.PackageRunner, "npx",
                AcpCommandLocator.FindCommandPath("npx") is null ? "Needs npx: install Node.js." : null);
        }

        if (manifest.Distribution.Uvx is not null)
        {
            return (PluginProviderCatalogInstallKind.PackageRunner, "uvx",
                AcpCommandLocator.FindCommandPath("uvx") is null ? "Needs uvx: install uv." : null);
        }

        return (PluginProviderCatalogInstallKind.Download, "binary", "Not available for this system.");
    }

    /// <summary>Installs an agent, or finds it installed, and returns what starts it.</summary>
    /// <exception cref="InvalidOperationException">The agent cannot be installed on this computer, or the download failed.</exception>
    public async Task<AcpInstalledAgent> InstallAsync(AcpRegistryAgentManifest manifest, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(progress);
        var (kind, distribution, unavailable) = Describe(manifest);
        if (unavailable is not null)
        {
            throw new InvalidOperationException($"{manifest.Name} cannot be added: {unavailable}");
        }

        if (kind == PluginProviderCatalogInstallKind.PackageRunner)
        {
            var package = distribution == "npx" ? manifest.Distribution.Npx! : manifest.Distribution.Uvx!;
            List<string> arguments = distribution == "npx" ? ["--yes", package.Package] : [package.Package];
            arguments.AddRange(package.Arguments ?? []);
            progress.Report(new PluginProviderInstallProgress($"{manifest.Name} runs through {distribution}: it is fetched at its first start."));
            return new AcpInstalledAgent(distribution, arguments, package.EnvironmentVariables ?? new Dictionary<string, string>(), distribution);
        }

        var target = AcpInstallResolver.GetCurrentTargetId();
        var binary = manifest.Distribution.Binary![target];
        var folder = Path.Combine(root, Sanitize(manifest.Id), Sanitize(manifest.Version), target);
        var command = ResolveCommand(folder, binary.Command);
        if (File.Exists(Path.Combine(folder, MarkerFile)) && File.Exists(command))
        {
            return new AcpInstalledAgent(command, binary.Arguments ?? [], binary.EnvironmentVariables ?? new Dictionary<string, string>(), "binary");
        }

        var partial = folder + ".partial";
        Directory.CreateDirectory(partial);
        FileStream? lockFile;
        try
        {
            lockFile = new FileStream(Path.Combine(partial, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException($"{manifest.Name} is being installed by another instance of CodeAlta: try again once it is done.");
        }

        using (lockFile)
        {
            var archiveUri = new Uri(binary.Archive, UriKind.Absolute);
            var archive = Path.Combine(partial, Path.GetFileName(archiveUri.LocalPath) is { Length: > 0 } name ? name : "agent.download");
            await DownloadAsync(archiveUri, archive, manifest.Name, progress, cancellationToken).ConfigureAwait(false);
            progress.Report(new PluginProviderInstallProgress($"Extracting {manifest.Name}…"));
            var content = Path.Combine(partial, "content");
            if (Directory.Exists(content))
            {
                Directory.Delete(content, recursive: true);
            }

            Directory.CreateDirectory(content);
            await ExtractAsync(archive, content, binary.Command, cancellationToken).ConfigureAwait(false);
            var extracted = ResolveCommand(content, binary.Command);
            if (!File.Exists(extracted))
            {
                throw new InvalidOperationException($"The archive of {manifest.Name} has no '{binary.Command}'.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(extracted, File.GetUnixFileMode(extracted) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            File.WriteAllBytes(Path.Combine(content, MarkerFile), JsonSerializer.SerializeToUtf8Bytes(
                new AcpInstallMarker { Id = manifest.Id, Version = manifest.Version, Command = binary.Command },
                AcpPluginJsonContext.Default.AcpInstallMarker));
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
            Directory.Move(content, folder);
        }

        try
        {
            Directory.Delete(partial, recursive: true);
        }
        catch (IOException)
        {
            // Removed with the next installation.
        }

        return new AcpInstalledAgent(command, binary.Arguments ?? [], binary.EnvironmentVariables ?? new Dictionary<string, string>(), "binary");
    }

    private async Task DownloadAsync(Uri uri, string path, string name, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(path);
            var buffer = new byte[81920];
            long received = 0;
            var reported = DateTimeOffset.MinValue;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (DateTimeOffset.UtcNow - reported > TimeSpan.FromMilliseconds(150))
                {
                    reported = DateTimeOffset.UtcNow;
                    progress.Report(new PluginProviderInstallProgress($"Downloading {name}…", received, total));
                }
            }

            progress.Report(new PluginProviderInstallProgress($"Downloading {name}…", received, total ?? received));
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"{name} could not be downloaded: {ex.Message}", ex);
        }
    }

    private static async Task ExtractAsync(string archive, string destination, string command, CancellationToken cancellationToken)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination, overwriteFiles: true);
            return;
        }

        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archive.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (archive.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
        {
            await TarFile.ExtractToDirectoryAsync(archive, destination, overwriteFiles: true, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Not an archive: the download is the executable itself.
        var target = ResolveCommand(destination, command);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(archive, target, overwrite: true);
    }

    private static string ResolveCommand(string folder, string command)
    {
        var relative = command.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        while (relative.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            relative = relative[2..];
        }

        var path = Path.GetFullPath(Path.Combine(folder, relative));
        if (!path.StartsWith(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The command '{command}' of the registry is outside the folder of the agent.");
        }

        return path;
    }

    private static string Sanitize(string value)
        => new([.. value.Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '_')]);
}
