using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// 1Password, through its <c>op</c> CLI: with the 1Password app (Settings › Developer › Integrate with 1Password CLI), which
/// asks Windows Hello, Touch ID or the system password once, or with a service account token, which needs nobody. CodeAlta
/// never asks a master password.
/// </summary>
internal sealed class OnePasswordProvider : ICredentialProvider, ICredentialBatchReader, ICredentialVaultApplication
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
    // which account is read, and an authorization is known from the commands that went through since it was given. Each
    // command is a start of op and its exchange with the app: whoami is not asked when the accounts and that memory answer.
    private async Task<CredentialProviderStatus> CheckAsync(string program, CancellationToken cancellationToken)
    {
        if (_serviceAccountToken is not null) return await WhoAmIAsync(program, null, cancellationToken).ConfigureAwait(false);
        var accounts = await RunAsync(program, ["account", "list", "--format", "json"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        var shown = accounts.ExitCode == 0 ? AccountOf(accounts.Output).Shown : null;
        if (shown is not null && Authorized()) return CredentialProviderStatus.Ready() with { Account = shown, ProgramPath = program };
        return await WhoAmIAsync(program, accounts, cancellationToken).ConfigureAwait(false);
    }

    // What whoami says, then, when it knows no signed-in account, what the accounts the app gives say (null with a service account).
    private async Task<CredentialProviderStatus> WhoAmIAsync(string program, VaultCommandResult? accounts, CancellationToken cancellationToken)
    {
        var whoami = await RunAsync(program, ["whoami", "--format", "json"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (whoami.ExitCode == 0) return Ready(program, whoami);
        if (accounts is null) return Classify(program, whoami);
        if (accounts.ExitCode != 0) return Classify(program, accounts);
        var (any, shown) = AccountOf(accounts.Output);
        if (!any || shown is null) return NoAccount(program);
        return CredentialProviderStatus.Locked("Unlock to let the 1Password app authorize CodeAlta.") with { Account = shown, ProgramPath = program };
    }

    // Whether a command went through recently enough for the app to still authorize this process.
    private bool Authorized()
    {
        lock (_gate) return DateTimeOffset.UtcNow < _authorizedUntil;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsApplicationReadyAsync(CancellationToken cancellationToken)
    {
        if (Program is not { } program || _serviceAccountToken is not null) return true;
        var accounts = await RunAsync(program, ["account", "list", "--format", "json"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        return accounts.ExitCode == 0 && AccountOf(accounts.Output).Shown is not null;
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
        // The app authorized this process: the account the last check showed is the one it reads, without asking op again.
        lock (_gate)
        {
            if (_lastStatus is { Status: { Account: not null } known } && known.ProgramPath == program)
            {
                var ready = CredentialProviderStatus.Ready() with { Account = known.Account, ProgramPath = program };
                _lastStatus = (DateTimeOffset.UtcNow, ready);
                return ready;
            }
        }

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

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<CredentialReadResult>> ReadManyAsync(IReadOnlyList<string> references, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        var results = new CredentialReadResult[references.Count];
        var batch = new List<int>();
        for (var index = 0; index < references.Count; index++)
        {
            if (IsTemplateReference(references[index])) batch.Add(index);
        }

        // Each start of op costs its exchange with the app and the server: the references a template can hold are read with one.
        var refused = batch.Count > 1 && Program is { } program
            ? await InjectAsync(program, references, batch, results, interactive, cancellationToken).ConfigureAwait(false)
            : null;
        for (var index = 0; index < references.Count; index++)
        {
            if (results[index] != default) continue;
            if (refused is not null)
            {
                results[index] = new CredentialReadResult(null, refused);
                continue;
            }

            try
            {
                results[index] = new CredentialReadResult(await ReadAsync(references[index], interactive, cancellationToken).ConfigureAwait(false), null);
            }
            catch (CredentialProviderException exception)
            {
                results[index] = new CredentialReadResult(null, exception);
                // The app no longer authorizes this process: a read of each of the others would show its prompt again, or fail the same.
                if (exception.Failure == CredentialFailure.Locked && _serviceAccountToken is null && !Authorized()) refused = exception;
            }
        }

        return results;
    }

    // Reads several references with one `op inject`: a template that holds each between two lines of a marker no value holds,
    // which op writes back with the values in their place. It fills the results it read, and returns why the vault refused when it
    // no longer authorizes this process. Any other failure leaves the results empty: one reference that cannot be read fails the
    // whole template, and a read of each alone says which.
    private async Task<CredentialProviderException?> InjectAsync(string program, IReadOnlyList<string> references, List<int> batch, CredentialReadResult[] results, bool interactive,
        CancellationToken cancellationToken)
    {
        if (!interactive)
        {
            var status = await RecentStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.State != CredentialProviderState.Ready)
                return new CredentialProviderException(status.State == CredentialProviderState.Locked ? CredentialFailure.Locked : CredentialFailure.SignInNeeded,
                    status.Message ?? "1Password is locked.");
        }

        var marker = "codealta-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var template = new StringBuilder();
        foreach (var index in batch) template.Append(marker).Append('\n').Append("{{ ").Append(references[index]).Append(" }}").Append('\n');
        template.Append(marker).Append('\n');
        var timeout = interactive ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(Math.Min(20 + 2 * batch.Count, 90));
        var result = await RunAsync(program, ["inject"], timeout, cancellationToken, template.ToString()).ConfigureAwait(false);
        try
        {
            if (result.ExitCode != 0) return _serviceAccountToken is null && !Authorized() ? Failed(program, result) : null;
            if (OnePasswordTemplate.Split(result.Output, marker, batch.Count) is not { } values) return null;
            for (var position = 0; position < batch.Count; position++) results[batch[position]] = new CredentialReadResult(new CredentialValue(values[position]), null);
            return null;
        }
        finally
        {
            CryptographicZero(result.Output);
        }
    }

    // Whether a template of op can hold a reference: op://vault/item/field or op://vault/item/section/field whose names are
    // letters, digits, - _ and . only. A template ends a reference at any other character (a space), expands $NAME, and a
    // one-time code (?attribute=otp) is read when it is needed: those are read one at a time.
    internal static bool IsTemplateReference(string reference)
    {
        if (!reference.StartsWith("op://", StringComparison.Ordinal)) return false;
        var parts = 1;
        var length = 0;
        foreach (var character in reference.AsSpan("op://".Length))
        {
            if (character == '/')
            {
                if (length == 0) return false;
                parts++;
                length = 0;
            }
            else if (char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            {
                length++;
            }
            else
            {
                return false;
            }
        }

        return length > 0 && parts is 3 or 4;
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

    private async Task<VaultCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? input = null)
    {
        var all = new List<string>(arguments);
        // The list of the accounts filters nothing: the account set is looked for in it.
        if (_account is not null && _serviceAccountToken is null && arguments[0] != "account") all.AddRange(["--account", _account]);
        Dictionary<string, string>? environment = null;
        if (_serviceAccountToken is not null) environment = new(StringComparer.Ordinal) { ["OP_SERVICE_ACCOUNT_TOKEN"] = _serviceAccountToken };
        var result = await _runner.RunAsync(program, all, new VaultCommandOptions { Timeout = timeout, Environment = environment, Input = input }, cancellationToken).ConfigureAwait(false);
        // A command of an account that went through: the app authorized this process, for a while after each one.
        if (result.ExitCode == 0 && arguments[0] != "account")
        {
            lock (_gate) _authorizedUntil = DateTimeOffset.UtcNow + AuthorizationLasts;
        }
        else if (result.ExitCode != 0 && arguments[0] is not ("account" or "whoami") && (result.TimedOut || NotAuthorized(result.Error)))
        {
            // The app no longer authorizes this process (it locked, the prompt was dismissed or not answered): the next read waits
            // for an unlock rather than show the prompt again. whoami says "not signed in" to an authorized process too.
            lock (_gate)
            {
                _authorizedUntil = default;
                _lastStatus = null;
            }
        }

        return result;
    }

    private static bool NotAuthorized(string error)
        => error.Contains("not currently signed in", StringComparison.OrdinalIgnoreCase) || error.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
            || error.Contains("authorization", StringComparison.OrdinalIgnoreCase) || error.Contains("locked", StringComparison.OrdinalIgnoreCase)
            || error.Contains("dismissed", StringComparison.OrdinalIgnoreCase);

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
        if (NotAuthorized(error))
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

// Reads the values `op inject` wrote for a template that holds each reference on a line of its own between two lines of a marker.
internal static class OnePasswordTemplate
{
    /// <summary>Splits what op wrote into the values of the references, in their order.</summary>
    /// <param name="output">What op wrote.</param>
    /// <param name="marker">The marker of the template.</param>
    /// <param name="count">The number of references.</param>
    /// <returns>The values; null when the output is not the template with its values, so that nothing of it is taken for one.</returns>
    public static string[]? Split(ReadOnlySpan<byte> output, string marker, int count)
    {
        Span<byte> mark = stackalloc byte[Encoding.UTF8.GetByteCount(marker)];
        Encoding.UTF8.GetBytes(marker, mark);
        var at = output.IndexOf(mark);
        if (at < 0) return null;
        var values = new string[count];
        for (var index = 0; index < count; index++)
        {
            var rest = output[(at + mark.Length)..];
            var next = rest.IndexOf(mark);
            if (next < 0) return null;
            // The line of the marker ends before the value, and the line of the value before the next marker.
            var value = rest[..next];
            if (value.StartsWith("\r\n"u8)) value = value[2..];
            else if (value.StartsWith("\n"u8)) value = value[1..];
            else return null;
            if (value.EndsWith("\r\n"u8)) value = value[..^2];
            else if (value.EndsWith("\n"u8)) value = value[..^1];
            else return null;
            // A reference op left as it was is not a value.
            if (value.StartsWith("{{"u8) && value.EndsWith("}}"u8) && value.IndexOf("op://"u8) >= 0) return null;
            values[index] = Encoding.UTF8.GetString(value);
            at += mark.Length + next;
        }

        return output[(at + mark.Length)..].IndexOf(mark) < 0 ? values : null;
    }
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
