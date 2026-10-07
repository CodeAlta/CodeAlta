using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// Built-in plugin for projects hosted on GitHub, GitLab or Azure DevOps: an issue prompt picker, and the
/// provider CLIs (<c>gh</c>, <c>glab</c>, <c>az</c>) as agent tools when they are installed.
/// </summary>
[Plugin("git", DisplayName = "Git", Description = "Adds an issue prompt picker for GitHub, GitLab and Azure DevOps repositories and exposes their CLIs (gh, glab, az) when available.")]
public sealed class GitPlugin : PluginBase, IIssueTrackerSource
{
    private readonly Func<GitPlugin, IEnumerable<PluginPromptEditorContribution>>? _createPromptEditorContributions;
    private readonly GitPluginBackend _backend;
    private IReadOnlyList<KeyValuePair<GitCliTool, GitCliCommand>> _clis = [];
    private HttpClient? _httpClient;
    private GitIssueLookup? _lookup;

    /// <summary>Initializes a Git backend without prompt-editor presentation.</summary>
    public GitPlugin()
        : this(null, GitPluginBackend.Default)
    {
    }

    /// <summary>Initializes a Git backend with explicitly composed prompt-editor contributions.</summary>
    /// <param name="createPromptEditorContributions">A factory receiving this backend when contributions are enumerated.</param>
    /// <remarks>The factory and its sequence are not evaluated during construction. Attachments borrow this backend; runtime initialization and disposal remain owned by the plugin runtime.</remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="createPromptEditorContributions"/> is null.</exception>
    public GitPlugin(Func<GitPlugin, IEnumerable<PluginPromptEditorContribution>> createPromptEditorContributions)
        : this(createPromptEditorContributions ?? throw new ArgumentNullException(nameof(createPromptEditorContributions)), GitPluginBackend.Default)
    {
    }

    internal GitPlugin(Func<GitPlugin, IEnumerable<PluginPromptEditorContribution>>? createPromptEditorContributions, GitPluginBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _createPromptEditorContributions = createPromptEditorContributions;
        _backend = backend;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, CancellationToken cancellationToken)
        => _lookup is { } lookup && await lookup.GetTrackerAsync(projectPath, cancellationToken).ConfigureAwait(false) is { } tracker ? [tracker] : [];

    /// <inheritdoc />
    public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _httpClient = _backend.CreateHttpClient();
        _lookup = new GitIssueLookup(_httpClient, _backend.LookupOptions);
        var clis = new List<KeyValuePair<GitCliTool, GitCliCommand>>(GitCliTool.All.Count);
        foreach (var tool in GitCliTool.All)
        {
            if (_backend.LocateCli(tool.Name) is { } command)
            {
                clis.Add(new(tool, command));
            }
        }

        _clis = clis;
        await ReportMissingCliAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks whether issue prompt lookup can run for the specified project path.
    /// </summary>
    /// <param name="projectPath">The selected project path.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true" /> when the project has a GitHub, GitLab or Azure DevOps remote.</returns>
    public async ValueTask<bool> CanResolveIssueReferencesAsync(string? projectPath, CancellationToken cancellationToken = default)
        => await ResolveIssueRepositoryAsync(projectPath, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Resolves the hosted repository whose issues the prompt lookup queries for the specified project path.
    /// </summary>
    /// <param name="projectPath">The selected project path.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The repository, or <see langword="null" /> when the project has no remote of a supported provider.</returns>
    public ValueTask<GitRepositoryReference?> ResolveIssueRepositoryAsync(string? projectPath, CancellationToken cancellationToken = default)
        => _lookup is { } lookup
            ? lookup.ResolveRepositoryAsync(ResolveProjectPath(projectPath), cancellationToken)
            : GitIssueLookup.DetectRepositoryAsync(ResolveProjectPath(projectPath), cancellationToken);

    /// <summary>
    /// Queries the issues of the project's repository for prompt lookup.
    /// </summary>
    /// <param name="projectPath">The selected project path.</param>
    /// <param name="queryText">The text typed after <c>#</c>.</param>
    /// <param name="maximumResults">The preferred maximum result count.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Issue references, or <see langword="null" /> when the project has no remote of a supported provider.</returns>
    public async ValueTask<IReadOnlyList<GitIssueReferenceItem>?> QueryIssueReferencesAsync(string? projectPath, string queryText, int maximumResults, CancellationToken cancellationToken = default)
    {
        if (_lookup is not { } lookup)
        {
            return null;
        }

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
        foreach (var (tool, command) in _clis)
        {
            yield return new PluginAgentToolContribution
            {
                Definition = new AgentToolDefinition(
                    new AgentToolSpec(tool.Name, tool.Description, CreateToolSchema(tool)),
                    (invocation, cancellationToken) => RunCliToolAsync(tool, command, invocation, cancellationToken)),
                PromptSnippet = tool.PromptSnippet,
                PromptGuidance = tool.PromptGuidance,
                ActivationPolicy = PluginToolActivationPolicy.CodeAltaManagedOnly,
            };
        }
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        _lookup = null;
        _httpClient?.Dispose();
        _httpClient = null;
        return ValueTask.CompletedTask;
    }

    private async Task<AgentToolResult> RunCliToolAsync(GitCliTool tool, GitCliCommand command, AgentToolInvocation invocation, CancellationToken cancellationToken)
    {
        var parse = CliToolArguments.TryParse(tool, invocation.Arguments, out var arguments, out var workingDirectory, out var timeout, out var error);
        if (!parse)
        {
            return Failure(error ?? $"Invalid {tool.Name} tool arguments.");
        }

        var projectPath = ResolveProjectPath(Services.Workspace.SelectedProjectPath);
        var cwd = string.IsNullOrWhiteSpace(workingDirectory)
            ? projectPath ?? Environment.CurrentDirectory
            : Path.GetFullPath(workingDirectory);
        if (!string.IsNullOrWhiteSpace(projectPath) && !IsInside(projectPath, cwd))
        {
            return Failure($"The {tool.Name} workingDirectory must be inside the selected project.");
        }

        var result = await GitCommandLine.RunAsync(command, arguments, cwd, timeout, cancellationToken).ConfigureAwait(false);
        var output = new StringBuilder();
        output.AppendLine(FormattableString.Invariant($"exit_code: {result.ExitCode}"));
        output.AppendLine(FormattableString.Invariant($"working_directory: {result.WorkingDirectory}"));
        AppendBlock(output, "stdout", result.Stdout);
        AppendBlock(output, "stderr", result.Stderr);
        return new AgentToolResult(result.ExitCode == 0, [new AgentToolResultItem.Text(output.ToString())], result.ExitCode == 0 ? null : $"{tool.Name} exited with a non-zero status.");
    }

    // Only the CLI of the provider that hosts the current project is worth installing: a GitHub user is
    // not told about glab or az.
    private async Task ReportMissingCliAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_lookup is not { } lookup ||
                await lookup.ResolveRepositoryAsync(ResolveProjectPath(Services.Workspace.SelectedProjectPath), cancellationToken).ConfigureAwait(false) is not { } repository)
            {
                return;
            }

            var tool = GitCliTool.All.First(candidate => candidate.Provider == repository.Provider);
            if (_clis.Any(cli => cli.Key == tool))
            {
                return;
            }

            Logger.Warn($"{tool.DisplayName} '{tool.Name}' was not found. Install it from {tool.InstallUrl} to enable the {tool.Name} agent tool.");
            var stateName = tool.Name + "-missing-notice.json";
            var state = await Services.State.ReadJsonAsync<MissingCliNoticeState>(PluginStateScope.User, stateName, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            if (state is not null && now - state.LastShownUtc < TimeSpan.FromDays(14))
            {
                return;
            }

            if (Ui.HasInteractiveUi)
            {
                await Ui.NotifyAsync($"{tool.DisplayName} '{tool.Name}' is not installed. Install it to enable the {tool.Name} tool: {tool.InstallUrl}", cancellationToken).ConfigureAwait(false);
            }

            await Services.State.WriteJsonAsync(PluginStateScope.User, stateName, new MissingCliNoticeState(now), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn(ex, "Failed to check the provider CLI of the current project.");
        }
    }

    private static JsonElement CreateToolSchema(GitCliTool tool)
        => JsonDocument.Parse($$"""
            {
              "type": "object",
              "properties": {
                "arguments": {
                  "type": "array",
                  "description": "Arguments to pass to {{tool.Name}}, excluding the {{tool.Name}} executable name.",
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

    private sealed record MissingCliNoticeState(DateTimeOffset LastShownUtc);

    private static class CliToolArguments
    {
        public static bool TryParse(GitCliTool tool, JsonElement json, out IReadOnlyList<string> arguments, out string? workingDirectory, out TimeSpan timeout, out string? error)
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
                    error = $"Every {tool.Name} argument must be a string.";
                    return false;
                }

                values.Add(item.GetString() ?? string.Empty);
            }

            if (values.Count == 0)
            {
                error = $"At least one {tool.Name} argument is required.";
                return false;
            }

            if (tool.CommandGroups is { } groups && !groups.Contains(values[0], StringComparer.Ordinal))
            {
                error = $"The {tool.Name} tool only runs {tool.Provider.GetDisplayName()} commands: the first argument must be one of {string.Join(", ", groups)}.";
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
}

/// <summary>What a <see cref="GitPlugin"/> reaches the machine through; tests replace it with literals.</summary>
/// <param name="LocateCli">Finds a provider CLI by its bare name, or returns null when it is not installed.</param>
/// <param name="CreateHttpClient">Creates the client the plugin owns for the provider REST requests.</param>
/// <param name="LookupOptions">The cache, git, host and credential behavior of the issue lookup.</param>
internal sealed record GitPluginBackend(Func<string, GitCliCommand?> LocateCli, Func<HttpClient> CreateHttpClient, GitIssueLookupOptions LookupOptions)
{
    public static GitPluginBackend Default { get; } = new(
        GitCliLocator.Find,
        GitIssueLookup.CreateHttpClient,
        new GitIssueLookupOptions { CredentialProvider = GitIssueLookup.ResolveCredentialAsync });
}

/// <summary>A provider CLI offered to agents as a tool of the same name.</summary>
/// <param name="Provider">The provider the CLI talks to.</param>
/// <param name="Name">The executable and tool name.</param>
/// <param name="DisplayName">The name of the CLI in messages.</param>
/// <param name="InstallUrl">Where the CLI is installed from.</param>
/// <param name="Description">The tool description given to the model.</param>
/// <param name="PromptSnippet">The line that advertises the tool in the system prompt.</param>
/// <param name="PromptGuidance">How the model should use the tool.</param>
/// <param name="CommandGroups">The only accepted first arguments, or null when any command may run.</param>
internal sealed record GitCliTool(
    GitRemoteProvider Provider,
    string Name,
    string DisplayName,
    string InstallUrl,
    string Description,
    string PromptSnippet,
    string PromptGuidance,
    IReadOnlyList<string>? CommandGroups)
{
    /// <summary>The tools, one per provider.</summary>
    public static IReadOnlyList<GitCliTool> All { get; } =
    [
        new(
            GitRemoteProvider.GitHub, "gh", "GitHub CLI", "https://github.com/cli/cli#installation",
            "Run the GitHub CLI (gh) in the selected project. Use for GitHub repository, issue, PR, release, and workflow operations when the user asks for GitHub interaction.",
            "The GitHub CLI `gh` is available as tool `gh` for repository, issue, pull request, release, and workflow tasks.",
            "Prefer `gh` for GitHub operations in the current repository. Pass arguments as an array; do not include the executable name.",
            null),
        new(
            GitRemoteProvider.GitLab, "glab", "GitLab CLI", "https://gitlab.com/gitlab-org/cli#installation",
            "Run the GitLab CLI (glab) in the selected project. Use for GitLab repository, issue, merge request, release, and CI/CD pipeline operations when the user asks for GitLab interaction.",
            "The GitLab CLI `glab` is available as tool `glab` for repository, issue, merge request, release, and CI/CD pipeline tasks.",
            "Prefer `glab` for GitLab operations in the current repository. Pass arguments as an array; do not include the executable name.",
            null),
        // The Azure CLI manages whole Azure subscriptions: the tool is kept to the command groups of its
        // azure-devops extension.
        new(
            GitRemoteProvider.AzureDevOps, "az", "Azure CLI", "https://learn.microsoft.com/cli/azure/install-azure-cli",
            "Run the Azure DevOps commands of the Azure CLI (az boards, az repos, az pipelines, az artifacts, az devops) in the selected project. Use for Azure DevOps work item, repository, pull request, and pipeline operations when the user asks for Azure DevOps interaction.",
            "The Azure CLI `az` is available as tool `az` for Azure DevOps work item, repository, pull request, and pipeline tasks (`az boards`, `az repos`, `az pipelines`, `az artifacts`, `az devops`).",
            "Prefer `az` for Azure DevOps operations in the current repository; the organization and project are detected from its remote. Pass arguments as an array starting with the command group (for example `boards`); do not include the executable name.",
            ["devops", "boards", "repos", "pipelines", "artifacts"]),
    ];
}
