using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node;

/// <summary>
/// The providers of plugins (Codex CLI, ACP agents) signed in from the desktop:
/// <list type="bullet">
/// <item>a sign-in that shows a code on a page any computer opens runs on the node, the plugin keeping its token there (Codex keeps it
/// in the home of the provider);</item>
/// <item>a sign-in by a variable of the environment (an ACP agent's <c>env_var</c>: an API key) takes the value the person types on the
/// desktop, kept as a secret of the machine that the provider's environment names (<c>${secret:NAME}</c>);</item>
/// <item>the others sign in on the machine itself, and the list says how: the command of a terminal to run there, or a browser whose
/// page comes back to that machine only.</item>
/// </list>
/// </summary>
internal sealed partial class NodeSignIns
{
    /// <summary>Gets the provider types of the plugins of the node; null for none.</summary>
    public PluginModelProviderTypes? PluginProviderTypes { get; init; }

    // A provider of a plugin is signed in when a variable of its environment names the secret the desktop gave it; otherwise the node
    // does not know (the plugin does not say), and its sign-in can run again.
    private async Task<IReadOnlyList<FleetMachineSignInItem>> PluginItemsAsync(CancellationToken cancellationToken)
    {
        var items = new List<FleetMachineSignInItem>();
        var secrets = SecretNames();
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

            var given = GivenVariables(provider, secrets).Any();
            if (methods.Count == 0 && !given) continue;
            string[] ways = RemoteMethod(methods) is not null ? [FleetMachineSignInWays.Device] : SecretMethod(methods) is not null ? [FleetMachineSignInWays.Secret] : [];
            items.Add(new FleetMachineSignInItem(FleetMachineSignInKinds.Provider, provider.ProviderKey, null, settings.DisplayName,
                given ? FleetMachineSignInStates.SignedIn : FleetMachineSignInStates.Unknown, null, null, ways, ways.Length == 0 ? Bounded(OnTheMachine(methods)) : null));
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

    // The method the person completes by typing one value on the desktop: one variable of the environment the method needs (the
    // others optional), written to a map setting the method names.
    private static (PluginProviderSignInMethod Method, PluginProviderSignInVariable Variable)? SecretMethod(IReadOnlyList<PluginProviderSignInMethod> methods)
    {
        foreach (var method in methods)
        {
            if (method is not { TerminalCommand: null, Variables: { Count: > 0 } variables, EnvironmentSetting.Length: > 0 }) continue;
            var needed = variables.Where(static variable => !variable.Optional).ToArray();
            if (needed is [{ } variable] && IsVariableName(variable.Name)) return (method, variable);
        }

        return null;
    }

    // Why a provider of a plugin signs in on the machine only, and how.
    private static string OnTheMachine(IReadOnlyList<PluginProviderSignInMethod> methods)
    {
        if (methods.FirstOrDefault(static method => method.TerminalCommand is { Count: > 0 }) is { TerminalCommand: { } command })
            return $"It signs in in a terminal of the machine: run {string.Join(' ', command.Select(static word => word.Contains(' ', StringComparison.Ordinal) ? $"\"{word}\"" : word))} there.";
        if (methods.Any(static method => method.Variables is { Count: > 0 }))
            return "It signs in with several variables of its environment: set them as secrets of the machine, and name them in the environment of the provider there.";
        return "It signs in with a browser on the machine itself, whose page comes back to that machine only: sign in there.";
    }

    // The names of the secrets of the machine; none when the store cannot be read.
    private HashSet<string> SecretNames()
    {
        try { return new LocalSecretStore(Root).ListSecrets().Select(static secret => secret.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { return []; }
    }

    // The variables of the environment of a provider that name the secret the desktop gave them, with their map setting.
    private static IEnumerable<(string Setting, string Variable, string Secret)> GivenVariables(CodeAltaExternalProvider provider, IReadOnlySet<string>? secrets)
    {
        foreach (var (setting, value) in provider.Values)
        {
            if (value is not IReadOnlyDictionary<string, object?> table) continue;
            foreach (var (variable, item) in table)
            {
                if (!IsVariableName(variable) || item is not string text) continue;
                var name = ProviderSecretName(variable, provider.ProviderKey);
                if (text == $"${{secret:{name}}}" && (secrets is null || secrets.Contains(name))) yield return (setting, variable, name);
            }
        }
    }

    // The sign-in of a provider of a plugin, with the code it shows or the value the person types; then the provider is prepared
    // again. Throws for a way it does not have.
    private async Task<FleetMachineSignInState> RunPluginAsync(Attempt attempt, FleetMachineSignInStartParams parameters,
        (CodeAltaExternalProvider Provider, IPluginProviderAccount Account, PluginProviderSettings Settings) plugin)
    {
        var methods = await plugin.Account.GetSignInMethodsAsync(plugin.Settings, attempt.Lifetime.Token).ConfigureAwait(false);
        var way = parameters.Way ?? (RemoteMethod(methods) is not null ? FleetMachineSignInWays.Device : SecretMethod(methods) is not null ? FleetMachineSignInWays.Secret : null)
            ?? throw new NotSupportedException($"{plugin.Settings.DisplayName} signs in on the machine itself. {OnTheMachine(methods)}");
        if (way == FleetMachineSignInWays.Secret)
        {
            await GiveVariableAsync(attempt, plugin, SecretMethod(methods) ?? throw new NotSupportedException($"{plugin.Settings.DisplayName} does not sign in with a value typed on another computer."))
                .ConfigureAwait(false);
        }
        else if (way == FleetMachineSignInWays.Device)
        {
            var method = RemoteMethod(methods) ?? throw new NotSupportedException($"{plugin.Settings.DisplayName} does not sign in with a code from another computer.");
            await plugin.Account.SignInAsync(plugin.Settings, method.Id, (prompt, _) =>
            {
                attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, FleetMachineSignInWays.Device, prompt.Uri.AbsoluteUri, null, prompt.UserCode, null,
                    prompt.ExpiresAt ?? attempt.ExpiresAt));
                return ValueTask.CompletedTask;
            }, attempt.Lifetime.Token).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException($"{plugin.Settings.DisplayName} does not sign in with a {way} from another computer.");
        }

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

    // The value of a variable of the environment, typed on the desktop: kept as a secret of the machine, which the variable names, and
    // the provider registered again by its plugin with it. The value is never written elsewhere, nor said back.
    private async Task GiveVariableAsync(Attempt attempt, (CodeAltaExternalProvider Provider, IPluginProviderAccount Account, PluginProviderSettings Settings) plugin,
        (PluginProviderSignInMethod Method, PluginProviderSignInVariable Variable) secret)
    {
        var given = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        attempt.AcceptCode = value => given.TrySetResult(value);
        var (method, variable) = secret;
        attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, FleetMachineSignInWays.Secret,
            method.VariablesPage is { Scheme: "https" } page ? page.AbsoluteUri : null, null, null,
            Bounded(variable.Label is { Length: > 0 } label && label != variable.Name ? $"{label} ({variable.Name})" : variable.Name), attempt.ExpiresAt));
        var value = await given.Task.WaitAsync(attempt.Lifetime.Token).ConfigureAwait(false);

        var name = ProviderSecretName(variable.Name, plugin.Provider.ProviderKey);
        new LocalSecretStore(Root).SetSecret(name, value, "machine sign-in (CodeAlta Desktop)");
        var written = providers.SetPluginEnvironment(plugin.Provider.ProviderKey, method.EnvironmentSetting!, variable.Name, $"${{secret:{name}}}")
            ?? throw new KeyNotFoundException($"No provider of a plugin of this machine has the key {plugin.Provider.ProviderKey}.");
        RegisterPlugin(written);
    }

    // Signs a provider of a plugin out from here: the variables the desktop gave it leave its environment and the secrets; its own
    // login stays. Throws when the desktop gave it none.
    private void SignOutPlugin(CodeAltaExternalProvider provider)
    {
        var given = GivenVariables(provider, null).ToArray();
        if (given.Length == 0) throw new NotSupportedException("It keeps its own login: sign it out on the machine.");
        var written = provider;
        foreach (var (setting, variable, secret) in given)
        {
            written = providers.SetPluginEnvironment(provider.ProviderKey, setting, variable, null) ?? written;
            new LocalSecretStore(Root).DeleteSecret(secret);
        }

        RegisterPlugin(written);
    }

    // The provider registered again by its plugin with its environment as written: its runtime is given the new value. A provider
    // still prepared refuses to be replaced: the change is applied at the next start of the node.
    private void RegisterPlugin(CodeAltaExternalProvider provider)
    {
        if (PluginProviderTypes is not { } types) return;
        try
        {
            var settings = PluginModelProviderTypes.CreateSettings(provider, Store.GetEffectiveDefaultProvider());
            if (!settings.IsEnabled) return;
            var registry = host.ModelProviderRegistry;
            if (types.TryCreateProviderRegistration(settings, Root, registry.SecretIssued, out var descriptor, out var createRuntime)) registry.RegisterOrReplace(descriptor, createRuntime);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or InvalidDataException)
        {
            // Applied at the next start.
        }
    }
}
