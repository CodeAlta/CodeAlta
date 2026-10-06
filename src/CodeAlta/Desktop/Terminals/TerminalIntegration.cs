using System.Globalization;
using System.Text;

namespace CodeAlta.Desktop.Terminals;

/// <summary>How a command interpreter is started: its arguments, and the variables added to its environment.</summary>
/// <param name="Arguments">The arguments of the program.</param>
/// <param name="Variables">The variables to set; a null value removes the variable.</param>
internal sealed record TerminalLaunch(IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string?> Variables);

/// <summary>
/// What a shell tells its terminal beside its text: where it is, where its prompt and its commands start and
/// end. A shell says so when it is started with a script that makes it write marks around its prompt and its
/// commands (the <c>OSC 633</c> sequences of Visual Studio Code, and <c>OSC 133</c>); this class writes those
/// scripts and starts each kind of shell with its own.
/// </summary>
internal static class TerminalIntegration
{
    /// <summary>The variable that tells the script of bash to read what a login shell reads.</summary>
    internal const string LoginVariable = "CODEALTA_SHELL_LOGIN";
    /// <summary>The variable that tells the scripts of zsh where the files of the user are.</summary>
    internal const string UserZdotdirVariable = "CODEALTA_USER_ZDOTDIR";

    /// <summary>
    /// How a profile is started so that its shell reports its prompts and its commands; the profile as it is
    /// for a program that has no script, or when the scripts cannot be written.
    /// </summary>
    /// <param name="profile">What the terminal starts.</param>
    /// <param name="folder">Where the scripts are written, or null to start the shells that need a file as they are.</param>
    /// <param name="environment">The environment the program is given, as it is before the variables of the launch.</param>
    internal static TerminalLaunch Launch(TerminalProfile profile, string? folder, IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(environment);
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        var arguments = profile.Arguments;
        switch (profile.Kind)
        {
            case TerminalShellKind.CommandPrompt:
                // The Command Prompt has no script: its prompt carries the marks, and the folder it is in.
                variables["PROMPT"] = "$e]133;A$e\\$e]9;9;$P$e\\" + (environment.GetValueOrDefault("PROMPT") is { Length: > 0 } prompt ? prompt : "$P$G") + "$e]133;B$e\\";
                break;
            case TerminalShellKind.PowerShell or TerminalShellKind.WindowsPowerShell when Write(folder, "integration.ps1", PowerShell) is { } script:
                // The script is one of the system as far as its execution policy goes: a policy that forbids scripts leaves the shell as it is.
                arguments = [.. arguments, "-NoExit", "-Command", "try { . '" + script.Replace("'", "''", StringComparison.Ordinal) + "' } catch {}"];
                break;
            case TerminalShellKind.Bash when Write(folder, "integration.bash", Bash) is { } script:
                // bash reads the file named here instead of its own, and a login shell reads none: the script reads those of the user itself.
                if (arguments.Any(static argument => argument is "--login" or "-l")) variables[LoginVariable] = "1";
                arguments = ["--init-file", script, .. arguments.Where(static argument => argument is not ("--login" or "-l"))];
                break;
            case TerminalShellKind.Zsh when Write(folder, Path.Combine("zsh", ".zshenv"), ZshEnvironment) is { } first
                && Write(folder, Path.Combine("zsh", ".zprofile"), ZshProfile) is not null && Write(folder, Path.Combine("zsh", ".zshrc"), ZshInteractive) is not null
                && Write(folder, Path.Combine("zsh", ".zlogin"), ZshLogin) is not null:
                // zsh reads its files from this folder: each one reads the file of the user it stands for.
                variables[UserZdotdirVariable] = environment.GetValueOrDefault("ZDOTDIR") ?? string.Empty;
                variables["ZDOTDIR"] = Path.GetDirectoryName(first);
                break;
            case TerminalShellKind.Fish when Write(folder, "integration.fish", Fish) is { } script:
                arguments = [.. arguments, "--init-command", "source \"" + script.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""];
                break;
        }
        return new TerminalLaunch(arguments, variables);
    }

    /// <summary>The folder a shell reports as a <c>file://host/path</c> URL, or null when it is not one.</summary>
    internal static string? FolderOfUrl(string url)
    {
        if (!url.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var path = url.IndexOf('/', "file://".Length);
        if (path < 0) return null;
        string folder;
        try { folder = Uri.UnescapeDataString(url[path..]); }
        catch (UriFormatException) { return null; }
        // A drive of Windows is written after the slash that starts the path.
        if (folder.Length >= 3 && folder[0] == '/' && char.IsAsciiLetter(folder[1]) && folder[2] == ':') folder = folder[1..];
        return folder.Length == 0 ? null : folder;
    }

    /// <summary>
    /// The text of a value a shell wrote escaped: a backslash and <c>x</c> with two hexadecimal digits for a
    /// character that would end or split the sequence, and two backslashes for one.
    /// </summary>
    internal static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var text = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\' || index + 1 >= value.Length)
            {
                text.Append(character);
                continue;
            }
            if (value[index + 1] == '\\')
            {
                text.Append('\\');
                index++;
            }
            else if (value[index + 1] == 'x' && index + 3 < value.Length
                && int.TryParse(value.AsSpan(index + 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
            {
                text.Append((char)code);
                index += 3;
            }
            else text.Append(character);
        }
        return text.ToString();
    }

    // Writes a script where the shells read it, unless it is there already; null when it cannot be.
    private static string? Write(string? folder, string name, string content)
    {
        if (folder is null) return null;
        var path = Path.Combine(folder, name);
        // The shells of Unix read their scripts with the line ends of Unix, whatever this file was checked out with.
        var text = content.ReplaceLineEndings("\n");
        try
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal)) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Written beside and moved in place: a shell that starts now reads the whole script, the old one or the new one.
            var written = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(written, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(written, path, overwrite: true);
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // PowerShell 5.1 and later: the prompt of the user is wrapped, and so is the function that reads a line.
    private const string PowerShell = """
        # CodeAlta shell integration for PowerShell: tells the terminal where the prompt is, what runs and how it ended.
        if ($Global:__CodeAlta -or $ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') { return }
        $Global:__CodeAlta = @{ Prompt = $function:prompt; Wrapper = $null; Prompted = $false; ReadLine = $null; Ran = $false; History = -1; Error = $null }

        function Global:__CodeAltaEscape([string]$Text) {
            [regex]::Replace($Text, '[\x00-\x1f\\;]', { param($Match) '\x{0:x2}' -f [int][char]$Match.Value })
        }

        function Global:prompt {
            $Ok = $?
            $Code = $Global:LASTEXITCODE
            $State = $Global:__CodeAlta
            $State.Prompted = $true
            $Esc = [char]27
            $Bel = [char]7
            $Last = Get-History -Count 1
            $Id = 0
            if ($Last) { $Id = $Last.Id }
            $Marks = ''
            if ($State.History -ne -1) {
                if ($State.Ran -or ($null -eq $State.ReadLine -and $Id -ne $State.History)) {
                    $Exit = 0
                    if (-not $Ok) {
                        # The exit code of a program, unless what failed is a command of PowerShell: that one left an error, and no code.
                        $Exit = 1
                        $Recent = $null
                        if ($Error.Count -gt 0) { $Recent = $Error[0] }
                        if ([object]::ReferenceEquals($Recent, $State.Error) -and $Code -is [int] -and $Code -ne 0) { $Exit = $Code }
                    }
                    $Marks = "$Esc]633;D;$Exit$Bel"
                } else {
                    $Marks = "$Esc]633;D$Bel"
                }
            }
            $State.Ran = $false
            $State.History = $Id
            $Marks += "$Esc]633;A$Bel"
            if ($PWD.Provider.Name -eq 'FileSystem') { $Marks += "$Esc]633;P;Cwd=$(__CodeAltaEscape $PWD.ProviderPath)$Bel" }
            # The prompt of the user sees how the command ended, not how this function did.
            $Global:LASTEXITCODE = $Code
            if (-not $Ok) { Write-Error 'failure' -ErrorAction Ignore }
            $Text = -join (& $State.Prompt)
            $Global:LASTEXITCODE = $Code
            "$Marks$Text$Esc]633;B$Bel"
        }
        $Global:__CodeAlta.Wrapper = $function:prompt

        if (Get-Command PSConsoleHostReadLine -CommandType Function -ErrorAction Ignore) {
            $Global:__CodeAlta.ReadLine = $function:PSConsoleHostReadLine
            function Global:PSConsoleHostReadLine {
                $State = $Global:__CodeAlta
                if (-not $State.Prompted) {
                    # Another prompt took the place of this one (a theme that was loaded since): it is wrapped
                    # from now on, and what it could not say of the prompt it just showed is said here.
                    $State.Prompt = $function:prompt
                    Set-Item -Path function:Global:prompt -Value $State.Wrapper
                    $State.Ran = $false
                    [Console]::Write("$([char]27)]633;D$([char]7)$([char]27)]633;A$([char]7)")
                }
                $State.Prompted = $false
                $Line = & $State.ReadLine
                if ("$Line".Trim()) {
                    $State.Ran = $true
                    $State.Error = $null
                    if ($Error.Count -gt 0) { $State.Error = $Error[0] }
                    [Console]::Write("$([char]27)]633;E;$(__CodeAltaEscape $Line)$([char]7)$([char]27)]633;C$([char]7)")
                }
                $Line
            }
        }
        """;

    // bash 3.2 and later, Git Bash included: a DEBUG trap sees a command start, PROMPT_COMMAND sees it end.
    private const string Bash = """
        # CodeAlta shell integration for bash: tells the terminal where the prompt is, what runs and how it ended.
        if [ -n "${CODEALTA_SHELL_LOGIN:-}" ]; then
          unset CODEALTA_SHELL_LOGIN
          [ -r /etc/profile ] && . /etc/profile
          for __alta_file in ~/.bash_profile ~/.bash_login ~/.profile; do
            if [ -r "$__alta_file" ]; then . "$__alta_file"; break; fi
          done
          unset __alta_file
        elif [ -r ~/.bashrc ]; then
          . ~/.bashrc
        fi
        if [[ $- != *i* || -n ${__alta_ready:-} ]]; then return; fi
        __alta_ready=1
        __alta_running=
        # Until the first prompt: what this script and the files of the user run is not a command of the user.
        __alta_prompting=1

        __alta_escape() {
          local text=$1 out= char i
          for ((i = 0; i < ${#text}; i++)); do
            char=${text:i:1}
            case $char in
              \\) out+='\\' ;;
              \;) out+='\x3b' ;;
              [[:cntrl:]]) printf -v char '\\x%02x' "'$char"; out+=$char ;;
              *) out+=$char ;;
            esac
          done
          printf '%s' "$out"
        }

        __alta_cwd() {
          if [ -n "${MSYSTEM:-}" ] && command -v cygpath > /dev/null 2>&1; then cygpath -w "$PWD"; else printf '%s' "$PWD"; fi
        }

        __alta_started() {
          __alta_running=1
          printf '\e]633;E;%s\a\e]633;C\a' "$(__alta_escape "$1")"
        }

        __alta_preexec() {
          if [[ -n $__alta_prompting || -n $__alta_running || -n ${COMP_LINE:-} || $BASH_COMMAND == __alta_* ]]; then return; fi
          local line=
          if [[ ! ${HISTCONTROL:-} =~ (ignore|erasedups) ]]; then
            line=$(HISTTIMEFORMAT= builtin history 1)
            if [[ $line =~ ^[[:space:]]*[0-9]+\*?[[:space:]]+(.*)$ ]]; then line=${BASH_REMATCH[1]}; else line=; fi
          fi
          [ -n "$line" ] || line=$BASH_COMMAND
          __alta_started "$line"
        }

        __alta_precmd() {
          local code=$?
          __alta_prompting=1
          if [ -n "$__alta_running" ]; then printf '\e]633;D;%s\a' "$code"; else printf '\e]633;D\a'; fi
          __alta_running=
          printf '\e]633;A\a\e]633;P;Cwd=%s\a' "$(__alta_escape "$(__alta_cwd)")"
          return "$code"
        }

        # Runs last, once what the user runs before each prompt has set the prompt.
        __alta_prompt() {
          local code=$?
          if [[ $PS1 != *'\[\e]633;B\a\]' ]]; then PS1=$PS1'\[\e]633;B\a\]'; fi
          __alta_prompting=
          return "$code"
        }

        if [[ -n ${bash_preexec_imported:-}${__bp_imported:-} ]]; then
          __alta_bp_preexec() { __alta_started "$1"; }
          preexec_functions+=(__alta_bp_preexec)
          precmd_functions=(__alta_precmd "${precmd_functions[@]}" __alta_prompt)
        else
          __alta_previous=
          __alta_take() { __alta_previous=$2; }
          __alta_trap=$(trap -p DEBUG)
          if [ -n "$__alta_trap" ]; then eval "__alta_take ${__alta_trap#trap }"; fi
          unset __alta_trap
          unset -f __alta_take
          __alta_debug() {
            __alta_preexec
            if [ -n "$__alta_previous" ]; then eval "$__alta_previous"; fi
          }
          if [[ $(declare -p PROMPT_COMMAND 2> /dev/null) == 'declare -a'* ]]; then
            PROMPT_COMMAND=(__alta_precmd "${PROMPT_COMMAND[@]}" __alta_prompt)
          else
            PROMPT_COMMAND=$'__alta_precmd\n'${PROMPT_COMMAND:-:}$'\n__alta_prompt'
          fi
          trap '__alta_debug' DEBUG
        fi
        """;

    // zsh reads four files from the folder ZDOTDIR names. Each of these reads the one of the user, from where
    // the files of the user are, and gives the folder back to zsh for the next one.
    private const string ZshEnvironment = """
        # CodeAlta shell integration for zsh: the files of the user are read from where they are.
        __alta_zdotdir=$ZDOTDIR
        if [[ -n $CODEALTA_USER_ZDOTDIR ]]; then ZDOTDIR=$CODEALTA_USER_ZDOTDIR; else unset ZDOTDIR; fi
        if [[ -f ${ZDOTDIR:-$HOME}/.zshenv ]]; then . ${ZDOTDIR:-$HOME}/.zshenv; fi
        # The file of the user can say where its other files are.
        __alta_user_zdotdir=${ZDOTDIR-}
        ZDOTDIR=$__alta_zdotdir
        """;

    private const string ZshProfile = """
        if [[ -n $__alta_user_zdotdir ]]; then ZDOTDIR=$__alta_user_zdotdir; else unset ZDOTDIR; fi
        if [[ -f ${ZDOTDIR:-$HOME}/.zprofile ]]; then . ${ZDOTDIR:-$HOME}/.zprofile; fi
        ZDOTDIR=$__alta_zdotdir
        """;

    private const string ZshInteractive = """
        if [[ -n $__alta_user_zdotdir ]]; then ZDOTDIR=$__alta_user_zdotdir; else unset ZDOTDIR; fi
        if [[ -f ${ZDOTDIR:-$HOME}/.zshrc ]]; then . ${ZDOTDIR:-$HOME}/.zshrc; fi

        # CodeAlta shell integration for zsh: tells the terminal where the prompt is, what runs and how it ended.
        if [[ -o interactive && -z ${__alta_ready-} ]]; then
          __alta_ready=1
          __alta_running=

          __alta_escape() {
            emulate -L zsh
            local text=$1 out= char i
            for (( i = 1; i <= ${#text}; i++ )); do
              char=$text[i]
              case $char in
                '\') out+='\\' ;;
                ';') out+='\x3b' ;;
                [[:cntrl:]]) out+=$(printf '\\x%02x' "'$char") ;;
                *) out+=$char ;;
              esac
            done
            print -rn -- $out
          }

          __alta_preexec() {
            __alta_running=1
            printf '\e]633;E;%s\a\e]633;C\a' "$(__alta_escape "$1")"
          }

          __alta_precmd() {
            local code=$?
            if [[ -n $__alta_running ]]; then printf '\e]633;D;%s\a' $code; else printf '\e]633;D\a'; fi
            __alta_running=
            printf '\e]633;A\a\e]633;P;Cwd=%s\a' "$(__alta_escape "$PWD")"
            if [[ $PS1 != *$'\e]633;B\a'* ]]; then PS1="$PS1%{"$'\e]633;B\a'"%}"; fi
            return $code
          }

          # First, to see how the command ended before another function runs one.
          precmd_functions=(__alta_precmd $precmd_functions)
          preexec_functions+=(__alta_preexec)
        fi

        # A login shell reads one more file from this folder.
        if [[ -o login ]]; then
          ZDOTDIR=$__alta_zdotdir
        else
          if [[ -n $__alta_user_zdotdir ]]; then ZDOTDIR=$__alta_user_zdotdir; else unset ZDOTDIR; fi
          unset __alta_zdotdir __alta_user_zdotdir CODEALTA_USER_ZDOTDIR
        fi
        """;

    private const string ZshLogin = """
        if [[ -n $__alta_user_zdotdir ]]; then ZDOTDIR=$__alta_user_zdotdir; else unset ZDOTDIR; fi
        if [[ -f ${ZDOTDIR:-$HOME}/.zlogin ]]; then . ${ZDOTDIR:-$HOME}/.zlogin; fi
        unset __alta_zdotdir __alta_user_zdotdir CODEALTA_USER_ZDOTDIR
        """;

    private const string Fish = """
        # CodeAlta shell integration for fish: tells the terminal where the prompt is, what runs and how it ended.
        if status is-interactive; and not set -q __alta_ready
            set -g __alta_ready 1

            function __alta_escape
                string join \n -- $argv | string replace -a '\\' '\\\\' | string replace -a ';' '\\x3b' | string replace -a \t '\\x09' | string join '\\x0a'
            end

            function __alta_preexec --on-event fish_preexec
                printf '\e]633;E;%s\a\e]633;C\a' (__alta_escape $argv)
            end

            function __alta_postexec --on-event fish_postexec
                printf '\e]633;D;%s\a' $status
            end

            function __alta_prompt --on-event fish_prompt
                printf '\e]633;A\a\e]633;P;Cwd=%s\a' (__alta_escape $PWD)
            end
        end
        """;
}
