using CodeAlta.Catalog.Secrets;

namespace CodeAlta.Credentials;

/// <summary>
/// Checks a shell command of an agent before it runs: once a provider of a vault is configured, a command that runs the program
/// of that vault (<c>op</c>, <c>bw</c>, <c>keepassxc-cli</c>, <c>pass</c>...) is refused with a note that tells the agent to name a
/// secret instead. It is a guardrail, not a wall: aliases, scripts and commands built at run time are not seen. The user turns it
/// off with <c>agent_vault_commands = "allow"</c> under <c>[secrets]</c> of the configuration.
/// </summary>
public sealed class VaultCommandGuard
{
    // The programs of each type of provider, by the type that configures them.
    private static readonly IReadOnlyDictionary<string, string[]> Programs = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["1password"] = ["op"],
        ["bitwarden"] = ["bw"],
        ["bitwarden-secrets"] = ["bws"],
        ["keepassxc"] = ["keepassxc-cli"],
        ["pass"] = ["pass", "gopass"],
    };

    private readonly CredentialBroker _broker;

    /// <summary>Initializes a new instance of the <see cref="VaultCommandGuard"/> class.</summary>
    /// <param name="broker">The broker whose providers say which vaults are configured.</param>
    /// <exception cref="ArgumentNullException"><paramref name="broker"/> is null.</exception>
    public VaultCommandGuard(CredentialBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        _broker = broker;
    }

    /// <summary>Checks a command line.</summary>
    /// <param name="command">The command line, as the agent wrote it.</param>
    /// <returns>Why it is refused, which the agent reads; null to let it run.</returns>
    public string? Check(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string[] guarded;
        try
        {
            if (_broker.Settings.AgentVaultCommandsAllowed()) return null;
            guarded = [.. _broker.ListProviders().SelectMany(provider => Programs.TryGetValue(provider.Type, out var names) ? names : [])];
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }

        if (guarded.Length == 0 || FindProgram(command, guarded) is not { } program) return null;
        return $"This command runs {program}, the program of a vault the secrets of this machine are kept in, which the user keeps out of the reach of agents. "
            + "Name the secret instead: write ${secret:NAME} where its value is needed (`alta secret list` gives the names; `alta secret test NAME` says whether it can be given), "
            + "or ask the user to run the command.";
    }

    /// <summary>Finds the first program of a list a command line runs, in any of its commands.</summary>
    /// <param name="command">The command line.</param>
    /// <param name="programs">The names of the programs, without extension.</param>
    /// <returns>The program found, or null.</returns>
    public static string? FindProgram(string command, IReadOnlyCollection<string> programs)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(programs);
        foreach (var segment in command.Split([';', '&', '|', '\n', '\r', '(', ')', '{', '}', '`', '$'], StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var word in Words(segment))
            {
                var name = word.Replace('\\', '/');
                var slash = name.LastIndexOf('/');
                if (slash >= 0) name = name[(slash + 1)..];
                var dot = name.LastIndexOf('.');
                if (dot > 0 && name[(dot + 1)..].ToLowerInvariant() is "exe" or "cmd" or "bat" or "ps1") name = name[..dot];
                if (programs.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
            }
        }

        return null;
    }

    private static IEnumerable<string> Words(string segment)
    {
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var character in segment)
        {
            if (quote is { } open)
            {
                if (character == open) quote = null;
                else current.Append(character);
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0) yield return current.ToString();
    }
}
