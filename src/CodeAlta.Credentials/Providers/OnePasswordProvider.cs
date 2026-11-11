using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// 1Password, through its <c>op</c> CLI: with the 1Password app (Settings › Developer › Integrate with 1Password CLI), which
/// asks Windows Hello, Touch ID or the system password once, or with a service account token, which needs nobody. CodeAlta
/// never asks a master password.
/// </summary>
internal sealed class OnePasswordProvider : ICredentialProvider
{
    internal const string TypeName = "1password";
    private static readonly TimeSpan StatusReuse = TimeSpan.FromSeconds(30);
    // The 1Password app keeps an authorization of the CLI for 10 minutes without a command: CodeAlta counts a little less.
    private static readonly TimeSpan AuthorizationLasts = TimeSpan.FromMinutes(9);

    private readonly VaultCommandRunner _runner;
    private readonly string? _configuredPath;
    private readonly string? _account;
    private readonly string? _serviceAccountToken;
    private readonly Lock _gate = new();
    private (DateTimeOffset At, CredentialProviderStatus Status)? _lastStatus;
    // Until when the app authorized the commands of this process: op says nothing of it without a prompt.
    private DateTimeOffset _authorizedUntil;

    public OnePasswordProvider(PluginCredentialProviderContext context, VaultCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _configuredPath = context.GetString("path");
        _account = context.GetString("account");
        _serviceAccountToken = context.GetString("service_account_token");
    }

    public static PluginCredentialProviderContribution Contribution(VaultCommandRunner runner) => new()
    {
        Type = TypeName,
        DisplayName = "1Password",
        Description = "Reads items of 1Password through its op CLI: the 1Password app unlocks it with Windows Hello, Touch ID or your system password, or a service account needs nobody.",
        Icon = "1password",
        Website = new Uri("https://developer.1password.com/docs/cli/get-started/"),
        ReferenceHint = "op://Private/Anthropic/credential",
        Settings =
        [
            new("account", "Account", PluginCredentialProviderSettingKind.Text) { Placeholder = "my.1password.com", Description = "The sign-in address or the id of the account, when the app has several." },
            new("service_account_token", "Service account token", PluginCredentialProviderSettingKind.Secret) { Placeholder = "${secret:OP_SERVICE_ACCOUNT_TOKEN}", Description = "A secret of this computer that holds the token of a service account. It cannot read the Private and Personal vaults." },
            new("path", "Path of op", PluginCredentialProviderSettingKind.Path) { Description = "Leave empty to find op on the PATH." },
        ],
        Create = context => new OnePasswordProvider(context, runner),
    };

    private string? Program => VaultCommandRunner.FindProgram("op", _configuredPath);

    public async ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (Program is not { } program) return NotInstalled();
        var status = await CheckAsync(program, cancellationToken).ConfigureAwait(false);
        lock (_gate) _lastStatus = (DateTimeOffset.UtcNow, status);
        return status;
    }

    /// <summary>Forgets that the app authorized CodeAlta: the next read waits for an unlock.</summary>
    public void Lock()
    {
        lock (_gate)
        {
            _authorizedUntil = default;
            _lastStatus = null;
        }
    }

    // The state, never with a prompt. A service account, or an op signed in by itself, answers whoami. With the 1Password app,
    // whoami says "not signed in" even to a process the app authorized: the accounts the app gives say whether it is on and
    // which account is read, and an authorization is known from the commands that went through since it was given.
    private async Task<CredentialProviderStatus> CheckAsync(string program, CancellationToken cancellationToken)
    {
        var whoami = await RunAsync(program, ["whoami", "--format", "json"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (whoami.ExitCode == 0) return Ready(program, whoami);
        if (_serviceAccountToken is not null) return Classify(program, whoami);
        var accounts = await RunAsync(program, ["account", "list", "--format", "json"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (accounts.ExitCode != 0) return Classify(program, accounts);
        var (any, shown) = AccountOf(accounts.Output);
        if (!any || shown is null) return NoAccount(program);
        bool authorized;
        lock (_gate) authorized = DateTimeOffset.UtcNow < _authorizedUntil;
        return authorized
            ? CredentialProviderStatus.Ready() with { Account = shown, ProgramPath = program }
            : CredentialProviderStatus.Locked("Unlock to let the 1Password app authorize CodeAlta.") with { Account = shown, ProgramPath = program };
    }

    // Whether the app gives op an account, and the one this provider reads: the one set, or the first; null when the one set is not given.
    private (bool Any, string? Shown) AccountOf(byte[] output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0) return (false, null);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                string? Text(string name) => entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                var url = Text("url");
                var shown = Text("email") is { Length: > 0 } email ? email : url;
                if (_account is null) return (true, shown);
                var wanted = _account.Trim();
                if (new[] { url, url is null ? null : "https://" + url, Text("email"), Text("shorthand"), Text("account_uuid"), Text("user_uuid") }
                    .Any(candidate => string.Equals(candidate, wanted, StringComparison.OrdinalIgnoreCase)))
                    return (true, shown);
            }

            return (true, null);
        }
        catch (JsonException)
        {
            // An older op that writes its accounts as a table: they are not known, nor the account read.
            return (false, null);
        }
    }

    private CredentialProviderStatus NoAccount(string program) => CredentialProviderStatus.Locked(_account is null
            ? "Unlock the 1Password app and turn on Integrate with 1Password CLI in its Settings › Developer, then Unlock here."
            : $"Unlock the 1Password app and turn on Integrate with 1Password CLI in its Settings › Developer, then Unlock here. Check that {_account} is the sign-in address of an account of the app, or leave Account empty.")
        with { ProgramPath = program };

    public async ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Program is not { } program) return NotInstalled();
        if (_serviceAccountToken is not null) return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        // The 1Password app shows its own prompt (Windows Hello, Touch ID, the system password) for the first command that needs
        // an account; listing the vaults is such a command, and reads nothing of an item.
        var result = await RunAsync(program, ["vault", "list", "--format", "json"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        CryptographicZero(result.Output);
        if (result.ExitCode != 0) return Classify(program, result);
        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (!reference.StartsWith("op://", StringComparison.OrdinalIgnoreCase))
            throw new CredentialProviderException(CredentialFailure.NotFound, $"'{reference}' is not a 1Password reference (op://vault/item/field).");
        if (Program is not { } program) throw new CredentialProviderException(CredentialFailure.NotInstalled, "The 1Password CLI (op) is not installed.");
        if (!interactive)
        {
            // A read would show the prompt of the app when it is not authorized: the state is checked first, which never does.
            var status = await RecentStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.State != CredentialProviderState.Ready)
                throw new CredentialProviderException(status.State == CredentialProviderState.Locked ? CredentialFailure.Locked : CredentialFailure.SignInNeeded,
                    status.Message ?? "1Password is locked.");
        }

        var result = await RunAsync(program, ["read", "--no-newline", reference], interactive ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw Failed(program, result);
        }

        var value = result.OutputText();
        CryptographicZero(result.Output);
        var otp = reference.Contains("attribute=otp", StringComparison.OrdinalIgnoreCase) || reference.Contains("one-time password", StringComparison.OrdinalIgnoreCase);
        return new CredentialValue(value) { ExpiresAt = otp ? NextTotpWindow() : null };
    }

    public async ValueTask<CredentialBrowsePage> BrowseAsync(CredentialBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Program is not { } program) throw new CredentialProviderException(CredentialFailure.NotInstalled, "The 1Password CLI (op) is not installed.");
        var parent = request.Parent;
        if (parent is null)
        {
            var vaults = await JsonAsync(program, ["vault", "list", "--format", "json"], cancellationToken).ConfigureAwait(false);
            return new CredentialBrowsePage([.. vaults.Select(static vault => new CredentialBrowseEntry($"vault:{vault.Id}:{vault.Name}", vault.Name, CredentialBrowseEntryKind.Folder))]);
        }

        var parts = parent.Split(':', 3);
        if (parts is ["vault", var vaultId, var vaultName])
        {
            var items = await JsonAsync(program, ["item", "list", "--vault", vaultId, "--format", "json"], cancellationToken).ConfigureAwait(false);
            return new CredentialBrowsePage([.. items.OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new CredentialBrowseEntry($"item:{vaultId}:{item.Id}", item.Name, CredentialBrowseEntryKind.Folder) { Detail = item.Detail })]) { Path = vaultName };
        }

        if (parts is ["item", var vault, var itemId])
        {
            var result = await RunAsync(program, ["item", "get", itemId, "--vault", vault, "--format", "json"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            try
            {
                if (result.ExitCode != 0) throw Failed(program, result);
                return OnePasswordItemFields.Read(result.Output);
            }
            finally
            {
                CryptographicZero(result.Output);
            }
        }

        throw new CredentialProviderException(CredentialFailure.NotFound, "This entry of 1Password is not known.");
    }

    private async ValueTask<CredentialProviderStatus> RecentStatusAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_lastStatus is { } last && DateTimeOffset.UtcNow - last.At < StatusReuse && last.Status.State == CredentialProviderState.Ready) return last.Status;
        }

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<(string Id, string Name, string? Detail)>> JsonAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(program, arguments, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.ExitCode != 0) throw Failed(program, result);
            using var document = JsonDocument.Parse(result.Output);
            var list = new List<(string, string, string?)>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                // The lists of vaults and items hold names and ids, never the values of fields.
                var id = entry.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
                var name = entry.TryGetProperty("name", out var nameValue) ? nameValue.GetString()
                    : entry.TryGetProperty("title", out var title) ? title.GetString() : null;
                var category = entry.TryGetProperty("category", out var categoryValue) ? categoryValue.GetString()?.Replace('_', ' ').ToLowerInvariant() : null;
                if (id is { Length: > 0 } && name is not null) list.Add((id, name, category));
            }

            return list;
        }
        catch (JsonException exception)
        {
            throw new CredentialProviderException(CredentialFailure.Failed, "1Password answered something that is not a list.", exception);
        }
        finally
        {
            CryptographicZero(result.Output);
        }
    }

    private async Task<VaultCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var all = new List<string>(arguments);
        // The list of the accounts filters nothing: the account set is looked for in it.
        if (_account is not null && _serviceAccountToken is null && arguments[0] != "account") all.AddRange(["--account", _account]);
        Dictionary<string, string>? environment = null;
        if (_serviceAccountToken is not null) environment = new(StringComparer.Ordinal) { ["OP_SERVICE_ACCOUNT_TOKEN"] = _serviceAccountToken };
        var result = await _runner.RunAsync(program, all, new VaultCommandOptions { Timeout = timeout, Environment = environment }, cancellationToken).ConfigureAwait(false);
        // A command of an account that went through: the app authorized this process, for a while after each one.
        if (result.ExitCode == 0 && arguments[0] != "account")
        {
            lock (_gate) _authorizedUntil = DateTimeOffset.UtcNow + AuthorizationLasts;
        }

        return result;
    }

    private CredentialProviderStatus Ready(string program, VaultCommandResult result)
    {
        string? account = null;
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            var root = document.RootElement;
            account = root.TryGetProperty("email", out var email) && email.GetString() is { Length: > 0 } address ? address
                : root.TryGetProperty("url", out var url) ? url.GetString() : null;
            if (_serviceAccountToken is not null) account = account is null ? "service account" : $"service account · {account}";
        }
        catch (JsonException)
        {
            // An older op that writes its account as text: the account is not shown.
        }

        return CredentialProviderStatus.Ready() with { Account = account, ProgramPath = program };
    }

    private CredentialProviderStatus Classify(string program, VaultCommandResult result) => Classified(program, result).WithDetail(result);

    // A failure of op, in words of CodeAlta: what op wrote is the detail of the status, for the user only.
    private CredentialProviderException Failed(string program, VaultCommandResult result)
    {
        var failure = Failure(program, result);
        var message = failure == CredentialFailure.NotFound ? "1Password has no such vault, item or field." : Classified(program, result).Message ?? $"1Password: {result.Summary()}.";
        return VaultText.Failure(failure, message, result);
    }

    private CredentialProviderStatus Classified(string program, VaultCommandResult result)
    {
        var error = result.Error;
        if (result.TimedOut) return CredentialProviderStatus.Locked("1Password did not answer in time: is the 1Password app waiting for you?") with { ProgramPath = program };
        // op knows no account: the 1Password app does not give it one (its integration with the CLI is off, or the app is
        // locked), or the account set is not one of the app. The app authorizes the process that runs op, so a sign-in typed in
        // a terminal would authorize that terminal, never CodeAlta: what the user does is turn the integration on, then Unlock.
        if (error.Contains("No accounts configured", StringComparison.OrdinalIgnoreCase) || error.Contains("no account found", StringComparison.OrdinalIgnoreCase))
            return NoAccount(program);
        if (error.Contains("LostConnectionToApp", StringComparison.OrdinalIgnoreCase) || error.Contains("connecting to desktop app", StringComparison.OrdinalIgnoreCase)
            || error.Contains("cannot connect", StringComparison.OrdinalIgnoreCase))
            return CredentialProviderStatus.Locked("Open the 1Password app, then unlock.") with { ProgramPath = program };
        if (error.Contains("not currently signed in", StringComparison.OrdinalIgnoreCase) || error.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
            || error.Contains("authorization", StringComparison.OrdinalIgnoreCase) || error.Contains("locked", StringComparison.OrdinalIgnoreCase)
            || error.Contains("dismissed", StringComparison.OrdinalIgnoreCase))
            return CredentialProviderStatus.Locked("Unlock to let the 1Password app authorize CodeAlta.") with { ProgramPath = program };
        if (_serviceAccountToken is not null && (error.Contains("token", StringComparison.OrdinalIgnoreCase) || error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)))
            return CredentialProviderStatus.Unavailable("The service account token was refused.") with { ProgramPath = program };
        return CredentialProviderStatus.Locked($"1Password: {result.Summary()}.") with { ProgramPath = program };
    }

    private CredentialFailure Failure(string program, VaultCommandResult result) => LooksNotFound(result.Error) ? CredentialFailure.NotFound : Classify(program, result).State switch
    {
        CredentialProviderState.Locked => CredentialFailure.Locked,
        CredentialProviderState.SignInNeeded => CredentialFailure.SignInNeeded,
        _ => LooksNotFound(result.Error) ? CredentialFailure.NotFound : CredentialFailure.Failed,
    };

    private static bool LooksNotFound(string error)
        => error.Contains("isn't an item", StringComparison.OrdinalIgnoreCase) || error.Contains("could not find", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no item", StringComparison.OrdinalIgnoreCase) || error.Contains("isn't a vault", StringComparison.OrdinalIgnoreCase)
            || error.Contains("does not have a field", StringComparison.OrdinalIgnoreCase) || error.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static CredentialProviderStatus NotInstalled() => new(CredentialProviderState.NotInstalled, "The 1Password CLI (op) is not installed.")
    {
        Install = new CredentialProviderInstall(
            OperatingSystem.IsWindows() ? ["winget", "install", "--id", "AgileBits.1Password.CLI", "-e"]
            : OperatingSystem.IsMacOS() ? ["brew", "install", "1password-cli"]
            : null,
            new Uri("https://developer.1password.com/docs/cli/get-started/")),
    };

    internal static DateTimeOffset NextTotpWindow()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(now - now % 30 + 30);
    }

    private static void CryptographicZero(byte[] bytes) => System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
}

// Reads the fields of an item of 1Password from what `op item get --format json` writes, without making a text of a value.
internal static class OnePasswordItemFields
{
    public static CredentialBrowsePage Read(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        string? title = null, vaultName = null;
        var entries = new List<CredentialBrowseEntry>();
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new CredentialProviderException(CredentialFailure.Failed, "1Password answered something that is not an item.");
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "title" when reader.TokenType == JsonTokenType.String:
                    title = reader.GetString();
                    break;
                case "vault" when reader.TokenType == JsonTokenType.StartObject:
                    vaultName = ReadVaultName(ref reader);
                    break;
                case "fields" when reader.TokenType == JsonTokenType.StartArray:
                    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        if (ReadField(ref reader) is { } field) entries.Add(field);
                    }

                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new CredentialBrowsePage(entries) { Path = vaultName is null ? title : $"{vaultName} › {title}" };
    }

    private static string? ReadVaultName(ref Utf8JsonReader reader)
    {
        string? name = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            reader.Read();
            if (property == "name" && reader.TokenType == JsonTokenType.String) name = reader.GetString();
            else reader.Skip();
        }

        return name;
    }

    // One field: its id, label, type, section and reference are read; its value is skipped without being decoded.
    private static CredentialBrowseEntry? ReadField(ref Utf8JsonReader reader)
    {
        string? id = null, label = null, type = null, section = null, reference = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var property = reader.GetString();
            reader.Read();
            switch (property)
            {
                case "id" when reader.TokenType == JsonTokenType.String: id = reader.GetString(); break;
                case "label" when reader.TokenType == JsonTokenType.String: label = reader.GetString(); break;
                case "type" when reader.TokenType == JsonTokenType.String: type = reader.GetString(); break;
                case "reference" when reader.TokenType == JsonTokenType.String: reference = reader.GetString(); break;
                case "section" when reader.TokenType == JsonTokenType.StartObject:
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        var sectionProperty = reader.GetString();
                        reader.Read();
                        if (sectionProperty == "label" && reader.TokenType == JsonTokenType.String) section = reader.GetString();
                        else reader.Skip();
                    }

                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (id is null || reference is null) return null;
        var shown = string.IsNullOrWhiteSpace(label) ? id : label;
        var detail = type?.ToLowerInvariant() switch
        {
            "concealed" => "concealed",
            "otp" => "one-time password",
            null => null,
            var other => other,
        };
        // The one-time password of a field is read as its current code.
        if (string.Equals(type, "OTP", StringComparison.OrdinalIgnoreCase) && !reference.Contains('?', StringComparison.Ordinal)) reference += "?attribute=otp";
        return new CredentialBrowseEntry(id, string.IsNullOrWhiteSpace(section) ? shown : $"{section} › {shown}", CredentialBrowseEntryKind.Field) { Reference = reference, Detail = detail };
    }
}
