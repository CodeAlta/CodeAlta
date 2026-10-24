using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodeAlta.Catalog;
using CodeAlta.Hosting;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Rpc;

// The providers whose type a plugin runs (ACP agents): listed and edited with the others, added from the catalog of
// their plugin, and registered again when the plugins change.
internal sealed partial class GlobalConfigService
{
    private const int MaximumPluginValueLength = 4096;
    private const int MaximumCatalogEntries = 200;

    /// <summary>Gets the provider types of the running plugins, or <see langword="null"/> for a host without plugins.</summary>
    internal PluginModelProviderTypes? PluginProviderTypes { get; init; }

    /// <summary>
    /// Stops the runs of the sessions of some providers, before their runtimes are replaced or taken away; null leaves
    /// them to end with their runtime.
    /// </summary>
    internal Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlyList<string>>>? AbortProviderRuns { get; init; }

    /// <summary>
    /// Registers the providers again when the provider types the running plugins contribute changed: a plugin that
    /// started brings its providers, one that stopped takes them away, one built again replaces their runtimes.
    /// The running turns of the providers that stop are stopped first, as the user stops a turn.
    /// A change of another plugin changes nothing.
    /// </summary>
    internal async Task ApplyPluginProviderChangesAsync()
    {
        if (_store is null || _registry is null || PluginProviderTypes is not { } types)
        {
            return;
        }

        if (AbortProviderRuns is { } abort && types.GetStoppingProviders() is { Count: > 0 } stopping)
        {
            try
            {
                var aborted = await abort(stopping, CancellationToken.None).ConfigureAwait(false);
                if (aborted.Count > 0)
                {
                    LogManager.GetLogger("CodeAlta.Desktop").Info($"Stopped {aborted.Count} running sessions of the providers of a plugin that stops: {string.Join(", ", stopping)}.");
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                // The host closes: the runtimes end with it.
            }
        }

        ApplyPluginProviderChanges(types);
    }

    private void ApplyPluginProviderChanges(PluginModelProviderTypes types)
    {
        lock (_gate)
        {
            if (string.Equals(types.Signature, types.RegisteredSignature, StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                ApplyProviders(_store!, _registry!, _stateRoot!, types, ModelCatalog);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                LogManager.GetLogger("CodeAlta.Desktop").Warn($"The providers of the plugins could not be registered again: {exception.Message}");
            }
        }
    }

    // The provider types of the running plugins, as the form edits them.
    private IReadOnlyList<GlobalConfigPluginProviderType> DescribePluginTypes()
        => PluginProviderTypes is not { } types
            ? []
            : [.. types.Types.Take(16).Select(static type => new GlobalConfigPluginProviderType(
                Bound(type.ProviderType)!, Bound(type.DisplayName)!, Bound(type.Tag),
                [.. type.Settings.Take(32).Select(static setting => new GlobalConfigPluginProviderSetting(
                    Bound(setting.Key)!, Bound(setting.Label)!, setting.Kind.ToString().ToLowerInvariant(), Bound(setting.Description), Bound(setting.Placeholder), setting.Advanced, setting.ReadOnly))],
                type.PermissionModes.Take(16).ToArray(), type.Catalog is not null, Bound(type.Catalog?.Title), type.Account is not null))];

    // The providers of the configuration whose type a running plugin contributes, as the list of the page shows them.
    private IReadOnlyList<GlobalConfigProvider> ListPluginProviders(CodeAltaConfigDocument document)
    {
        if (_store is null || PluginProviderTypes is not { } types || types.Types.Count == 0)
        {
            return [];
        }

        var defaultProvider = document.Chat?.DefaultProvider?.Trim();
        var empty = new GlobalConfigProviderDefaults(null, null, null, null, null);
        return [.. _store.LoadGlobalExternalProviders()
            .Where(provider => types.Find(provider.ProviderType) is not null)
            .Take(MaximumProviders)
            .Select(provider =>
            {
                var contribution = types.Find(provider.ProviderType)!;
                var settings = PluginModelProviderTypes.CreateSettings(provider, defaultProvider);
                return new GlobalConfigProvider(
                    Bound(provider.ProviderKey)!, Bound(provider.ProviderType)!, settings.IsEnabled,
                    Bound(settings.GetString("display_name")), Bound(settings.DisplayName)!,
                    Bound(settings.GetString("model")), Bound(settings.GetString("reasoning_effort")),
                    null, null, null, false, empty)
                {
                    Icon = Bound(settings.GetString("icon")),
                    Color = Bound(settings.GetString("color")),
                    PermissionMode = Bound(settings.PermissionMode),
                    Plugin = new GlobalConfigPluginProviderValues(
                        Bound(contribution.Tag),
                        [.. contribution.Settings.Select(setting => ToValue(setting, settings))],
                        IconData(contribution, settings)),
                };
            })];
    }

    private static GlobalConfigPluginSettingValue ToValue(PluginProviderSetting setting, PluginProviderSettings settings)
        => setting.Kind switch
        {
            PluginProviderSettingKind.List => new(setting.Key) { List = [.. settings.GetList(setting.Key).Take(64).Select(static item => Bound(item)!)] },
            PluginProviderSettingKind.Map => new(setting.Key)
            {
                Map = [.. settings.GetMap(setting.Key).Take(64).Select(static pair => new GlobalConfigPluginMapEntry(Bound(pair.Key)!, Bound(pair.Value)!))],
            },
            PluginProviderSettingKind.Boolean => new(setting.Key) { Flag = settings.GetBoolean(setting.Key) },
            _ => new(setting.Key) { Text = Bound(settings.GetString(setting.Key)) },
        };

    // The icon a plugin gives a provider, rebuilt as a data URL that cannot run or fetch anything.
    internal static string? IconData(PluginModelProviderContribution contribution, PluginProviderSettings settings)
    {
        try
        {
            return contribution.GetIconSvg?.Invoke(settings) is { } svg ? PluginIcons.Sanitize(svg) : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// Adds or updates a provider whose type a running plugin contributes: the settings every provider has, the
    /// settings of its type the form shows, and the other keys of its section as they are. Optionally makes it the
    /// default provider; the providers are registered again.
    /// </summary>
    [NeoRpcMethod("savePluginProvider")]
    public async Task<GlobalConfigSaveResponse> SavePluginProviderAsync(GlobalConfigSavePluginProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = SavePluginProvider(request);
        var original = request.OriginalKey?.Trim();
        var key = request.Key?.Trim().ToLowerInvariant();
        if (response.Status is "ok" or "apply_failed" && !string.IsNullOrEmpty(original) && !string.IsNullOrEmpty(key)
            && !string.Equals(original, key, StringComparison.OrdinalIgnoreCase))
            await ReplaceProviderInSpaceTiersAsync(original, key).ConfigureAwait(false);
        return response;
    }

    internal GlobalConfigSaveResponse SavePluginProvider(GlobalConfigSavePluginProviderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var key = request.Key?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key) || key.Length > 64 || !key.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            return new("invalid", null, "A provider key uses letters, digits, '-' or '_' (at most 64).", null, null, 0);
        if (PluginProviderTypes?.Find(request.Type) is not { } contribution)
            return new("invalid", null, "No running plugin provides this type of provider.", null, null, 0);
        var icon = Optional(request.Icon)?.ToLowerInvariant();
        if (icon is not null && (icon.Length > 64 || !icon.All(static character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            return new("invalid", null, "An icon is named by lowercase letters, digits or '-' (at most 64).", null, null, 0);
        var color = Optional(request.Color);
        if (color is not null && !IsHexColor(color))
            return new("invalid", null, "A color is written as #rgb or #rrggbb.", null, null, 0);
        var permissionMode = Optional(request.PermissionMode);
        if (permissionMode is not null && !contribution.PermissionModes.Contains(permissionMode, StringComparer.Ordinal))
            return new("invalid", null, $"A permission mode is one of: {string.Join(", ", contribution.PermissionModes)}.", null, null, 0);
        if (new[] { request.DisplayName, request.Model, request.ReasoningEffort }.Any(static value => value?.Length > 2048))
            return new("invalid", null, "A provider field is too long.", null, null, 0);

        return MutateExternal(request.ExpectedEpoch, request.ExpectedRevision, applyProviders: true, store =>
        {
            var original = request.OriginalKey?.Trim();
            var external = store.LoadGlobalExternalProviders();
            var current = string.IsNullOrEmpty(original) ? null : external.FirstOrDefault(value => string.Equals(value.ProviderKey, original, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(original) && current is null) return "The provider no longer exists in the configuration.";
            if (!string.Equals(original, key, StringComparison.OrdinalIgnoreCase) && Exists(store, key)) return "Another provider already uses this key.";
            var values = new Dictionary<string, object?>(current?.Values ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase)
            {
                ["enabled"] = request.Enabled,
            };
            Set(values, "display_name", Optional(request.DisplayName));
            Set(values, "icon", icon);
            Set(values, "color", color);
            Set(values, "model", Optional(request.Model));
            Set(values, "reasoning_effort", Optional(request.ReasoningEffort));
            Set(values, "permission_mode", permissionMode);
            foreach (var setting in contribution.Settings.Where(static setting => !setting.ReadOnly))
            {
                var value = request.Values?.FirstOrDefault(value => string.Equals(value.Key, setting.Key, StringComparison.Ordinal));
                if (value is null) continue;
                Set(values, setting.Key, FromValue(setting.Kind, value));
            }

            if (contribution.Validate?.Invoke(new PluginProviderSettings(key, contribution.ProviderType, values)) is { Count: > 0 } problems)
                return string.Join(" ", problems);
            var renamedSpaces = !string.IsNullOrEmpty(original) && !string.Equals(original, key, StringComparison.OrdinalIgnoreCase)
                ? store.LoadGlobalSpaceAssignments().Of(SpaceResourceKind.Provider, original) : [];
            store.SaveGlobalExternalProvider(key, contribution.ProviderType, values);
            if (!string.IsNullOrEmpty(original) && !string.Equals(original, key, StringComparison.OrdinalIgnoreCase))
            {
                store.RemoveGlobalExternalProvider(original);
                if (renamedSpaces.Count > 0) store.SaveGlobalSpaceAssignment(SpaceResourceKind.Provider, key, renamedSpaces);
                ReplaceProviderInTiers(store, original, key);
            }

            if (request.MakeDefault && request.Enabled) store.SaveGlobalDefaultProvider(key);
            else if (store.GetEffectiveDefaultProvider() is { } currentDefault
                && (string.Equals(currentDefault, key, StringComparison.OrdinalIgnoreCase) || string.Equals(currentDefault, original, StringComparison.OrdinalIgnoreCase)))
                store.SaveGlobalDefaultProvider(null);
            return null;
        });
    }

    /// <summary>Lists the entries of the catalog of a provider type of a plugin, such as the agents of the ACP registry.</summary>
    [NeoRpcMethod("pluginCatalog")]
    public async Task<GlobalConfigPluginCatalogResponse> PluginCatalogAsync(GlobalConfigPluginCatalogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", null, [], null, null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, [], null, null);
        if (PluginProviderTypes?.Find(request.Type)?.Catalog is not { } catalog) return new("not_found", null, [], null, null);
        PluginProviderCatalogSnapshot snapshot;
        try
        {
            snapshot = await catalog.ListAsync(request.Refresh, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return new("read_failed", Bound(catalog.Title), [], null, Bound(exception.Message));
        }

        var installed = InstalledEntries(request.Type!);
        var entries = snapshot.Entries.Take(MaximumCatalogEntries).Select(entry => new GlobalConfigPluginCatalogEntry(
            Bound(entry.Id)!, Bound(entry.Name)!, Bound(entry.Version), Bound(entry.Description), Bound(entry.Website), Bound(entry.Authors),
            entry.IconSvg is { } svg ? PluginIcons.Sanitize(svg) : null, Bound(entry.Distribution), Bound(entry.Unavailable),
            installed.GetValueOrDefault(entry.Id))).ToArray();
        return new("ok", Bound(catalog.Title), entries, snapshot.FetchedAt, Bound(snapshot.Error));
    }

    // The providers of the configuration that run an entry of the catalog, by the identifier of the entry: the
    // dialog shows them as added. A plugin names the entry of a provider by its "agent" setting.
    private Dictionary<string, string> InstalledEntries(string type)
    {
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_store is null) return installed;
        try
        {
            foreach (var provider in _store.LoadGlobalExternalProviders().Where(provider => string.Equals(provider.ProviderType, type, StringComparison.OrdinalIgnoreCase)))
            {
                if (provider.Values.TryGetValue("agent", out var agent) && agent is string id && !installed.ContainsKey(id)) installed[id] = provider.ProviderKey;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }

        return installed;
    }

    /// <summary>
    /// Installs an entry of the catalog of a provider type and adds its provider, enabled, under the key it suggests
    /// or the first free one after it. The stream gives the progress, then how it ended.
    /// </summary>
    [NeoRpcMethod("installPluginProvider")]
    public NeoRpcChannel<GlobalConfigPluginInstallEvent> InstallPluginProvider(GlobalConfigInstallPluginProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(InstallAsync(request, cancellationToken), DesktopJsonContext.Default.GlobalConfigPluginInstallEvent);
    }

    internal async IAsyncEnumerable<GlobalConfigPluginInstallEvent> InstallAsync(GlobalConfigInstallPluginProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_store is null || _registry is null) { yield return Failed("unavailable"); yield break; }
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) { yield return Failed("stale_epoch"); yield break; }
        if (PluginProviderTypes?.Find(request.Type) is not { Catalog: { } catalog } contribution || string.IsNullOrWhiteSpace(request.EntryId))
        {
            yield return Failed("not_found");
            yield break;
        }

        var events = Channel.CreateUnbounded<GlobalConfigPluginInstallEvent>(new UnboundedChannelOptions { SingleReader = true });
        var work = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress(events.Writer);
                var result = await catalog.InstallAsync(request.EntryId!, progress, cancellationToken).ConfigureAwait(false);
                string? added = null;
                var saved = MutateExternal(request.ExpectedEpoch, null, applyProviders: true, store =>
                {
                    added = FreeKey(store, result.SuggestedKey);
                    var values = new Dictionary<string, object?>(result.Values, StringComparer.OrdinalIgnoreCase)
                    {
                        ["enabled"] = true,
                        ["display_name"] = FreeDisplayName(result.DisplayName, contribution.Tag),
                    };
                    if (result.Icon is { } icon) values["icon"] = icon;
                    store.SaveGlobalExternalProvider(added, contribution.ProviderType, values);
                    return null;
                }, checkRevision: false);
                events.Writer.TryWrite(saved.Status is "ok" or "apply_failed"
                    ? new GlobalConfigPluginInstallEvent("done", null, null, null) { Key = added, Status = saved.Status }
                    : Failed(saved.Status) with { Message = Bound(saved.Message) });
            }
            catch (OperationCanceledException)
            {
                events.Writer.TryWrite(Failed("canceled"));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                events.Writer.TryWrite(Failed("install_failed") with { Message = Bound(exception.Message) });
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var item in events.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return item;
        }

        await work.ConfigureAwait(false);
    }

    /// <summary>
    /// Updates a provider of a plugin to the version its catalog has now, and registers the providers again. The
    /// stream gives the progress, then how it ended.
    /// </summary>
    [NeoRpcMethod("updatePluginProvider")]
    public NeoRpcChannel<GlobalConfigPluginInstallEvent> UpdatePluginProvider(GlobalConfigPluginProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(UpdateAsync(request, cancellationToken), DesktopJsonContext.Default.GlobalConfigPluginInstallEvent);
    }

    internal async IAsyncEnumerable<GlobalConfigPluginInstallEvent> UpdateAsync(GlobalConfigPluginProviderRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_store is null || _registry is null) { yield return Failed("unavailable"); yield break; }
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) { yield return Failed("stale_epoch"); yield break; }
        if (FindPluginProvider(request.Key) is not { Contribution.Catalog: { } catalog } found)
        {
            yield return Failed("not_found");
            yield break;
        }

        var events = Channel.CreateUnbounded<GlobalConfigPluginInstallEvent>(new UnboundedChannelOptions { SingleReader = true });
        var work = Task.Run(async () =>
        {
            try
            {
                var changed = await catalog.UpdateAsync(found.Settings, new Progress(events.Writer), cancellationToken).ConfigureAwait(false);
                var saved = MutateExternal(request.ExpectedEpoch, null, applyProviders: true, store =>
                {
                    var current = store.LoadGlobalExternalProviders().FirstOrDefault(value => string.Equals(value.ProviderKey, found.Settings.ProviderKey, StringComparison.OrdinalIgnoreCase));
                    if (current is null) return "The provider no longer exists in the configuration.";
                    var values = new Dictionary<string, object?>(current.Values, StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, value) in changed) values[name] = value;
                    store.SaveGlobalExternalProvider(current.ProviderKey, current.ProviderType, values);
                    return null;
                }, checkRevision: false);
                events.Writer.TryWrite(saved.Status is "ok" or "apply_failed"
                    ? new GlobalConfigPluginInstallEvent("done", null, null, null) { Key = found.Settings.ProviderKey, Status = saved.Status }
                    : Failed(saved.Status) with { Message = Bound(saved.Message) });
            }
            catch (OperationCanceledException)
            {
                events.Writer.TryWrite(Failed("canceled"));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                events.Writer.TryWrite(Failed("install_failed") with { Message = Bound(exception.Message) });
            }
            finally
            {
                events.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var item in events.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return item;
        }

        await work.ConfigureAwait(false);
    }

    /// <summary>Says what a provider of a plugin runs, the version it can be updated to, and how it signs in.</summary>
    [NeoRpcMethod("pluginProviderDetails")]
    public async Task<GlobalConfigPluginProviderDetailsResponse> PluginProviderDetailsAsync(GlobalConfigPluginProviderDetailsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", null, null, null, []);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, null, null, []);
        if (FindPluginProvider(request.Key) is not { } found) return new("not_found", null, null, null, []);
        PluginProviderDescription? description = null;
        string? update = null;
        IReadOnlyList<PluginProviderSignInMethod> methods = [];
        try
        {
            description = found.Contribution.Describe is { } describe ? await describe(found.Settings, cancellationToken).ConfigureAwait(false) : null;
            update = found.Contribution.Catalog is { } catalog ? await catalog.GetUpdateAsync(found.Settings, cancellationToken).ConfigureAwait(false) : null;
            if (request.SignIn && found.Contribution.Account is { } account)
                methods = await account.GetSignInMethodsAsync(found.Settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return new("read_failed", Bound(description?.Summary), Bound(exception.Message), update, []);
        }

        return new("ok", Bound(description?.Summary), Bound(description?.Detail), Bound(update),
            [.. methods.Take(8).Select(static method => new GlobalConfigPluginSignInMethod(Bound(method.Id)!, Bound(method.Label)!, Bound(method.Description),
                method.TerminalCommand is { Count: > 0 } command ? [.. command.Take(32).Select(static part => Bound(part)!)] : null,
                method.TerminalEnvironment is { Count: > 0 } environment ? [.. environment.Take(32).Select(static pair => new GlobalConfigPluginMapEntry(Bound(pair.Key)!, Bound(pair.Value)!))] : null))]);
    }

    /// <summary>Signs a provider of a plugin in with a method that is not a terminal command, and tests it again.</summary>
    [NeoRpcMethod("pluginProviderSignIn")]
    public async Task<GlobalConfigPluginSignInResponse> PluginProviderSignInAsync(GlobalConfigPluginSignInRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_store is null) return new("unavailable", null);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null);
        if (FindPluginProvider(request.Key) is not { Contribution.Account: { } account } found || string.IsNullOrWhiteSpace(request.MethodId)) return new("not_found", null);
        try
        {
            await account.SignInAsync(found.Settings, request.MethodId!, cancellationToken).ConfigureAwait(false);
            return new("ok", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return new("failed", Bound(exception.Message));
        }
    }

    private (PluginModelProviderContribution Contribution, PluginProviderSettings Settings)? FindPluginProvider(string? key)
    {
        if (_store is null || PluginProviderTypes is not { } types || string.IsNullOrWhiteSpace(key)) return null;
        CodeAltaExternalProvider? provider;
        try
        {
            provider = _store.LoadGlobalExternalProviders().FirstOrDefault(value => string.Equals(value.ProviderKey, key.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }

        return provider is not null && types.Find(provider.ProviderType) is { } contribution
            ? (contribution, PluginModelProviderTypes.CreateSettings(provider, _store.GetEffectiveDefaultProvider()))
            : null;
    }

    // Runs one edit of the sections of the providers of plugins under the epoch, revision and apply rules of the others.
    private GlobalConfigSaveResponse MutateExternal(string? expectedEpoch, string? expectedRevision, bool applyProviders, Func<CodeAltaConfigStore, string?> edit, bool checkRevision = true)
    {
        if (_store is null || _registry is null) return Failure("unavailable");
        if (!string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal)) return Failure("stale_epoch");
        return Mutate(_store, _registry, expectedRevision, checkRevision, applyProviders, (store, _) => edit(store));
    }

    // Whether a provider of the configuration, built-in or of a plugin, has a key.
    private static bool Exists(CodeAltaConfigStore store, string key)
        => store.LoadGlobalProviderDefinitions(includeDisabled: true).Any(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase))
           || store.LoadGlobal().UnsupportedProviders.Any(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase));

    // The key itself when no provider has it, else <key>-2, <key>-3...
    private static string FreeKey(CodeAltaConfigStore store, string suggested)
    {
        var stem = new string([.. suggested.Trim().ToLowerInvariant().Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-')]);
        if (stem.Length == 0) stem = "agent";
        if (stem.Length > 60) stem = stem[..60];
        if (!Exists(store, stem)) return stem;
        for (var number = 2; ; number++)
        {
            var candidate = $"{stem}-{number.ToString(CultureInfo.InvariantCulture)}";
            if (!Exists(store, candidate)) return candidate;
        }
    }

    // The name of an added provider, told apart by the tag of its type when another provider has it already: the agent
    // Copilot of the ACP registry is "Copilot (ACP)" beside the provider Copilot of CodeAlta, in every list of providers.
    internal string FreeDisplayName(string name, string? tag)
    {
        var taken = _registry?.ListProviders(includeDisabled: true).Any(provider => string.Equals(provider.DisplayName, name, StringComparison.OrdinalIgnoreCase)) == true;
        return taken && !string.IsNullOrWhiteSpace(tag) ? $"{name} ({tag})" : name;
    }

    private static void Set(Dictionary<string, object?> values, string key, object? value)
    {
        if (value is null) values.Remove(key);
        else values[key] = value;
    }

    private static object? FromValue(PluginProviderSettingKind kind, GlobalConfigPluginSettingValue value)
        => kind switch
        {
            PluginProviderSettingKind.List => value.List is { Count: > 0 } list
                ? list.Where(static item => !string.IsNullOrWhiteSpace(item)).Take(64).Select(static item => (object?)Limit(item)).ToArray()
                : null,
            PluginProviderSettingKind.Map => value.Map is { Count: > 0 } map
                ? map.Where(static entry => !string.IsNullOrWhiteSpace(entry.Name)).Take(64).ToDictionary(static entry => entry.Name.Trim(), static entry => (object?)Limit(entry.Value), StringComparer.Ordinal)
                : null,
            PluginProviderSettingKind.Boolean => value.Flag,
            _ => Optional(value.Text) is { } text ? Limit(text) : null,
        };

    private static string Limit(string value) => value.Length <= MaximumPluginValueLength ? value : value[..MaximumPluginValueLength];

    private static GlobalConfigPluginInstallEvent Failed(string status) => new("failed", null, null, null) { Status = status };

    private sealed class Progress(ChannelWriter<GlobalConfigPluginInstallEvent> writer) : IProgress<PluginProviderInstallProgress>
    {
        public void Report(PluginProviderInstallProgress value)
            => writer.TryWrite(new GlobalConfigPluginInstallEvent("progress", Bound(value.Message), Clamp(value.Received), Clamp(value.Total)));

        private static int? Clamp(long? value) => value is { } bytes ? (int)Math.Min(bytes, int.MaxValue) : null;
    }
}

/// <summary>A provider type a running plugin contributes, as the form edits it.</summary>
/// <param name="Type">Its type.</param>
/// <param name="Name">Its name.</param>
/// <param name="Tag">The short tag shown with its providers, or null.</param>
/// <param name="Settings">The settings of the type.</param>
/// <param name="PermissionModes">The permission modes of its sessions.</param>
/// <param name="HasCatalog">Whether it has a catalog of providers to add.</param>
/// <param name="CatalogTitle">The title of that catalog, or null.</param>
/// <param name="HasAccount">Whether its providers sign in.</param>
internal sealed record GlobalConfigPluginProviderType(string Type, string Name, string? Tag, IReadOnlyList<GlobalConfigPluginProviderSetting> Settings,
    IReadOnlyList<string> PermissionModes, bool HasCatalog, string? CatalogTitle, bool HasAccount);

/// <param name="Kind"><c>text</c>, <c>path</c>, <c>list</c>, <c>map</c> or <c>boolean</c>.</param>
internal sealed record GlobalConfigPluginProviderSetting(string Key, string Label, string Kind, string? Description, string? Placeholder, bool Advanced, bool ReadOnly);

/// <summary>The values of the settings of a provider of a plugin.</summary>
/// <param name="Tag">The short tag of its type, or null.</param>
/// <param name="Values">The value of each setting of its type.</param>
/// <param name="IconData">The icon its plugin gives it, as a data URL, or null.</param>
internal sealed record GlobalConfigPluginProviderValues(string? Tag, IReadOnlyList<GlobalConfigPluginSettingValue> Values, string? IconData);

/// <summary>The value of one setting: the field of its kind is set.</summary>
internal sealed record GlobalConfigPluginSettingValue(string Key)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? List { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<GlobalConfigPluginMapEntry>? Map { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Flag { get; init; }
}

internal sealed record GlobalConfigPluginMapEntry(string Name, string Value);

internal sealed record GlobalConfigSavePluginProviderRequest(string? ExpectedEpoch, string? ExpectedRevision, string? OriginalKey, string? Key, string? Type, bool Enabled,
    string? DisplayName, string? Model, string? ReasoningEffort, IReadOnlyList<GlobalConfigPluginSettingValue>? Values, bool MakeDefault)
{
    public string? Icon { get; init; }

    public string? Color { get; init; }

    public string? PermissionMode { get; init; }
}

internal sealed record GlobalConfigPluginCatalogRequest(string? ExpectedEpoch, string? Type, bool Refresh);

/// <param name="Status"><c>ok</c>, <c>not_found</c>, <c>read_failed</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
/// <param name="Error">Why the catalog could not be fetched, when its entries are the ones kept from an earlier fetch.</param>
internal sealed record GlobalConfigPluginCatalogResponse(string Status, string? Title, IReadOnlyList<GlobalConfigPluginCatalogEntry> Entries, DateTimeOffset? FetchedAt, string? Error);

/// <param name="IconData">Its icon as a data URL, or null.</param>
/// <param name="Unavailable">Why it cannot be added on this computer, or null.</param>
/// <param name="InstalledKey">The key of the provider that runs it already, or null.</param>
internal sealed record GlobalConfigPluginCatalogEntry(string Id, string Name, string? Version, string? Description, string? Website, string? Authors,
    string? IconData, string? Distribution, string? Unavailable, string? InstalledKey);

internal sealed record GlobalConfigInstallPluginProviderRequest(string? ExpectedEpoch, string? Type, string? EntryId);

internal sealed record GlobalConfigPluginProviderRequest(string? ExpectedEpoch, string? Key);

/// <summary>One step of an installation or an update.</summary>
/// <param name="State"><c>progress</c>, <c>done</c> or <c>failed</c>.</param>
/// <param name="Message">What is being done.</param>
/// <param name="Received">The bytes received so far, or null.</param>
/// <param name="Total">The bytes to receive, or null when not known.</param>
internal sealed record GlobalConfigPluginInstallEvent(string State, string? Message, int? Received, int? Total)
{
    /// <summary>The key of the provider that was added or updated, once done.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; init; }

    /// <summary>How the save ended once done (<c>ok</c>, <c>apply_failed</c>), or why it failed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }
}

internal sealed record GlobalConfigPluginProviderDetailsRequest(string? ExpectedEpoch, string? Key, bool SignIn);

/// <param name="Summary">What the provider runs, such as the command line of its agent.</param>
/// <param name="Detail">A sentence that says more, or the error that stopped the reading.</param>
/// <param name="Update">The version the provider can be updated to, or null.</param>
internal sealed record GlobalConfigPluginProviderDetailsResponse(string Status, string? Summary, string? Detail, string? Update, IReadOnlyList<GlobalConfigPluginSignInMethod> SignInMethods);

/// <param name="TerminalCommand">The command to run in a terminal to sign in, or null for a sign-in the agent runs.</param>
internal sealed record GlobalConfigPluginSignInMethod(string Id, string Label, string? Description, IReadOnlyList<string>? TerminalCommand, IReadOnlyList<GlobalConfigPluginMapEntry>? TerminalEnvironment);

internal sealed record GlobalConfigPluginSignInRequest(string? ExpectedEpoch, string? Key, string? MethodId);

/// <param name="Status"><c>ok</c>, <c>failed</c>, <c>not_found</c>, <c>unavailable</c> or <c>stale_epoch</c>.</param>
internal sealed record GlobalConfigPluginSignInResponse(string Status, string? Message);
