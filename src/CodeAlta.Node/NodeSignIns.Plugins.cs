using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node;

/// <summary>
/// The providers of plugins (Codex CLI, ACP agents): those whose sign-in shows a code on a page any computer opens sign in
/// from the desktop, the plugin running it on the node and keeping its token there (Codex keeps it in the home of the
/// provider); the others sign in on the machine itself.
/// </summary>
internal sealed partial class NodeSignIns
{
    /// <summary>Gets the provider types of the plugins of the node; null for none.</summary>
    public PluginModelProviderTypes? PluginProviderTypes { get; init; }

    // The node does not know whether a provider of a plugin is signed in: its state is unknown, and its sign-in can run again.
    private async Task<IReadOnlyList<FleetMachineSignInItem>> PluginItemsAsync(CancellationToken cancellationToken)
    {
        var items = new List<FleetMachineSignInItem>();
        foreach (var (provider, account, settings) in PluginAccounts().Take(32))
        {
            IReadOnlyList<PluginProviderSignInMethod> methods;
            try
            {
                methods = await account.GetSignInMethodsAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                continue;
            }

            if (methods.Count == 0) continue;
            var remote = RemoteMethod(methods) is not null;
            items.Add(new FleetMachineSignInItem(FleetMachineSignInKinds.Provider, provider.ProviderKey, null, provider.ProviderKey, FleetMachineSignInStates.Unknown,
                null, null, remote ? [FleetMachineSignInWays.Device] : [], remote ? null : "It signs in on the machine itself."));
        }

        return items;
    }

    // The provider of a plugin with this key that has an account; null when the key is not one.
    private (CodeAltaExternalProvider Provider, IPluginProviderAccount Account, PluginProviderSettings Settings)? PluginAccount(string key)
        => PluginAccounts().FirstOrDefault(value => string.Equals(value.Provider.ProviderKey, key, StringComparison.OrdinalIgnoreCase)) is { Provider: not null } found ? found : null;

    private IEnumerable<(CodeAltaExternalProvider Provider, IPluginProviderAccount Account, PluginProviderSettings Settings)> PluginAccounts()
    {
        if (PluginProviderTypes is not { } types) yield break;
        IReadOnlyList<CodeAltaExternalProvider> providers;
        string? defaultProvider;
        try
        {
            var store = Store;
            providers = store.LoadGlobalExternalProviders();
            defaultProvider = store.GetEffectiveDefaultProvider();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            yield break;
        }

        foreach (var provider in providers)
        {
            if (types.Find(provider.ProviderType) is { Account: { } account }) yield return (provider, account, PluginModelProviderTypes.CreateSettings(provider, defaultProvider));
        }
    }

    // The method another computer completes: one that shows a code, not a command of a terminal there.
    private static PluginProviderSignInMethod? RemoteMethod(IReadOnlyList<PluginProviderSignInMethod> methods)
        => methods.FirstOrDefault(static method => method is { ShowsCode: true, TerminalCommand: null });

    // The sign-in of a provider of a plugin, with the code it shows; then the provider is prepared again. Throws for a way it does not have.
    private async Task<FleetMachineSignInState> RunPluginAsync(Attempt attempt, FleetMachineSignInStartParams parameters,
        (CodeAltaExternalProvider Provider, IPluginProviderAccount Account, PluginProviderSettings Settings) plugin)
    {
        if (parameters.Way is { } way && way != FleetMachineSignInWays.Device)
            throw new NotSupportedException($"{plugin.Provider.ProviderKey} signs in with a code from another computer only.");
        var method = RemoteMethod(await plugin.Account.GetSignInMethodsAsync(plugin.Settings, attempt.Lifetime.Token).ConfigureAwait(false))
            ?? throw new NotSupportedException($"{plugin.Provider.ProviderKey} signs in on the machine itself: sign in there, or over SSH.");
        await plugin.Account.SignInAsync(plugin.Settings, method.Id, (prompt, _) =>
        {
            attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, FleetMachineSignInWays.Device, prompt.Uri.AbsoluteUri, null, prompt.UserCode, null,
                prompt.ExpiresAt ?? attempt.ExpiresAt));
            return ValueTask.CompletedTask;
        }, attempt.Lifetime.Token).ConfigureAwait(false);
        try
        {
            // Its state (its account, its models) was read before the sign-in: sessions take it from the provider prepared again.
            await host.ModelProviderInitializationService.RefreshProviderAfterChangeAsync(new ModelProviderId(plugin.Provider.ProviderKey), attempt.Lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // The sign-in stands: the state of the provider says why it is not ready.
        }

        return new FleetMachineSignInState(attempt.Id, FleetMachineSignInStates.Completed, null, null, null);
    }
}
