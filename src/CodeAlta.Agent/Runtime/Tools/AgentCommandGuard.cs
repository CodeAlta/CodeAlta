namespace CodeAlta.Agent.Runtime.Tools;

/// <summary>
/// Checks a shell command of an agent before it runs, whatever the permission mode of the session: for example a
/// <c>git commit</c> while the user reviews the changes before they are committed. It is read at each command, so
/// what it checks may change during a run.
/// </summary>
/// <param name="command">The command line, as the agent wrote it.</param>
/// <param name="workingDirectory">The folder it runs in.</param>
/// <param name="cancellationToken">Stops the check.</param>
/// <returns>Why the command is refused, which the agent reads; null to let it run.</returns>
public delegate ValueTask<string?> AgentCommandGuard(string command, string workingDirectory, CancellationToken cancellationToken);

/// <summary>Recognizes git commands in a shell command line.</summary>
public static class AgentGitCommands
{
    // The options of git that take the next word as their value.
    private static readonly string[] OptionsWithValue = ["-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--config-env"];

    /// <summary>
    /// Answers whether a command line runs <c>git commit</c>: in any of its commands joined by <c>;</c>, <c>&amp;&amp;</c>,
    /// <c>||</c>, <c>|</c> or lines, with git options before the subcommand (<c>git -C dir commit</c>). Aliases,
    /// scripts and commands built at run time are not seen: this is a guardrail, not a sandbox.
    /// </summary>
    /// <param name="command">The command line.</param>
    /// <returns>True when one of its commands is a <c>git commit</c>.</returns>
    public static bool IsCommit(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        foreach (var segment in command.Split([';', '&', '|', '\n', '\r', '(', ')', '{', '}', '`'], StringSplitOptions.RemoveEmptyEntries))
        {
            var words = Words(segment);
            for (var index = 0; index < words.Count; index++)
            {
                if (!IsGit(words[index]))
                {
                    continue;
                }

                for (var next = index + 1; next < words.Count; next++)
                {
                    var word = words[next];
                    if (!word.StartsWith('-'))
                    {
                        if (word == "commit")
                        {
                            return true;
                        }

                        break;
                    }

                    if (OptionsWithValue.Contains(word, StringComparer.Ordinal))
                    {
                        next++;
                    }
                }
            }
        }

        return false;
    }

    private static bool IsGit(string word)
    {
        var name = word.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name is "git" or "git.exe" or "git.cmd";
    }

    // The words of a command, with the quotes of the shell removed.
    private static List<string> Words(string segment)
    {
        var words = new List<string>();
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
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
