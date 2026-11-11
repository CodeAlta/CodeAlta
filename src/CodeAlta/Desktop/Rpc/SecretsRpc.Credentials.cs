using System.Globalization;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Credentials;
using CodeAlta.Plugins.Abstractions;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Settings > Secrets, the vaults: the credential providers of the user, the types the window offers, the picker of a vault, the
// bindings of the secrets to its items, the unlock and the lock, and the confirmations of the secrets that ask before use. No
// method returns a value.
internal sealed partial class SecretsService
{
    private const int MaximumSettingLength = 4096;

    /// <summary>Gets the credential broker of the host; null for a host without it, where the page shows no vault.</summary>
    internal CredentialBroker? Credentials { get; init; }

    /// <summary>Adds or changes a credential provider, then checks it.</summary>
    [NeoRpcMethod("saveProvider")]
    public async Task<CredentialActionResult> SaveProviderAsync(CredentialProviderWriteRequest request, CancellationToken cancellationToken)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!SecretBindings.IsProviderId(request!.Id)) return new("invalid", "An id is lowercase letters, digits, - and _, starting with a letter.");
        if (Credentials!.Types.Find(request.Type) is not { } type) return new("invalid", $"No credential provider of type '{request.Type}' is known.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var setting in request.Settings ?? [])
        {
            if (setting is null || type.Settings.FirstOrDefault(known => known.Key == setting.Key) is not { } known) return new("invalid", $"'{setting?.Key}' is not a setting of {type.DisplayName}.");
            object? value = known.Kind switch
            {
                PluginCredentialProviderSettingKind.Boolean => setting.Flag,
                PluginCredentialProviderSettingKind.List => setting.List is { Length: > 0 } list ? list.Select(static item => (object?)item).ToArray() : null,
                _ => string.IsNullOrWhiteSpace(setting.Text) ? null : setting.Text.Trim(),
            };
            if (value is string text && text.Length > MaximumSettingLength || value is object?[] items && items.Sum(static item => ((string?)item)?.Length ?? 0) > MaximumSettingLength)
                return new("invalid", $"The setting '{known.Label}' is too long.");
            // A token of a vault is named, never written: the page sends a reference to a secret of this computer.
            if (known.Kind == PluginCredentialProviderSettingKind.Secret && value is string reference
                && (!SecretReferences.TryParseLone(reference, out var parsed) || parsed.Kind != SecretReferenceKind.Secret))
                return new("invalid", $"The setting '{known.Label}' is a reference to a secret of this computer, such as ${{secret:NAME}}.");
            if (value is not null) values[known.Key] = value;
        }

        if (type.Settings.FirstOrDefault(setting => setting.Required && !values.ContainsKey(setting.Key)) is { } missing) return new("invalid", $"The setting '{missing.Label}' is required.");
        if (!CredentialCacheDurations.IsDuration(request.Cache)) return new("invalid", "How long its values stay in memory is not one of the durations offered.");
        try
        {
            Credentials.Settings.Save(new CredentialProviderDefinition(request.Id, request.Type, values)
            {
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
                Cache = request.Cache,
                UnlockAtStart = request.UnlockAtStart,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new("write_failed", exception.Message);
        }

        var view = await Credentials.TestProviderAsync(request.Id, cancellationToken).ConfigureAwait(false);
        Changed(CredentialsOf(request.Id));
        return new("ok", null) { Provider = Present(view) };
    }

    /// <summary>Removes a credential provider: the secrets bound to it are removed with it; nothing changes in the vault.</summary>
    [NeoRpcMethod("removeProvider")]
    public CredentialActionResult RemoveProvider(CredentialProviderRequest request)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        try
        {
            var names = Credentials!.Store.Bindings.UnbindProvider(request!.Id);
            // Its sign-ins of websites go with it, and the sites allowed for them.
            Credentials.SignIns.RemoveProvider(request.Id);
            Credentials.Lock(request.Id);
            if (!Credentials.Settings.Remove(request.Id) && names.Count == 0) return new("not_found", null);
            Changed(names);
            return new("ok", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new("write_failed", exception.Message);
        }
    }

    /// <summary>Asks the vault of a provider to unlock: its own prompt, or the password typed in the page.</summary>
    [NeoRpcMethod("unlockProvider")]
    public async Task<CredentialActionResult> UnlockProviderAsync(CredentialUnlockRequest request, CancellationToken cancellationToken)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (!Credentials!.CanPrompt) return new("refused", "This instance of CodeAlta never asks a vault to unlock: unlock it from the window of the normal instance.");
        try
        {
            var view = await Credentials.UnlockAsync(request!.Id, string.IsNullOrEmpty(request.Password) ? null : request.Password, cancellationToken).ConfigureAwait(false);
            Changed(CredentialsOf(request.Id));
            return new(view.Status.State == CredentialProviderState.Ready ? "ok" : "locked", view.Status.Message) { Provider = Present(view) };
        }
        catch (KeyNotFoundException)
        {
            return new("not_found", null);
        }
    }

    /// <summary>Forgets the values a provider read; without an id, those of every provider.</summary>
    [NeoRpcMethod("lockProvider")]
    public CredentialActionResult LockProvider(CredentialProviderRequest request)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        Credentials!.Lock(string.IsNullOrWhiteSpace(request!.Id) ? null : request.Id);
        return new("ok", null);
    }

    /// <summary>Checks a provider now, without a prompt.</summary>
    [NeoRpcMethod("testProvider")]
    public async Task<CredentialActionResult> TestProviderAsync(CredentialProviderRequest request, CancellationToken cancellationToken)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        try
        {
            var view = await Credentials!.TestProviderAsync(request!.Id, cancellationToken).ConfigureAwait(false);
            Changed(CredentialsOf(request.Id));
            return new("ok", view.Status.Message) { Provider = Present(view) };
        }
        catch (KeyNotFoundException)
        {
            return new("not_found", null);
        }
    }

    /// <summary>Lists entries of the vault of a provider, for the picker: names, never values.</summary>
    [NeoRpcMethod("browse")]
    public async Task<CredentialBrowseResult> BrowseAsync(CredentialBrowseRpcRequest request, CancellationToken cancellationToken)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null, null, []);
        try
        {
            var page = await Credentials!.BrowseAsync(request!.Id, string.IsNullOrWhiteSpace(request.Parent) ? null : request.Parent, cancellationToken).ConfigureAwait(false);
            return new("ok", null, page.Path, [.. page.Entries.Take(2000).Select(static entry =>
                new CredentialEntryView(entry.Id, entry.Label, entry.Kind == CredentialBrowseEntryKind.Field ? "field" : "folder", entry.Reference, entry.Detail))]);
        }
        catch (KeyNotFoundException)
        {
            return new("not_found", null, null, []);
        }
        catch (NotSupportedException)
        {
            return new("not_supported", "This vault lists nothing: type the reference.", null, []);
        }
        catch (CredentialProviderException exception)
        {
            return new(exception.Failure == CredentialFailure.Locked ? "locked" : "failed", exception.Message, null, []);
        }
    }

    /// <summary>Keeps a secret in a vault: binds its name to what a provider reads. A name that has a value here is replaced only when the page says so.</summary>
    [NeoRpcMethod("bindSecret")]
    public async Task<CredentialActionResult> BindSecretAsync(SecretBindRequest request, CancellationToken cancellationToken)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        var name = request!.Name?.Trim();
        if (!LocalSecretStore.IsName(name)) return new("invalid", "A name is letters, digits and _, not starting with a digit, as the name of an environment variable.");
        if (Credentials!.GetProvider(request.Provider ?? string.Empty) is null) return new("not_found", "No credential provider has this id.");
        if (string.IsNullOrWhiteSpace(request.Reference) || request.Reference.Length > SecretBindings.MaximumTextLength) return new("invalid", "The reference is empty or too long.");
        try
        {
            var existing = Credentials.Store.ListSecrets().FirstOrDefault(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is { Provider: null } && !request.ReplaceLocal) return new("exists", $"{name} has a value on this computer: it is replaced by the item of the vault.");
            Credentials.Store.Bind(name, request.Provider!, request.Reference.Trim(), string.IsNullOrWhiteSpace(request.Label) ? request.Reference.Trim() : request.Label.Trim(), SetBy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return new("write_failed", exception.Message);
        }

        var availability = await Credentials.ReadSecretAsync(name, cancellationToken).ConfigureAwait(false);
        _changed?.Invoke();
        Changed([name]);
        return new("ok", availability.State == SecretAvailabilityState.Ready ? null : availability.Message);
    }

    /// <summary>Makes a secret ask before use, or not.</summary>
    [NeoRpcMethod("setAskBeforeUse")]
    public async Task<CredentialActionResult> SetAskBeforeUseAsync(SecretAskRequest request, CancellationToken cancellationToken)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        var duration = string.IsNullOrWhiteSpace(request!.Duration) ? null : request.Duration;
        if (duration is not null)
        {
            // The secret is protected only where the system can verify the user: otherwise it could never be given here.
            var availability = Credentials!.Verifier is { } verifier ? await verifier.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false) : UserPresenceAvailability.None;
            if (!availability.Available) return new("unavailable", availability.Reason);
        }

        try
        {
            if (!LocalSecretStore.IsName(request.Name)) return new("invalid", "Not the name of a secret.");
            Credentials!.Store.Bindings.SetAskBeforeUse(request.Name, duration);
            Credentials.ForgetConfirmations(request.Name);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new("write_failed", exception.Message);
        }

        return new("ok", null);
    }

    /// <summary>Confirms a use of a secret that asks before use: the system verifies the user first.</summary>
    [NeoRpcMethod("confirmUse")]
    public async Task<CredentialActionResult> ConfirmUseAsync(SecretUseRequest request, CancellationToken cancellationToken)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        var result = await Credentials!.ConfirmAsync(request!.Name, string.IsNullOrEmpty(request.Use) ? null : request.Use, cancellationToken).ConfigureAwait(false);
        if (result == UserPresenceResult.Verified) Changed([request.Name]);
        return new(result switch { UserPresenceResult.Verified => "ok", UserPresenceResult.Canceled => "canceled", _ => "unavailable" }, null);
    }

    /// <summary>Declines a use of a secret that asks before use.</summary>
    [NeoRpcMethod("declineUse")]
    public CredentialActionResult DeclineUse(SecretUseRequest request)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        Credentials!.Decline(request!.Name, string.IsNullOrEmpty(request.Use) ? null : request.Use);
        return new("ok", null);
    }

    /// <summary>Adds or changes a sign-in of websites: the vault items of its user name, password and one-time code, and its sites.</summary>
    [NeoRpcMethod("saveSignIn")]
    public CredentialActionResult SaveSignIn(SignInWriteRequest request)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        if (Credentials!.GetProvider(request!.Provider ?? string.Empty) is null) return new("not_found", "No credential provider has this id.");
        var label = request.Label?.Trim() ?? string.Empty;
        if (label.Length == 0) return new("invalid", "A sign-in needs a name.");
        if (request.Sites is not { Length: > 0 } sites || sites.Any(static site => SignIns.NormalizeSite(site) is null))
            return new("invalid", "A site is an address such as https://github.com, without a path.");
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password)) return new("invalid", "A sign-in needs the user name and the password of an item.");
        try
        {
            var existing = Credentials.SignIns.List();
            var id = request.Id is { Length: > 0 } given && existing.Any(signIn => signIn.Id == given) ? given : NewSignInId(label, existing);
            Credentials.SignIns.Save(new SignIn(id, label, request.Provider!, sites, request.Username, request.Password,
                string.IsNullOrWhiteSpace(request.Totp) ? null : request.Totp), SetBy);
            return new("ok", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return new(exception is ArgumentException ? "invalid" : "write_failed", exception.Message);
        }
    }

    /// <summary>Removes a sign-in and the sites the user allowed it on; nothing changes in the vault.</summary>
    [NeoRpcMethod("removeSignIn")]
    public CredentialActionResult RemoveSignIn(SignInRequest request)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        try
        {
            return new(Credentials!.SignIns.Remove(request!.Id ?? string.Empty) ? "ok" : "not_found", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("write_failed", exception.Message);
        }
    }

    /// <summary>Ends what the user allowed: a sign-in on a site is asked for again.</summary>
    [NeoRpcMethod("revokeSiteGrant")]
    public CredentialActionResult RevokeSiteGrant(SiteGrantRequest request)
    {
        if (RefuseCredentials(request?.ExpectedEpoch) is { } refused) return new(refused, null);
        try
        {
            return new(Credentials!.SignIns.Revoke(request!.Id ?? string.Empty, string.IsNullOrWhiteSpace(request.Origin) ? null : request.Origin) ? "ok" : "not_found", null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new("write_failed", exception.Message);
        }
    }

    // An id from the name, such as github or github-2: lowercase letters, digits and -, starting with a letter.
    private static string NewSignInId(string label, IReadOnlyList<SignIn> existing)
    {
        var text = new System.Text.StringBuilder();
        foreach (var character in label.ToLowerInvariant())
        {
            if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) && text.Length > 0) text.Append(character);
            else if (text.Length > 0 && text[^1] != '-') text.Append('-');
            if (text.Length >= 40) break;
        }

        var stem = text.ToString().Trim('-');
        if (stem.Length == 0) stem = "sign-in";
        var id = stem;
        for (var index = 2; existing.Any(signIn => signIn.Id == id); index++) id = stem + "-" + index.ToString(CultureInfo.InvariantCulture);
        return id;
    }

    // The sign-ins and their grants; a damaged file shows none, and the page says why through its list.
    private static SignInView[] SignInsOf(CredentialBroker broker, IReadOnlyDictionary<string, CredentialProviderView> providers)
    {
        try
        {
            var grants = broker.SignIns.ListGrants();
            return [.. broker.SignIns.List().Select(signIn =>
            {
                var provider = providers.GetValueOrDefault(signIn.Provider);
                return new SignInView(signIn.Id, signIn.Label, signIn.Provider, [.. signIn.Sites], signIn.Username, signIn.Password, signIn.Totp)
                {
                    ProviderName = provider?.DisplayName,
                    Icon = provider?.Icon,
                    Grants = [.. grants.Where(grant => grant.SignIn == signIn.Id).Select(static grant => new SiteGrantView(grant.Origin, Format(grant.Until)))],
                };
            })];
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    // What the list adds for the vaults.
    private async Task<SecretsList> WithCredentialsAsync(SecretsList list, CancellationToken cancellationToken)
    {
        if (Credentials is not { } broker) return list;
        var views = broker.ListProviders();
        var providers = views.ToDictionary(static view => view.Id, StringComparer.Ordinal);
        var verification = broker.Verifier is { } verifier ? await verifier.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false) : UserPresenceAvailability.None;
        var infos = broker.Store.ListSecrets().ToDictionary(static secret => secret.Name, StringComparer.OrdinalIgnoreCase);
        return list with
        {
            Secrets = [.. list.Secrets.Select(secret =>
            {
                var info = infos.GetValueOrDefault(secret.Name);
                var availability = broker.GetAvailability(secret.Name);
                var provider = info?.Provider is { } id ? providers.GetValueOrDefault(id) : null;
                return secret with
                {
                    Provider = info?.Provider,
                    ProviderName = provider?.DisplayName,
                    Icon = provider?.Icon,
                    Label = info?.Label,
                    AskBeforeUse = info?.AskBeforeUse,
                    State = availability is null ? "ready" : StateName(availability.State),
                    StateMessage = availability?.Message,
                };
            })],
            Providers = [.. views.Select(Present)],
            Types = [.. broker.Types.List().Select(type => new CredentialTypeView(type.Type, type.DisplayName, type.Description, type.Icon, Safe(type.IconSvg), type.Website?.ToString(),
                type.ReferenceHint, type.UnlockAsksPassword, broker.Types.IsBuiltIn(type.Type),
                [.. type.Settings.Select(static setting => new CredentialTypeSettingView(setting.Key, setting.Label, setting.Kind.ToString().ToLowerInvariant(), setting.Description, setting.Placeholder, setting.Required))]))],
            Pending = [.. broker.ListPending().Select(static pending => new PendingUseView(pending.Name, pending.Use, Format(pending.At)))],
            Uses = [.. broker.ListUses().Take(50).Select(static use => new SecretUseView(use.Name, use.Use, Format(use.At), use.Outcome))],
            Verification = new VerificationView(verification.Available, verification.Reason),
            CanPrompt = broker.CanPrompt,
            ConfigurationProblems = [.. broker.ConfigurationProblems],
            SignIns = SignInsOf(broker, providers),
        };
    }

    private static CredentialProviderRpcView Present(CredentialProviderView view) => new(view.Id, view.Type, view.DisplayName, StateName(view.Status.State), view.Status.Message)
    {
        TypeName = view.TypeName,
        Icon = view.Icon,
        Account = view.Status.Account,
        ProgramPath = view.Status.ProgramPath,
        SignInCommand = view.Status.SignInCommand is { } signIn ? [.. signIn] : null,
        InstallCommand = view.Status.Install?.Command is { } install ? [.. install] : null,
        InstallPage = view.Status.Install?.Page?.ToString(),
        Cache = view.Cache,
        UnlockAtStart = view.UnlockAtStart,
        UnlockAsksPassword = view.UnlockAsksPassword,
        Secrets = view.Secrets,
        Held = view.Held,
        Settings = [.. view.Settings.Select(static pair => pair.Value switch
        {
            bool flag => new CredentialSettingValue(pair.Key) { Flag = flag },
            IEnumerable<object?> list when pair.Value is not string => new CredentialSettingValue(pair.Key) { List = [.. list.Select(static item => Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty)] },
            _ => new CredentialSettingValue(pair.Key) { Text = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) },
        })],
    };

    private IReadOnlyCollection<string> CredentialsOf(string provider)
    {
        try
        {
            return [.. Credentials!.Store.Bindings.List().Where(binding => binding.Provider == provider).Select(static binding => binding.Name)];
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    // What masks the secrets reads them again; the broker tells the host itself which secrets can be given now.
    private void Changed(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return;
        _changed?.Invoke();
    }

    private string? RefuseCredentials(string? expectedEpoch)
        => Refuse(expectedEpoch) ?? (Credentials is null ? "unavailable" : null);

    private static string StateName(CredentialProviderState state) => state switch
    {
        CredentialProviderState.Ready => "ready",
        CredentialProviderState.Locked => "locked",
        CredentialProviderState.SignInNeeded => "sign_in",
        CredentialProviderState.NotInstalled => "not_installed",
        _ => "unavailable",
    };

    private static string StateName(SecretAvailabilityState state) => state switch
    {
        SecretAvailabilityState.Ready => "ready",
        SecretAvailabilityState.Locked => "locked",
        SecretAvailabilityState.SignInNeeded => "sign_in",
        SecretAvailabilityState.NotInstalled => "not_installed",
        SecretAvailabilityState.WaitsForYou => "waits",
        SecretAvailabilityState.NotFound => "not_found",
        _ => "unavailable",
    };

    // The SVG of a type of a plugin, without what could run: the page draws it as an image.
    private static string? Safe(string? svg)
        => svg is { Length: > 0 and < 64 * 1024 } && svg.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(svg, @"<\s*(script|foreignObject|iframe|image|use|a|style)\b|\son\w+\s*=|javascript:|(href|src)\s*=\s*[""'](?!#)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            ? svg : null;
}

/// <summary>A credential provider as the page shows it: never a value.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Type">Its type.</param>
/// <param name="DisplayName">Its name.</param>
/// <param name="State"><c>ready</c>, <c>locked</c>, <c>sign_in</c>, <c>not_installed</c> or <c>unavailable</c>.</param>
/// <param name="Message">What the user reads beside it; null for nothing.</param>
internal sealed record CredentialProviderRpcView(string Id, string Type, string DisplayName, string State, string? Message)
{
    /// <summary>The name of its type; null for a type no running plugin brings.</summary>
    public string? TypeName { get; init; }

    /// <summary>Its icon, a brand or a Lucide icon.</summary>
    public string? Icon { get; init; }

    /// <summary>The account it reads.</summary>
    public string? Account { get; init; }

    /// <summary>The full path of the program it runs.</summary>
    public string? ProgramPath { get; init; }

    /// <summary>The command to run in a terminal to sign in.</summary>
    public string[]? SignInCommand { get; init; }

    /// <summary>The command of the package manager that installs its program.</summary>
    public string[]? InstallCommand { get; init; }

    /// <summary>The page that explains how to install its program.</summary>
    public string? InstallPage { get; init; }

    /// <summary>How long its values stay in memory.</summary>
    public string Cache { get; init; } = CredentialCacheDurations.UntilClosed;

    /// <summary>Whether CodeAlta Desktop asks its vault to unlock when it starts.</summary>
    public bool UnlockAtStart { get; init; } = true;

    /// <summary>Whether its unlock takes a password typed in the page.</summary>
    public bool UnlockAsksPassword { get; init; }

    /// <summary>The number of secrets bound to it.</summary>
    public int Secrets { get; init; }

    /// <summary>The number of its secrets held in memory.</summary>
    public int Held { get; init; }

    /// <summary>Its settings as written: a secret setting holds its reference.</summary>
    public CredentialSettingValue[] Settings { get; init; } = [];
}

/// <summary>A setting of a provider, as written.</summary>
/// <param name="Key">The key.</param>
internal sealed record CredentialSettingValue(string Key)
{
    /// <summary>A text.</summary>
    public string? Text { get; init; }

    /// <summary>A flag.</summary>
    public bool? Flag { get; init; }

    /// <summary>A list.</summary>
    public string[]? List { get; init; }
}

/// <summary>A type of credential provider the page offers.</summary>
/// <param name="Type">The type.</param>
/// <param name="DisplayName">Its name.</param>
/// <param name="Description">What it reads.</param>
/// <param name="Icon">Its icon.</param>
/// <param name="IconSvg">The SVG of the icon of a plugin type, made safe.</param>
/// <param name="Website">Its page.</param>
/// <param name="ReferenceHint">An example of a reference.</param>
/// <param name="UnlockAsksPassword">Whether its unlock takes a password.</param>
/// <param name="BuiltIn">Whether it is built in.</param>
/// <param name="Settings">Its settings.</param>
internal sealed record CredentialTypeView(string Type, string DisplayName, string? Description, string? Icon, string? IconSvg, string? Website, string? ReferenceHint,
    bool UnlockAsksPassword, bool BuiltIn, CredentialTypeSettingView[] Settings);

/// <summary>A setting of a type.</summary>
/// <param name="Key">Its key.</param>
/// <param name="Label">Its label.</param>
/// <param name="Kind"><c>text</c>, <c>path</c>, <c>secret</c>, <c>list</c> or <c>boolean</c>.</param>
/// <param name="Description">A text under the field.</param>
/// <param name="Placeholder">The text of the empty field.</param>
/// <param name="Required">Whether it is required.</param>
internal sealed record CredentialTypeSettingView(string Key, string Label, string Kind, string? Description, string? Placeholder, bool Required);

/// <summary>A use of a secret that waits for a confirmation.</summary>
/// <param name="Name">The secret.</param>
/// <param name="Use">What asked; null when nothing named it.</param>
/// <param name="At">When, in ISO 8601.</param>
internal sealed record PendingUseView(string Name, string? Use, string At);

/// <summary>A recent use of a secret: never a value.</summary>
/// <param name="Name">The secret.</param>
/// <param name="Use">What asked for it.</param>
/// <param name="At">When, in ISO 8601.</param>
/// <param name="Outcome"><c>given</c>, <c>waits</c>, <c>confirmed</c>, <c>refused</c> or <c>unavailable</c>.</param>
internal sealed record SecretUseView(string Name, string? Use, string At, string Outcome);

/// <summary>Whether the system can verify the user here.</summary>
/// <param name="Available">Whether it can.</param>
/// <param name="Reason">Why not.</param>
internal sealed record VerificationView(bool Available, string? Reason);

/// <summary>An entry of a vault in the picker: a name, never a value.</summary>
/// <param name="Id">What the provider opens it by.</param>
/// <param name="Label">Its name.</param>
/// <param name="Kind"><c>folder</c> or <c>field</c>.</param>
/// <param name="Reference">What the provider reads, for a field.</param>
/// <param name="Detail">A short text beside it.</param>
internal sealed record CredentialEntryView(string Id, string Label, string Kind, string? Reference, string? Detail);

/// <summary>What the picker opened.</summary>
/// <param name="Status"><c>ok</c>, or why not.</param>
/// <param name="Message">What failed.</param>
/// <param name="Path">The label of what was opened.</param>
/// <param name="Entries">The entries.</param>
internal sealed record CredentialBrowseResult(string Status, string? Message, string? Path, CredentialEntryView[] Entries);

/// <summary>The result of a change of the vaults.</summary>
/// <param name="Status"><c>ok</c>, or why not.</param>
/// <param name="Message">What the user reads.</param>
internal sealed record CredentialActionResult(string Status, string? Message)
{
    /// <summary>The provider after the change.</summary>
    public CredentialProviderRpcView? Provider { get; init; }
}

/// <summary>A provider the page writes.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Id">Its id.</param>
/// <param name="Type">Its type.</param>
/// <param name="DisplayName">Its name; empty for its type.</param>
/// <param name="Cache">How long its values stay in memory.</param>
/// <param name="UnlockAtStart">Whether CodeAlta Desktop asks its vault to unlock when it starts.</param>
/// <param name="Settings">Its settings.</param>
internal sealed record CredentialProviderWriteRequest(string ExpectedEpoch, string Id, string Type, string? DisplayName, string Cache, bool UnlockAtStart, CredentialSettingInput[]? Settings);

/// <summary>A setting the page writes.</summary>
/// <param name="Key">The key.</param>
/// <param name="Text">A text.</param>
/// <param name="Flag">A flag.</param>
/// <param name="List">A list.</param>
internal sealed record CredentialSettingInput(string Key, string? Text, bool? Flag, string[]? List);

/// <summary>A provider the page names.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Id">Its id; empty for every provider where that applies.</param>
internal sealed record CredentialProviderRequest(string ExpectedEpoch, string Id);

/// <summary>An unlock the page asks.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Id">The provider.</param>
/// <param name="Password">The password typed, for a provider that takes one.</param>
internal sealed record CredentialUnlockRequest(string ExpectedEpoch, string Id, string? Password);

/// <summary>What the picker opens.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Id">The provider.</param>
/// <param name="Parent">The entry to open; empty for the top.</param>
internal sealed record CredentialBrowseRpcRequest(string ExpectedEpoch, string Id, string? Parent);

/// <summary>A binding the page writes.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Name">The name of the secret.</param>
/// <param name="Provider">The provider.</param>
/// <param name="Reference">What it reads.</param>
/// <param name="Label">What the list shows.</param>
/// <param name="ReplaceLocal">Whether a value of this computer under the name is replaced.</param>
internal sealed record SecretBindRequest(string ExpectedEpoch, string Name, string? Provider, string Reference, string? Label, bool ReplaceLocal);

/// <summary>A protection the page sets.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Name">The secret.</param>
/// <param name="Duration">How long a confirmation lasts; empty to stop asking.</param>
internal sealed record SecretAskRequest(string ExpectedEpoch, string Name, string? Duration);

/// <summary>A sign-in of websites as the page shows and edits it: the references of its items in the vault, never a value.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Label">Its name.</param>
/// <param name="Provider">The id of the provider of its vault.</param>
/// <param name="Sites">The origins it is typed on.</param>
/// <param name="Username">The reference of the user name.</param>
/// <param name="Password">The reference of the password.</param>
/// <param name="Totp">The reference of the one-time code; null for none.</param>
internal sealed record SignInView(string Id, string Label, string Provider, string[] Sites, string Username, string Password, string? Totp)
{
    /// <summary>The name of its provider.</summary>
    public string? ProviderName { get; init; }

    /// <summary>The icon of its provider.</summary>
    public string? Icon { get; init; }

    /// <summary>The sites the user allowed it on without being asked, until when.</summary>
    public SiteGrantView[] Grants { get; init; } = [];
}

/// <summary>A site the user allowed a sign-in on.</summary>
/// <param name="Origin">The site.</param>
/// <param name="Until">When it is asked for again, in ISO 8601.</param>
internal sealed record SiteGrantView(string Origin, string Until);

/// <summary>A sign-in the page saves.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Id">The sign-in it changes; null for a new one.</param>
/// <param name="Label">Its name.</param>
/// <param name="Provider">The id of the provider of its vault.</param>
/// <param name="Sites">The sites it is typed on.</param>
/// <param name="Username">The reference of the user name.</param>
/// <param name="Password">The reference of the password.</param>
/// <param name="Totp">The reference of the one-time code; null or empty for none.</param>
internal sealed record SignInWriteRequest(string ExpectedEpoch, string? Id, string Label, string? Provider, string[]? Sites, string Username, string Password, string? Totp);

/// <summary>A sign-in the page names.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Id">The sign-in.</param>
internal sealed record SignInRequest(string ExpectedEpoch, string Id);

/// <summary>A site the user allowed, which the page ends.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Id">The sign-in.</param>
/// <param name="Origin">The site; null for all of them.</param>
internal sealed record SiteGrantRequest(string ExpectedEpoch, string Id, string? Origin);

/// <summary>A use the page confirms or declines.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Name">The secret.</param>
/// <param name="Use">What asked; empty when nothing named it.</param>
internal sealed record SecretUseRequest(string ExpectedEpoch, string Name, string? Use);
