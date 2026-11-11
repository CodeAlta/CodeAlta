using CodeAlta.Agent;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

// The vaults of the secrets for agents: `alta credential` lists the credential providers and their state, unlocks one with the
// vault's own prompt or forgets what it read, and proposes a provider; `alta secret test|uses|bind|unbind` checks a secret, lists
// its uses and proposes where it is kept. An agent never gets a value, nor the item of a vault a secret names; every change asks
// the user on a card of the session that no permission mode answers. A sandboxed session gets none of these commands (AltaSandboxGate).
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly TimeSpan AgentUnlockPause = TimeSpan.FromMinutes(10);

    private static readonly AltaCommandPolicy[] CredentialPolicies =
    [
        Read("secret test"),
        Read("secret uses"),
        Mutating("secret bind"),
        Mutating("secret unbind"),
        Read("credential types"),
        Read("credential list"),
        Read("credential status"),
        Mutating("credential unlock"),
        Mutating("credential lock"),
        Mutating("credential add"),
        Mutating("credential remove"),
    ];

    private static void AddSecretVaultCommands(Command group, AltaCommandContext context)
    {
        if (context.Services.Get<CredentialBroker>() is null) return;
        group.Add(CreateSecretTestCommand(context));
        group.Add(CreateSecretUsesCommand(context));
        group.Add(CreateSecretBindCommand(context));
        group.Add(CreateSecretUnbindCommand(context));
    }

    private static Command CreateCredentialCommand(AltaCommandContext context)
    {
        var group = Group("credential", "Use the credential providers of this machine: the vaults its secrets are kept in (1Password, Bitwarden, KeePassXC, a command...).");
        group.Add(CreateCredentialTypesCommand(context));
        group.Add(CreateCredentialListCommand(context));
        group.Add(CreateCredentialStatusCommand(context));
        group.Add(CreateCredentialUnlockCommand(context));
        group.Add(CreateCredentialLockCommand(context));
        group.Add(CreateCredentialAddCommand(context));
        group.Add(CreateCredentialRemoveCommand(context));
        AddHelpText(
            group,
            "A secret keeps its name (`${secret:NAME}`); its value can come from an item of a vault. Agents see the providers and the state of each secret, never a value nor the item a secret names.",
            "`unlock` lets the vault show its own prompt (Windows Hello, Touch ID) in CodeAlta Desktop; `add` and `remove` ask the user on a card of the session. A provider of type `command` is added in Settings only.",
            "Examples: `alta credential list`; `alta credential unlock personal`; `alta credential add 1password --id personal`; `alta secret bind ANTHROPIC_API_KEY --provider personal --ref op://Private/Anthropic/credential`.");
        return group;
    }

    private static Command CreateSecretTestCommand(AltaCommandContext context)
    {
        string? name = null;
        var command = Leaf("test", "Say whether a secret can be given now: ready, locked, waits for a confirmation...; never its value.");
        command.Add("<name>", "The name of the secret.", value => name = value);
        command.Add(async (_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return AltaExitCodes.ServiceUnavailable;
            if (!LocalSecretStore.IsName(name)) return UsageError(context, "secret.invalidName", "A name is letters, digits and _, not starting with a digit.", "secret test");
            if (!broker.Store.ListSecrets().Any(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase)))
                return NotFound(context, "secret.notFound", $"No secret is named {name} on this machine.");
            var availability = await broker.ReadSecretAsync(name, context.CancellationToken).ConfigureAwait(false);
            WriteAvailability(context, availability);
            return AltaExitCodes.Success;
        });
        return command;
    }

    private static Command CreateSecretUsesCommand(AltaCommandContext context)
    {
        var command = Leaf("uses", "List the recent uses of the secrets: which secret, by what, when and the outcome; never a value.");
        command.Add((_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return ValueTask.FromResult(AltaExitCodes.ServiceUnavailable);
            foreach (var use in broker.ListUses())
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.secret.use", version = 1, correlationId = context.CorrelationId, name = use.Name, use = use.Use, at = use.At, outcome = use.Outcome });
            }

            foreach (var pending in broker.ListPending())
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.secret.pending", version = 1, correlationId = context.CorrelationId, name = pending.Name, use = pending.Use, at = pending.At });
            }

            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateSecretBindCommand(AltaCommandContext context)
    {
        string? name = null, provider = null, reference = null, label = null;
        var command = Leaf("bind", "Propose to keep a secret in a vault: the user agrees on a card of the session.");
        command.Add("<name>", "The name of the secret.", value => name = value);
        command.Add("provider=", "The id of the credential provider.", value => provider = value);
        command.Add("ref=", "What the provider reads, such as op://Private/Anthropic/credential.", value => reference = value);
        command.Add("label=", "What the list of the secrets shows; the reference when left out.", value => label = value);
        command.Add(async (_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return AltaExitCodes.ServiceUnavailable;
            if (!LocalSecretStore.IsName(name)) return UsageError(context, "secret.invalidName", "A name is letters, digits and _, not starting with a digit.", "secret bind");
            if (string.IsNullOrWhiteSpace(provider) || broker.GetProvider(provider) is not { } view) return NotFound(context, "credential.notFound", $"No credential provider is named '{provider}'.");
            if (string.IsNullOrWhiteSpace(reference) || reference.Length > SecretBindings.MaximumTextLength) return UsageError(context, "secret.invalidReference", "--ref is required.", "secret bind");
            if (broker.Store.ListSecrets().FirstOrDefault(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase)) is { Provider: null })
                return PermissionDenied(context, "secret.hasLocalValue", $"{name} has a value on this computer: the user replaces it in Settings > Secrets of CodeAlta Desktop.");
            var shown = string.IsNullOrWhiteSpace(label) ? reference.Trim() : label.Trim();
            if (await AskCredentialAsync(context, "bind", name, BindCardDetail(view, reference.Trim(), shown)).ConfigureAwait(false) is { } refused) return refused;
            broker.Store.Bind(name, view.Id, reference.Trim(), shown, AgentSetBy(context));
            var availability = await broker.ReadSecretAsync(name, context.CancellationToken).ConfigureAwait(false);
            WriteAvailability(context, availability);
            return AltaExitCodes.Success;
        });
        return command;
    }

    private static Command CreateSecretUnbindCommand(AltaCommandContext context)
    {
        string? name = null;
        var command = Leaf("unbind", "Propose to stop keeping a secret in a vault: the secret is removed from this machine; nothing changes in the vault.");
        command.Add("<name>", "The name of the secret.", value => name = value);
        command.Add(async (_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return AltaExitCodes.ServiceUnavailable;
            if (!LocalSecretStore.IsName(name) || !broker.Store.Bindings.TryGet(name, out var binding)) return NotFound(context, "secret.notBound", $"{name} is not kept in a vault.");
            if (await AskCredentialAsync(context, "unbind", binding.Name, $"It is kept in {ProviderOf(broker.GetProvider(binding.Provider), binding.Provider)} as {Quote(binding.Reference)}; it is removed from this machine, and nothing changes in the vault.")
                .ConfigureAwait(false) is { } refused) return refused;
            broker.Store.Bindings.Unbind(binding.Name);
            AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.secret.unbound", version = 1, correlationId = context.CorrelationId, name = binding.Name });
            return AltaExitCodes.Success;
        });
        return command;
    }

    private static Command CreateCredentialTypesCommand(AltaCommandContext context)
    {
        var command = Leaf("types", "List the types of credential providers this machine knows, with their settings.");
        command.Add((_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return ValueTask.FromResult(AltaExitCodes.ServiceUnavailable);
            foreach (var type in broker.Types.List())
            {
                AltaJsonlWriter.WriteRecord(context.Stdout, new
                {
                    type = "alta.credential.type", version = 1, correlationId = context.CorrelationId, providerType = type.Type, displayName = type.DisplayName,
                    description = type.Description, builtIn = broker.Types.IsBuiltIn(type.Type), unlockAsksPassword = type.UnlockAsksPassword, referenceHint = type.ReferenceHint,
                    settings = type.Settings.Select(static setting => new { key = setting.Key, label = setting.Label, kind = setting.Kind.ToString().ToLowerInvariant(), required = setting.Required }).ToArray(),
                });
            }

            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateCredentialListCommand(AltaCommandContext context)
    {
        var command = Leaf("list", "List the credential providers of this machine and their state: ready, locked, sign-in needed, not installed.");
        command.Add((_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return ValueTask.FromResult(AltaExitCodes.ServiceUnavailable);
            foreach (var provider in broker.ListProviders()) WriteProvider(context, provider);
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateCredentialStatusCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("status", "Check a credential provider now, without a prompt, and say what it needs.");
        command.Add("<id>", "The id of the provider.", value => id = value);
        command.Add(async (_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return AltaExitCodes.ServiceUnavailable;
            if (string.IsNullOrWhiteSpace(id) || broker.GetProvider(id) is null) return NotFound(context, "credential.notFound", $"No credential provider is named '{id}'.");
            WriteProvider(context, await broker.TestProviderAsync(id, context.CancellationToken).ConfigureAwait(false));
            return AltaExitCodes.Success;
        });
        return command;
    }

    private static Command CreateCredentialUnlockCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("unlock", "Ask a vault to unlock: it shows its own prompt (Windows Hello, Touch ID) to the user of CodeAlta Desktop.");
        command.Add("<id>", "The id of the provider.", value => id = value);
        command.Add(async (_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return AltaExitCodes.ServiceUnavailable;
            if (string.IsNullOrWhiteSpace(id) || broker.GetProvider(id) is not { } view) return NotFound(context, "credential.notFound", $"No credential provider is named '{id}'.");
            if (!broker.CanPrompt) return PermissionDenied(context, "credential.noPrompt", "This instance of CodeAlta asks no vault to unlock: ask the user to unlock it in the normal instance of CodeAlta Desktop, or, in the developer instance, to turn on Let the developer instance unlock the vaults in Settings › Secrets.");
            if (view.UnlockAsksPassword) return PermissionDenied(context, "credential.password", $"{view.DisplayName} takes its master password: ask the user to unlock it in Settings > Secrets of CodeAlta Desktop.");
            if (!broker.TryBeginAgentUnlock(view.Id, AgentUnlockPause))
                return PermissionDenied(context, "credential.unlockPaused", $"The user dismissed the unlock of {view.DisplayName} a moment ago: it is not asked again for a while. Ask the user instead.");
            var after = await broker.UnlockAsync(view.Id, null, context.CancellationToken).ConfigureAwait(false);
            if (after.Status.State != CredentialProviderState.Ready) broker.EndAgentUnlock(view.Id, dismissed: true);
            else broker.EndAgentUnlock(view.Id, dismissed: false);
            WriteProvider(context, after);
            return after.Status.State == CredentialProviderState.Ready ? AltaExitCodes.Success : AltaExitCodes.PolicyDenied;
        });
        return command;
    }

    private static Command CreateCredentialLockCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("lock", "Forget the values a provider read, or those of every provider: the next use needs its vault again.");
        command.Add("<id>?", "The id of the provider; every provider when left out.", value => id = value);
        command.Add((_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return ValueTask.FromResult(AltaExitCodes.ServiceUnavailable);
            if (!string.IsNullOrWhiteSpace(id) && broker.GetProvider(id) is null) return ValueTask.FromResult(NotFound(context, "credential.notFound", $"No credential provider is named '{id}'."));
            broker.Lock(string.IsNullOrWhiteSpace(id) ? null : id);
            AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.credential.locked", version = 1, correlationId = context.CorrelationId, provider = id });
            return ValueTask.FromResult(AltaExitCodes.Success);
        });
        return command;
    }

    private static Command CreateCredentialAddCommand(AltaCommandContext context)
    {
        string? type = null, id = null, name = null;
        var settings = new List<string>();
        var command = Leaf("add", "Propose a credential provider: the user agrees on a card of the session. A command, or the path of a program, is set in Settings only.");
        command.Add("<type>", "Its type (`alta credential types`).", value => type = value);
        command.Add("id=", "Its id: lowercase letters, digits, - and _; the type when left out.", value => id = value);
        command.Add("name=", "The name the list shows.", value => name = value);
        command.Add("set=", "A setting, as key=value; repeat for several.", value => { if (value is not null) settings.Add(value); });
        command.Add(async (_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return AltaExitCodes.ServiceUnavailable;
            if (broker.Types.Find(type) is not { } contribution) return NotFound(context, "credential.typeNotFound", $"No credential provider of type '{type}' is known: see `alta credential types`.");
            // A command, or a program named by its path, would let an agent choose what CodeAlta runs to read the secrets.
            if (contribution.Type == "command") return PermissionDenied(context, "credential.commandRefused", "A provider that runs a command is added by the user in Settings > Secrets.");
            var providerId = string.IsNullOrWhiteSpace(id) ? contribution.Type : id.Trim();
            if (!SecretBindings.IsProviderId(providerId)) return UsageError(context, "credential.invalidId", "An id is lowercase letters, digits, - and _, starting with a letter.", "credential add");
            if (broker.GetProvider(providerId) is not null) return UsageError(context, "credential.exists", $"A credential provider is named '{providerId}' already.", "credential add");
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var item in settings)
            {
                var at = item.IndexOf('=');
                var key = at > 0 ? item[..at].Trim() : item.Trim();
                var value = at > 0 ? item[(at + 1)..] : string.Empty;
                if (contribution.Settings.FirstOrDefault(setting => setting.Key == key) is not { } setting) return UsageError(context, "credential.unknownSetting", $"'{key}' is not a setting of {contribution.DisplayName}.", "credential add");
                if (setting.Kind == PluginCredentialProviderSettingKind.Path) return PermissionDenied(context, "credential.pathRefused", $"The path of a program is set by the user in Settings > Secrets.");
                if (setting.Kind == PluginCredentialProviderSettingKind.Secret && (!SecretReferences.TryParseLone(value.Trim(), out var parsed) || parsed.Kind != SecretReferenceKind.Secret))
                    return UsageError(context, "credential.secretSetting", $"'{key}' names a secret of this computer, such as ${{secret:NAME}}, never its value.", "credential add");
                values[key] = setting.Kind switch
                {
                    PluginCredentialProviderSettingKind.Boolean => value.Trim() is "true" or "1" or "yes",
                    PluginCredentialProviderSettingKind.List => value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static part => (object?)part).ToArray(),
                    _ => value.Trim(),
                };
            }

            if (contribution.Settings.FirstOrDefault(setting => setting.Required && !values.ContainsKey(setting.Key)) is { } missing)
                return UsageError(context, "credential.missingSetting", $"The setting '{missing.Key}' is required.", "credential add");
            if (await AskCredentialAsync(context, "add_provider", providerId, ProviderCardDetail(contribution, providerId, name, values)).ConfigureAwait(false) is { } refused) return refused;
            broker.Settings.Save(new CredentialProviderDefinition(providerId, contribution.Type, values) { DisplayName = string.IsNullOrWhiteSpace(name) ? null : name.Trim() });
            WriteProvider(context, await broker.TestProviderAsync(providerId, context.CancellationToken).ConfigureAwait(false));
            return AltaExitCodes.Success;
        });
        return command;
    }

    private static Command CreateCredentialRemoveCommand(AltaCommandContext context)
    {
        string? id = null;
        var command = Leaf("remove", "Propose to remove a credential provider and the secrets kept in its vault: the user agrees on a card. Nothing changes in the vault.");
        command.Add("<id>", "The id of the provider.", value => id = value);
        command.Add(async (_, _) =>
        {
            if (!TryGetBroker(context, out var broker)) return AltaExitCodes.ServiceUnavailable;
            if (string.IsNullOrWhiteSpace(id) || broker.GetProvider(id) is not { } view) return NotFound(context, "credential.notFound", $"No credential provider is named '{id}'.");
            if (await AskCredentialAsync(context, "remove_provider", view.Id, view.Secrets == 0 ? $"{view.DisplayName}; nothing changes in the vault."
                : $"{view.DisplayName} and its {view.Secrets} secrets; nothing changes in the vault.").ConfigureAwait(false) is { } refused) return refused;
            var names = broker.Store.Bindings.UnbindProvider(view.Id);
            broker.SignIns.RemoveProvider(view.Id);
            broker.Lock(view.Id);
            broker.Settings.Remove(view.Id);
            AltaJsonlWriter.WriteRecord(context.Stdout, new { type = "alta.credential.removed", version = 1, correlationId = context.CorrelationId, provider = view.Id, secrets = names.ToArray() });
            return AltaExitCodes.Success;
        });
        return command;
    }

    // The card of the session of the agent, which no permission mode answers: null when the user allowed it, else the exit code.
    private static async Task<int?> AskCredentialAsync(AltaCommandContext context, string action, string subject, string detail)
    {
        if (context.Caller.Kind != "agent" || context.Caller.SourceSessionId is not { Length: > 0 } sessionId
            || context.Services.Get<Orchestration.Runtime.SessionRuntimeService>() is not { } runtime)
            return PermissionDenied(context, "credential.noUser", "Only the user changes where the secrets are kept: in Settings > Secrets of CodeAlta Desktop.");
        var request = new AgentCredentialPermissionRequest(new ModelProviderId("alta"), sessionId, DateTimeOffset.UtcNow, null, Guid.NewGuid().ToString("N"), action, subject, detail);
        var decision = await runtime.Permissions.RequestForSessionAsync(sessionId, request, context.CancellationToken).ConfigureAwait(false);
        return decision.Kind is AgentPermissionDecisionKind.AllowOnce or AgentPermissionDecisionKind.AllowForSession or AgentPermissionDecisionKind.AllowAlways
            ? null
            : PermissionDenied(context, "credential.denied", decision.DescribeDenial());
    }

    // What a card says of a binding: the vault and the exact reference it reads, whatever label the agent gave; the label
    // is said apart, as what the list will show.
    internal static string BindCardDetail(CredentialProviderView provider, string reference, string label)
        => $"In {ProviderOf(provider, provider.Id)}: {Quote(reference)}{(string.Equals(label, reference, StringComparison.Ordinal) ? string.Empty : $", listed as {Quote(label)}")}.";

    // What a card says of a new provider: its type, its id and every setting with its value as it will be written (a secret
    // setting is a reference to a secret of this computer, never a value), whatever name the agent gave it.
    internal static string ProviderCardDetail(PluginCredentialProviderContribution contribution, string id, string? name, IReadOnlyDictionary<string, object?> values)
    {
        var order = contribution.Settings.Select(static setting => setting.Key).ToList();
        var settings = values.OrderBy(pair => order.IndexOf(pair.Key)).ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key} = {Format(pair.Value)}").ToArray();
        return $"A provider of type {contribution.Type} ({contribution.DisplayName}), id {id}{(string.IsNullOrWhiteSpace(name) ? string.Empty : $", listed as {Quote(name.Trim())}")}. "
            + (settings.Length == 0 ? "No settings." : $"Settings: {string.Join("; ", settings)}.");

        static string Format(object? value) => value switch
        {
            null => "(none)",
            bool flag => flag ? "true" : "false",
            IEnumerable<object?> list when value is not string => $"[{string.Join(", ", list.Select(static item => Quote(item?.ToString() ?? string.Empty)))}]",
            _ => Quote(value.ToString() ?? string.Empty),
        };
    }

    // A provider as a card names it: its name, its id and its type, which an agent does not choose for an existing one.
    private static string ProviderOf(CredentialProviderView? provider, string id)
        => provider is null ? $"the provider {id}" : $"{provider.DisplayName} (provider {provider.Id}, type {provider.Type})";

    // A text as a card shows it: quoted, its quotes and control characters escaped, so that it cannot look like the rest of the card.
    private static string Quote(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c) || (int)c is 0x200E or 0x200F or 0x2028 or 0x2029 or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069))
                        builder.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else builder.Append(c);
                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static string AgentSetBy(AltaCommandContext context) => $"an agent, allowed by {Environment.UserName} (CodeAlta Desktop)";

    private static bool TryGetBroker(AltaCommandContext context, out CredentialBroker broker)
        => context.TryGetRequired(nameof(CredentialBroker), out broker);

    private static void WriteProvider(AltaCommandContext context, CredentialProviderView provider)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.credential", version = 1, correlationId = context.CorrelationId, id = provider.Id, providerType = provider.Type, displayName = provider.DisplayName,
            state = StateOf(provider.Status.State), message = provider.Status.Message, account = provider.Status.Account, secrets = provider.Secrets, held = provider.Held,
            signInCommand = provider.Status.SignInCommand, installCommand = provider.Status.Install?.Command, installPage = provider.Status.Install?.Page?.ToString(),
        });

    private static void WriteAvailability(AltaCommandContext context, SecretAvailability availability)
        => AltaJsonlWriter.WriteRecord(context.Stdout, new
        {
            type = "alta.secret.availability", version = 1, correlationId = context.CorrelationId, name = availability.Name, provider = availability.Provider,
            state = availability.State switch
            {
                SecretAvailabilityState.Ready => "ready",
                SecretAvailabilityState.Locked => "locked",
                SecretAvailabilityState.SignInNeeded => "sign_in",
                SecretAvailabilityState.NotInstalled => "not_installed",
                SecretAvailabilityState.WaitsForYou => "waits",
                SecretAvailabilityState.NotFound => "not_found",
                _ => "unavailable",
            },
            message = availability.Message,
        });

    private static string StateOf(CredentialProviderState state) => state switch
    {
        CredentialProviderState.Ready => "ready",
        CredentialProviderState.Locked => "locked",
        CredentialProviderState.SignInNeeded => "sign_in",
        CredentialProviderState.NotInstalled => "not_installed",
        _ => "unavailable",
    };
}
