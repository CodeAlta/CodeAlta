using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Hosting;
using CodeAlta.LiveTool;

namespace CodeAlta.Desktop;

/// <summary>
/// The providers of <c>config.toml</c>, registered again for <c>alta provider refresh</c> as <b>Save and apply</b> of
/// Settings registers them: with the provider types of the running plugins and the models.dev catalog of the application.
/// </summary>
/// <param name="store">The configuration of the global root.</param>
/// <param name="registry">The providers of the host.</param>
/// <param name="stateRoot">The state root the providers are registered with.</param>
/// <param name="pluginTypes">The provider types of the running plugins, or null.</param>
/// <param name="modelCatalog">The models.dev catalog, or null.</param>
/// <param name="applied">What is done once the providers were applied, or null.</param>
internal sealed class DesktopAltaProviderConfiguration(CodeAltaConfigStore store, ModelProviderRegistry registry, string stateRoot,
    PluginModelProviderTypes? pluginTypes, ModelsDevCatalogService? modelCatalog, Action? applied) : IAltaProviderConfiguration
{
    /// <inheritdoc />
    public Task<int> ApplyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = GlobalConfigService.ApplyProviders(store, registry, stateRoot, pluginTypes, modelCatalog);
        // A provider added, removed or changed can change the policy that applies, as for a save of Settings.
        applied?.Invoke();
        return Task.FromResult(count);
    }
}
