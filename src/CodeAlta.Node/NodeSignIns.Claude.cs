using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
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

    /// <summary>Reads what <c>claude setup-token</c> printed in its terminal: the page to open, once it asks for the code; null before.</summary>
    /// <param name="output">What it printed, escapes of the terminal included.</param>
    /// <returns>The page.</returns>
    internal static string? ReadClaudePage(string output)
        => ClaudeLinkPattern().Match(output) is { Success: true } link && AskCodePattern().IsMatch(Plain(output)) ? link.Groups["page"].Value : null;

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

    private static string Plain(string output)
        => TerminalEscapePattern().Replace(CursorMovePattern().Replace(output, " "), "").Replace("\r", "", StringComparison.Ordinal);

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

    // Why claude setup-token cannot run here from another computer; null when it can.
    private static string? ClaudeCanSignIn()
    {
        if (OperatingSystem.IsWindows()) return "Claude Code signs in from another computer on a macOS or Linux machine: on Windows, run claude setup-token there.";
        if (FindExecutable("claude") is null) return "Claude Code is not installed on the machine, or not on the PATH of the node.";
        return File.Exists("/usr/bin/script") ? null : "The machine has no script command, which runs claude setup-token in a terminal.";
    }

    // claude setup-token in a pseudo-terminal: its page goes to the desktop, the code the person pastes to its input, and the token
    // it prints to a secret of the machine, which the provider names. Never throws.
    private async Task<FleetMachineSignInState> RunClaudeAsync(Attempt attempt, FleetMachineSignInStartParams parameters, CodeAltaProviderDocument definition)
    {
        if (parameters.Way is { } way && way != FleetMachineSignInWays.Code) throw new NotSupportedException("Claude Code signs in with the code its page shows.");
        if (ClaudeCanSignIn() is { } why) throw new NotSupportedException(why);
        using var process = StartClaudeSetupToken(FindExecutable("claude")!);
        attempt.AcceptCode = code =>
        {
            // The terminal takes the code as typed, then Enter.
            process.StandardInput.Write(code + "\r");
            process.StandardInput.Flush();
        };
        using var kill = attempt.Lifetime.Token.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var output = new StringBuilder();
        var buffer = new char[4096];
        string? token = null;
        while (await process.StandardOutput.ReadAsync(buffer, attempt.Lifetime.Token).ConfigureAwait(false) is var read and > 0)
        {
            if (output.Length < MaxOutput) output.Append(buffer, 0, read);
            var text = output.ToString();
            if (!attempt.Prompt.Task.IsCompleted && ReadClaudePage(text) is { } page)
            {
                attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, FleetMachineSignInWays.Code, page, null, null,
                    "The code the page shows once you signed in.", attempt.ExpiresAt));
            }

            if ((token = ReadClaudeToken(text)) is not null) break;
        }

        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
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

    // The pseudo-terminal of script, wide enough that the token is printed on one line; no browser opens here.
    private static Process StartClaudeSetupToken(string claude)
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
        return Process.Start(start) ?? throw new InvalidOperationException("script could not start on this machine.");
    }
}
