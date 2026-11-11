using System.Text.Json;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Providers;

/// <summary>
/// A command line that prints one secret: the user's own (<c>type = "command"</c>) or a ready-made one of a secret manager
/// (<see cref="CommandPresets"/>). The reference is split at its last <c>#</c>: <c>{item}</c> before, <c>{field}</c> after, and
/// <c>{ref}</c> is all of it. The program is found by its name and run by its full path, with the arguments as they are.
/// </summary>
internal sealed class CommandProvider : ICredentialProvider
{
    internal const string TypeName = "command";

    private readonly VaultCommandRunner _runner;
    private readonly CommandShape _shape;

    public CommandProvider(CommandShape shape, VaultCommandRunner runner)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(runner);
        _shape = shape;
        _runner = runner;
    }

    public static PluginCredentialProviderContribution Contribution(VaultCommandRunner runner) => new()
    {
        Type = TypeName,
        DisplayName = "A command",
        Description = "Runs a command line that prints the secret: any tool of a vault (Proton Pass, Keeper, Dashlane, LastPass, Passbolt...) or a script of yours.",
        Icon = "terminal",
        ReferenceHint = "item#field",
        Settings =
        [
            new("read", "Command that prints a secret", PluginCredentialProviderSettingKind.List)
            {
                Required = true, Placeholder = "pass-cli item view {ref}",
                Description = "The program and its arguments; {ref} is the reference, {item} and {field} its parts before and after #. Never a shell: no pipe, no variable.",
            },
            new("status", "Command that says it is unlocked", PluginCredentialProviderSettingKind.List) { Description = "Optional: exits with 0 when the vault can be read without asking anybody." },
            new("sign_in", "Command to sign in", PluginCredentialProviderSettingKind.List) { Description = "Optional: run in a terminal you see when the vault needs a sign-in." },
            new("json", "Member of a JSON answer", PluginCredentialProviderSettingKind.Text) { Placeholder = "data.value", Description = "Optional: the command prints JSON, and the secret is this member." },
        ],
        Create = context => new CommandProvider(CommandShape.FromSettings(context), runner),
    };

    public async ValueTask<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        if (_shape.Read.Count == 0) return CredentialProviderStatus.Unavailable("No command reads the secrets of this provider.");
        if (Program(_shape.Read[0]) is not { } program) return NotInstalled(_shape.Read[0]);
        if (_shape.Status is not { Count: > 0 } status) return CredentialProviderStatus.Ready() with { ProgramPath = program };
        if (Program(status[0]) is not { } statusProgram) return NotInstalled(status[0]);
        var result = await _runner.RunAsync(statusProgram, Arguments(status, null, extras: false), new VaultCommandOptions { Environment = _shape.Environment, Timeout = TimeSpan.FromSeconds(20) }, cancellationToken).ConfigureAwait(false);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(result.Output);
        return result.ExitCode == 0
            ? CredentialProviderStatus.Ready() with { ProgramPath = program }
            : new CredentialProviderStatus(_shape.SignIn is { Count: > 0 } ? CredentialProviderState.SignInNeeded : CredentialProviderState.Locked, $"{_shape.DisplayName}: {result.ErrorLine()}")
            {
                ProgramPath = program,
                SignInCommand = SignInCommand(),
            };
    }

    public ValueTask<CredentialProviderStatus> UnlockAsync(CredentialUnlockContext context, CancellationToken cancellationToken) => GetStatusAsync(cancellationToken);

    public async ValueTask<CredentialValue> ReadAsync(string reference, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (_shape.Read.Count == 0 || Program(_shape.Read[0]) is not { } program)
            throw new CredentialProviderException(CredentialFailure.NotInstalled, $"{(_shape.Read.Count == 0 ? "The command" : _shape.Read[0])} is not found.");
        var arguments = Arguments(_shape.Read, reference, extras: true);
        var result = await _runner.RunAsync(program, arguments, new VaultCommandOptions { Environment = _shape.Environment, Timeout = interactive ? TimeSpan.FromMinutes(2) : TimeSpan.FromSeconds(30) }, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (result.ExitCode != 0)
                throw new CredentialProviderException(result.TimedOut ? CredentialFailure.Locked : CredentialFailure.Failed, $"{_shape.DisplayName}: {result.ErrorLine()}");
            var json = _shape.JsonMember is { } member ? member : _shape.JsonFromField && Split(reference).Field is { Length: > 0 } field ? field : null;
            var value = json is null ? TrimOneNewline(result.OutputText()) : Member(result.Output, json, _shape.JsonFromField ? reference : null);
            if (value.Length == 0) throw new CredentialProviderException(CredentialFailure.NotFound, $"{_shape.DisplayName} printed nothing for {reference}.");
            return new CredentialValue(value);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(result.Output);
        }
    }

    private IReadOnlyList<string>? SignInCommand()
        => _shape.SignIn is { Count: > 0 } signIn && Program(signIn[0]) is { } signInProgram ? [signInProgram, .. signIn.Skip(1)] : null;

    private CredentialProviderStatus NotInstalled(string name) => new(CredentialProviderState.NotInstalled, $"{name} is not found on this computer.")
    {
        Install = _shape.Install,
    };

    private string? Program(string name) => VaultCommandRunner.FindProgram(name, name == _shape.Read.FirstOrDefault() ? _shape.ProgramPath : null);

    // The extra arguments of a preset (its options) go to the command that reads only.
    private List<string> Arguments(IReadOnlyList<string> command, string? reference, bool extras)
    {
        var (item, field) = reference is null ? (string.Empty, string.Empty) : Split(reference);
        var arguments = new List<string>(command.Count + _shape.ExtraArguments.Count);
        foreach (var argument in command.Skip(1))
        {
            arguments.Add(argument.Replace("{ref}", reference ?? string.Empty, StringComparison.Ordinal)
                .Replace("{item}", item, StringComparison.Ordinal)
                .Replace("{field}", field, StringComparison.Ordinal));
        }

        if (extras) arguments.AddRange(_shape.ExtraArguments);
        return arguments;
    }

    internal static (string Item, string Field) Split(string reference)
    {
        var at = reference.LastIndexOf('#');
        return at < 0 ? (reference, string.Empty) : (reference[..at], reference[(at + 1)..]);
    }

    private static string TrimOneNewline(string text)
        => text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text;

    // A member of the JSON the command printed, by a path of names (data.value); a text of JSON inside (the secret of a secret
    // manager that holds JSON) is read as JSON when the path goes on.
    private static string Member(byte[] output, string path, string? reference)
    {
        try
        {
            var names = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            using var document = JsonDocument.Parse(output);
            var element = document.RootElement;
            for (var index = 0; index < names.Length; index++)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    // The secret is itself JSON: the rest of the path is read in it.
                    var inner = System.Text.Encoding.UTF8.GetBytes(element.GetString()!);
                    try
                    {
                        return Member(inner, string.Join('.', names[index..]), reference);
                    }
                    finally
                    {
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(inner);
                    }
                }

                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(names[index], out element))
                    throw new CredentialProviderException(CredentialFailure.NotFound, $"The answer has no member '{names[index]}'{(reference is null ? "" : $" for {reference}")}.");
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();
        }
        catch (JsonException exception)
        {
            throw new CredentialProviderException(CredentialFailure.Failed, "The command did not print JSON.", exception);
        }
    }
}

/// <summary>What a command provider runs.</summary>
internal sealed record CommandShape(string DisplayName, IReadOnlyList<string> Read)
{
    public IReadOnlyList<string>? Status { get; init; }

    public IReadOnlyList<string>? SignIn { get; init; }

    public string? JsonMember { get; init; }

    // Whether the field of the reference (after #) is a member of the JSON of the secret, for the secret managers that keep JSON.
    public bool JsonFromField { get; init; }

    public string? ProgramPath { get; init; }

    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public CredentialProviderInstall? Install { get; init; }

    public static CommandShape FromSettings(PluginCredentialProviderContext context)
        => new("The command", context.GetList("read"))
        {
            Status = context.GetList("status") is { Count: > 0 } status ? status : null,
            SignIn = context.GetList("sign_in") is { Count: > 0 } signIn ? signIn : null,
            JsonMember = context.GetString("json"),
        };
}
