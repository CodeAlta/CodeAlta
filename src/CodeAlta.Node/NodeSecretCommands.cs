using System.Globalization;
using System.Text;
using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode secrets</c>.</summary>
internal sealed class NodeSecretCommandOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets the words: the subcommand and its arguments.</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// The secrets and variables of the machine of a profile, for its owner on that machine (<c>~/.alta/secrets</c>, the
/// store CodeAlta Desktop shows in Settings &gt; Secrets): <c>altanode secrets list</c>, <c>set</c>, <c>rotate</c> and
/// <c>delete</c>, and <c>set-var</c> and <c>delete-var</c> for variables. The value of a secret is read from the standard
/// input, or typed at a prompt that does not show it: never from an argument, which other processes can read. A node
/// that runs reads the store again at its next use.
/// </summary>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error.</param>
/// <param name="input">Standard input, which gives the value when it is redirected.</param>
internal sealed class NodeSecretCommands(TextWriter output, TextWriter error, TextReader input)
{
    /// <summary>Gets whether the standard input is redirected: the value is read from it, else typed at a prompt.</summary>
    public bool InputRedirected { get; init; } = Console.IsInputRedirected;

    /// <summary>Gets what reads a value typed at the prompt without showing it; null when it reads none.</summary>
    public Func<string?> ReadHidden { get; init; } = ReadHiddenFromConsole;

    /// <summary>Gets what protects the values of a new store, for tests; null for the protected store of the machine.</summary>
    public Func<string, ISecretProtector>? Protector { get; init; }

    /// <summary>Gets who sets a secret, as the store records it.</summary>
    public string SetBy { get; init; } = $"{Environment.UserName} (altanode)";

    public async ValueTask<int> RunAsync(NodeSecretCommandOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var store = new LocalSecretStore(profile.GlobalRoot, Protector?.Invoke(profile.GlobalRoot));
        var (command, arguments) = options.Words is [var first, .. var rest] ? (first, rest) : ("list", []);
        try
        {
            switch (command)
            {
                case "list" when arguments.Count == 0:
                    return await ListAsync(store).ConfigureAwait(false);
                case "set" or "rotate" when arguments.Count == 1:
                    return await SetAsync(store, arguments[0], rotate: command == "rotate").ConfigureAwait(false);
                case "set" or "rotate" when arguments.Count > 1:
                    return await UsageAsync($"secrets {command} takes the name alone: the value is read from the standard input, or typed at the prompt, never given as an argument.").ConfigureAwait(false);
                case "delete" when arguments.Count == 1:
                    return await DeleteAsync(store.DeleteSecret(arguments[0]), $"secret {arguments[0]}").ConfigureAwait(false);
                case "set-var" when arguments.Count == 2:
                    var variable = store.SetVariable(arguments[0], arguments[1], SetBy);
                    await output.WriteLineAsync($"Variable {variable.Name} is set (version {variable.Version}).").ConfigureAwait(false);
                    return NodeExitCodes.Success;
                case "delete-var" when arguments.Count == 1:
                    return await DeleteAsync(store.DeleteVariable(arguments[0]), $"variable {arguments[0]}").ConfigureAwait(false);
                case "key-file" when arguments.Count == 0:
                    return await KeyFileAsync(store).ConfigureAwait(false);
                case "key-file":
                    return await UsageAsync("secrets key-file takes no argument.").ConfigureAwait(false);
                case "list" or "set" or "rotate" or "delete" or "set-var" or "delete-var":
                    return await UsageAsync(command switch
                    {
                        "list" => "secrets list takes no argument.",
                        "set-var" => "secrets set-var takes a name and a value.",
                        _ => $"secrets {command} takes a name.",
                    }).ConfigureAwait(false);
                default:
                    return await UsageAsync($"'{command}' is not a command of secrets: list, set, rotate, delete, set-var, delete-var or key-file.").ConfigureAwait(false);
            }
        }
        catch (ArgumentException exception)
        {
            return await UsageAsync(exception.Message.Split(" (Parameter", 2)[0]).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or SecretStoreUnavailableException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync("altanode: " + exception.Message).ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
    }

    private async Task<int> ListAsync(LocalSecretStore store)
    {
        var secrets = store.ListSecrets();
        var variables = store.ListVariables();
        if (secrets.Count == 0 && variables.Count == 0)
        {
            await output.WriteLineAsync("No secret nor variable is set on this machine. altanode secrets set <name> sets one.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        var width = Math.Min(48, Math.Max(4, secrets.Select(static secret => secret.Name.Length).Concat(variables.Select(static variable => variable.Name.Length)).Max()));
        if (secrets.Count > 0)
        {
            await output.WriteLineAsync($"{"SECRET".PadRight(width)}  {"VERSION",-7}  {"SET",-16}  BY").ConfigureAwait(false);
            foreach (var secret in secrets)
            {
                await output.WriteLineAsync($"{secret.Name.PadRight(width)}  {secret.Version,-7}  {Format(secret.SetAt),-16}  {secret.SetBy}").ConfigureAwait(false);
            }
        }

        if (variables.Count > 0)
        {
            if (secrets.Count > 0) await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync($"{"VARIABLE".PadRight(width)}  {"VERSION",-7}  VALUE").ConfigureAwait(false);
            foreach (var variable in variables)
            {
                await output.WriteLineAsync($"{variable.Name.PadRight(width)}  {variable.Version,-7}  {variable.Value}").ConfigureAwait(false);
            }
        }

        return NodeExitCodes.Success;
    }

    private async Task<int> SetAsync(LocalSecretStore store, string name, bool rotate)
    {
        if (!LocalSecretStore.IsName(name)) return await UsageAsync($"'{name}' is not a valid name: letters, digits and _, not starting with a digit.").ConfigureAwait(false);
        if (rotate && !store.ListSecrets().Any(secret => string.Equals(secret.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            await error.WriteLineAsync($"altanode: no secret is named {name} on this machine: altanode secrets set {name} sets it.").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        string? value;
        if (InputRedirected)
        {
            value = await input.ReadToEndAsync().ConfigureAwait(false);
            // The line end a pipe or a here-string adds is not part of the value.
            if (value.EndsWith('\n')) value = value[..^1];
            if (value.EndsWith('\r')) value = value[..^1];
        }
        else
        {
            await output.WriteAsync($"Value of {name} (not shown): ").ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            value = ReadHidden();
            await output.WriteLineAsync().ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(value)) return await UsageAsync($"No value was given for {name}.").ConfigureAwait(false);
        var secret = rotate ? store.RotateSecret(name, value, SetBy) : store.SetSecret(name, value, SetBy);
        await output.WriteLineAsync($"Secret {secret.Name} is set (version {secret.Version}).").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    // A node that a LaunchDaemon or a system unit starts has no login session, where the Keychain and libsecret answer:
    // the key of its secrets moves into a file only this user reads, while they still answer.
    private async Task<int> KeyFileAsync(LocalSecretStore store)
    {
        var moved = KeyFile is { } keyFile ? keyFile(store) : store.UseKeyFile();
        await output.WriteLineAsync(moved is null
            ? $"The secrets of this machine need no move: {(store.Protection is { } kind ? $"they are protected by {kind}" : "none is set")}."
            : $"The key of the secrets of this machine moved from the {(moved == SecretProtectionKinds.Keychain ? "Keychain" : "keyring (libsecret)")} to a file only {Environment.UserName} reads: a node started at boot reads them.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    /// <summary>Gets what moves the key of a store into a file, for tests; null for <see cref="LocalSecretStore.UseKeyFile()"/>.</summary>
    public Func<LocalSecretStore, string?>? KeyFile { get; init; }

    private async Task<int> DeleteAsync(bool deleted, string what)
    {
        if (!deleted)
        {
            await error.WriteLineAsync($"altanode: no {what} on this machine.").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }

        await output.WriteLineAsync($"The {what} is deleted.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    private static string Format(DateTimeOffset at) => at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    // Reads a line typed on the console without echoing it.
    internal static string? ReadHiddenFromConsole()
    {
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return text.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                return null;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
    }

    private async Task<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
