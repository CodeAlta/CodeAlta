using System.Runtime.InteropServices;
using System.Text.Json;
using CodeAlta.Acp;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

/// <summary>What CodeAlta installs for an agent of the ACP registry.</summary>
/// <param name="Manifest">The entry of the agent, with the version and the distribution that are installed.</param>
/// <param name="Sha512">The SHA-512 of the download in hexadecimal, or <see langword="null"/> when its source gives none.</param>
internal sealed record AcpAgentRelease(AcpRegistryAgentManifest Manifest, string? Sha512 = null);

/// <summary>
/// The Antigravity CLI (<c>agy</c>) as Google releases it, which CodeAlta installs for the agent
/// <c>antigravity-acp</c> in the place of the server of the ACP registry (<c>agy_acp_server</c>): its ACP server
/// (<c>agy acp</c>) starts a session in a second where the other takes eight, and reports the thoughts of the model.
/// The release is read from the manifest the install scripts of Google read
/// (<c>https://antigravity.google/cli/install.ps1</c>): one per platform, with a version, an address and a SHA-512.
/// </summary>
internal sealed class AntigravityCliReleases(HttpClient httpClient, Logger? logger = null)
{
    /// <summary>The id of the agent in the ACP registry.</summary>
    public const string AgentId = "antigravity-acp";

    private const string ManifestRoot = "https://antigravity-cli-auto-updater-974169037036.us-central1.run.app/manifests/";
    private static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Version, string Url, string Sha512)? _release;
    private DateTimeOffset _readAt;

    /// <summary>Gets whether an entry of the registry is the agent this class has the release of.</summary>
    public static bool Serves(AcpRegistryAgentManifest entry) => string.Equals(entry.Id, AgentId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the release of the CLI for this computer as an entry of the registry: the entry of the agent with the
    /// version of the CLI and its download; <see langword="null"/> when the manifest cannot be read or has nothing for
    /// this system.
    /// </summary>
    /// <param name="entry">The entry of the agent in the registry.</param>
    /// <param name="refresh">Whether the manifest is read again even when it was read lately.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    public async Task<AcpAgentRelease?> FindAsync(AcpRegistryAgentManifest entry, bool refresh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (GetPlatform() is not { } platform)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (refresh || _release is null || DateTimeOffset.UtcNow - _readAt > FreshFor)
            {
                if (await ReadAsync(platform, cancellationToken).ConfigureAwait(false) is { } read)
                {
                    _release = read;
                    _readAt = DateTimeOffset.UtcNow;
                }
                else if (refresh)
                {
                    _release = null;
                }
            }

            return _release is { } release ? Create(entry, release.Version, release.Url, release.Sha512) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the name of this system in the manifests of the CLI, such as <c>windows_amd64</c>, or <see langword="null"/> for a system it has no build for.</summary>
    internal static string? GetPlatform()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (architecture is null)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return "windows_" + architecture;
        }

        if (OperatingSystem.IsMacOS())
        {
            return "darwin_" + architecture;
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        return RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
            ? $"linux_{architecture}_musl"
            : "linux_" + architecture;
    }

    // The entry of the registry with what is installed: the download is the executable itself on Windows, and an
    // archive that holds it under the name `antigravity` elsewhere.
    private static AcpAgentRelease Create(AcpRegistryAgentManifest entry, string version, string url, string sha512)
        => new(
            new AcpRegistryAgentManifest
            {
                Id = entry.Id,
                Name = entry.Name,
                Version = version,
                Description = entry.Description,
                Repository = entry.Repository,
                Website = entry.Website,
                Authors = entry.Authors,
                License = entry.License,
                Icon = entry.Icon,
                Distribution = new AcpRegistryDistribution
                {
                    Binary = new Dictionary<string, AcpRegistryBinaryPackage>(StringComparer.Ordinal)
                    {
                        [AcpInstallResolver.GetCurrentTargetId()] = new()
                        {
                            Archive = url,
                            Command = OperatingSystem.IsWindows() ? "agy.exe" : "antigravity",
                            Arguments = ["acp"],
                        },
                    },
                },
            },
            sha512);

    private async Task<(string Version, string Url, string Sha512)?> ReadAsync(string platform, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReadTimeout);
            using var response = await httpClient.GetAsync(new Uri(ManifestRoot + platform + ".json"), timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            var root = document.RootElement;
            if (AcpJson.GetString(root, "version") is { Length: > 0 } version &&
                AcpJson.GetString(root, "url") is { Length: > 0 } url &&
                Uri.TryCreate(url, UriKind.Absolute, out var address) && address.Scheme == Uri.UriSchemeHttps &&
                AcpJson.GetString(root, "sha512") is { Length: 128 } sha512)
            {
                return (version, url, sha512);
            }

            logger?.Info($"The release manifest of the Antigravity CLI for {platform} names no download.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger?.Info($"The release manifest of the Antigravity CLI could not be read: {ex.Message}");
        }

        return null;
    }
}
