using System.Globalization;
using CodeAlta.Catalog.Secrets;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Settings &gt; Secrets: the secrets and variables of this machine (<see cref="LocalSecretStore"/>). A secret is
/// listed by name, set, rotated and deleted: the page sends a value, and never receives one back. Variables are read
/// and written.
/// </summary>
[NeoRpcService("secrets", Version = 1)]
internal sealed partial class SecretsService
{
    private const int MaximumNameLength = 128;
    private readonly LocalSecretStore? _store;
    private readonly string? _epoch;
    private readonly Action? _changed;

    internal SecretsService() { }

    /// <summary>Creates the service over the store of the profile.</summary>
    /// <param name="store">The secrets and variables of this machine.</param>
    /// <param name="epoch">The host epoch requests must name.</param>
    /// <param name="changed">Called after a change, so that what masks the secrets reads them again; null for nothing.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    internal SecretsService(LocalSecretStore store, string epoch, Action? changed = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _store = store;
        _epoch = epoch;
        _changed = changed;
    }

    /// <summary>Gets who sets a value from this window, as the store records it.</summary>
    internal string SetBy { get; init; } = $"{Environment.UserName} (CodeAlta Desktop)";

    /// <summary>Lists the secrets, by name, and the variables; with a broker, the vaults, their types and what waits for a confirmation.</summary>
    [NeoRpcMethod("list")]
    public async Task<SecretsList> ListAsync(SecretsRequest request, CancellationToken cancellationToken)
    {
        var list = List(request);
        return list.Status == "ok" ? await WithCredentialsAsync(list, cancellationToken).ConfigureAwait(false) : list;
    }

    /// <summary>Lists the secrets, by name, and the variables, without the vaults.</summary>
    internal SecretsList List(SecretsRequest request)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, [], []);
        try
        {
            return new("ok", _store!.Protection,
                [.. _store.ListSecrets().Select(static secret => new SecretView(secret.Name, secret.Version, secret.SetBy, Format(secret.SetAt)))],
                [.. _store.ListVariables().Select(static variable => new VariableView(variable.Name, variable.Value, variable.Version, variable.SetBy, Format(variable.SetAt)))]);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new("read_failed", null, [], []);
        }
    }

    /// <summary>Adds a secret, or gives it a new value and a new version.</summary>
    [NeoRpcMethod("setSecret")]
    public SecretsActionResult SetSecret(SecretWriteRequest request)
        => Write(request?.ExpectedEpoch, request?.Name, request?.Value, (store, name, value) => store.SetSecret(name, value, SetBy));

    /// <summary>Gives a secret that is set a new value and a new version.</summary>
    [NeoRpcMethod("rotateSecret")]
    public SecretsActionResult RotateSecret(SecretWriteRequest request)
        => Write(request?.ExpectedEpoch, request?.Name, request?.Value, (store, name, value) => store.RotateSecret(name, value, SetBy));

    /// <summary>Deletes a secret.</summary>
    [NeoRpcMethod("deleteSecret")]
    public SecretsActionResult DeleteSecret(SecretNameRequest request)
        => Write(request?.ExpectedEpoch, request?.Name, "-", (store, name, _) => store.DeleteSecret(name) ? null : throw new KeyNotFoundException());

    /// <summary>Adds a variable, or changes its value.</summary>
    [NeoRpcMethod("setVariable")]
    public SecretsActionResult SetVariable(SecretWriteRequest request)
        => Write(request?.ExpectedEpoch, request?.Name, request?.Value, (store, name, value) => store.SetVariable(name, value, SetBy));

    /// <summary>Deletes a variable.</summary>
    [NeoRpcMethod("deleteVariable")]
    public SecretsActionResult DeleteVariable(SecretNameRequest request)
        => Write(request?.ExpectedEpoch, request?.Name, "-", (store, name, _) => store.DeleteVariable(name) ? null : throw new KeyNotFoundException());

    private SecretsActionResult Write(string? expectedEpoch, string? name, string? value, Func<LocalSecretStore, string, string, object?> change)
    {
        if (Refuse(expectedEpoch) is { } refused) return new(refused, null);
        if (name is not { Length: > 0 and <= MaximumNameLength } || !LocalSecretStore.IsName(name.Trim()))
            return new("invalid", "A name is letters, digits and _, not starting with a digit, as the name of an environment variable.");
        if (string.IsNullOrEmpty(value)) return new("invalid", "The value is empty.");
        if (value.Length > LocalSecretStore.MaximumValueLength) return new("too_large", null);
        try
        {
            change(_store!, name.Trim(), value);
        }
        catch (KeyNotFoundException) { return new("not_found", null); }
        catch (ArgumentException exception) { return new("invalid", exception.Message.Split(" (Parameter", 2)[0]); }
        catch (InvalidOperationException exception) { return new("invalid", exception.Message); }
        catch (SecretStoreUnavailableException exception) { return new("write_failed", exception.Message); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException) { return new("write_failed", exception.Message); }

        _changed?.Invoke();
        return new("ok", null);
    }

    private string? Refuse(string? expectedEpoch)
        => _store is null ? "unavailable" : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;

    private static string Format(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

/// <summary>A request of the page.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
internal sealed record SecretsRequest(string ExpectedEpoch);

/// <summary>The secrets and variables of this machine.</summary>
/// <param name="Status"><c>ok</c>, or why they are not listed.</param>
/// <param name="Protection">What protects the values of the secrets (<c>dpapi</c>, <c>keychain</c>, <c>libsecret</c>, <c>file</c>); null while none is set.</param>
/// <param name="Secrets">The secrets, by name.</param>
/// <param name="Variables">The variables, with their values.</param>
internal sealed record SecretsList(string Status, string? Protection, SecretView[] Secrets, VariableView[] Variables)
{
    /// <summary>The credential providers of the user.</summary>
    public CredentialProviderRpcView[] Providers { get; init; } = [];

    /// <summary>The types of providers the page offers.</summary>
    public CredentialTypeView[] Types { get; init; } = [];

    /// <summary>The uses of secrets that wait for a confirmation.</summary>
    public PendingUseView[] Pending { get; init; } = [];

    /// <summary>The recent uses of the secrets, the newest first.</summary>
    public SecretUseView[] Uses { get; init; } = [];

    /// <summary>Whether the system can verify the user, for the secrets that ask before use; null without a broker.</summary>
    public VerificationView? Verification { get; init; }

    /// <summary>Whether this instance asks a vault to unlock; the developer instance never does.</summary>
    public bool CanPrompt { get; init; }

    /// <summary>What is wrong with the tables of the configuration that were left out.</summary>
    public string[] ConfigurationProblems { get; init; } = [];
}

/// <summary>A secret as the page shows it: never its value.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Version">Its version.</param>
/// <param name="SetBy">Who set its value.</param>
/// <param name="SetAt">When, in ISO 8601.</param>
internal sealed record SecretView(string Name, int Version, string SetBy, string SetAt)
{
    /// <summary>The id of the credential provider that keeps its value; null for a value of this computer.</summary>
    public string? Provider { get; init; }

    /// <summary>The name of that provider.</summary>
    public string? ProviderName { get; init; }

    /// <summary>The icon of that provider.</summary>
    public string? Icon { get; init; }

    /// <summary>What the user picked in the vault.</summary>
    public string? Label { get; init; }

    /// <summary>How long a confirmation lasts when it asks before use; null when it does not ask.</summary>
    public string? AskBeforeUse { get; init; }

    /// <summary><c>ready</c>, <c>locked</c>, <c>sign_in</c>, <c>not_installed</c>, <c>waits</c>, <c>not_found</c> or <c>unavailable</c>.</summary>
    public string State { get; init; } = "ready";

    /// <summary>What the user reads beside its state.</summary>
    public string? StateMessage { get; init; }
}

/// <summary>A variable as the page shows it.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Value">Its value.</param>
/// <param name="Version">Its version.</param>
/// <param name="SetBy">Who set its value.</param>
/// <param name="SetAt">When, in ISO 8601.</param>
internal sealed record VariableView(string Name, string Value, int Version, string SetBy, string SetAt);

/// <summary>A value the page writes.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Name">The name.</param>
/// <param name="Value">The value.</param>
internal sealed record SecretWriteRequest(string ExpectedEpoch, string Name, string Value);

/// <summary>A name the page deletes.</summary>
/// <param name="ExpectedEpoch">The host epoch the page knows.</param>
/// <param name="Name">The name.</param>
internal sealed record SecretNameRequest(string ExpectedEpoch, string Name);

/// <summary>The result of a change.</summary>
/// <param name="Status"><c>ok</c>, or why it was refused.</param>
/// <param name="Message">What is wrong, for <c>invalid</c> and <c>write_failed</c>.</param>
internal sealed record SecretsActionResult(string Status, string? Message);
