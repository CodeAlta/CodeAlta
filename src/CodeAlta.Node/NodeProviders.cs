using System.Collections.Immutable;
using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.Hosting;
using CodeAlta.Orchestration.Hosting;

namespace CodeAlta.Node;

/// <summary>
/// The providers of a running node for its owner and the devices that manage it (<c>control.providers.*</c>,
/// <c>doc/fleet/node.md</c>, credentials on a node): each provider of its configuration with whether it is ready and why not,
/// a provider added or changed with its key kept in the secrets store of the machine (the configuration names that secret,
/// never the key), and the device-code sign-in of the providers that have one, run here while the desktop shows the code.
/// </summary>
/// <param name="host">The runtime of the node.</param>
/// <param name="modelCatalog">The catalog of models the providers are started with; null for none.</param>
internal sealed class NodeProviders(CodeAltaHost host, ModelsDevCatalogService? modelCatalog) : IFleetNodeProviders
{
    /// <summary>The most providers listed.</summary>
    internal const int MaxProviders = 64;

    // How long a sign-in may wait for the person; the device codes of the providers expire before.
    private static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(15);

    // How long the start of a sign-in waits for its code.
    private static readonly TimeSpan PromptWait = TimeSpan.FromSeconds(60);

    /// <summary>The types a provider added from the desktop can have: those that take a key, and those that sign in with a device code.</summary>
    internal static readonly ImmutableArray<string> OfferedTypes = ["openai-responses", "anthropic", "google-genai", "mistral", "openai-chat", "copilot", "xai"];

    // The provider types that run a CLI with its own login on the machine, and those that need no credential.
    private static readonly ImmutableArray<string> CliTypes = ["claude-code", "mistral-vibe", "codex-cli"];
    private static readonly ImmutableArray<string> NoCredentialTypes = ["llama-cpp"];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SignIn> _signIns = new(StringComparer.OrdinalIgnoreCase);

    private CodeAltaConfigStore Store => new(host.CatalogOptions);

    private string Root => host.CatalogOptions.GlobalRoot;

    /// <inheritdoc />
    public async Task<FleetProviderList> ListAsync(CancellationToken cancellationToken)
    {
        var store = Store;
        var definitions = store.LoadGlobalProviderDefinitions(includeDisabled: true).Take(MaxProviders).ToArray();
        var protection = TryRead(() => new LocalSecretStore(Root).Protection);
        var types = OfferedTypes.Select(static type => new FleetProviderType(type, TypeName(type), Credential(type), SignsInWithCode(type))).ToArray();
        var providers = new List<FleetProviderStatus>(definitions.Length);
        foreach (var definition in definitions)
        {
            var status = Describe(definition, store);
            // A provider that signs in with an account and is not ready: its stored sign-in says whether that is the reason (its start
            // then fails on a probe that times out, which says nothing to the person).
            if (status.Credential == "sign_in" && status.State is FleetProviderStates.Failed or FleetProviderStates.NotReady && !await IsSignedInAsync(definition, cancellationToken).ConfigureAwait(false))
                status = status with { State = FleetProviderStates.NotReady, Reason = FleetProviderReasons.SignInNeeded, Detail = null };
            providers.Add(status);
        }

        return new FleetProviderList(providers, types, protection);
    }

    // Whether a provider is signed in, as its stored credential says, without the network; a credential that cannot be read is not.
    private async Task<bool> IsSignedInAsync(CodeAltaProviderDocument definition, CancellationToken cancellationToken)
    {
        try
        {
            return (await ConfiguredProviderLogin.GetStatusAsync(definition, Root, cancellationToken).ConfigureAwait(false)).SignedIn;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<FleetProviderStatus> SetAsync(FleetProviderSetParams parameters, string setBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrWhiteSpace(setBy);
        var key = parameters.Key.Trim();
        var store = Store;
        lock (_gate)
        {
            var document = store.LoadGlobal();
            var definitions = (document.Providers ?? []).Select(static pair => { pair.Value.ProviderKey = pair.Key; return pair.Value; }).ToList();
            var definition = definitions.FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase));
            var type = Optional(parameters.Type);
            if (definition is null)
            {
                if (type is null || !OfferedTypes.Contains(type, StringComparer.Ordinal))
                    throw new ArgumentException($"A new provider has one of the types: {string.Join(", ", OfferedTypes)}.", nameof(parameters));
                if (definitions.Count >= MaxProviders) throw new ArgumentException($"A node has at most {MaxProviders} providers.", nameof(parameters));
                definitions.Add(definition = new CodeAltaProviderDocument { ProviderKey = key, ProviderType = type });
            }
            else if (type is not null && !string.Equals(type, definition.ProviderType, StringComparison.Ordinal))
            {
                if (!OfferedTypes.Contains(type, StringComparer.Ordinal)) throw new ArgumentException($"A provider changed from the desktop has one of the types: {string.Join(", ", OfferedTypes)}.", nameof(parameters));
                definition.ProviderType = type;
            }

            // A value given replaces the one written, an empty one clears it; a value not given stays.
            if (parameters.DisplayName is not null) definition.DisplayName = Optional(parameters.DisplayName);
            if (parameters.Model is not null) definition.Model = Optional(parameters.Model);
            if (parameters.ApiUrl is not null)
            {
                var url = Optional(parameters.ApiUrl);
                if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http")))
                    throw new ArgumentException("An endpoint is an http or https address.", nameof(parameters));
                definition.ApiUrl = url;
            }

            definition.Enabled = parameters.Enabled;
            if (Optional(parameters.ApiKey) is { } apiKey)
            {
                // The key goes to the secrets store of the machine; the configuration names the secret, never the key.
                var name = SecretNameOf(definition.ProviderKey);
                new LocalSecretStore(Root).SetSecret(name, apiKey, setBy);
                definition.ApiKeySecret = name;
                definition.ApiKey = null;
            }
            else if (Credential(definition.ProviderType) == "key" && Optional(definition.ApiKey) is null && Optional(definition.ApiKeySecret) is null && Optional(definition.ApiKeyEnv) is null)
            {
                // A provider given no key yet names the secret its key will be: it says which one it waits for until the key is given.
                definition.ApiKeySecret = SecretNameOf(definition.ProviderKey);
            }

            store.SaveGlobalProviderDefinitions(definitions);
            if (parameters.MakeDefault && parameters.Enabled) store.SaveGlobalDefaultProvider(definition.ProviderKey);
            else if (!parameters.Enabled && string.Equals(store.GetEffectiveDefaultProvider(), definition.ProviderKey, StringComparison.OrdinalIgnoreCase))
                store.SaveGlobalDefaultProvider(null);
            Apply(store, definition.ProviderKey);
            var saved = store.LoadGlobalProviderDefinitions(includeDisabled: true).FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase))
                ?? definition;
            return Task.FromResult(Describe(saved, store));
        }
    }

    /// <inheritdoc />
    public async Task<FleetProviderSignInPrompt> StartSignInAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var definition = Store.LoadGlobalProviderDefinitions(includeDisabled: true).FirstOrDefault(value => string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No provider of this node has the key {key}.");
        if (!SignsInWithCode(definition.ProviderType))
        {
            throw new NotSupportedException(ConfiguredProviderLogin.SupportsLogin(definition.ProviderType)
                ? $"{TypeName(definition.ProviderType)} signs in with a browser on the machine itself: sign in there, or over SSH."
                : $"{TypeName(definition.ProviderType)} takes a key, not a sign-in.");
        }

        SignIn signIn;
        lock (_gate)
        {
            // One sign-in of a provider at a time: a start while one waits gives its code again.
            if (!_signIns.TryGetValue(definition.ProviderKey, out var running) || running.Completion.IsCompleted)
            {
                running = new SignIn();
                running.Completion = RunSignInAsync(definition, running);
                _signIns[definition.ProviderKey] = running;
            }

            signIn = running;
        }

        var first = await Task.WhenAny(signIn.Prompt.Task, signIn.Completion).WaitAsync(PromptWait, cancellationToken).ConfigureAwait(false);
        if (first != signIn.Prompt.Task)
        {
            var ended = await signIn.Completion.ConfigureAwait(false);
            throw new InvalidOperationException(ended.Status == "completed" ? "The provider is signed in already." : $"The sign-in did not start ({ended.Code}).");
        }

        var prompt = await signIn.Prompt.Task.ConfigureAwait(false);
        return new FleetProviderSignInPrompt(definition.ProviderKey, prompt.Uri.AbsoluteUri, prompt.UserCode, prompt.ExpiresAt);
    }

    /// <inheritdoc />
    public async Task<FleetProviderSignInState> WaitSignInAsync(string key, TimeSpan wait, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        SignIn? signIn;
        lock (_gate) _signIns.TryGetValue(key, out signIn);
        if (signIn is null) return new FleetProviderSignInState(key, "none", null, null);
        try
        {
            return await signIn.Completion.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new FleetProviderSignInState(key, "waiting", null, null);
        }
    }

    // The sign-in, run beside the calls that started it: its first prompt is the code the desktop shows; once signed in, the
    // provider is enabled and the providers are started again, so that it is ready without a restart. Never throws.
    private Task<FleetProviderSignInState> RunSignInAsync(CodeAltaProviderDocument definition, SignIn signIn) => Task.Run(async () =>
    {
        using var lifetime = new CancellationTokenSource(SignInLifetime);
        try
        {
            var status = await ConfiguredProviderLogin.LoginAsync(definition, Root, "device", (prompt, _) =>
            {
                signIn.Prompt.TrySetResult(prompt);
                return ValueTask.CompletedTask;
            }, lifetime.Token).ConfigureAwait(false);
            if (status.Usable)
            {
                lock (_gate)
                {
                    var store = Store;
                    if (definition.Enabled == false)
                    {
                        var raw = (store.LoadGlobal().Providers ?? []).Select(static pair => { pair.Value.ProviderKey = pair.Key; return pair.Value; }).ToList();
                        if (raw.FirstOrDefault(value => string.Equals(value.ProviderKey, definition.ProviderKey, StringComparison.OrdinalIgnoreCase)) is { } written)
                        {
                            written.Enabled = true;
                            store.SaveGlobalProviderDefinitions(raw);
                        }
                    }

                    Apply(store, definition.ProviderKey);
                }
            }

            return new FleetProviderSignInState(definition.ProviderKey, status.SignedIn ? "completed" : "failed", status.Account, status.SignedIn ? null : "login_failed");
        }
        catch (OperationCanceledException)
        {
            return new FleetProviderSignInState(definition.ProviderKey, "failed", null, lifetime.IsCancellationRequested ? "timeout" : "canceled");
        }
        catch (TimeoutException)
        {
            return new FleetProviderSignInState(definition.ProviderKey, "failed", null, "timeout");
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or ArgumentException or HttpRequestException or UnauthorizedAccessException)
        {
            return new FleetProviderSignInState(definition.ProviderKey, "failed", null, "login_failed");
        }
        finally
        {
            // A sign-in that ends without a prompt ends the wait of its start.
            signIn.Prompt.TrySetCanceled();
        }
    });

    /// <summary>
    /// Runs the providers a change of the configuration made by the fleet touched as they are now: one given or redefined, or
    /// whose key was rotated, is registered again with its key; one taken away is unregistered, so that nothing uses it or its
    /// key any more. The others stay as they are.
    /// </summary>
    /// <param name="changed">The providers changed.</param>
    internal void Reconcile(IReadOnlyCollection<string> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        var store = Store;
        foreach (var provider in changed.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lock (_gate) Apply(store, provider);
        }
    }

    // Starts the providers of the configuration again, as Settings > Providers does on the desktop: the one changed is registered
    // (or replaced) when it can run, and unregistered when it cannot any more. The others, and those of plugins, stay as they are.
    // While a provider is still prepared (its models listed), the runtime refuses to replace it: the change is saved, and applied
    // once that preparation ended.
    private void Apply(CodeAltaConfigStore store, string changed)
    {
        try
        {
            ApplyNow(store, changed);
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            _ = ApplyLaterAsync(changed);
        }
    }

    // The deferred apply: tried again every half second for a minute, then left to the next start of the node.
    private async Task ApplyLaterAsync(string changed)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            try
            {
                lock (_gate) ApplyNow(Store, changed);
                return;
            }
            catch (ObjectDisposedException)
            {
                // The node stops: its next start applies the configuration.
                return;
            }
            catch (InvalidOperationException)
            {
                // Still prepared: tried again.
            }
        }
    }

    private void ApplyNow(CodeAltaConfigStore store, string changed)
    {
        var registry = host.ModelProviderRegistry;
        var enabled = store.LoadGlobalProviderDefinitions(includeDisabled: true).Where(static definition => definition.Enabled != false).ToArray();
        var applied = ConfiguredModelProviderRegistryBuilder.RegisterOrReplaceConfiguredProviders(registry, enabled, Root, modelCatalog);
        if (!applied.Any(descriptor => string.Equals(descriptor.ProviderId.Value, changed, StringComparison.OrdinalIgnoreCase))
            && registry.ListProviders(includeDisabled: true).FirstOrDefault(descriptor => string.Equals(descriptor.ProviderId.Value, changed, StringComparison.OrdinalIgnoreCase)) is { } left)
            registry.Unregister(left.ProviderId);
        // Prepared now, so that the desktop sees it ready (or why not) without starting a session first.
        var id = applied.FirstOrDefault(descriptor => string.Equals(descriptor.ProviderId.Value, changed, StringComparison.OrdinalIgnoreCase))?.ProviderId;
        if (id is { } provider) _ = PrepareAsync(provider);
    }

    private async Task PrepareAsync(ModelProviderId provider)
    {
        try { _ = await host.ModelProviderInitializationService.GetModelsAsync(provider, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { /* Its state says why it is not ready. */ }
    }

    // A provider of the configuration as the desktop shows it: its state, from the runtime when it was started, else why it was
    // left out (the credential it lacks), read from the configuration and the secrets of the machine.
    private FleetProviderStatus Describe(CodeAltaProviderDocument definition, CodeAltaConfigStore store)
    {
        var key = definition.ProviderKey;
        var type = definition.ProviderType ?? "";
        var credential = Credential(type);
        var keySecret = KeySecretOf(definition);
        var isDefault = string.Equals(TryRead(() => store.GetEffectiveDefaultProvider()), key, StringComparison.OrdinalIgnoreCase);
        var name = string.IsNullOrWhiteSpace(definition.DisplayName) ? TypeName(type) : definition.DisplayName!;
        var state = host.ModelProviderInitializationService.CurrentStates.FirstOrDefault(value => string.Equals(value.ProviderId.Value, key, StringComparison.OrdinalIgnoreCase));
        string status;
        string? reason = null, detail = null;
        if (definition.Enabled == false) status = FleetProviderStates.Disabled;
        // A key that is the secret of a machine and is not set: whatever runs, it cannot sign its requests.
        else if (credential == "key" && keySecret is not null && !IsSecretSet(keySecret))
        {
            status = FleetProviderStates.NotReady;
            (reason, detail) = (FleetProviderReasons.SecretMissing, keySecret);
        }
        else if (state is not null && state.Descriptor.IsEnabled)
        {
            (status, reason, detail) = state.Availability switch
            {
                ModelProviderAvailability.Ready => (FleetProviderStates.Ready, (string?)null, (string?)null),
                // What a provider says of its failure may hold its key (a gateway that says it back): masked before it is cut.
                ModelProviderAvailability.Failed => (FleetProviderStates.Failed, FleetProviderReasons.StartFailed,
                    Bounded(Masked(definition, state.StatusMessage)) ?? state.ErrorCategory?.ToString()),
                _ => (FleetProviderStates.Preparing, null, null),
            };
        }
        else
        {
            status = FleetProviderStates.NotReady;
            (reason, detail) = credential switch
            {
                "sign_in" => (FleetProviderReasons.SignInNeeded, (string?)null),
                "key" => KeyProblem(definition, keySecret),
                _ => (FleetProviderReasons.Skipped, null),
            };
        }

        return new FleetProviderStatus(key, Bounded(name)!, type, definition.Enabled != false, status, reason, detail, credential, keySecret, SignsInWithCode(type), isDefault,
            Bounded(definition.Model), Bounded(Masked(definition, definition.ApiUrl)));
    }

    // Why a provider that takes a key has none: a secret named and not set (its name), else no key at all (the variable it waits for).
    private (string Reason, string? Detail) KeyProblem(CodeAltaProviderDocument definition, string? keySecret)
    {
        if (keySecret is not null && !IsSecretSet(keySecret)) return (FleetProviderReasons.SecretMissing, keySecret);
        if (keySecret is null && Optional(definition.ApiKeyEnv) is { } variable) return (FleetProviderReasons.KeyMissing, variable);
        return (FleetProviderReasons.KeyMissing, null);
    }

    private bool IsSecretSet(string name)
        => TryRead(() => new LocalSecretStore(Root).ListSecrets().Any(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase)));

    // The secret of the machine that holds the key: the one api_key_secret names, or the one reference api_key is.
    private static string? KeySecretOf(CodeAltaProviderDocument definition)
    {
        if (Optional(definition.ApiKey) is { } literal)
            return SecretReferences.TryParseLone(literal, out var reference) && reference.Kind == SecretReferenceKind.Secret ? reference.Name : null;
        return Optional(definition.ApiKeySecret);
    }

    /// <summary>The name of the secret of the machine that holds the key of a provider: <c>PROVIDER_&lt;KEY&gt;_API_KEY</c>.</summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <returns>The name, a valid name of a secret.</returns>
    internal static string SecretNameOf(string providerKey)
    {
        var name = "PROVIDER_" + new string([.. providerKey.ToUpperInvariant().Select(static character => char.IsAsciiLetterOrDigit(character) ? character : '_')]) + "_API_KEY";
        return LocalSecretStore.IsName(name) ? name : "PROVIDER_API_KEY";
    }

    /// <summary>How a provider type gets its credential: <c>sign_in</c>, <c>cli</c>, <c>none</c> or <c>key</c>.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The kind of credential.</returns>
    internal static string Credential(string? type)
        => ConfiguredProviderLogin.SupportsLogin(type) ? "sign_in"
            : type is not null && CliTypes.Contains(type, StringComparer.Ordinal) ? "cli"
            : type is not null && NoCredentialTypes.Contains(type, StringComparer.Ordinal) ? "none"
            : "key";

    /// <summary>Whether a provider type signs in with a device code that another computer shows.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Whether it does.</returns>
    internal static bool SignsInWithCode(string? type) => ConfiguredProviderLogin.GetLoginModes(type).Contains("device", StringComparer.Ordinal);

    private static string TypeName(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Provider";
        try { return CodeAltaConfigStore.CreateProviderTypeDefaults(type).DisplayName ?? type; }
        catch (ArgumentException) { return type; }
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Bounded(string? value) => value is null ? null : value.Length <= 512 ? value : value[..512];

    // A text of a provider as the desktop may see it: the secrets of the machine masked, and the key of this provider wherever it
    // comes from (the configuration, a secret, a variable of the environment), never sent back.
    private string? Masked(CodeAltaProviderDocument definition, string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var masked = host.AgentHub.SecretMasker?.MaskText(text) ?? text;
        foreach (var key in KeysOf(definition).Where(static key => key.Length >= CodeAlta.Agent.Runtime.AgentSecretMasker.MinimumLength).OrderByDescending(static key => key.Length))
            masked = masked.Replace(key, CodeAlta.Agent.Runtime.AgentSecretMasker.Mask, StringComparison.Ordinal);
        return masked;
    }

    private IEnumerable<string> KeysOf(CodeAltaProviderDocument definition)
    {
        if (Optional(definition.ApiKey) is { } literal && !SecretReferences.TryParseLone(literal, out _)) yield return literal;
        if (KeySecretOf(definition) is { } secret)
        {
            string? value = null;
            try { new LocalSecretStore(Root).TryGetSecret(secret, out value); }
            catch (Exception exception) when (exception is SecretStoreUnavailableException or InvalidDataException or IOException or UnauthorizedAccessException) { }
            if (value is not null) yield return value;
        }

        if (Optional(definition.ApiKeyEnv) is { } variable && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnvironment) yield return fromEnvironment;
    }

    private static T? TryRead<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or SecretStoreUnavailableException)
        {
            return default;
        }
    }

    // A sign-in of a provider: the code it gives first, and how it ends.
    private sealed class SignIn
    {
        public TaskCompletionSource<ProviderLoginPrompt> Prompt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<FleetProviderSignInState> Completion { get; set; } = Task.FromResult(new FleetProviderSignInState("", "none", null, null));
    }
}
