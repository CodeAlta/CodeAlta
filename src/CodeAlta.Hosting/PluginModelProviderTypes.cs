using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Agent.Harness;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Hosting;

/// <summary>
/// The model provider types the running plugins contribute (<see cref="PluginModelProviderContribution"/>), and the
/// registration of the providers of the configuration that have one of them as their type.
/// </summary>
/// <remarks>
/// The host attaches the source of the contributions once its plugins started; until then, and when no plugin
/// contributes a type, no provider is registered through a plugin. A provider of a type no running plugin
/// contributes stays in the configuration, set aside as one of a newer version is.
/// </remarks>
public sealed class PluginModelProviderTypes
{
    private static readonly Logger Logger = LogManager.GetLogger("CodeAlta.RawApi");

    private readonly IHarnessToolEndpoints _toolEndpoints;
    private Func<IReadOnlyList<PluginModelProviderContribution>> _source = static () => [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginModelProviderTypes"/> class.
    /// </summary>
    /// <param name="toolEndpoints">Serves the tools of sessions to the providers that ask for an MCP endpoint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="toolEndpoints"/> is <see langword="null"/>.</exception>
    public PluginModelProviderTypes(IHarnessToolEndpoints toolEndpoints)
    {
        _toolEndpoints = toolEndpoints ?? throw new ArgumentNullException(nameof(toolEndpoints));
    }

    /// <summary>Gets the provider types the running plugins contribute now.</summary>
    public IReadOnlyList<PluginModelProviderContribution> Types => Volatile.Read(ref _source)();

    /// <summary>
    /// Gets a value that changes when the contributed types change: another type, or the same type from a plugin that
    /// was started again.
    /// </summary>
    public string Signature
        => string.Join('\u001f', Types.Select(static type => $"{type.ProviderType}:{RuntimeHelpers.GetHashCode(type)}"));

    /// <summary>
    /// Gets the <see cref="Signature"/> of the types when their providers were last registered
    /// (<see cref="RegisterOrReplace"/>), or <see langword="null"/> before.
    /// </summary>
    public string? RegisteredSignature { get; private set; }

    /// <summary>Attaches the source of the contributions: the host calls it once its plugins started.</summary>
    /// <param name="source">Returns the contributions of the running plugins.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void Attach(Func<IReadOnlyList<PluginModelProviderContribution>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Volatile.Write(ref _source, source);
    }

    /// <summary>Returns the contribution of a type, or <see langword="null"/> when no running plugin contributes it.</summary>
    /// <param name="providerType">The type.</param>
    public PluginModelProviderContribution? Find(string? providerType)
        => string.IsNullOrWhiteSpace(providerType)
            ? null
            : Types.FirstOrDefault(type => string.Equals(type.ProviderType, providerType.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Creates the settings of a provider of the configuration, as a plugin is given them.</summary>
    /// <param name="provider">The provider.</param>
    /// <param name="defaultProviderKey">The key of the default provider of the configuration, or <see langword="null"/>.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    public static PluginProviderSettings CreateSettings(CodeAltaExternalProvider provider, string? defaultProviderKey)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new PluginProviderSettings(provider.ProviderKey, provider.ProviderType, provider.Values)
        {
            IsDefault = string.Equals(provider.ProviderKey, defaultProviderKey, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Tries to compose the descriptor of a provider of a contributed type and its lazy runtime factory, without
    /// registering or invoking it.
    /// </summary>
    /// <param name="settings">The settings of the provider.</param>
    /// <param name="stateRootPath">The state folder of the application.</param>
    /// <param name="descriptor">The descriptor, or null when the provider is skipped.</param>
    /// <param name="createRuntime">The factory, or null when the provider is skipped.</param>
    /// <returns>Whether a running plugin contributes the type and accepts the settings.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stateRootPath"/> is empty.</exception>
    public bool TryCreateProviderRegistration(
        PluginProviderSettings settings,
        string stateRootPath,
        out ModelProviderDescriptor descriptor,
        out Func<IModelProviderRuntime> createRuntime)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        descriptor = null!;
        createRuntime = null!;
        if (Find(settings.ProviderType) is not { } contribution)
        {
            return false;
        }

        IReadOnlyList<string> problems;
        try
        {
            problems = contribution.Validate?.Invoke(settings) ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems = [ex.Message];
        }

        if (problems.Count > 0)
        {
            Logger.Warn($"Skipped model provider key={settings.ProviderKey} type={settings.ProviderType}: {string.Join(" ", problems)}");
            return false;
        }

        var created = CreateDescriptor(contribution, settings);
        descriptor = created;
        var endpoints = _toolEndpoints;
        createRuntime = () => contribution.CreateRuntime(new PluginModelProviderContext(settings, created, stateRootPath, endpoints)
        {
            Logger = LogManager.GetLogger($"CodeAlta.Provider.{settings.ProviderType}"),
        });
        return true;
    }

    /// <summary>
    /// Registers or replaces the providers of the global configuration whose type a running plugin contributes.
    /// </summary>
    /// <param name="registry">The registry to update.</param>
    /// <param name="store">The configuration store.</param>
    /// <param name="stateRootPath">The state folder of the application.</param>
    /// <param name="includeDisabled">Whether to register the providers that are turned off, as such.</param>
    /// <returns>The descriptors registered, in the order of the configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="stateRootPath"/> is empty.</exception>
    public IReadOnlyList<ModelProviderDescriptor> RegisterOrReplace(
        ModelProviderRegistry registry,
        CodeAltaConfigStore store,
        string stateRootPath,
        bool includeDisabled)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        RegisteredSignature = Signature;
        if (Types.Count == 0)
        {
            return [];
        }

        var defaultProvider = store.GetEffectiveDefaultProvider();
        var registered = new List<ModelProviderDescriptor>();
        foreach (var provider in store.LoadGlobalExternalProviders())
        {
            var settings = CreateSettings(provider, defaultProvider);
            if (!includeDisabled && !settings.IsEnabled)
            {
                continue;
            }

            if (TryCreateProviderRegistration(settings, stateRootPath, out var descriptor, out var createRuntime))
            {
                registry.RegisterOrReplace(descriptor, createRuntime);
                registered.Add(descriptor);
            }
        }

        return registered;
    }

    private static ModelProviderDescriptor CreateDescriptor(PluginModelProviderContribution contribution, PluginProviderSettings settings)
    {
        var permissionMode = settings.PermissionMode;
        return new ModelProviderDescriptor(new ModelProviderId(settings.ProviderKey), settings.DisplayName, contribution.ProviderType)
        {
            IsDefault = settings.IsDefault,
            IsEnabled = settings.IsEnabled,
            DefaultModelId = settings.DefaultModelId,
            DefaultReasoningEffort = settings.DefaultReasoningEffort,
            SortModels = settings.GetBoolean("sort_models") ?? false,
            PermissionModes = contribution.PermissionModes,
            DefaultPermissionMode = permissionMode is not null && contribution.PermissionModes.Contains(permissionMode, StringComparer.Ordinal) ? permissionMode : null,
        };
    }
}
