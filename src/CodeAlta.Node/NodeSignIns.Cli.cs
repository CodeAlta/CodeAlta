using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Node;

// The command line tools of the spaces of the node (doc/fleet/node.md, credentials on a node): the GitHub CLI of each space, and
// each CLI profile of a space that names a login command (SpaceCliProfile.Login), checked by its check command. A login runs a
// command of an allowlist, never a shell, with the identity environment of the space, and without a terminal: the node reads the
// page and the device code it prints, or the page whose code the person pastes back, and the desktop shows them. The tool keeps
// its own login on the machine.
internal sealed partial class NodeSignIns
{
    /// <summary>The key of the GitHub CLI of a space.</summary>
    internal const string GitHubKey = "gh";

    /// <summary>The tools whose login a node runs from the desktop: the first word of a login or check command.</summary>
    internal static readonly IReadOnlySet<string> AllowedTools = new HashSet<string>(["gh", "glab", "az", "aws", "gcloud"], StringComparer.Ordinal);

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private const int MaxOutput = 64 * 1024;

    /// <summary>Reads what a tool printed: the page and the device code it gives, or the page whose code the person pastes; null while it gave neither.</summary>
    /// <param name="output">What it printed so far.</param>
    /// <returns>The way, the page and the code.</returns>
    internal static (string Way, string Page, string? Code)? ReadPrompt(string output)
    {
        if (PagePattern().Match(output) is not { Success: true } page) return null;
        var address = page.Value.TrimEnd('.', ',', ')');
        if (DeviceCodePattern().Match(output) is { Success: true } code) return (FleetMachineSignInWays.Device, address, code.Groups["code"].Value);
        return AskCodePattern().IsMatch(output) ? (FleetMachineSignInWays.Code, address, null) : null;
    }

    // "First copy your one-time code: EB71-6B69" (gh), "enter the code ABCD12345 to authenticate" (az), "user code: ABCD-EFGH".
    [GeneratedRegex(@"(?:one-time code|user code|enter the code|code)\s*[:\s]\s*(?<code>[A-Z0-9]{4,10}(?:-[A-Z0-9]{4,10})?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceCodePattern();

    [GeneratedRegex(@"https://[^\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex PagePattern();

    // A tool that asks for a code it shows on its page: "Paste code here", "Enter the authorization code:", "Enter verification code:".
    [GeneratedRegex(@"paste (the )?code|enter (the )?(authorization|verification) code", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AskCodePattern();

    [GeneratedRegex(@"account\s+(?<account>[A-Za-z0-9][A-Za-z0-9-]{0,38})\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubAccountPattern();

    // The tools of the spaces of the node, each with how it signs in and how it is checked.
    private async Task<IReadOnlyList<CliTarget>> CliTargetsAsync(CancellationToken cancellationToken)
    {
        var targets = new List<CliTarget>();
        var spaces = await host.SpaceCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var hasGitHub = FindExecutable(GitHubKey) is not null;
        foreach (var space in spaces.Take(16))
        {
            var identity = await host.Identities.ResolveAsync(space.Id, null, cancellationToken).ConfigureAwait(false);
            var suffix = spaces.Count > 1 ? $" ({space.Name})" : "";
            if (hasGitHub)
            {
                targets.Add(new CliTarget(space.Id, GitHubKey, "GitHub (gh)" + suffix, identity.Environment.Variables,
                    ["gh", "auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web", "--skip-ssh-key"],
                    ["gh", "auth", "status", "--hostname", "github.com"]));
            }

            foreach (var profile in identity.Identity?.Cli ?? [])
            {
                if (profile.Login is not { } login || string.Equals(profile.Name, GitHubKey, StringComparison.Ordinal)) continue;
                var loginWords = LoginCommand(SplitCommand(login));
                var checkWords = profile.Check is { } check ? SplitCommand(check) : null;
                targets.Add(new CliTarget(space.Id, profile.Name, profile.Name + suffix, identity.Environment.Variables,
                    loginWords.Count > 0 && AllowedTools.Contains(loginWords[0]) ? loginWords : null,
                    checkWords is { Count: > 0 } && AllowedTools.Contains(checkWords[0]) ? checkWords : null));
            }
        }

        return targets;
    }

    private async Task<IReadOnlyList<FleetMachineSignInItem>> CliItemsAsync(CancellationToken cancellationToken)
    {
        var targets = await CliTargetsAsync(cancellationToken).ConfigureAwait(false);
        var items = new FleetMachineSignInItem[targets.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, targets.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (index, token) =>
        {
            var target = targets[index];
            var (state, account) = await CheckAsync(target, token).ConfigureAwait(false);
            items[index] = new FleetMachineSignInItem(FleetMachineSignInKinds.Cli, target.Key, target.Space, target.Title, state, account, null,
                target.Login is null ? [] : [FleetMachineSignInWays.Device, FleetMachineSignInWays.Code],
                target.Login is null ? "Its login command is not one this machine runs from another computer: sign in on the machine." : null);
        }).ConfigureAwait(false);
        return items;
    }

    // Whether a tool is signed in: its check command ends well. Its output is read for the account of the GitHub CLI only.
    private async Task<(string State, string? Account)> CheckAsync(CliTarget target, CancellationToken cancellationToken)
    {
        if (target.Check is null) return (FleetMachineSignInStates.Unknown, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CheckTimeout);
        try
        {
            using var process = Start(target.Check, target.Environment);
            if (process is null) return (FleetMachineSignInStates.Unknown, null);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
            if (process.ExitCode != 0) return (FleetMachineSignInStates.SignedOut, null);
            var account = target.Key == GitHubKey && GitHubAccountPattern().Match(text) is { Success: true } match ? match.Groups["account"].Value : null;
            return (FleetMachineSignInStates.SignedIn, account);
        }
        catch (Exception exception) when (exception is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (FleetMachineSignInStates.Unknown, null);
        }
    }

    // A tool: its login runs here, the page and the code it prints go to the desktop, and what the person pastes goes to its input. Never throws.
    private Task<FleetMachineSignInState> RunCli(Attempt attempt, FleetMachineSignInStartParams parameters) => Task.Run(async () =>
    {
        Process? process = null;
        try
        {
            var target = (await CliTargetsAsync(attempt.Lifetime.Token).ConfigureAwait(false))
                .FirstOrDefault(value => string.Equals(value.Key, parameters.Key, StringComparison.Ordinal) && string.Equals(value.Space, parameters.Space ?? SpaceDescriptor.DefaultId, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException($"No tool of this machine named {parameters.Key} signs in in that space.");
            if (target.Login is null) throw new NotSupportedException("Its login command is not one this machine runs from another computer: sign in on the machine.");
            if (parameters.Way is { } asked && asked is not (FleetMachineSignInWays.Device or FleetMachineSignInWays.Code))
                throw new NotSupportedException($"{target.Title} signs in with a code, not a browser of another computer.");
            process = Start(target.Login, target.Environment) ?? throw new InvalidOperationException($"{target.Login[0]} could not start on this machine.");
            var started = process;
            attempt.AcceptCode = code =>
            {
                started.StandardInput.WriteLine(code);
                started.StandardInput.Flush();
            };
            using var kill = attempt.Lifetime.Token.Register(() => { try { started.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            var output = new StringBuilder();
            var reading = Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError));
            await process.WaitForExitAsync(attempt.Lifetime.Token).ConfigureAwait(false);
            await reading.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                attempt.Prompt.TrySetException(new InvalidOperationException($"{target.Title}: its login ended with the code {process.ExitCode} before it gave a page to open."));
                return new FleetMachineSignInState(attempt.Id, FleetMachineSignInStates.Failed, null, "login_failed", $"Its login ended with the code {process.ExitCode}.");
            }

            var (_, account) = await CheckAsync(target, CancellationToken.None).ConfigureAwait(false);
            attempt.Prompt.TrySetException(new InvalidOperationException("It is signed in already."));
            return new FleetMachineSignInState(attempt.Id, FleetMachineSignInStates.Completed, account, null, null);

            // Reads what the tool prints, line by line, and gives the desktop its page and its code once both are there.
            async Task ReadAsync(StreamReader reader)
            {
                while (await reader.ReadLineAsync(attempt.Lifetime.Token).ConfigureAwait(false) is { } line)
                {
                    string text;
                    lock (output)
                    {
                        if (output.Length < MaxOutput) output.AppendLine(line);
                        text = output.ToString();
                    }

                    if (attempt.Prompt.Task.IsCompleted || ReadPrompt(text) is not { } prompt) continue;
                    attempt.Prompt.TrySetResult(new FleetMachineSignInPrompt(attempt.Id, prompt.Way, prompt.Page, null, prompt.Code,
                        prompt.Way == FleetMachineSignInWays.Code ? "The code the page shows once you signed in." : null, attempt.ExpiresAt));
                }
            }
        }
        catch (Exception exception)
        {
            return attempt.Failed(exception);
        }
        finally
        {
            process?.Dispose();
        }
    });

    /// <summary>The login command a tool runs without a browser of its own: <c>az login</c> with its device code.</summary>
    /// <param name="words">The words of the login command.</param>
    /// <returns>The words to run.</returns>
    internal static IReadOnlyList<string> LoginCommand(IReadOnlyList<string> words)
    {
        if (words is ["az", "login", ..] && !words.Contains("--use-device-code", StringComparer.Ordinal)) return [.. words, "--use-device-code"];
        return words;
    }

    /// <summary>Splits a command line into its words, with double and single quotes; never through a shell.</summary>
    /// <param name="command">The command line.</param>
    /// <returns>Its words.</returns>
    internal static IReadOnlyList<string> SplitCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var words = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var any = false;
        foreach (var character in command)
        {
            if (quote is { } open)
            {
                if (character == open) quote = null; else current.Append(character);
                continue;
            }

            if (character is '"' or '\'') { quote = character; any = true; continue; }
            if (char.IsWhiteSpace(character))
            {
                if (any) { words.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }

            current.Append(character);
            any = true;
        }

        if (any) words.Add(current.ToString());
        return words;
    }

    // Starts a tool of the allowlist with the variables of the space, without a browser of its own on this machine.
    private static Process? Start(IReadOnlyList<string> words, IReadOnlyList<KeyValuePair<string, string>> environment)
    {
        if (words.Count == 0 || !AllowedTools.Contains(words[0]) || FindExecutable(words[0]) is not { } executable) return null;
        var batch = OperatingSystem.IsWindows() && (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));
        var start = new ProcessStartInfo(batch ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : executable)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        if (batch)
        {
            foreach (var word in new[] { "/d", "/s", "/c", executable }) start.ArgumentList.Add(word);
        }

        foreach (var word in words.Skip(1)) start.ArgumentList.Add(word);
        foreach (var (name, value) in environment) start.Environment[name] = value;
        // A tool that wants to open a browser opens none here: the page goes to the desktop.
        if (!OperatingSystem.IsWindows())
        {
            start.Environment["BROWSER"] = "true";
            start.Environment["GH_BROWSER"] = "true";
        }

        start.Environment["NO_COLOR"] = "1";
        return Process.Start(start);
    }

    // The executable of a tool on the PATH of the node.
    private static string? FindExecutable(string name)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : [""];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(folder.Trim('"'), name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    // A tool of a space: how it signs in and how it is checked (null when its command is not one of the allowlist).
    private sealed record CliTarget(string Space, string Key, string Title, IReadOnlyList<KeyValuePair<string, string>> Environment,
        IReadOnlyList<string>? Login, IReadOnlyList<string>? Check);
}
