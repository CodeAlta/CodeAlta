using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using XenoAtom.CommandLine;
using Command = XenoAtom.CommandLine.Command;

namespace CodeAlta.LiveTool;

// The `alta secret` commands: the names of the secrets and variables of this machine, which an agent writes into the
// configuration as ${secret:NAME} or ${var:NAME}. An agent never reads a value, and never sets one: the user does, in
// Settings > Secrets or with `altanode secrets set`.
internal sealed partial class BuiltInAltaCommandContributor
{
    private static readonly AltaCommandPolicy[] SecretPolicies =
    [
        Read("secret list"),
        // It is always refused: an agent sets no value.
        Read("secret set"),
    ];

    private static Command CreateSecretCommand(AltaCommandContext context)
    {
        var group = Group("secret", "List the names of the secrets and variables of this machine, to refer to them in the configuration.");
        group.Add(CreateSecretListCommand(context));
        group.Add(CreateSecretSetCommand(context));
        AddSecretVaultCommands(group, context);
        AddHelpText(
            group,
            "A secret or a variable is named in the configuration, never copied there: `${secret:NAME}` and `${var:NAME}` wherever `${env:NAME}` works (CLI profiles of spaces, `env` and headers of MCP servers), and `api_key_secret = \"NAME\"` for a provider.",
            "Agents read names only. The user sets values in Settings > Secrets of CodeAlta Desktop, or with `altanode secrets set <name>`.",
            "A secret can be kept in a vault (`alta credential`): `secret list` names its provider and its state, `secret test` checks it, `secret bind` proposes to keep one in a vault.",
            "Example: `alta secret list`.");
        return group;
    }

    private static Command CreateSecretListCommand(AltaCommandContext context)
    {
        var command = Leaf("list", "List the secrets and variables of this machine, by name: never a value.");
        command.Add((_, _) => HandleSecretList(context));
        AddHelpText(command, "A record names a secret or a variable, its version, and when it was set; never its value.");
        return command;
    }

    private static Command CreateSecretSetCommand(AltaCommandContext context)
    {
        var command = Leaf("set", "Refused: an agent never sets the value of a secret.");
        command.Add("<words>*", "Ignored.", _ => { });
        command.Add((_, _) => ValueTask.FromResult(PermissionDenied(context, "secret.setByAgent",
            "An agent never sets the value of a secret. Ask the user to set it in Settings > Secrets of CodeAlta Desktop, or with `altanode secrets set <name>` on the machine.")));
        return command;
    }

    private static ValueTask<int> HandleSecretList(AltaCommandContext context)
    {
        if (!context.TryGetRequired<ProjectCatalog>(nameof(ProjectCatalog), out var catalog)) return ValueTask.FromResult(AltaExitCodes.ServiceUnavailable);
        var store = new LocalSecretStore(catalog.Options.GlobalRoot);
        IReadOnlyList<SecretInfo> secrets;
        IReadOnlyList<VariableInfo> variables;
        try
        {
            secrets = store.ListSecrets();
            variables = store.ListVariables();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(Unsupported(context, "secret.unreadable", exception.Message));
        }

        var broker = context.Services.Get<CodeAlta.Credentials.CredentialBroker>();
        var providers = broker?.ListProviders().ToDictionary(static provider => provider.Id, StringComparer.Ordinal);
        foreach (var secret in secrets)
        {
            // Where it is kept and whether it can be given, never the item of the vault: the titles of a vault are private.
            var availability = broker?.GetAvailability(secret.Name);
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.secret",
                version = 1,
                correlationId = context.CorrelationId,
                kind = "secret",
                name = secret.Name,
                reference = new SecretReference(SecretReferenceKind.Secret, secret.Name).ToString(),
                secretVersion = secret.Version,
                setAt = secret.SetAt,
                scope = secret.Scope,
                provider = secret.Provider ?? "this-computer",
                providerType = secret.Provider is { } id ? providers?.GetValueOrDefault(id)?.Type : null,
                state = availability is null ? null : availability.State switch
                {
                    CodeAlta.Credentials.SecretAvailabilityState.Ready => "ready",
                    CodeAlta.Credentials.SecretAvailabilityState.Locked => "locked",
                    CodeAlta.Credentials.SecretAvailabilityState.WaitsForYou => "waits",
                    CodeAlta.Credentials.SecretAvailabilityState.NotFound => "not_found",
                    CodeAlta.Credentials.SecretAvailabilityState.SignInNeeded => "sign_in",
                    CodeAlta.Credentials.SecretAvailabilityState.NotInstalled => "not_installed",
                    _ => "unavailable",
                },
                askBeforeUse = secret.AskBeforeUse is not null,
            });
        }

        foreach (var variable in variables)
        {
            AltaJsonlWriter.WriteRecord(context.Stdout, new
            {
                type = "alta.secret",
                version = 1,
                correlationId = context.CorrelationId,
                kind = "variable",
                name = variable.Name,
                reference = new SecretReference(SecretReferenceKind.Variable, variable.Name).ToString(),
                secretVersion = variable.Version,
                setAt = variable.SetAt,
                scope = variable.Scope,
            });
        }

        return ValueTask.FromResult(AltaExitCodes.Success);
    }
}
