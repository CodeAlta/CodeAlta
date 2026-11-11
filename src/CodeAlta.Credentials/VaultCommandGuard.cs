using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;

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
    private static readonly FrozenDictionary<string, ImmutableArray<string>> Programs = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal)
    {
        ["1password"] = ["op"],
        ["bitwarden"] = ["bw"],
        ["bitwarden-secrets"] = ["bws"],
        ["keepassxc"] = ["keepassxc-cli"],
        ["pass"] = ["pass", "gopass"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    // Programs that run the command that follows their options: the program of a vault after them runs.
    private static readonly FrozenSet<string> Wrappers = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "sudo", "doas", "env", "nohup", "time", "exec", "command", "nice", "ionice", "stdbuf", "timeout", "xargs", "watch", "caffeinate", "call", "start",
        "start-process", "saps", "runas", "wsl", "busybox");

    // The options of the wrappers that take a value, which is not the program.
    private static readonly FrozenSet<string> ValueOptions = FrozenSet.Create(StringComparer.Ordinal,
        "-u", "-g", "-C", "-D", "-p", "-r", "-U", "-h", "-n", "-I", "-L", "-P", "-d", "-a",
        "--user", "--group", "--chdir", "--signal", "--kill-after", "-ArgumentList", "-Args", "-WorkingDirectory", "-Verb", "-WindowStyle", "--distribution", "--cd");

    // Shells that run the text after their command option.
    private static readonly FrozenSet<string> Shells = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "bash", "sh", "zsh", "dash", "ksh", "fish", "pwsh", "powershell", "cmd");

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

    /// <summary>
    /// Finds the first program of a list a command line runs: the program of each command of its pipelines and lists (the first word,
    /// after the assignments of variables and the programs that run another one, such as <c>sudo</c>, <c>env</c>, the call operator
    /// <c>&amp;</c>, <c>cmd /c</c> or <c>pwsh -c</c>), and of the commands substituted in it (<c>$(…)</c>, backquotes). A program
    /// named as an argument (<c>echo op</c>, <c>Get-Command bw</c>) does not run.
    /// </summary>
    /// <param name="command">The command line.</param>
    /// <param name="programs">The names of the programs, without extension.</param>
    /// <returns>The program found, or null.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string? FindProgram(string command, IReadOnlyCollection<string> programs)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(programs);
        return Find(command, programs, depth: 0);
    }

    private static string? Find(string text, IReadOnlyCollection<string> programs, int depth)
    {
        if (depth > 6) return null;
        foreach (var words in Commands(text))
        {
            // A substitution inside a word (a quoted "$(op read x)") runs too.
            foreach (var word in words)
            {
                foreach (var inner in Substitutions(word))
                {
                    if (Find(inner, programs, depth + 1) is { } substituted) return substituted;
                }
            }

            if (ProgramOf(words, programs, depth) is { } program) return program;
        }

        return null;
    }

    // The program a command runs, when it is one of the list.
    private static string? ProgramOf(List<string> words, IReadOnlyCollection<string> programs, int depth)
    {
        var index = 0;
        while (index < words.Count)
        {
            var word = words[index];
            if (IsAssignment(word))
            {
                index++;
                continue;
            }

            var name = NameOf(word);
            if (programs.Contains(name, StringComparer.OrdinalIgnoreCase)) return name;
            if (Shells.Contains(name)) return ShellCommand(name, words, index + 1) is { } line ? Find(line, programs, depth + 1) : null;
            if (name.Equals("invoke-expression", StringComparison.OrdinalIgnoreCase) || name.Equals("iex", StringComparison.OrdinalIgnoreCase))
                return Find(string.Join(' ', words.Skip(index + 1)), programs, depth + 1);
            if (!Wrappers.Contains(name) && name != ".") return null;

            // The options of the wrapper, then the program it runs.
            index++;
            var slashOptions = name.Equals("runas", StringComparison.OrdinalIgnoreCase) || name.Equals("start", StringComparison.OrdinalIgnoreCase);
            while (index < words.Count && (words[index].StartsWith('-') || slashOptions && words[index].StartsWith('/')))
            {
                // command -v op says where op is, and runs nothing.
                if (name.Equals("command", StringComparison.OrdinalIgnoreCase) && words[index] is "-v" or "-V") return null;
                index += ValueOptions.Contains(words[index]) ? 2 : 1;
            }

            // timeout 10 op: the duration comes first.
            if (name.Equals("timeout", StringComparison.OrdinalIgnoreCase) && index < words.Count && words[index].Length > 0 && char.IsAsciiDigit(words[index][0])) index++;
        }

        return null;
    }

    // The command line a shell runs: the text after -c (bash -lc too), /c, -Command, or the decoded -EncodedCommand of PowerShell.
    private static string? ShellCommand(string shell, List<string> words, int start)
    {
        var powershell = shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || shell.Equals("powershell", StringComparison.OrdinalIgnoreCase);
        var cmd = shell.Equals("cmd", StringComparison.OrdinalIgnoreCase);
        for (var index = start; index < words.Count; index++)
        {
            var option = words[index];
            var rest = string.Join(' ', words.Skip(index + 1));
            if (cmd && option.Length == 2 && option[0] == '/' && char.ToLowerInvariant(option[1]) is 'c' or 'k' or 'r') return rest;
            if (powershell && option.Length >= 2 && option[0] == '-')
            {
                var lower = option.ToLowerInvariant();
                if ("-command".StartsWith(lower, StringComparison.Ordinal) && lower.StartsWith("-c", StringComparison.Ordinal)) return rest;
                if (("-encodedcommand".StartsWith(lower, StringComparison.Ordinal) && lower.StartsWith("-e", StringComparison.Ordinal) || lower == "-ec") && index + 1 < words.Count)
                {
                    try
                    {
                        return Encoding.Unicode.GetString(Convert.FromBase64String(words[index + 1]));
                    }
                    catch (FormatException)
                    {
                        return null;
                    }
                }
            }

            if (!cmd && !powershell && option.Length >= 2 && option[0] == '-' && option[1] != '-' && option.Contains('c', StringComparison.Ordinal)) return rest;
        }

        return null;
    }

    // The commands of a command line, each as its words: separated by ; & | new lines, parentheses, braces, backquotes and $( outside
    // quotes. Quotes are removed from the words.
    private static List<List<string>> Commands(string text)
    {
        var commands = new List<List<string>>();
        var words = new List<string>();
        var current = new StringBuilder();
        var inWord = false;
        char? quote = null;

        void EndWord()
        {
            if (inWord) words.Add(current.ToString());
            current.Clear();
            inWord = false;
        }

        void EndCommand()
        {
            EndWord();
            if (words.Count > 0) commands.Add(words);
            words = [];
        }

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote is { } open)
            {
                if (character == open) quote = null;
                else current.Append(character);
                continue;
            }

            switch (character)
            {
                case '"' or '\'':
                    quote = character;
                    inWord = true;
                    break;
                case '$' when index + 1 < text.Length && text[index + 1] == '(':
                    EndCommand();
                    index++;
                    break;
                case ';' or '&' or '|' or '\n' or '\r' or '(' or ')' or '{' or '}' or '`':
                    EndCommand();
                    break;
                default:
                    if (char.IsWhiteSpace(character))
                    {
                        EndWord();
                    }
                    else
                    {
                        current.Append(character);
                        inWord = true;
                    }

                    break;
            }
        }

        EndCommand();
        return commands;
    }

    // The command lines substituted in a word that was quoted: "$(…)" and `…`.
    private static IEnumerable<string> Substitutions(string word)
    {
        for (var start = word.IndexOf("$(", StringComparison.Ordinal); start >= 0; start = word.IndexOf("$(", start + 2, StringComparison.Ordinal))
        {
            var end = word.IndexOf(')', start + 2);
            yield return end < 0 ? word[(start + 2)..] : word[(start + 2)..end];
        }

        var parts = word.Split('`');
        for (var index = 1; index < parts.Length; index += 2) yield return parts[index];
    }

    private static bool IsAssignment(string word)
    {
        var equals = word.IndexOf('=');
        return equals > 0 && (char.IsAsciiLetter(word[0]) || word[0] == '_') && word[..equals].All(static ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');
    }

    // The name of a program from a word: its file name without the extension of a program.
    private static string NameOf(string word)
    {
        var name = word.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot > 0 && name[(dot + 1)..].ToLowerInvariant() is "exe" or "cmd" or "bat" or "ps1" or "com") name = name[..dot];
        return name;
    }
}
