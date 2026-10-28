using CodeAlta.CodexAppServer;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>
/// The entry of Codex in the Add provider dialog: adding it downloads the pinned app-server, with its progress, and
/// writes a provider that runs it.
/// </summary>
internal sealed class CodexCliCatalog(Func<CodexAppServerInstaller> installer) : IPluginProviderCatalog
{
    /// <summary>The identifier of the entry.</summary>
    public const string EntryId = "codex-cli";

    public string Title => "Codex";

    public Task<PluginProviderCatalogSnapshot> ListAsync(bool refresh, CancellationToken cancellationToken)
    {
        var supported = CodexPinnedRelease.FindPackage(CodexPinnedRelease.CurrentTarget()) is not null;
        PluginProviderCatalogEntry entry = new(EntryId, "Codex CLI", CodexPinnedRelease.Version)
        {
            Description = "OpenAI's Codex agent, signed in with Codex's own ChatGPT sign-in, which ChatGPT Business and Enterprise workspaces accept. CodeAlta downloads the version it is tested with.",
            Website = "https://github.com/openai/codex",
            Authors = "OpenAI",
            InstallKind = PluginProviderCatalogInstallKind.Download,
            Distribution = "binary",
            Unavailable = supported ? null : "Codex publishes no app-server for this platform.",
        };
        return Task.FromResult(new PluginProviderCatalogSnapshot([entry], DateTimeOffset.UtcNow));
    }

    public async Task<PluginProviderInstallResult> InstallAsync(string entryId, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!string.Equals(entryId, EntryId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The Codex catalog has no entry '{entryId}'.");
        }

        try
        {
            await installer().EnsureInstalledAsync(new Progress<CodexInstallProgress>(value => progress.Report(new PluginProviderInstallProgress(value.Message, value.Received, value.Total))), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CodexInstallException exception)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }

        // No command: the provider runs the pinned app-server, the one just downloaded.
        return new PluginProviderInstallResult(EntryId, "Codex CLI", new Dictionary<string, object?>(StringComparer.Ordinal));
    }

    public Task<string?> GetUpdateAsync(PluginProviderSettings settings, CancellationToken cancellationToken)
        // The pinned version comes with CodeAlta: a provider without a command runs it, and moves with each release.
        => Task.FromResult<string?>(null);

    public Task<IReadOnlyDictionary<string, object?>> UpdateAsync(PluginProviderSettings settings, IProgress<PluginProviderInstallProgress> progress, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyDictionary<string, object?>>(new Dictionary<string, object?>(StringComparer.Ordinal));
}
