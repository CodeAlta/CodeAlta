using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// The secret managers read through their own command line, already signed in by the user: each a fixed command, a check of the
/// sign-in that never prints a value, the command to sign in, an icon and how to install it.
/// </summary>
internal static class CommandPresets
{
    public static IEnumerable<PluginCredentialProviderContribution> All(VaultCommandRunner runner)
    {
        yield return Preset(runner, "vault", "HashiCorp Vault", "vault", "Reads the key-value secrets of HashiCorp Vault with the vault CLI, signed in with vault login.",
            "secret/myapp#api_key", new Uri("https://developer.hashicorp.com/vault/install"),
            [new("address", "Address", PluginCredentialProviderSettingKind.Text) { Placeholder = "https://vault.example.com:8200" },
             new("namespace", "Namespace", PluginCredentialProviderSettingKind.Text)],
            context => new CommandShape("Vault", ["vault", "kv", "get", "-field={field}", "{item}"])
            {
                Status = ["vault", "token", "lookup", "-format=json"], SignIn = ["vault", "login"],
                Environment = Variables(("VAULT_ADDR", context.GetString("address")), ("VAULT_NAMESPACE", context.GetString("namespace"))),
                Install = Install(["winget", "install", "HashiCorp.Vault"], ["brew", "install", "hashicorp/tap/vault"], "https://developer.hashicorp.com/vault/install"),
            });
        yield return Preset(runner, "openbao", "OpenBao", "openbao", "Reads the key-value secrets of OpenBao with the bao CLI, signed in with bao login.",
            "secret/myapp#api_key", new Uri("https://openbao.org/docs/install/"),
            [new("address", "Address", PluginCredentialProviderSettingKind.Text) { Placeholder = "https://bao.example.com:8200" },
             new("namespace", "Namespace", PluginCredentialProviderSettingKind.Text)],
            context => new CommandShape("OpenBao", ["bao", "kv", "get", "-field={field}", "{item}"])
            {
                Status = ["bao", "token", "lookup", "-format=json"], SignIn = ["bao", "login"],
                Environment = Variables(("BAO_ADDR", context.GetString("address")), ("BAO_NAMESPACE", context.GetString("namespace"))),
                Install = Install(null, ["brew", "install", "openbao"], "https://openbao.org/docs/install/"),
            });
        yield return Preset(runner, "aws-secrets-manager", "AWS Secrets Manager", "aws", "Reads AWS Secrets Manager with the aws CLI, signed in with its profiles or aws sso login. A secret that holds JSON gives one of its keys after #.",
            "prod/myapp#api_key", new Uri("https://aws.amazon.com/cli/"),
            [new("profile", "Profile", PluginCredentialProviderSettingKind.Text), new("region", "Region", PluginCredentialProviderSettingKind.Text) { Placeholder = "eu-west-1" }],
            context => new CommandShape("AWS", ["aws", "secretsmanager", "get-secret-value", "--secret-id", "{item}", "--query", "SecretString", "--output", "text"])
            {
                Status = ["aws", "sts", "get-caller-identity", "--output", "json"], SignIn = ["aws", "sso", "login"], JsonFromField = true,
                Environment = Variables(("AWS_PROFILE", context.GetString("profile")), ("AWS_REGION", context.GetString("region"))),
                Install = Install(["winget", "install", "Amazon.AWSCLI"], ["brew", "install", "awscli"], "https://aws.amazon.com/cli/"),
            });
        yield return Preset(runner, "azure-key-vault", "Azure Key Vault", "azure", "Reads the secrets of an Azure Key Vault with the az CLI, signed in with az login.",
            "openai-api-key", new Uri("https://learn.microsoft.com/cli/azure/install-azure-cli"),
            [new("vault", "Key vault", PluginCredentialProviderSettingKind.Text) { Required = true, Placeholder = "contoso-vault" }],
            context => new CommandShape("Azure", ["az", "keyvault", "secret", "show", "--vault-name", context.GetString("vault") ?? string.Empty, "--name", "{item}", "--query", "value", "-o", "tsv"])
            {
                Status = ["az", "account", "show", "-o", "none"], SignIn = ["az", "login"],
                Install = Install(["winget", "install", "Microsoft.AzureCLI"], ["brew", "install", "azure-cli"], "https://learn.microsoft.com/cli/azure/install-azure-cli"),
            });
        yield return Preset(runner, "google-secret-manager", "Google Secret Manager", "googlecloud", "Reads the latest version of the secrets of Google Secret Manager with the gcloud CLI, signed in with gcloud auth login.",
            "openai-api-key", new Uri("https://cloud.google.com/sdk/docs/install"),
            [new("project", "Project", PluginCredentialProviderSettingKind.Text) { Placeholder = "my-project" }],
            context => new CommandShape("Google Cloud", ["gcloud", "secrets", "versions", "access", "latest", "--secret={item}"])
            {
                Status = ["gcloud", "auth", "list", "--filter=status:ACTIVE", "--format=value(account)"], SignIn = ["gcloud", "auth", "login"],
                Environment = Variables(("CLOUDSDK_CORE_PROJECT", context.GetString("project"))),
                Install = Install(["winget", "install", "Google.CloudSDK"], ["brew", "install", "--cask", "google-cloud-sdk"], "https://cloud.google.com/sdk/docs/install"),
            });
        yield return Preset(runner, "doppler", "Doppler", "key-round", "Reads the secrets of a Doppler project with the doppler CLI, signed in with doppler login.",
            "OPENAI_API_KEY", new Uri("https://docs.doppler.com/docs/install-cli"),
            [new("project", "Project", PluginCredentialProviderSettingKind.Text), new("config", "Config", PluginCredentialProviderSettingKind.Text) { Placeholder = "dev" }],
            context => new CommandShape("Doppler", ["doppler", "secrets", "get", "{item}", "--plain"])
            {
                Status = ["doppler", "me", "--json"], SignIn = ["doppler", "login"],
                Environment = Variables(("DOPPLER_PROJECT", context.GetString("project")), ("DOPPLER_CONFIG", context.GetString("config"))),
                Install = Install(["winget", "install", "doppler.doppler"], ["brew", "install", "dopplerhq/cli/doppler"], "https://docs.doppler.com/docs/install-cli"),
            });
        yield return Preset(runner, "infisical", "Infisical", "key-round", "Reads the secrets of an Infisical project with the infisical CLI, signed in with infisical login.",
            "OPENAI_API_KEY", new Uri("https://infisical.com/docs/cli/overview"),
            [new("project_id", "Project id", PluginCredentialProviderSettingKind.Text), new("environment", "Environment", PluginCredentialProviderSettingKind.Text) { Placeholder = "dev" }],
            context => new CommandShape("Infisical", ["infisical", "secrets", "get", "{item}", "--plain", "--silent"])
            {
                SignIn = ["infisical", "login"],
                ExtraArguments = Options(("--projectId", context.GetString("project_id")), ("--env", context.GetString("environment"))),
                Install = Install(null, ["brew", "install", "infisical/get-cli/infisical"], "https://infisical.com/docs/cli/overview"),
            });
    }

    private static PluginCredentialProviderContribution Preset(VaultCommandRunner runner, string type, string name, string icon, string description, string hint, Uri website,
        IReadOnlyList<PluginCredentialProviderSetting> settings, Func<PluginCredentialProviderContext, CommandShape> shape) => new()
    {
        Type = type,
        DisplayName = name,
        Description = description,
        Icon = icon,
        ReferenceHint = hint,
        Website = website,
        Settings = [.. settings, new("path", "Path of the program", PluginCredentialProviderSettingKind.Path) { Description = "Leave empty to find it on the PATH." }],
        Create = context => new CommandProvider(shape(context) with { ProgramPath = context.GetString("path") }, runner),
    };

    private static Dictionary<string, string>? Variables(params (string Name, string? Value)[] variables)
    {
        var set = variables.Where(static variable => variable.Value is not null).ToDictionary(static variable => variable.Name, static variable => variable.Value!, StringComparer.Ordinal);
        return set.Count == 0 ? null : set;
    }

    private static IReadOnlyList<string> Options(params (string Option, string? Value)[] options)
        => [.. options.Where(static option => option.Value is not null).SelectMany(static option => new[] { option.Option, option.Value! })];

    private static CredentialProviderInstall Install(IReadOnlyList<string>? windows, IReadOnlyList<string>? macOS, string page)
        => new(OperatingSystem.IsWindows() ? windows : OperatingSystem.IsMacOS() ? macOS : null, new Uri(page));
}
