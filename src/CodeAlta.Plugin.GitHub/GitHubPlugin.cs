using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.GitHub;

/// <summary>
/// Built-in plugin that adds a GitHub issue prompt picker and an optional GitHub CLI tool.
/// </summary>
[Plugin("github", DisplayName = "GitHub", Description = "Adds a GitHub issue prompt picker and exposes the GitHub CLI when available.")]
public sealed class GitHubPlugin : PluginBase
{
    private const string MissingGhStateName = "github-gh-missing-notice.json";
    private const string InstallGhUrl = "https://github.com/cli/cli#installation";
    private bool _ghAvailable;
    private HttpClient? _httpClient;

    /// <inheritdoc />
    public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodeAlta-GitHub-Plugin");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }

        _ghAvailable = await GitHubCli.TryGetVersionAsync(cancellationToken).ConfigureAwait(false) is not null;
        if (!_ghAvailable)
        {
            Logger.Warn($"GitHub CLI 'gh' was not found. Install it from {InstallGhUrl} to enable the GitHub CLI agent tool.");
            await MaybeNotifyMissingGhAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks whether GitHub issue prompt lookup can run for the specified project path.
    /// </summary>
    /// <param name="projectPath">The selected project path.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true" /> when the project has a GitHub remote.</returns>
    public ValueTask<bool> CanResolveIssueReferencesAsync(string? projectPath, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return new ValueTask<bool>(GitHubRepositoryDetector.TryDetect(ResolveProjectPath(projectPath), out _));
    }

    /// <summary>
    /// Queries GitHub issues for prompt lookup.
    /// </summary>
    /// <param name="projectPath">The selected project path.</param>
    /// <param name="queryText">The text typed after <c>#</c>.</param>
    /// <param name="maximumResults">The preferred maximum result count.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Issue references, or <see langword="null" /> when the project is not a GitHub repository.</returns>
    public async ValueTask<IReadOnlyList<GitHubIssueReferenceItem>?> QueryIssueReferencesAsync(string? projectPath, string queryText, int maximumResults, CancellationToken cancellationToken = default)
    {
        if (!GitHubRepositoryDetector.TryDetect(ResolveProjectPath(projectPath), out var repository) || _httpClient is null)
        {
            return null;
        }

        var resultLimit = Math.Clamp(maximumResults, 1, 100);
        var query = queryText.Trim();
        var issues = await QueryIssuesAsync(_httpClient, repository, query, resultLimit, cancellationToken).ConfigureAwait(false);

        return issues
            .OrderByDescending(static issue => issue.UpdatedAt)
            .Select(static issue => new GitHubIssueReferenceItem(
                issue.Number,
                issue.Title,
                issue.Url,
                issue.UpdatedAt,
                issue.State,
                issue.Repository))
            .ToArray();
    }

    private static bool ShouldFilterRecentIssues(string query)
        => query.Length < 3 || query.All(char.IsAsciiDigit);

    private static async Task<IReadOnlyList<GitHubIssue>> QueryIssuesAsync(HttpClient client, GitHubRepository repository, string query, int resultLimit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || ShouldFilterRecentIssues(query))
        {
            return FilterRecentIssues(
                await GitHubIssueClient.SearchIssuesAsync(client, repository, string.Empty, 100, cancellationToken).ConfigureAwait(false),
                query,
                resultLimit);
        }

        if (!IsPlainIssueWordQuery(query))
        {
            return await GitHubIssueClient.SearchIssuesAsync(client, repository, query, resultLimit, cancellationToken).ConfigureAwait(false);
        }

        var localMatches = FilterRecentIssues(
            await GitHubIssueClient.SearchIssuesAsync(client, repository, string.Empty, 100, cancellationToken).ConfigureAwait(false),
            query,
            resultLimit);
        if (localMatches.Count >= resultLimit)
        {
            return localMatches;
        }

        var searchMatches = await GitHubIssueClient.SearchIssuesAsync(client, repository, query, resultLimit, cancellationToken).ConfigureAwait(false);
        return MergeIssueResults(localMatches, searchMatches, resultLimit);
    }

    private static bool IsPlainIssueWordQuery(string query)
    {
        foreach (var ch in query)
        {
            if (char.IsWhiteSpace(ch) || ch is ':' or '"' or '\'')
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<GitHubIssue> FilterRecentIssues(IReadOnlyList<GitHubIssue> issues, string query, int maximumResults)
    {
        var filtered = issues;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = issues
                .Where(issue => IssueMatchesRecentFilter(issue, query))
                .ToArray();
        }

        return filtered.Count <= maximumResults ? filtered : filtered.Take(maximumResults).ToArray();
    }

    private static bool IssueMatchesRecentFilter(GitHubIssue issue, string query)
        => issue.Number.ToString(CultureInfo.InvariantCulture).StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
            issue.Title.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<GitHubIssue> MergeIssueResults(IReadOnlyList<GitHubIssue> primary, IReadOnlyList<GitHubIssue> secondary, int maximumResults)
    {
        var results = new List<GitHubIssue>(maximumResults);
        AddIssues(primary);
        AddIssues(secondary);
        return results.ToArray();

        void AddIssues(IReadOnlyList<GitHubIssue> source)
        {
            foreach (var issue in source)
            {
                if (results.Count >= maximumResults)
                {
                    return;
                }

                if (results.Any(existing => existing.Number == issue.Number && string.Equals(existing.Repository, issue.Repository, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                results.Add(issue);
            }
        }
    }

    /// <inheritdoc />
    public override IEnumerable<PluginPromptEditorContribution> GetPromptEditorContributions()
    {
        yield return new PluginPromptEditorContribution
        {
            Name = "GitHub issue prompt picker",
            PlaceholderText = "[#] to reference a GitHub issue",
            Attach = host => new GitHubIssuePromptAttachment(this, host),
        };
    }

    internal string? GetSelectedProjectPath()
        => Services.Workspace.SelectedProjectPath;

    private static string? ResolveProjectPath(string? projectPath)
        => string.IsNullOrWhiteSpace(projectPath) ? Environment.CurrentDirectory : projectPath;

    /// <inheritdoc />
    public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
    {
        if (!_ghAvailable)
        {
            yield break;
        }

        yield return new PluginAgentToolContribution
        {
            Definition = new AgentToolDefinition(
                new AgentToolSpec(
                    "gh",
                    "Run the GitHub CLI (gh) in the selected project. Use for GitHub repository, issue, PR, release, and workflow operations when the user asks for GitHub interaction.",
                    CreateGhToolSchema()),
                RunGhToolAsync),
            PromptSnippet = "The GitHub CLI `gh` is available as tool `gh` for repository, issue, pull request, release, and workflow tasks.",
            PromptGuidance = "Prefer `gh` for GitHub operations in the current repository. Pass arguments as an array; do not include the executable name.",
            ActivationPolicy = PluginToolActivationPolicy.CodeAltaManagedOnly,
        };
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        _httpClient = null;
        return ValueTask.CompletedTask;
    }

    private async Task<AgentToolResult> RunGhToolAsync(AgentToolInvocation invocation, CancellationToken cancellationToken)
    {
        var parse = GhToolArguments.TryParse(invocation.Arguments, out var arguments, out var workingDirectory, out var timeout, out var error);
        if (!parse)
        {
            return Failure(error ?? "Invalid gh tool arguments.");
        }

        var projectPath = ResolveProjectPath(Services.Workspace.SelectedProjectPath);
        var cwd = string.IsNullOrWhiteSpace(workingDirectory)
            ? projectPath ?? Environment.CurrentDirectory
            : Path.GetFullPath(workingDirectory);
        if (!string.IsNullOrWhiteSpace(projectPath) && !IsInside(projectPath, cwd))
        {
            return Failure("The gh workingDirectory must be inside the selected project.");
        }

        var result = await GitHubCli.RunAsync(arguments, cwd, timeout, cancellationToken).ConfigureAwait(false);
        var output = new StringBuilder();
        output.AppendLine(FormattableString.Invariant($"exit_code: {result.ExitCode}"));
        output.AppendLine(FormattableString.Invariant($"working_directory: {result.WorkingDirectory}"));
        AppendBlock(output, "stdout", result.Stdout);
        AppendBlock(output, "stderr", result.Stderr);
        return new AgentToolResult(result.ExitCode == 0, [new AgentToolResultItem.Text(output.ToString())], result.ExitCode == 0 ? null : "gh exited with a non-zero status.");
    }

    private async Task MaybeNotifyMissingGhAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await Services.State.ReadJsonAsync<MissingGhNoticeState>(PluginStateScope.User, MissingGhStateName, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (state is not null && now - state.LastShownUtc < TimeSpan.FromDays(14))
            {
                return;
            }

            if (Ui.HasInteractiveUi)
            {
                await Ui.NotifyAsync($"GitHub CLI 'gh' is not installed. Install it to enable the GitHub tool: {InstallGhUrl}", cancellationToken).ConfigureAwait(false);
            }

            await Services.State.WriteJsonAsync(PluginStateScope.User, MissingGhStateName, new MissingGhNoticeState(now), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn(ex, "Failed to persist GitHub CLI missing notification state.");
        }
    }

    private static JsonElement CreateGhToolSchema()
        => JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "arguments": {
                  "type": "array",
                  "description": "Arguments to pass to gh, excluding the gh executable name.",
                  "items": { "type": "string" }
                },
                "workingDirectory": {
                  "type": "string",
                  "description": "Optional working directory. Defaults to the selected project. Must stay inside the selected project when one is selected."
                },
                "timeoutSeconds": {
                  "type": "integer",
                  "description": "Optional timeout in seconds. Defaults to 60 and is capped at 300.",
                  "minimum": 1,
                  "maximum": 300
                }
              },
              "required": ["arguments"],
              "additionalProperties": false
            }
            """).RootElement.Clone();

    private static AgentToolResult Failure(string message)
        => new(false, [new AgentToolResultItem.Text(message)], message);

    private static void AppendBlock(StringBuilder output, string name, string value)
    {
        output.Append(name).AppendLine(":");
        output.AppendLine(string.IsNullOrEmpty(value) ? "(empty)" : value.TrimEnd());
    }

    private static bool IsInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record MissingGhNoticeState(DateTimeOffset LastShownUtc);

    private sealed record GitHubRepository(string Owner, string Name)
    {
        public string FullName => Owner + "/" + Name;
    }

    private sealed record GitHubIssue(int Number, string Title, string Url, DateTimeOffset UpdatedAt, string State, string Repository);

    private static class GitHubRepositoryDetector
    {
        public static bool TryDetect(string? projectPath, out GitHubRepository repository)
        {
            repository = default!;
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                return false;
            }

            var gitPath = FindGitPath(projectPath);
            if (gitPath is null)
            {
                return false;
            }

            var configPath = Directory.Exists(gitPath)
                ? Path.Combine(gitPath, "config")
                : ResolveGitFileConfigPath(gitPath, projectPath);
            if (configPath is null || !File.Exists(configPath))
            {
                return false;
            }

            foreach (var line in File.ReadLines(configPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("url =", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryParseGitHubRemote(trimmed[5..].Trim(), out repository))
                {
                    return true;
                }
            }

            return false;
        }

        private static string? FindGitPath(string projectPath)
        {
            var directory = new DirectoryInfo(Path.GetFullPath(projectPath));
            while (directory is not null)
            {
                var dotGit = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(dotGit) || File.Exists(dotGit))
                {
                    return dotGit;
                }

                directory = directory.Parent;
            }

            return null;
        }

        private static string? ResolveGitFileConfigPath(string gitFilePath, string projectPath)
        {
            var line = File.ReadLines(gitFilePath).FirstOrDefault();
            if (line is null || !line.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var gitDir = line[7..].Trim();
            if (!Path.IsPathRooted(gitDir))
            {
                gitDir = Path.GetFullPath(Path.Combine(projectPath, gitDir));
            }

            return Path.Combine(gitDir, "config");
        }

        private static bool TryParseGitHubRemote(string remoteUrl, out GitHubRepository repository)
        {
            repository = default!;
            var normalized = remoteUrl.Trim();
            if (normalized.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "https://github.com/" + normalized[15..];
            }
            else if (normalized.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "https://github.com/" + normalized[21..];
            }

            if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length < 2)
            {
                return false;
            }

            var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
            if (string.IsNullOrWhiteSpace(segments[0]) || string.IsNullOrWhiteSpace(repo))
            {
                return false;
            }

            repository = new GitHubRepository(segments[0], repo);
            return true;
        }
    }

    private static class GitHubIssueClient
    {
        public static async Task<IReadOnlyList<GitHubIssue>> SearchIssuesAsync(HttpClient client, GitHubRepository repository, string query, int maximumResults, CancellationToken cancellationToken)
        {
            var url = string.IsNullOrWhiteSpace(query)
                ? FormattableString.Invariant($"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/issues?state=all&sort=updated&direction=desc&per_page={maximumResults}")
                : FormattableString.Invariant($"https://api.github.com/search/issues?q={Uri.EscapeDataString($"repo:{repository.FullName} is:issue {query}")}&sort=updated&order=desc&per_page={maximumResults}");
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items : root;
            return ReadIssues(array, repository.FullName).ToArray();
        }

        public static async Task<IReadOnlyList<GitHubIssue>> GetIssueAsync(HttpClient client, GitHubRepository repository, int number, CancellationToken cancellationToken)
        {
            var url = FormattableString.Invariant($"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/issues/{number}");
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return TryReadIssue(document.RootElement, repository.FullName, out var issue) ? [issue] : [];
        }

        private static IEnumerable<GitHubIssue> ReadIssues(JsonElement array, string repository)
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var item in array.EnumerateArray())
            {
                if (TryReadIssue(item, repository, out var issue))
                {
                    yield return issue;
                }
            }
        }

        private static bool TryReadIssue(JsonElement item, string repository, out GitHubIssue issue)
        {
            issue = default!;
            if (item.TryGetProperty("pull_request", out _))
            {
                return false;
            }

            if (!item.TryGetProperty("number", out var numberElement) ||
                !item.TryGetProperty("title", out var titleElement) ||
                !item.TryGetProperty("html_url", out var urlElement) ||
                !item.TryGetProperty("updated_at", out var updatedElement))
            {
                return false;
            }

            var number = numberElement.GetInt32();
            var title = titleElement.GetString() ?? string.Empty;
            var url = urlElement.GetString() ?? string.Empty;
            var state = item.TryGetProperty("state", out var stateElement) ? stateElement.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(url) || !DateTimeOffset.TryParse(updatedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var updatedAt))
            {
                return false;
            }

            issue = new GitHubIssue(number, title, url, updatedAt, state, repository);
            return true;
        }
    }

    private static class GhToolArguments
    {
        public static bool TryParse(JsonElement json, out IReadOnlyList<string> arguments, out string? workingDirectory, out TimeSpan timeout, out string? error)
        {
            arguments = [];
            workingDirectory = null;
            timeout = TimeSpan.FromSeconds(60);
            error = null;
            if (json.ValueKind != JsonValueKind.Object)
            {
                error = "Arguments must be a JSON object.";
                return false;
            }

            if (!json.TryGetProperty("arguments", out var argsElement) || argsElement.ValueKind != JsonValueKind.Array)
            {
                error = "Property 'arguments' is required and must be an array of strings.";
                return false;
            }

            var values = new List<string>();
            foreach (var item in argsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    error = "Every gh argument must be a string.";
                    return false;
                }

                values.Add(item.GetString() ?? string.Empty);
            }

            if (values.Count == 0)
            {
                error = "At least one gh argument is required.";
                return false;
            }

            arguments = values;
            if (json.TryGetProperty("workingDirectory", out var cwdElement) && cwdElement.ValueKind == JsonValueKind.String)
            {
                workingDirectory = cwdElement.GetString();
            }

            if (json.TryGetProperty("timeoutSeconds", out var timeoutElement) && timeoutElement.TryGetInt32(out var seconds))
            {
                timeout = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300));
            }

            return true;
        }
    }

    private static class GitHubCli
    {
        public static async Task<string?> TryGetVersionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await RunProcessAsync("gh", ["--version"], Environment.CurrentDirectory, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return result.ExitCode == 0 ? result.Stdout : null;
            }
            catch
            {
                return null;
            }
        }

        public static Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
            => RunProcessAsync("gh", arguments, workingDirectory, timeout, cancellationToken);

        private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
                EnableRaisingEvents = true,
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                return new ProcessResult(process.ExitCode, workingDirectory, stdout, stderr);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new ProcessResult(-1, workingDirectory, string.Empty, "gh timed out.");
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string WorkingDirectory, string Stdout, string Stderr);
}
