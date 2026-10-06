using System.Collections;
using System.Globalization;

namespace CodeAlta.Desktop.Terminals;

/// <summary>The environment of the program of a terminal.</summary>
internal static class TerminalEnvironment
{
    /// <summary>The value of <c>TERM_PROGRAM</c>: how a program knows which terminal it runs in.</summary>
    internal const string Program = "CodeAlta";
    /// <summary>The variable that gives a program the identifier of its terminal.</summary>
    internal const string Identifier = "CODEALTA_TERMINAL";

    // What this process was given for itself, or what describes another terminal it was started from.
    private static readonly string[] Removed = ["CODEALTA_START_TOKEN", "TMUX", "TMUX_PANE", "STY", "WINDOW", "WINDOWID", "TERMCAP", "COLUMNS", "LINES"];
    // What Windows gives a process at sign-in, from the account rather than from the registry.
    private static readonly string[] SignIn = ["USERNAME", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "HOMESHARE",
        "APPDATA", "LOCALAPPDATA", "LOGONSERVER", "COMPUTERNAME", "SESSIONNAME", "CLIENTNAME"];

    /// <summary>The environment of this process, as a terminal gives it to its program.</summary>
    /// <param name="terminal">The identifier of the terminal.</param>
    /// <param name="version">The version of the application.</param>
    internal static Dictionary<string, string> Create(string terminal, string version)
    {
        var windows = OperatingSystem.IsWindows();
        var variables = Read(EnvironmentVariableTarget.Process, windows);
        if (windows)
        {
            // What is in the registry now: the application can run for days, and what was installed since it
            // started is on the path of a new terminal, as in any terminal opened from the desktop.
            Refresh(variables, Read(EnvironmentVariableTarget.Machine, true), Read(EnvironmentVariableTarget.User, true));
        }
        return Prepare(variables, terminal, version, windows, CultureInfo.CurrentUICulture.Name);
    }

    /// <summary>
    /// Lays the variables of the machine and of the user over those a process started with, but for the ones
    /// a sign-in gives: who the user is and where their folders are (the registry of a machine names its own
    /// account there, <c>USERNAME=SYSTEM</c>).
    /// </summary>
    internal static void Refresh(Dictionary<string, string> variables, IReadOnlyDictionary<string, string> machine, IReadOnlyDictionary<string, string> user)
    {
        foreach (var (name, value) in machine)
        {
            if (!SignIn.Contains(name, StringComparer.OrdinalIgnoreCase)) variables[name] = value;
        }
        foreach (var (name, value) in user)
        {
            if (!SignIn.Contains(name, StringComparer.OrdinalIgnoreCase)) variables[name] = value;
        }
        // The path of a user comes after the one of the machine, not instead of it.
        if (machine.TryGetValue("Path", out var first) && user.TryGetValue("Path", out var second))
        {
            variables["Path"] = first.TrimEnd(';') + ";" + second;
        }
    }

    /// <summary>Removes what is not for the program of a terminal and adds what tells it where it runs.</summary>
    /// <param name="variables">The variables to change.</param>
    /// <param name="terminal">The identifier of the terminal.</param>
    /// <param name="version">The version of the application.</param>
    /// <param name="windows">Whether the system is Windows.</param>
    /// <param name="culture">The language of the user, as .NET names it (<c>fr-FR</c>).</param>
    internal static Dictionary<string, string> Prepare(Dictionary<string, string> variables, string terminal, string version, bool windows, string culture)
    {
        foreach (var name in Removed) variables.Remove(name);
        foreach (var name in variables.Keys.Where(static name => name.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)).ToArray()) variables.Remove(name);
        variables["TERM_PROGRAM"] = Program;
        variables["TERM_PROGRAM_VERSION"] = version;
        variables["COLORTERM"] = "truecolor";
        variables[Identifier] = terminal;
        if (windows) return variables;
        variables["TERM"] = "xterm-256color";
        // Programs write what the terminal reads, UTF-8: an application started from the desktop may have no locale at all.
        if (!Utf8(variables.GetValueOrDefault("LC_ALL")) && !Utf8(variables.GetValueOrDefault("LC_CTYPE")) && !Utf8(variables.GetValueOrDefault("LANG")))
        {
            variables["LANG"] = Locale(culture);
            variables.Remove("LC_ALL");
        }
        return variables;
    }

    /// <summary>The locale of a language, in UTF-8: <c>fr_FR.UTF-8</c> for <c>fr-FR</c>, <c>en_US.UTF-8</c> without one.</summary>
    internal static string Locale(string culture)
    {
        var parts = culture.Split('-', '_');
        if (parts.Length == 0 || parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter)) return "en_US.UTF-8";
        var language = parts[0].ToLowerInvariant();
        var region = parts.Length > 1 && parts[^1].Length == 2 && parts[^1].All(char.IsAsciiLetter) ? parts[^1].ToUpperInvariant() : language switch
        {
            "en" => "US", "zh" => "CN", "ja" => "JP", "ko" => "KR", "pt" => "BR", "sv" => "SE", "da" => "DK", "cs" => "CZ", "el" => "GR", "uk" => "UA",
            _ => language.ToUpperInvariant(),
        };
        return language + "_" + region + ".UTF-8";
    }

    private static bool Utf8(string? locale)
        => locale is not null && (locale.EndsWith(".UTF-8", StringComparison.OrdinalIgnoreCase) || locale.EndsWith(".utf8", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string> Read(EnvironmentVariableTarget target, bool windows)
    {
        var variables = new Dictionary<string, string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        try
        {
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(target))
            {
                if (entry.Key is string { Length: > 0 } name && entry.Value is string value) variables[name] = value;
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The registry cannot be read: the terminal has what the application started with.
        }
        return variables;
    }
}
