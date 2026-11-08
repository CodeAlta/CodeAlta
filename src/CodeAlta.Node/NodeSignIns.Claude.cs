using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Core.Terminals;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Node;

// Claude Code (doc/fleet/node.md, credentials on a node): its own login is in the Keychain on macOS, which a node started at boot
// cannot read, so the node signs it in with a token of the account. claude setup-token runs here in a pseudo-terminal it needs
// (script); the desktop opens the page it gives, the person pastes back the code that page shows, and the token it prints is kept
// as a secret of the machine, which the provider's CLAUDE_CODE_OAUTH_TOKEN names (${secret:NAME}): resolved when the CLI starts,
// and masked. The token never leaves the machine.
internal sealed partial class NodeSignIns
{
    /// <summary>The variable of the environment of a Claude Code provider that holds a token of the account.</summary>
    internal const string ClaudeTokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    private const string ClaudeType = "claude-code";

    /// <summary>The name of the secret that keeps the token of a Claude Code provider.</summary>
    /// <param name="key">The key of the provider.</param>
    /// <returns>The name, such as <c>CLAUDE_CODE_OAUTH_TOKEN_CLAUDE_1A2B3C4D</c>: the key as letters, then 128 bits of a hash of the key, so that two keys never share one.</returns>
    internal static string ClaudeSecretName(string key) => ProviderSecretName(ClaudeTokenVariable, key);

    /// <summary>The name of the secret that keeps the value of a variable of the environment of a provider, given from the desktop.</summary>
    /// <param name="variable">The variable, a name of at most 64 characters.</param>
    /// <param name="key">The key of the provider.</param>
    /// <returns>The variable, the key as letters, then 128 bits of a hash of the key: at most 128 characters, and two keys never share one.</returns>
    internal static string ProviderSecretName(string variable, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!IsVariableName(variable)) throw new ArgumentException("A variable is a name of at most 64 letters, digits and _.", nameof(variable));
        var name = new StringBuilder(variable).Append('_');
        var letters = Math.Min(64, 94 - variable.Length);
        foreach (var character in key.Length > letters ? key[..letters] : key) name.Append(char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        // Provider keys are one key whatever their case.
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        return name.Append('_').Append(Convert.ToHexString(hash, 0, 16)).ToString();
    }

    /// <summary>Whether a text is the name of a variable a provider can be given from the desktop: a name of a secret of at most 64 characters.</summary>
    /// <param name="name">The text.</param>
    /// <returns>Whether it is.</returns>
    internal static bool IsVariableName([NotNullWhen(true)] string? name) => name is { Length: <= 64 } && LocalSecretStore.IsName(name);

    /// <summary>
    /// Reads what <c>claude setup-token</c> printed in its terminal: the page to open, once it asks for the code; null before. The page
    /// is the link of the terminal it prints, or else, as a console of Windows may render it, its address in the text, on a site of
    /// Claude only.
    /// </summary>
    /// <param name="output">What it printed, escapes of the terminal included.</param>
    /// <returns>The page.</returns>
    internal static string? ReadClaudePage(string output)
    {
        var plain = Plain(output);
        if (!AskCodePattern().IsMatch(plain)) return null;
        if (ClaudeLinkPattern().Match(output) is { Success: true } link) return link.Groups["page"].Value;
        return ClaudePagePattern().Match(plain) is { Success: true } page ? page.Value : null;
    }

    // The address of the page of the sign-in in the text, ended by what follows it.
    [GeneratedRegex(@"https://(claude\.ai|claude\.com|console\.anthropic\.com)/[^\s""'<>]+(?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudePagePattern();

    /// <summary>Reads the token <c>claude setup-token</c> printed once the code was given; null before.</summary>
    /// <param name="output">What it printed.</param>
    /// <returns>The token.</returns>
    internal static string? ReadClaudeToken(string output) => ClaudeTokenPattern().Match(Plain(output)) is { Success: true } token ? token.Value : null;

    // A link of the terminal (OSC 8): ESC ] 8 ; params ; URI, ended by BEL or ESC \.
    [GeneratedRegex(@"\u001b\]8;[^;\u0007\u001b]*;(?<page>https://[^\u0007\u001b\s]+)(?=\u0007|\u001b\\)", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeLinkPattern();

    // The whole token: what follows it ends it (a line end, a space), so that a read that stops within it gives none yet.
    [GeneratedRegex(@"sk-ant-oat\d{2}-[A-Za-z0-9_\-]{20,}(?=\s)", RegexOptions.CultureInvariant)]
    private static partial Regex ClaudeTokenPattern();

    // The escapes of a terminal: CSI sequences, OSC sequences, and the other ESC pairs.
    [GeneratedRegex(@"\u001b\[[0-9;?<>=]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)|\u001b[@-Z\\-_]", RegexOptions.CultureInvariant)]
    private static partial Regex TerminalEscapePattern();

    // A terminal app moves the cursor between its words (ESC[nG, ESC[nC) instead of printing spaces: those moves read as one.
    [GeneratedRegex(@"\u001b\[\d*[CG]", RegexOptions.CultureInvariant)]
    private static partial Regex CursorMovePattern();

    // A console of Windows draws its screen again by placing the cursor on a line (ESC[row;colH) or moving it up and down: those
    // moves end a line.
    [GeneratedRegex(@"\u001b\[\d*(;\d*)?[Hf]|\u001b\[\d*[ABEF]", RegexOptions.CultureInvariant)]
    private static partial Regex LineMovePattern();

    private static string Plain(string output)
        => TerminalEscapePattern().Replace(CursorMovePattern().Replace(LineMovePattern().Replace(output, "\n"), " "), "").Replace("\r", "", StringComparison.Ordinal);

    // The Claude Code providers of the node: signed in when their token is a secret of the machine, else unknown (the CLI may
    // keep its own login here).
    private IEnumerable<FleetMachineSignInItem> ClaudeItems()
    {
        IReadOnlyList<SecretInfo> secrets = [];
        try { secrets = new LocalSecretStore(Root).ListSecrets(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
        var runs = ClaudeCanSignIn();
        foreach (var definition in Store.LoadGlobalProviderDefinitions(includeDisabled: true).Where(static value => value.ProviderType == ClaudeType).Take(8))
        {
            var name = ClaudeSecretName(definition.ProviderKey);
            var secret = definition.Env?.GetValueOrDefault(ClaudeTokenVariable) == $"${{secret:{name}}}"
                ? secrets.FirstOrDefault(value => string.Equals(value.Name, name, StringComparison.OrdinalIgnoreCase)) : null;
            yield return new FleetMachineSignInItem(FleetMachineSignInKinds.Provider, definition.ProviderKey, null, definition.DisplayName ?? definition.ProviderKey,
                secret is not null ? FleetMachineSignInStates.SignedIn : FleetMachineSignInStates.Unknown, null,
                // A token of claude setup-token lasts a year.
                secret?.SetAt.AddYears(1), runs is null ? [FleetMachineSignInWays.Code] : [],
                runs ?? (secret is null ? "Its own login on the machine, if it has one: a token from here also works where that cannot be read, in a service started at boot." : null));
        }
    }

    /// <summary>Gets the executable of Claude Code; null to find <c>claude</c> on the PATH of the node. Set by tests.</summary>
    internal string? ClaudeExecutable { get; init; }

    private string? FindClaude() => ClaudeExecutable is { } path ? (File.Exists(path) ? path : null) : FindExecutable("claude");

    // Why claude setup-token cannot run here from another computer; null when it can.
    private string? ClaudeCanSignIn()
    {
        if (FindClaude() is not { } claude) return "Claude Code is not installed on the machine, or not on the PATH of the node.";
        if (OperatingSystem.IsWindows())
            return !IsBatch(claude) || IsPlainForCmd(claude) ? null : "The folder of Claude Code holds a character cmd.exe reads: run claude setup-token on the machine.";
        return File.Exists("/usr/bin/script") ? null : "The machine has no script command, which runs claude setup-token in a terminal.";
    }

    // claude setup-token in a pseudo-terminal (script on macOS and Linux, a pseudoconsole on Windows): its page goes to the desktop,
    // the code the person pastes to its input, and the token it prints to a secret of the machine, which the provider names. Never throws.
    private async Task<FleetMachineSignInState> RunClaudeAsync(Attempt attempt, FleetMachineSignInStartParams parameters, CodeAltaProviderDocument definition)
    {
        if (parameters.Way is { } way && way != FleetMachineSignInWays.Code) throw new NotSupportedException("Claude Code signs in with the code its page shows.");
        if (ClaudeCanSignIn() is { } why) throw new NotSupportedException(why);
        using var terminal = ClaudeTerminal.Start(FindClaude()!);
        // The terminal takes the code as typed, then Enter.
        attempt.AcceptCode = code => terminal.Write(code + "\r");
        using var kill = attempt.Lifetime.Token.Register(terminal.Kill);
        var output = new StringBuilder();
        var buffer = new char[4096];
        string? token = null;
        while (await terminal.ReadAsync(buffer, attempt.Lifetime.Token).ConfigureAwait(false) is var read and > 0)
        {
            if (output.Length < MaxOutput) output.Append(buffer, 0, Math.Min(read, MaxOutput - output.Length));
            var text = output.ToString();
            if (!attempt.Prompt.Task.IsCompleted && ReadClaudePage(text) is { } page)
            {
                attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, FleetMachineSignInWays.Code, page, null, null,
                    "The code the page shows once you signed in.", attempt.ExpiresAt));
            }

            if ((token = ReadClaudeToken(text)) is not null) break;
        }

        terminal.Kill();
        attempt.Lifetime.Token.ThrowIfCancellationRequested();
        if (token is null)
        {
            attempt.Prompt.TrySetException(new InvalidOperationException("claude setup-token ended before it gave a page to open."));
            return new FleetMachineSignInState(attempt.Id, FleetMachineSignInStates.Failed, null, "login_failed", "claude setup-token ended without a token.");
        }

        var name = ClaudeSecretName(definition.ProviderKey);
        new LocalSecretStore(Root).SetSecret(name, token, "machine sign-in (CodeAlta Desktop)");
        providers.SetEnvironment(definition.ProviderKey, ClaudeTokenVariable, $"${{secret:{name}}}");
        return new FleetMachineSignInState(attempt.Id, FleetMachineSignInStates.Completed, null, null, null);
    }

    // The Claude Code provider of this key; null when the key is not one.
    private CodeAltaProviderDocument? ClaudeProvider(string key)
        => Store.LoadGlobalProviderDefinitions(includeDisabled: true).FirstOrDefault(value => value.ProviderType == ClaudeType && string.Equals(value.ProviderKey, key, StringComparison.OrdinalIgnoreCase));

    // Signs a Claude Code provider out from here: its token leaves the secrets and the provider; its own login stays.
    private void SignOutClaude(CodeAltaProviderDocument definition)
    {
        providers.SetEnvironment(definition.ProviderKey, ClaudeTokenVariable, null);
        new LocalSecretStore(Root).DeleteSecret(ClaudeSecretName(definition.ProviderKey));
    }

    private static bool IsBatch(string path)
        => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    // The terminal claude setup-token runs in, wide enough that the page and the token are printed on one line; no browser opens here.
    private abstract class ClaudeTerminal : IDisposable
    {
        public static ClaudeTerminal Start(string claude) => OperatingSystem.IsWindows() ? new ConsoleTerminal(claude) : new ScriptTerminal(claude);

        public abstract ValueTask<int> ReadAsync(char[] buffer, CancellationToken cancellationToken);

        public abstract void Write(string text);

        public abstract void Kill();

        public abstract void Dispose();
    }

    // macOS and Linux: the pseudo-terminal of script.
    private sealed class ScriptTerminal : ClaudeTerminal
    {
        private readonly Process _process;

        public ScriptTerminal(string claude)
        {
            var quoted = "'" + claude.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            var command = $"stty cols 1000 rows 50 2>/dev/null; exec {quoted} setup-token";
            var start = new ProcessStartInfo("/usr/bin/script")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            // BSD script (macOS) takes the command and its words; util-linux script (Linux) a command line with -c.
            string[] arguments = OperatingSystem.IsMacOS() ? ["-q", "/dev/null", "/bin/sh", "-c", command] : ["-q", "-e", "-c", command, "/dev/null"];
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.Environment["BROWSER"] = "true";
            start.Environment["TERM"] = "xterm-256color";
            _process = Process.Start(start) ?? throw new InvalidOperationException("script could not start on this machine.");
        }

        public override ValueTask<int> ReadAsync(char[] buffer, CancellationToken cancellationToken) => _process.StandardOutput.ReadAsync(buffer, cancellationToken);

        public override void Write(string text)
        {
            _process.StandardInput.Write(text);
            _process.StandardInput.Flush();
        }

        public override void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }

        public override void Dispose() => _process.Dispose();
    }

    // Windows: a pseudoconsole, which claude asks its questions in as in any console; a .cmd of npm runs through cmd.exe.
    private sealed class ConsoleTerminal : ClaudeTerminal
    {
        private readonly PseudoTerminal _terminal;
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly byte[] _bytes = new byte[4096];

        public ConsoleTerminal(string claude)
        {
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
            {
                if (pair.Key is string name && pair.Value is string value) environment[name] = value;
            }

            environment["BROWSER"] = "true";
            environment["TERM"] = "xterm-256color";
            var (fileName, arguments) = IsBatch(claude)
                ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/d", "/c", claude, "setup-token" })
                : (claude, new[] { "setup-token" });
            var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _terminal = PseudoTerminal.Start(new PseudoTerminalStart(fileName, arguments, Directory.Exists(folder) ? folder : Environment.CurrentDirectory, environment,
                PseudoTerminal.MaximumSize, 50));
        }

        // A read waits in the console: it ends with what the program printed, or with 0 once the console is closed.
        public override async ValueTask<int> ReadAsync(char[] buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                var read = await Task.Run(() => _terminal.Read(_bytes), cancellationToken).ConfigureAwait(false);
                if (read == 0) return 0;
                var chars = _decoder.GetChars(_bytes, 0, read, buffer, 0, flush: false);
                if (chars > 0) return chars;
            }
        }

        public override void Write(string text) => _terminal.Write(Encoding.UTF8.GetBytes(text));

        public override void Kill() => _terminal.Kill();

        public override void Dispose() => _terminal.Dispose();
    }
}
