using System.Text;

namespace CodeAlta.Sandbox;

/// <summary>
/// Writes the command line a sandbox starts a program with: on Windows the line of <c>CreateProcess</c>, which the
/// program splits by the rules of the C runtime; elsewhere a line for <c>/bin/sh -c</c>, which mxc runs it with.
/// </summary>
public static class SandboxCommandLine
{
    /// <summary>Writes a command line for this system.</summary>
    /// <param name="executable">The program.</param>
    /// <param name="arguments">Its arguments, given to it one by one as they are.</param>
    /// <returns>The command line.</returns>
    /// <exception cref="ArgumentException"><paramref name="executable"/> is empty.</exception>
    public static string Create(string executable, IEnumerable<string> arguments)
        => OperatingSystem.IsWindows() ? CreateForWindows(executable, arguments) : CreateForShell(executable, arguments);

    /// <summary>Writes a command line for <c>CreateProcess</c>.</summary>
    public static string CreateForWindows(string executable, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        // The program name ends at the next quote: it cannot hold one, and is always quoted.
        var line = new StringBuilder().Append('"').Append(executable).Append('"');
        foreach (var argument in arguments)
        {
            line.Append(' ');
            AppendWindowsArgument(line, argument);
        }

        return line.ToString();
    }

    /// <summary>Writes a command line for a POSIX shell: each part in single quotes.</summary>
    public static string CreateForShell(string executable, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        return string.Join(' ', new[] { executable }.Concat(arguments).Select(static part => "'" + part.Replace("'", "'\\''", StringComparison.Ordinal) + "'"));
    }

    // The rules of CommandLineToArgvW: backslashes are literal unless they precede a quote, where they double.
    private static void AppendWindowsArgument(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        for (var index = 0; index < argument.Length; index++)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                backslashes++;
                index++;
            }

            if (index == argument.Length)
            {
                line.Append('\\', backslashes * 2);
                break;
            }

            if (argument[index] == '"')
            {
                line.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                line.Append('\\', backslashes).Append(argument[index]);
            }
        }

        line.Append('"');
    }
}
