using System.Text;
using CodeAlta.Acp;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Acp;

/// <summary>
/// The agents of the ACP registry, which the Add provider dialog lists and installs. The last copy of the registry
/// is kept for when it cannot be fetched, with the icons of its agents.
/// </summary>
internal sealed class AcpRegistryCatalog : IPluginProviderCatalog
{
    private const int MaximumIconBytes = 64 * 1024;
    private static readonly TimeSpan FreshFor = TimeSpan.FromHours(6);

    private readonly string _root;
    private readonly HttpClient _httpClient;
    private readonly Uri _registryUri;
    private readonly AcpAgentInstaller _installer;
    private readonly Logger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AcpRegistryDocument? _document;
    private DateTimeOffset? _fetchedAt;

    public AcpRegistryCatalog(string root, HttpClient httpClient, Uri? registryUri = null, Logger? logger = null)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _registryUri = registryUri ?? AcpRegistryClient.DefaultRegistryUri;
        _installer = new AcpAgentInstaller(Path.Combine(root, "agents"), httpClient);
        _logger = logger;
    }

    public string Title => "ACP registry";

    private string RegistryPath => Path.Combine(_root, "registry.json");

    private string IconsRoot => Path.Combine(_root, "icons");

    public async Task<PluginProviderCatalogSnapshot> ListAsync(bool refresh, CancellationToken cancellationToken)
    {
        var (document, fetchedAt, error) = await LoadAsync(refresh, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return new PluginProviderCatalogSnapshot([], null) { Error = error };
        }

        var icons = await Task.WhenAll(document.Agents.Select(agent => ReadIconAsync(agent, refresh, cancellationToken))).ConfigureAwait(false);
        var entries = document.Agents
            .Select((agent, index) =>
            {
                var (kind, distribution, unavailable) = AcpAgentInstaller.Describe(agent);
                return new PluginProviderCatalogEntry(agent.Id, string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name, NullIfEmpty(agent.Version))
                {
                    Description = NullIfEmpty(agent.Description),
                    Website = NullIfEmpty(agent.Website) ?? NullIfEmpty(agent.Repository),
                    Authors = agent.Authors is { Count: > 0 } authors ? string.Join(", ", authors) : null,
                    IconSvg = icons[index],
                    InstallKind = kind,
                    Distribution = distribution,
                    Unavailable = unavailable,
                };
            })
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new PluginProviderCatalogSnapshot(entries, fetchedAt) { Error = error };
    }

    public async Task<PluginProviderInstallResult> InstallAsync(string entryId, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentNullException.ThrowIfNull(progress);
        var manifest = await FindAsync(entryId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The ACP registry has no agent '{entryId}'.");
        var installed = await _installer.InstallAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
        return new PluginProviderInstallResult(manifest.Id, string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name, CreateValues(manifest, installed));
    }

    public async Task<string?> GetUpdateAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GetString(AcpAgentSettings.AgentKey) is not { } agentId)
        {
            return null;
        }

        var manifest = await FindAsync(agentId, cancellationToken).ConfigureAwait(false);
        return manifest is not null && IsNewer(manifest.Version, settings.GetString(AcpAgentSettings.AgentVersionKey)) ? manifest.Version : null;
    }

    public async Task<IReadOnlyDictionary<string, object?>> UpdateAsync(PluginProviderSettings settings, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var agentId = settings.GetString(AcpAgentSettings.AgentKey)
            ?? throw new InvalidOperationException($"The provider '{settings.ProviderKey}' was not added from the ACP registry.");
        var manifest = await FindAsync(agentId, cancellationToken, refresh: true).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"The ACP registry no longer has the agent '{agentId}'.");
        var installed = await _installer.InstallAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
        return CreateValues(manifest, installed);
    }

    /// <summary>Returns the manifest of an agent of the registry, or <see langword="null"/>.</summary>
    public async Task<AcpRegistryAgentManifest?> FindAsync(string agentId, CancellationToken cancellationToken, bool refresh = false)
    {
        var (document, _, _) = await LoadAsync(refresh, cancellationToken).ConfigureAwait(false);
        return document?.Agents.FirstOrDefault(agent => string.Equals(agent.Id, agentId, StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, object?> CreateValues(AcpRegistryAgentManifest manifest, AcpInstalledAgent installed)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [AcpAgentSettings.AgentKey] = manifest.Id,
            [AcpAgentSettings.AgentVersionKey] = manifest.Version,
            [AcpAgentSettings.DistributionKey] = installed.Distribution,
            [AcpAgentSettings.CommandKey] = installed.Command,
            [AcpAgentSettings.ArgumentsKey] = installed.Arguments.Cast<object?>().ToArray(),
        };
        if (installed.Environment.Count > 0)
        {
            values[AcpAgentSettings.EnvironmentKey] = installed.Environment.ToDictionary(static pair => pair.Key, static pair => (object?)pair.Value, StringComparer.Ordinal);
        }

        return values;
    }

    private async Task<(AcpRegistryDocument? Document, DateTimeOffset? FetchedAt, string? Error)> LoadAsync(bool refresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var client = new AcpRegistryClient(_httpClient);
            if (_document is null && File.Exists(RegistryPath))
            {
                try
                {
                    _document = await client.LoadFromFileAsync(RegistryPath, cancellationToken).ConfigureAwait(false);
                    _fetchedAt = File.GetLastWriteTimeUtc(RegistryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                {
                    _document = null;
                }
            }

            if (!refresh && _document is not null && _fetchedAt is { } fetched && DateTimeOffset.UtcNow - fetched < FreshFor)
            {
                return (_document, _fetchedAt, null);
            }

            try
            {
                var document = await client.DownloadAsync(_registryUri, cancellationToken).ConfigureAwait(false);
                await client.SaveToFileAsync(RegistryPath, document, cancellationToken).ConfigureAwait(false);
                _document = document;
                _fetchedAt = DateTimeOffset.UtcNow;
                return (_document, _fetchedAt, null);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger?.Info($"The ACP registry could not be fetched: {ex.Message}");
                return (_document, _fetchedAt, $"The ACP registry could not be fetched: {ex.Message}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> ReadIconAsync(AcpRegistryAgentManifest agent, bool refresh, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agent.Icon) || !Uri.TryCreate(agent.Icon, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var path = Path.Combine(IconsRoot, new string([.. agent.Id.Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_')]) + ".svg");
        if (!refresh && File.Exists(path))
        {
            try
            {
                return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumIconBytes)
            {
                return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length > MaximumIconBytes)
            {
                return null;
            }

            var svg = Encoding.UTF8.GetString(bytes);
            if (!svg.Contains("<svg", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            Directory.CreateDirectory(IconsRoot);
            await File.WriteAllTextAsync(path, svg, cancellationToken).ConfigureAwait(false);
            return svg;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException && !cancellationToken.IsCancellationRequested)
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
        }
    }

    // Whether a version of the registry is newer than the one installed; versions that are not numbers are told apart only.
    internal static bool IsNewer(string? available, string? installed)
    {
        if (string.IsNullOrWhiteSpace(available))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(installed))
        {
            return true;
        }

        return TryParse(available, out var newer) && TryParse(installed, out var older)
            ? newer > older
            : !string.Equals(available.Trim(), installed.Trim(), StringComparison.OrdinalIgnoreCase);

        static bool TryParse(string text, out Version version)
        {
            var core = text.Trim().TrimStart('v');
            var end = core.IndexOfAny(['-', '+']);
            return Version.TryParse(end >= 0 ? core[..end] : core, out version!);
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
