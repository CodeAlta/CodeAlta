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
    private readonly Func<GitHubPlugin, IEnumerable<PluginPromptEditorContribution>>? _createPromptEditorContributions;

    /// <summary>Initializes a GitHub backend without prompt-editor presentation.</summary>
    public GitHubPlugin()
    {
    }

    /// <summary>Initializes a GitHub backend with explicitly composed prompt-editor contributions.</summary>
    /// <param name="createPromptEditorContributions">A factory receiving this backend when contributions are enumerated.</param>
    /// <remarks>The factory and its sequence are not evaluated during construction. Attachments borrow this backend; runtime initialization and disposal remain owned by the plugin runtime.</remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="createPromptEditorContributions"/> is null.</exception>
    public GitHubPlugin(Func<GitHubPlugin, IEnumerable<PluginPromptEditorContribution>> createPromptEditorContributions)
    {
        ArgumentNullException.ThrowIfNull(createPromptEditorContributions);
        _createPromptEditorContributions = createPromptEditorContributions;
    }

    /// <inheritdoc />
    public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _httpClient = GitHubIssueLookup.CreateHttpClient();
        _ghAvailable = await GitHubCli.TryGetVersionAsync(cancellationToken).ConfigureAwait(false) is not null;

        var token = await GitHubIssueLookup.ResolveTokenAsync(cancellationToken).ConfigureAwait(false);
        if (token is not null)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

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
    public async ValueTask<bool> CanResolveIssueReferencesAsync(string? projectPath, CancellationToken cancellationToken = default)
    {
        var repository = await GitHubIssueLookup.DetectRepositoryAsync(ResolveProjectPath(projectPath), cancellationToken).ConfigureAwait(false);
        return repository is not null;
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
        if (_httpClient is null)
        {
            return null;
        }

        // The picker keeps its uncached behavior: every query detects the repository and asks GitHub again.
        var lookup = new GitHubIssueLookup(_httpClient, new GitHubIssueLookupOptions { CacheDuration = TimeSpan.Zero });
        var repository = await lookup.ResolveRepositoryAsync(ResolveProjectPath(projectPath), cancellationToken).ConfigureAwait(false);
        if (repository is null)
        {
            return null;
        }

        var result = await lookup.QueryAsync(repository, queryText, maximumResults, cancellationToken).ConfigureAwait(false);
        return result.Issues;
    }

    /// <inheritdoc />
    public override IEnumerable<PluginPromptEditorContribution> GetPromptEditorContributions()
    {
        if (_createPromptEditorContributions is null)
        {
            yield break;
        }

        foreach (var contribution in _createPromptEditorContributions(this))
        {
            yield return contribution;
        }
    }

    /// <summary>Gets the selected project path from this backend's runtime workspace.</summary>
    /// <returns>The selected project path, or null when no project is selected.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no runtime context has been attached.</exception>
    public string? GetSelectedProjectPath()
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
                var result = await GitHubCommandLine.RunAsync("gh", ["--version"], Environment.CurrentDirectory, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                return result.ExitCode == 0 ? result.Stdout : null;
            }
            catch
            {
                return null;
            }
        }

        public static Task<GitHubCommandLineResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
            => GitHubCommandLine.RunAsync("gh", arguments, workingDirectory, timeout, cancellationToken);
    }
}
