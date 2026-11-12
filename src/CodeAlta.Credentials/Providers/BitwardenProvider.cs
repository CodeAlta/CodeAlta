using System.Collections.Frozen;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// Bitwarden, and Vaultwarden, through the <c>bw</c> CLI signed in by the user (<c>bw login</c>). Its unlock takes the master
/// password the user types in the window, given to <c>bw unlock</c> in its environment; the session key it answers is kept in
/// memory only, given to every call as <c>BW_SESSION</c>, and forgotten when CodeAlta locks it.
/// </summary>
/// <remarks>A reference is <c>&lt;item id&gt;/&lt;field&gt;</c>: password, username, totp, notes, uri, or <c>field:&lt;name&gt;</c> for a custom field.</remarks>
internal sealed class BitwardenProvider : ICredentialProvider
{
    internal const string TypeName = "bitwarden";
    private const string PasswordVariable = "BW_CODEALTA_PASSWORD";
    private static readonly FrozenSet<string> Fields = FrozenSet.Create(StringComparer.Ordinal, "password", "username", "totp", "notes", "uri");

    private readonly VaultCommandRunner _runner;
    private readonly string? _configuredPath;
    private readonly Action<string> _mask;
    private readonly Lock _gate = new();
    private string? _sessionKey;

    public BitwardenProvider(PluginCredentialProviderContext context, VaultCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _configuredPath = context.GetString("path");
        _mask = context.Mask;
    }

    public static PluginCredentialProviderContribution Contribution(VaultCommandRunner runner) => new()
    {
        Type = TypeName,
        DisplayName = "Bitwarden",
        Description = "Reads items of Bitwarden or Vaultwarden through the bw CLI, signed in with bw login and unlocked with your master password.",
        Icon = "bitwarden",
        Website = new Uri("https://bitwarden.com/help/cli/"),
        ReferenceHint = "<item id>/password",
        UnlockAsksPassword = true,
        Settings =
        [
            new("path", "Path of bw", PluginCredentialProviderSettingKind.Path) { Description = "Leave empty to find bw on the PATH." },
        ],
        Create = context => new BitwardenProvider(context, runner),
    };

    private string? Program => VaultCommandRunner.FindProgram("bw", _configuredPath);

    private string? SessionKey
    {
        get
        {
            lock (_gate) return _sessionKey;
        }
    }

    public async ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (Program is not { } program) return NotInstalled();
        var session = SessionKey;
        var result = await RunAsync(program, ["status"], session, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.ExitCode != 0) return CredentialProviderStatus.Unavailable($"Bitwarden did not say its state: {result.Summary()}.").WithDetail(result) with { ProgramPath = program };
            string? state = null, email = null;
            try
            {
                using var document = JsonDocument.Parse(result.Output);
                var root = document.RootElement;
                state = root.TryGetProperty("status", out var value) ? value.GetString() : null;
                email = root.TryGetProperty("userEmail", out var address) && address.ValueKind == JsonValueKind.String ? address.GetString() : null;
            }
            catch (JsonException)
            {
                return CredentialProviderStatus.Unavailable("Bitwarden answered a status that is not JSON.") with { ProgramPath = program };
            }

            switch (state)
            {
                case "unlocked" when session is not null:
                    return CredentialProviderStatus.Ready() with { Account = email, ProgramPath = program };
                case "unauthenticated":
                    Forget(session);
                    return new CredentialProviderStatus(CredentialProviderState.SignInNeeded, "Sign in to Bitwarden with bw login, then unlock.")
                    {
                        ProgramPath = program,
                        SignInCommand = [program, "login"],
                    };
                default:
                    // A session key bw no longer takes (the user locked or logged out in a terminal) is of no use.
                    Forget(session);
                    return CredentialProviderStatus.Locked("Type the master password of Bitwarden to unlock it.") with { Account = email, ProgramPath = program };
            }
        }
        finally
        {
            VaultText.Zero(result.Output);
        }
    }

    public async ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status.State is CredentialProviderState.Ready or CredentialProviderState.NotInstalled or CredentialProviderState.SignInNeeded or CredentialProviderState.Unavailable) return status;
        if (string.IsNullOrEmpty(context.Password)) return CredentialProviderStatus.Locked("Type the master password of Bitwarden to unlock it.") with { Account = status.Account, ProgramPath = status.ProgramPath };

        var program = status.ProgramPath!;
        // The host masked the password before it gave it (CredentialBroker.UnlockAsync).
        var result = await _runner.RunAsync(program, ["unlock", "--raw", "--passwordenv", PasswordVariable], new VaultCommandOptions
        {
            Environment = Variables(null, (PasswordVariable, context.Password)),
            Timeout = TimeSpan.FromMinutes(1),
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.ExitCode != 0)
            {
                return CredentialProviderStatus.Locked(result.TimedOut ? "Bitwarden did not answer in time."
                        : result.Error.Contains("master password", StringComparison.OrdinalIgnoreCase) ? "Bitwarden refused the master password."
                        : $"Bitwarden did not unlock: {result.Summary()}.").WithDetail(result)
                    with { Account = status.Account, ProgramPath = program };
            }

            var key = result.OutputText().Trim();
            if (key.Length == 0) return CredentialProviderStatus.Locked("Bitwarden gave no session key.") with { Account = status.Account, ProgramPath = program };
            _mask(key);
            lock (_gate) _sessionKey = key;
        }
        finally
        {
            VaultText.Zero(result.Output);
        }

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var (itemId, field) = Split(reference);
        if (Program is not { } program) throw new CredentialProviderException(CredentialFailure.NotInstalled, "The Bitwarden CLI (bw) is not installed.");
        // Only the master password opens the vault, and it is typed in the window: without a session key nothing is run.
        var session = SessionKey ?? throw new CredentialProviderException(CredentialFailure.Locked, "Bitwarden is locked: unlock it with its master password.");

        if (field.StartsWith("field:", StringComparison.OrdinalIgnoreCase))
        {
            var name = field["field:".Length..];
            var item = await RunCheckedAsync(program, ["get", "item", itemId], session, cancellationToken).ConfigureAwait(false);
            try
            {
                return new CredentialValue(CustomField(item, name) ?? throw new CredentialProviderException(CredentialFailure.NotFound, $"The Bitwarden item {itemId} has no field '{name}'."));
            }
            finally
            {
                VaultText.Zero(item);
            }
        }

        var output = await RunCheckedAsync(program, ["get", field, itemId], session, cancellationToken).ConfigureAwait(false);
        try
        {
            var value = VaultText.TrimOneNewline(System.Text.Encoding.UTF8.GetString(output));
            if (value.Length == 0) throw new CredentialProviderException(CredentialFailure.NotFound, $"The Bitwarden item {itemId} has no {field}.");
            return new CredentialValue(value) { ExpiresAt = field == "totp" ? OnePasswordProvider.NextTotpWindow() : null };
        }
        finally
        {
            VaultText.Zero(output);
        }
    }

    public async ValueTask<CredentialBrowsePage> BrowseAsync(CredentialBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Program is not { } program) throw new CredentialProviderException(CredentialFailure.NotInstalled, "The Bitwarden CLI (bw) is not installed.");
        var session = SessionKey ?? throw new CredentialProviderException(CredentialFailure.Locked, "Bitwarden is locked: unlock it with its master password.");
        if (request.Parent is null)
        {
            // An item added in the app or the web vault is in the cache of bw only after a sync: the list follows one.
            await SyncAsync(program, session, cancellationToken).ConfigureAwait(false);
            // The list of items holds their values too: only the ids, names and types are read of it.
            var list = await RunCheckedAsync(program, ["list", "items"], session, cancellationToken, maximumOutput: 32 * 1024 * 1024).ConfigureAwait(false);
            try
            {
                return BitwardenJson.Items(list);
            }
            finally
            {
                VaultText.Zero(list);
            }
        }

        if (!request.Parent.StartsWith("item:", StringComparison.Ordinal)) throw new CredentialProviderException(CredentialFailure.NotFound, "This entry of Bitwarden is not known.");
        var id = request.Parent["item:".Length..];
        var item = await RunCheckedAsync(program, ["get", "item", id], session, cancellationToken).ConfigureAwait(false);
        try
        {
            return BitwardenJson.Fields(item, id);
        }
        finally
        {
            VaultText.Zero(item);
        }
    }

    public void Lock()
    {
        lock (_gate) _sessionKey = null;
    }

    private void Forget(string? session)
    {
        if (session is null) return;
        lock (_gate)
        {
            if (ReferenceEquals(_sessionKey, session)) _sessionKey = null;
        }
    }

    internal static (string ItemId, string Field) Split(string reference)
    {
        var at = reference.IndexOf('/');
        var itemId = at < 0 ? string.Empty : reference[..at].Trim();
        var field = at < 0 ? string.Empty : reference[(at + 1)..].Trim();
        if (itemId.Length == 0 || !(Fields.Contains(field) || field.StartsWith("field:", StringComparison.OrdinalIgnoreCase) && field.Length > "field:".Length))
            throw new CredentialProviderException(CredentialFailure.NotFound,
                $"'{reference}' is not a Bitwarden reference (<item id>/password, username, totp, notes, uri or field:<name>).");
        return (itemId, field);
    }

    private async Task<byte[]> RunCheckedAsync(string program, IReadOnlyList<string> arguments, string session, CancellationToken cancellationToken, int maximumOutput = 4 * 1024 * 1024)
    {
        var result = await _runner.RunAsync(program, arguments, new VaultCommandOptions
        {
            Environment = Variables(session),
            Timeout = TimeSpan.FromSeconds(30),
            MaximumOutput = maximumOutput,
        }, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0) return result.Output;
        VaultText.Zero(result.Output);
        var error = result.Error;
        if (result.TimedOut) throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden did not answer in time.");
        if (error.Contains("Not found", StringComparison.OrdinalIgnoreCase) || error.Contains("No TOTP", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No notes", StringComparison.OrdinalIgnoreCase) || error.Contains("No uri", StringComparison.OrdinalIgnoreCase)
            || error.Contains("No username", StringComparison.OrdinalIgnoreCase) || error.Contains("No password", StringComparison.OrdinalIgnoreCase))
            throw VaultText.Failure(CredentialFailure.NotFound, "Bitwarden has no such item or field.", result);
        if (error.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
        {
            Forget(session);
            throw new CredentialProviderException(CredentialFailure.SignInNeeded, "Sign in to Bitwarden with bw login, then unlock.");
        }

        if (error.Contains("locked", StringComparison.OrdinalIgnoreCase))
        {
            Forget(session);
            throw new CredentialProviderException(CredentialFailure.Locked, "Bitwarden is locked: unlock it with its master password.");
        }

        throw VaultText.Failure(CredentialFailure.Failed, $"Bitwarden failed: {result.Summary()}.", result);
    }

    // bw sync asks the server whether anything changed, and downloads only then. Offline, or when the server does not answer in a
    // few seconds, the list goes on with what bw has cached; a vault locked or signed out meanwhile fails as the list would.
    private async Task SyncAsync(string program, string session, CancellationToken cancellationToken)
    {
        var result = await RunAsync(program, ["sync"], session, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode == 0 || result.TimedOut) return;
            if (result.Error.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
            {
                Forget(session);
                throw new CredentialProviderException(CredentialFailure.SignInNeeded, "Sign in to Bitwarden with bw login, then unlock.");
            }

            if (result.Error.Contains("locked", StringComparison.OrdinalIgnoreCase))
            {
                Forget(session);
                throw new CredentialProviderException(CredentialFailure.Locked, "Bitwarden is locked: unlock it with its master password.");
            }
        }
        finally
        {
            VaultText.Zero(result.Output);
        }
    }

    private Task<VaultCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, string? session, TimeSpan timeout, CancellationToken cancellationToken)
        => _runner.RunAsync(program, arguments, new VaultCommandOptions { Environment = Variables(session), Timeout = timeout }, cancellationToken);

    // bw never asks a question on its console (BW_NOINTERACTION): it fails instead, and the window asks.
    private static Dictionary<string, string> Variables(string? session, (string Name, string Value)? extra = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["BW_NOINTERACTION"] = "true" };
        if (session is not null) environment["BW_SESSION"] = session;
        if (extra is { } variable) environment[variable.Name] = variable.Value;
        return environment;
    }

    // The value of a custom field of an item: the field is found by its name, and only its value is decoded.
    private static string? CustomField(byte[] json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array) return null;
            foreach (var field in fields.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.Object && field.TryGetProperty("name", out var fieldName) && fieldName.ValueKind == JsonValueKind.String
                    && fieldName.ValueEquals(name) && field.TryGetProperty("value", out var value))
                {
                    return value.ValueKind switch
                    {
                        JsonValueKind.String => value.GetString(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => null,
                    };
                }
            }

            return null;
        }
        catch (JsonException exception)
        {
            throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden answered something that is not an item.", exception);
        }
    }

    private static CredentialProviderStatus NotInstalled() => new(CredentialProviderState.NotInstalled, "The Bitwarden CLI (bw) is not installed.")
    {
        Install = new CredentialProviderInstall(
            OperatingSystem.IsWindows() ? ["winget", "install", "--id", "Bitwarden.CLI", "-e"]
            : OperatingSystem.IsMacOS() ? ["brew", "install", "bitwarden-cli"]
            : null,
            new Uri("https://bitwarden.com/help/cli/#download-and-install")),
    };
}

// Reads the items of Bitwarden from what bw writes, without making a text of a value: the members that hold values are skipped.
internal static class BitwardenJson
{
    public static CredentialBrowsePage Items(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        var entries = new List<CredentialBrowseEntry>();
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden answered something that is not a list.");
            while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                string? id = null, name = null;
                var type = 0;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var property = reader.GetString();
                    reader.Read();
                    switch (property)
                    {
                        case "id" when reader.TokenType == JsonTokenType.String: id = reader.GetString(); break;
                        case "name" when reader.TokenType == JsonTokenType.String: name = reader.GetString(); break;
                        case "type" when reader.TokenType == JsonTokenType.Number: type = reader.GetInt32(); break;
                        default: reader.Skip(); break;
                    }
                }

                if (id is { Length: > 0 }) entries.Add(new CredentialBrowseEntry($"item:{id}", string.IsNullOrWhiteSpace(name) ? id : name, CredentialBrowseEntryKind.Folder) { Detail = TypeName(type) });
            }
        }
        catch (JsonException exception)
        {
            throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden answered something that is not a list.", exception);
        }

        return new CredentialBrowsePage([.. entries.OrderBy(static entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)]);
    }

    public static CredentialBrowsePage Fields(byte[] json, string itemId)
    {
        var reader = new Utf8JsonReader(json);
        string? name = null;
        bool login = false, username = false, password = false, totp = false, uri = false, notes = false;
        var custom = new List<(string Name, int Type)>();
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden answered something that is not an item.");
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var property = reader.GetString();
                reader.Read();
                switch (property)
                {
                    case "name" when reader.TokenType == JsonTokenType.String:
                        name = reader.GetString();
                        break;
                    case "notes":
                        notes = IsSet(ref reader);
                        break;
                    case "login" when reader.TokenType == JsonTokenType.StartObject:
                        login = true;
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var member = reader.GetString();
                            reader.Read();
                            switch (member)
                            {
                                case "username": username = IsSet(ref reader); break;
                                case "password": password = IsSet(ref reader); break;
                                case "totp": totp = IsSet(ref reader); break;
                                case "uris" when reader.TokenType == JsonTokenType.StartArray:
                                    // An array that holds anything has a first uri.
                                    reader.Read();
                                    uri = reader.TokenType != JsonTokenType.EndArray;
                                    if (uri)
                                    {
                                        reader.Skip();
                                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) reader.Skip();
                                    }

                                    break;
                                default:
                                    reader.Skip();
                                    break;
                            }
                        }

                        break;
                    case "fields" when reader.TokenType == JsonTokenType.StartArray:
                        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                        {
                            string? fieldName = null;
                            var fieldType = 0;
                            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                            {
                                var member = reader.GetString();
                                reader.Read();
                                if (member == "name" && reader.TokenType == JsonTokenType.String) fieldName = reader.GetString();
                                else if (member == "type" && reader.TokenType == JsonTokenType.Number) fieldType = reader.GetInt32();
                                else reader.Skip();
                            }

                            // A linked field (3) points at another field: it has no value of its own.
                            if (!string.IsNullOrEmpty(fieldName) && fieldType != 3) custom.Add((fieldName, fieldType));
                        }

                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden answered something that is not an item.", exception);
        }

        var entries = new List<CredentialBrowseEntry>();
        void Add(string field, string label, string? detail) => entries.Add(new CredentialBrowseEntry($"{itemId}/{field}", label, CredentialBrowseEntryKind.Field) { Reference = $"{itemId}/{field}", Detail = detail });
        if (login && password) Add("password", "password", "concealed");
        if (login && username) Add("username", "username", null);
        if (login && totp) Add("totp", "one-time password", "one-time password");
        if (login && uri) Add("uri", "uri", null);
        if (notes) Add("notes", "notes", null);
        foreach (var (fieldName, fieldType) in custom) Add($"field:{fieldName}", fieldName, fieldType == 1 ? "hidden" : fieldType == 2 ? "boolean" : "text");
        return new CredentialBrowsePage(entries) { Path = name };
    }

    // Whether a member holds a text that is not empty, read without decoding it.
    private static bool IsSet(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.HasValueSequence ? reader.ValueSequence.Length > 0 : reader.ValueSpan.Length > 0;
        reader.Skip();
        return false;
    }

    private static string? TypeName(int type) => type switch
    {
        1 => "login",
        2 => "note",
        3 => "card",
        4 => "identity",
        5 => "ssh key",
        _ => null,
    };
}
