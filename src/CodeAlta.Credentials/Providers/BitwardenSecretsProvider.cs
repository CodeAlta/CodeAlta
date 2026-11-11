using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// Bitwarden Secrets Manager, through its <c>bws</c> CLI and the access token of a machine account, a secret of this computer
/// given to <c>bws</c> in its environment (<c>BWS_ACCESS_TOKEN</c>). It needs nobody. A reference is the id of a secret.
/// </summary>
internal sealed class BitwardenSecretsProvider : ICredentialProvider
{
    internal const string TypeName = "bitwarden-secrets";

    private readonly VaultCommandRunner _runner;
    private readonly string? _configuredPath;
    private readonly string? _accessToken;

    public BitwardenSecretsProvider(PluginCredentialProviderContext context, VaultCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _configuredPath = context.GetString("path");
        _accessToken = context.GetString("access_token");
    }

    public static PluginCredentialProviderContribution Contribution(VaultCommandRunner runner) => new()
    {
        Type = TypeName,
        DisplayName = "Bitwarden Secrets Manager",
        Description = "Reads the secrets of Bitwarden Secrets Manager through the bws CLI, with the access token of a machine account.",
        Icon = "bitwarden",
        Website = new Uri("https://bitwarden.com/help/secrets-manager-cli/"),
        ReferenceHint = "<secret id>",
        Settings =
        [
            new("access_token", "Access token", PluginCredentialProviderSettingKind.Secret)
            {
                Required = true, Placeholder = "${secret:BWS_ACCESS_TOKEN}",
                Description = "A secret of this computer that holds the access token of a machine account.",
            },
            new("path", "Path of bws", PluginCredentialProviderSettingKind.Path) { Description = "Leave empty to find bws on the PATH." },
        ],
        Create = context => new BitwardenSecretsProvider(context, runner),
    };

    private string? Program => VaultCommandRunner.FindProgram("bws", _configuredPath);

    public async ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (Program is not { } program) return NotInstalled();
        if (_accessToken is null) return CredentialProviderStatus.Unavailable("The access token of Bitwarden Secrets Manager is not set.") with { ProgramPath = program };
        var result = await RunAsync(program, ["project", "list", "--output", "json"], cancellationToken).ConfigureAwait(false);
        VaultText.Zero(result.Output);
        return result.ExitCode == 0
            ? CredentialProviderStatus.Ready() with { Account = "machine account", ProgramPath = program }
            : CredentialProviderStatus.Unavailable($"Bitwarden Secrets Manager: {VaultText.ErrorLine(result)}") with { ProgramPath = program };
    }

    public ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);

    public async ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var output = await RunCheckedAsync(["secret", "get", reference.Trim(), "--output", "json"], cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.String)
                throw new CredentialProviderException(CredentialFailure.NotFound, $"Bitwarden Secrets Manager gave no value for {reference}.");
            return new CredentialValue(value.GetString()!);
        }
        catch (JsonException exception)
        {
            throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden Secrets Manager answered something that is not a secret.", exception);
        }
        finally
        {
            VaultText.Zero(output);
        }
    }

    public async ValueTask<CredentialBrowsePage> BrowseAsync(CredentialBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Parent is not null) throw new CredentialProviderException(CredentialFailure.NotFound, "This entry of Bitwarden Secrets Manager is not known.");
        // The list holds the values of the secrets: only their ids and keys are read of it.
        var output = await RunCheckedAsync(["secret", "list", "--output", "json"], cancellationToken).ConfigureAwait(false);
        try
        {
            var reader = new Utf8JsonReader(output);
            var entries = new List<CredentialBrowseEntry>();
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden Secrets Manager answered something that is not a list.");
            while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
            {
                string? id = null, key = null;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var property = reader.GetString();
                    reader.Read();
                    if (property == "id" && reader.TokenType == JsonTokenType.String) id = reader.GetString();
                    else if (property == "key" && reader.TokenType == JsonTokenType.String) key = reader.GetString();
                    else reader.Skip();
                }

                if (id is { Length: > 0 }) entries.Add(new CredentialBrowseEntry(id, string.IsNullOrWhiteSpace(key) ? id : key, CredentialBrowseEntryKind.Field) { Reference = id });
            }

            return new CredentialBrowsePage([.. entries.OrderBy(static entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)]);
        }
        catch (JsonException exception)
        {
            throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden Secrets Manager answered something that is not a list.", exception);
        }
        finally
        {
            VaultText.Zero(output);
        }
    }

    private async Task<byte[]> RunCheckedAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (Program is not { } program) throw new CredentialProviderException(CredentialFailure.NotInstalled, "The Bitwarden Secrets Manager CLI (bws) is not installed.");
        if (_accessToken is null) throw new CredentialProviderException(CredentialFailure.Failed, "The access token of Bitwarden Secrets Manager is not set.");
        var result = await RunAsync(program, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == 0) return result.Output;
        VaultText.Zero(result.Output);
        if (result.TimedOut) throw new CredentialProviderException(CredentialFailure.Failed, "Bitwarden Secrets Manager did not answer in time.");
        var error = result.Error;
        var notFound = error.Contains("404", StringComparison.Ordinal) || error.Contains("not found", StringComparison.OrdinalIgnoreCase) || error.Contains("invalid value", StringComparison.OrdinalIgnoreCase);
        throw new CredentialProviderException(notFound ? CredentialFailure.NotFound : CredentialFailure.Failed, $"Bitwarden Secrets Manager: {VaultText.ErrorLine(result)}");
    }

    private Task<VaultCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        => _runner.RunAsync(program, arguments, new VaultCommandOptions
        {
            Environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["BWS_ACCESS_TOKEN"] = _accessToken ?? string.Empty },
            Timeout = TimeSpan.FromSeconds(30),
        }, cancellationToken);

    private static CredentialProviderStatus NotInstalled() => new(CredentialProviderState.NotInstalled, "The Bitwarden Secrets Manager CLI (bws) is not installed.")
    {
        // bws is a download of its releases, or cargo install bws: no package manager of the systems has it.
        Install = new CredentialProviderInstall(null, new Uri("https://bitwarden.com/help/secrets-manager-cli/#download-and-install")),
    };
}
