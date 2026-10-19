using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Plugin.Jira;

namespace CodeAlta.Desktop.Spaces;

/// <summary>What a run of a command line tool gave: its exit code, <c>-1</c> when the tool is not installed.</summary>
/// <param name="ExitCode">The exit code; <c>-1</c> when it could not be started.</param>
/// <param name="Output">What it wrote on its standard output.</param>
/// <param name="Error">What it wrote on its standard error.</param>
internal readonly record struct IdentityToolRun(int ExitCode, string Output, string Error)
{
    /// <summary>Gets whether the tool could not be started: it is not installed.</summary>
    public bool Missing => ExitCode == -1;
}

/// <summary>Runs a command line tool with the variables of an identity, over the environment of the application.</summary>
internal delegate Task<IdentityToolRun> IdentityToolRunner(string fileName, IReadOnlyList<string> arguments, AgentProcessEnvironment environment, CancellationToken cancellationToken);

/// <summary>
/// Runs a command line in the shell of <c>shell_command</c> with the variables of an identity, over the environment of
/// the application: what it writes on both outputs is its <see cref="IdentityToolRun.Output"/>.
/// </summary>
internal delegate Task<IdentityToolRun> IdentityShellRunner(string command, AgentProcessEnvironment environment, CancellationToken cancellationToken);

/// <summary>One line of what a check found: <c>ok</c>, <c>info</c> or <c>error</c>, and what it says.</summary>
/// <param name="Kind">Whether it is what was expected (<c>ok</c>), worth knowing (<c>info</c>), or wrong (<c>error</c>).</param>
/// <param name="Text">What it says, in words for the user; never a secret.</param>
internal sealed record IdentityCheckLine(string Kind, string Text);

/// <summary>
/// The usual settings of the user, which a space without identity keeps: read from the global Git configuration and
/// the environment of the application, and kept for a short while since the page reads them each time it opens.
/// </summary>
/// <param name="GitName">The name of <c>git config --global user.name</c>.</param>
/// <param name="GitEmail">The email of <c>git config --global user.email</c>.</param>
/// <param name="SigningKey">The key of <c>git config --global user.signingkey</c>.</param>
/// <param name="SigningFormat">The format of <c>git config --global gpg.format</c>.</param>
/// <param name="GitHubFolder">The folder of the usual profile of the GitHub CLI.</param>
/// <param name="JiraEmail">The email of <c>JIRA_EMAIL</c> or <c>ATLASSIAN_EMAIL</c>.</param>
/// <param name="JiraTokenVariable">The variable the Jira plugin reads its token from without a space.</param>
/// <param name="Home">The folder of the user, which <c>~</c> stands for.</param>
/// <param name="Platform"><c>windows</c>, <c>macos</c> or <c>linux</c>: how a command to copy is written.</param>
internal sealed record SpaceIdentityUsual(string? GitName, string? GitEmail, string? SigningKey, string? SigningFormat, string GitHubFolder,
    string? JiraEmail, string JiraTokenVariable, string Home, string Platform);

/// <summary>
/// Checks an identity of a space with the tools it is for: who Git commits as and whether its keys exist, which account
/// the GitHub CLI is signed in to with its profile, whether Jira accepts its account, and what the check command of a
/// CLI profile says. Nothing is changed, and no secret is shown.
/// </summary>
internal sealed partial class SpaceIdentityChecks : IDisposable
{
    private static readonly TimeSpan ToolPatience = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UsualLifetime = TimeSpan.FromSeconds(30);
    private const int MostOutputLines = 8;
    private const int MostOutputCharacters = 16 * 1024;
    private readonly IdentityToolRunner _run;
    private readonly IdentityShellRunner _shell;
    private readonly HttpClient _http;
    private readonly Func<string, string?> _variable;
    private readonly string _home;
    private readonly bool _windows;
    private readonly Lock _gate = new();
    private (SpaceIdentityUsual Value, long At)? _usual;

    /// <summary>Creates the checks of this machine.</summary>
    internal SpaceIdentityChecks()
        : this(RunAsync, new HttpClientHandler(), Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), OperatingSystem.IsWindows(),
            RunShellAsync)
    {
    }

    /// <summary>Creates the checks over what a test gives.</summary>
    /// <param name="run">Runs the tools.</param>
    /// <param name="handler">Answers the requests to Jira; disposed with the checks.</param>
    /// <param name="variable">Reads a variable of the environment of the application.</param>
    /// <param name="home">The folder of the user.</param>
    /// <param name="windows">Whether the application runs on Windows.</param>
    /// <param name="shell">Runs the check commands of CLI profiles; null for none, which says the shell cannot start.</param>
    internal SpaceIdentityChecks(IdentityToolRunner run, HttpMessageHandler handler, Func<string, string?> variable, string home, bool windows,
        IdentityShellRunner? shell = null)
    {
        (_run, _variable, _home, _windows) = (run, variable, home, windows);
        _shell = shell ?? (static (_, _, _) => Task.FromResult(new IdentityToolRun(-1, string.Empty, string.Empty)));
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CodeAlta");
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>Reads the usual settings of the user.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The settings; what cannot be read is null.</returns>
    internal async Task<SpaceIdentityUsual> UsualAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_usual is { } known && Environment.TickCount64 - known.At < UsualLifetime.TotalMilliseconds) return known.Value;
        }

        var global = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var read = await _run("git", ["config", "--global", "--get-regexp", @"^(user\.(name|email|signingkey)|gpg\.format)$"], AgentProcessEnvironment.Empty, cancellationToken).ConfigureAwait(false);
        foreach (var line in read.ExitCode == 0 ? read.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [])
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0) global[line[..space]] = line[(space + 1)..].Trim();
        }

        var usual = new SpaceIdentityUsual(
            global.GetValueOrDefault("user.name"), global.GetValueOrDefault("user.email"), global.GetValueOrDefault("user.signingkey"), global.GetValueOrDefault("gpg.format"),
            UsualGitHubFolder(), Text(_variable("JIRA_EMAIL")) ?? Text(_variable("ATLASSIAN_EMAIL")),
            JiraSettings.TokenVariables.FirstOrDefault(name => Text(_variable(name)) is not null) ?? JiraSettings.TokenVariables[0],
            _home, _windows ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux");
        lock (_gate) _usual = (usual, Environment.TickCount64);
        return usual;
    }

    /// <summary>Checks the Git identity: the author Git commits as, the SSH key, the signing key, the sign-off.</summary>
    /// <param name="git">The Git identity of the space; null for the usual one.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the check found.</returns>
    internal async Task<IReadOnlyList<IdentityCheckLine>> CheckGitAsync(SpaceGitIdentity? git, CancellationToken cancellationToken)
    {
        var lines = new List<IdentityCheckLine>();
        var environment = SpaceIdentityEnvironment.Create(git is null ? null : new SpaceIdentity(git), _variable, _home, _windows);
        var ident = await _run("git", ["var", "GIT_AUTHOR_IDENT"], environment, cancellationToken).ConfigureAwait(false);
        string? author = null;
        if (ident.Missing) lines.Add(new("error", "Git was not found."));
        else if (ident.ExitCode != 0) lines.Add(new("error", "Git has no author: " + FirstLine(ident.Error, "set a name and an email.")));
        else lines.Add(new("ok", "Commits as " + (author = Author(ident.Output))));

        if (git?.SshKey is { } key)
        {
            var path = SpaceIdentityEnvironment.ExpandPath(key, _home);
            lines.Add(File.Exists(path) ? new("ok", "Fetches and pushes with the SSH key " + path) : new("error", "The SSH key " + path + " does not exist."));
        }

        switch (git?.Sign)
        {
            case SpaceGitSigning.Ssh when git.SigningKey is { } signing:
            {
                var path = signing.StartsWith('~') ? SpaceIdentityEnvironment.ExpandPath(signing, _home) : signing;
                lines.Add(File.Exists(path) ? new("ok", "Signs with the SSH key " + path) : new("error", "The signing key " + path + " does not exist."));
                break;
            }

            case SpaceGitSigning.Gpg when git.SigningKey is { } signing:
            {
                var listed = await _run("gpg", ["--list-secret-keys", "--with-colons", signing], environment, cancellationToken).ConfigureAwait(false);
                lines.Add(listed.Missing ? new("error", "GPG was not found.")
                    : listed.ExitCode == 0 ? new("ok", "Signs with the GPG key " + signing)
                    : new("error", "GPG has no secret key " + signing + "."));
                break;
            }

            case SpaceGitSigning.Off:
                lines.Add(new("ok", "Commits are not signed."));
                break;
        }

        if (git?.SignOff == true && author is not null) lines.Add(new("ok", "Signed-off-by: " + author));
        return lines;
    }

    /// <summary>Checks the GitHub profile: the accounts the GitHub CLI is signed in to with it.</summary>
    /// <param name="gitHub">The GitHub identity of the space; null for the usual profile.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the check found.</returns>
    internal async Task<IReadOnlyList<IdentityCheckLine>> CheckGitHubAsync(SpaceGitHubIdentity? gitHub, CancellationToken cancellationToken)
    {
        var lines = new List<IdentityCheckLine>();
        var environment = SpaceIdentityEnvironment.Create(gitHub is null ? null : new SpaceIdentity(GitHub: gitHub), _variable, _home, _windows);
        if (environment.Get(SpaceIdentityEnvironment.GitHubConfigDirectoryVariable) is { } folder && !Directory.Exists(folder))
            lines.Add(new("info", "The folder " + folder + " does not exist yet: the login command creates it."));
        var status = await _run("gh", ["auth", "status"], environment, cancellationToken).ConfigureAwait(false);
        if (status.Missing)
        {
            lines.Add(new("error", "The GitHub CLI (gh) was not found."));
            return lines;
        }

        // The CLI writes its accounts on one output or the other depending on its version.
        var accounts = (status.Output + "\n" + status.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => line.Contains("Logged in to", StringComparison.Ordinal))
            .Select(static line => line.TrimStart('✓', '-', '*', ' ').Trim()).Take(4).ToArray();
        if (status.ExitCode == 0 && accounts.Length > 0) lines.AddRange(accounts.Select(static account => new IdentityCheckLine("ok", account)));
        else lines.Add(new("error", "Not signed in to GitHub with this profile: run the login command."));
        return lines;
    }

    /// <summary>
    /// Checks the Jira account: with an API token, whether Jira accepts it for the email; without one, whether the
    /// site answers. The token is read from its variable for the request only.
    /// </summary>
    /// <param name="jira">The Jira identity of the space; null for the Jira of each project.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the check found.</returns>
    internal async Task<IReadOnlyList<IdentityCheckLine>> CheckJiraAsync(SpaceJiraIdentity? jira, CancellationToken cancellationToken)
    {
        if (jira is null || jira.IsEmpty) return [new("info", "Each project names its Jira in its .alta/config.toml.")];
        if (JiraSettings.NormalizeSite(jira.Site) is not { } site) return [new("error", "Write the site of Jira, such as contoso.atlassian.net.")];
        var token = jira.TokenEnv is { } name ? Text(_variable(name)) : null;
        if (jira.TokenEnv is { } missing && token is null)
            return [new("error", "The variable " + missing + " is not set in the environment of CodeAlta.")];
        try
        {
            if (token is null || jira.Email is null)
            {
                using var anonymous = await _http.GetAsync(new Uri($"https://{site}/rest/api/3/serverInfo"), cancellationToken).ConfigureAwait(false);
                return anonymous.IsSuccessStatusCode
                    ? [new("ok", site + " answers."), new("info", token is null ? "Without an API token, Jira is signed in to in the browser: “Jira: Sign in” in the search of the window." : "The API token needs the email of its account.")]
                    : [new("error", $"{site} answered HTTP {(int)anonymous.StatusCode}: is it a Jira site?")];
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{site}/rest/api/3/myself"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(jira.Email + ":" + token)));
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return [new("error", $"Jira refused the token of {jira.TokenEnv} for {jira.Email}.")];
            if (!response.IsSuccessStatusCode) return [new("error", $"{site} answered HTTP {(int)response.StatusCode}.")];
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var shown = document.RootElement.TryGetProperty("displayName", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() : null;
            return [new("ok", $"Signed in to {site} as {(string.IsNullOrWhiteSpace(shown) ? jira.Email : $"{shown} ({jira.Email})")}.")];
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return [new("error", $"{site} cannot be reached: {FirstLine(exception.Message, "no answer.")}")];
        }
    }

    /// <summary>
    /// Checks a CLI profile: what its variables are given (a reference shows the variable it reads, never its value),
    /// then what its check command writes, run with these variables alone over the environment of the application.
    /// </summary>
    /// <param name="profile">The profile as the page writes it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the check found.</returns>
    internal async Task<IReadOnlyList<IdentityCheckLine>> CheckCliAsync(SpaceCliProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ProblemOfItself(requireVariables: true) is { } problem) return [new("error", problem)];
        var missing = new List<KeyValuePair<string, string>>();
        var environment = SpaceIdentityEnvironment.Create(profile, _variable, _home, _windows, missing);
        var lines = new List<IdentityCheckLine>();
        foreach (var (name, value) in profile.Environment)
        {
            if (missing.FirstOrDefault(left => left.Key == name) is { Key: not null } unset)
                lines.Add(new("error", $"{name} is left out: the variable {unset.Value} is not set in the environment of CodeAlta."));
            else if (SpaceCliProfile.References(value) is { Count: > 0 } references)
                lines.Add(new("ok", $"{name} is set from {string.Join(", ", references.Select(static reference => "${env:" + reference + "}"))}."));
            else
                lines.Add(new("ok", $"{name}={environment.Get(name)}"));
        }

        if (profile.Check is not { } command)
        {
            lines.Add(new("info", "Without a check command, only the variables are checked."));
            return lines;
        }

        var run = await _shell(command, environment, cancellationToken).ConfigureAwait(false);
        if (run.Missing)
        {
            lines.Add(new("error", "The shell could not start the check command."));
            return lines;
        }

        var output = (run.Output + "\n" + run.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kind = run.ExitCode == 0 ? "ok" : "error";
        lines.AddRange(output.Take(MostOutputLines).Select(line => new IdentityCheckLine(kind, line.Length > 200 ? line[..200] + "…" : line)));
        if (output.Length > MostOutputLines) lines.Add(new("info", $"… {output.Length - MostOutputLines} more lines."));
        if (run.ExitCode != 0) lines.Add(new("error", $"The check command ended with the code {run.ExitCode}."));
        else if (output.Length == 0) lines.Add(new("ok", "The check command succeeded."));
        return lines;
    }

    // "A. Mutel <alex@contoso.example> 1760000000 +0200": the author, without the time.
    private static string Author(string ident)
    {
        var text = ident.Trim();
        var match = AuthorTime().Match(text);
        return match.Success ? match.Groups[1].Value : text;
    }

    private string UsualGitHubFolder()
    {
        if (Text(_variable(SpaceIdentityEnvironment.GitHubConfigDirectoryVariable)) is { } named) return named;
        if (_windows) return Path.Join(Text(_variable("APPDATA")) ?? Path.Join(_home, "AppData", "Roaming"), "GitHub CLI");
        return Path.Join(Text(_variable("XDG_CONFIG_HOME")) ?? Path.Join(_home, ".config"), "gh");
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string FirstLine(string text, string fallback)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrEmpty(line) ? fallback : line.Length > 200 ? line[..200] + "…" : line;
    }

    // A tool started with the variables of the identity, nothing on its input, and a limit on its time.
    private static async Task<IdentityToolRun> RunAsync(string fileName, IReadOnlyList<string> arguments, AgentProcessEnvironment environment, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        environment.ApplyTo(start.Environment);
        // A tool never waits for a prompt it could ask.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(-1, string.Empty, string.Empty);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new(-1, string.Empty, string.Empty);
        }

        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(ToolPatience);
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
            return new(124, string.Empty, fileName + " did not answer in time.");
        }
    }

    // A check command in the shell of shell_command, started in the folder of the user, nothing on its input, its two
    // outputs read together up to a limit, and a limit on its time.
    private static async Task<IdentityToolRun> RunShellAsync(string command, AgentProcessEnvironment environment, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var gate = new Lock();
        CodeAlta.Orchestration.Jobs.ShellCommandProcess process;
        try
        {
            process = CodeAlta.Orchestration.Jobs.ShellCommandProcess.Start(command, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), environment, text =>
            {
                lock (gate)
                {
                    if (output.Length < MostOutputCharacters) output.Append(text);
                }
            });
        }
        catch (InvalidOperationException)
        {
            return new(-1, string.Empty, string.Empty);
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ToolPatience);
            try
            {
                var code = await process.Completion.WaitAsync(limit.Token).ConfigureAwait(false);
                lock (gate) return new(code, output.ToString(), string.Empty);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill();
                return new(124, string.Empty, "The check command did not answer in time.");
            }
        }
    }

    [GeneratedRegex(@"^(.*>)\s+\d+\s+[+-]\d{4}$")]
    private static partial Regex AuthorTime();
}
