using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Jira;

/// <summary>
/// Built-in plugin for projects whose issues are in Jira. A project names its Atlassian site and its Jira
/// project in <c>[plugins.jira]</c> of its <c>.alta/config.toml</c>; the plugin then lists and reads its issues
/// for the Issues tab, and gives agents the <c>alta jira</c> commands. Jira is reached through the Atlassian
/// CLI (<c>acli</c>), which the plugin downloads the first time a project needs it.
/// </summary>
[Plugin("jira", DisplayName = "Jira", Description = "The issues of a Jira project, for projects that say so in their configuration.")]
public sealed class JiraPlugin : PluginBase, IIssueTrackerSource, IIssueEventSource
{
    /// <summary>The most events one look at Jira reads.</summary>
    internal const int MaximumEvents = 20;

    /// <summary>How long the browser sign-in waits for the user.</summary>
    internal static readonly TimeSpan BrowserPatience = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan AccountLifetime = TimeSpan.FromSeconds(60);
    private readonly Func<string, IJiraCli>? _createCli;
    private readonly Func<string, string?> _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private IJiraCli? _cli;
    private JiraClient? _client;
    private (string Site, long Checked)? _ready;

    /// <summary>Initializes the Jira plugin.</summary>
    public JiraPlugin()
        : this(null, null)
    {
    }

    /// <summary>Initializes the plugin over a CLI and an environment a test gives.</summary>
    internal JiraPlugin(Func<string, IJiraCli>? createCli, Func<string, string?>? environment)
    {
        _createCli = createCli;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>Gets the Jira of the CLI, once the plugin is initialized.</summary>
    internal JiraClient Client => _client ?? throw new InvalidOperationException("The Jira plugin is not initialized.");

    /// <inheritdoc />
    public override ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var cache = Path.Combine(Context.Host.UserDataDirectory, "cache", "jira", "acli");
        _cli = _createCli?.Invoke(cache) ?? new JiraCli(cache);
        _client = new JiraClient(_cli);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        (_cli as IDisposable)?.Dispose();
        _gate.Dispose();
        return base.DisposeAsync();
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, CancellationToken cancellationToken)
    {
        if (_client is null || JiraSettings.Read(projectPath) is not { } settings) return ValueTask.FromResult<IReadOnlyList<IIssueTracker>>([]);
        return ValueTask.FromResult<IReadOnlyList<IIssueTracker>>([new JiraTracker(settings, _client, token => EnsureReadyAsync(settings, token))]);
    }

    /// <inheritdoc />
    public async ValueTask<TrackedEventPage?> ReadEventsAsync(string projectPath, string service, TrackedEventKind kind, TimeSpan window, CancellationToken cancellationToken)
    {
        if (service != "jira" || _client is not { } client || JiraSettings.Read(projectPath) is not { } settings) return null;
        var page = new TrackedEventPage("Jira", settings.Project, []);
        if ((await EnsureReadyAsync(settings, cancellationToken).ConfigureAwait(false)).Problem is { } problem) return page with { Problem = problem };
        // Jira reads a date as the account's own time: a number of minutes means the same everywhere.
        var minutes = Math.Clamp((int)Math.Ceiling(window.TotalMinutes) + 1, 2, 7 * 24 * 60);
        var field = kind == TrackedEventKind.Created ? "created" : "updated";
        var jql = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"project = {JiraClient.Quote(settings.Project)} AND {field} >= -{minutes}m ORDER BY {field} ASC");
        var found = await client.SearchAsync(settings, jql, MaximumEvents, cancellationToken).ConfigureAwait(false);
        if (found.Value is not { } items) return page with { Problem = found.Problem ?? "Jira did not answer." };
        if (kind == TrackedEventKind.Created) return page with { Events = [.. items.Select(static item => new TrackedEvent(item, item.Id))] };

        // A search does not say when an issue changed: each one that did is read for that.
        var events = new List<TrackedEvent>(items.Count);
        foreach (var item in items)
        {
            var read = (await client.ViewAsync(settings, item.Id, cancellationToken).ConfigureAwait(false)).Value?.Item;
            if (read?.UpdatedAt is not { } updated) continue;
            // An issue that was only created has not been updated.
            if (read.CreatedAt is { } created && (updated - created).Duration() < TimeSpan.FromSeconds(5)) continue;
            events.Add(new(read, updated.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return page with { Events = events };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution
        {
            Path = "jira",
            Description = "Set up the Jira of the project and change its issues: sign in, create, comment, move, assign.",
            Policy = new PluginAltaCommandPolicy { RequiresInProcessRuntime = true, IsMutating = true, SupportsCatalogOnlyContext = false },
            CreateCommandNode = context => JiraCommands.Create(this, context),
        };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("jira-sign-in", "Signs in to the Jira of the selected project, in the browser.", SignInCommandAsync) with { Label = "Jira: Sign in" };
        yield return Command.Shell("jira-status", "Says whether the Jira of the selected project is ready.", StatusCommandAsync) with { Label = "Jira: Status" };
    }

    /// <inheritdoc />
    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        yield return Prompt.Dynamic(PluginPromptChannel.Developer, (context, _) =>
        {
            var settings = JiraSettings.Read(context.ProjectPath ?? context.Services.Workspace.SelectedProjectPath);
            return ValueTask.FromResult(settings is null ? null
                : $"This project keeps its issues in Jira: project `{settings.Project}` on `{settings.Site}`. Read them with `alta issue list --tracker jira` and "
                  + $"`alta issue show {settings.Project}-<n>`. Change them with `alta jira` (`create`, `comment`, `transition`, `assign`), only when the user asks for it. "
                  + $"An issue key such as `{settings.Project}-12` names an issue of that project.");
        }, title: "Jira", kind: PluginPromptPartKind.ToolGuidance, order: 60);
    }

    /// <summary>
    /// Makes Jira usable for a project: the CLI is there (downloaded when it is not) and signed in for the site of
    /// the project, with the API token of the environment when the project has one. The user is told what happens
    /// the first time, and why when it cannot be done.
    /// </summary>
    /// <returns>Null when Jira is ready; otherwise what is missing, and whether signing in is what it takes.</returns>
    internal async ValueTask<(string? Problem, bool NeedsSignIn)> EnsureReadyAsync(JiraSettings settings, CancellationToken cancellationToken)
    {
        if (_cli is not { } cli || _client is not { } client) return ("The Jira plugin is not ready.", false);
        if (_ready is { } ready && ready.Site == settings.Site && Environment.TickCount64 - ready.Checked < AccountLifetime.TotalMilliseconds && cli.IsInstalled) return (null, false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_ready is { } known && known.Site == settings.Site && Environment.TickCount64 - known.Checked < AccountLifetime.TotalMilliseconds && cli.IsInstalled) return (null, false);
            var downloaded = false;
            var missing = await cli.EnsureInstalledAsync(() =>
            {
                downloaded = true;
                Notify("Jira: downloading the Atlassian CLI for this project. This takes a moment.");
            }, cancellationToken).ConfigureAwait(false);
            if (missing is not null)
            {
                Announce("cli:" + missing, "Jira: " + missing);
                return (missing, false);
            }

            if (downloaded) Notify("Jira: the Atlassian CLI is installed.");
            var account = await client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
            if (account.SignedIn && !SameSite(account.Site, settings.Site) && await client.SwitchAsync(settings.Site, cancellationToken).ConfigureAwait(false))
            {
                account = await client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!account.SignedIn || !SameSite(account.Site, settings.Site))
            {
                if (Token(settings) is { } token && Email(settings) is { } email)
                {
                    if (await client.SignInWithTokenAsync(settings.Site, email, token, cancellationToken).ConfigureAwait(false) is { } refused)
                    {
                        var text = $"Jira did not accept the API token for {settings.Site}: {refused}";
                        Announce("token:" + settings.Site, text);
                        return (text, true);
                    }

                    Notify($"Jira: signed in to {settings.Site} with the API token.");
                }
                else
                {
                    var text = $"Jira is not signed in for {settings.Site}. Run the command “Jira: Sign in” from the search of the window (Ctrl+P), or set an API token (JIRA_API_TOKEN and JIRA_EMAIL).";
                    Announce("signin:" + settings.Site, text);
                    return (text, true);
                }
            }

            _ready = (settings.Site, Environment.TickCount64);
            return (null, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Signs in through the browser, then checks that the account is the one of the site of the project.</summary>
    internal async Task<string?> SignInWithBrowserAsync(JiraSettings settings, CancellationToken cancellationToken)
    {
        if (_cli is not { } cli || _client is not { } client) return "The Jira plugin is not ready.";
        if (await cli.EnsureInstalledAsync(() => Notify("Jira: downloading the Atlassian CLI. This takes a moment."), cancellationToken).ConfigureAwait(false) is { } missing) return missing;
        if (await client.SignInWithBrowserAsync(BrowserPatience, cancellationToken).ConfigureAwait(false) is { } problem) return problem;
        _ready = null;
        var account = await client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        if (!account.SignedIn) return "The sign-in did not complete.";
        if (!SameSite(account.Site, settings.Site) && !await client.SwitchAsync(settings.Site, cancellationToken).ConfigureAwait(false))
            return $"The account is signed in to {account.Site}, and this project uses {settings.Site}. Sign in again and choose {settings.Site}.";
        _announced.Clear();
        return null;
    }

    /// <summary>Forgets what was checked, so that the next use looks again.</summary>
    internal void Invalidate() => _ready = null;

    /// <summary>Gets the folder of the project a command is about.</summary>
    internal static string? ProjectPath(PluginAltaCommandContext context)
        => First(context.Services.Workspace.SelectedProjectPath, context.ScopeProjectPath, context.WorkingDirectory);

    private static string? First(params ReadOnlySpan<string?> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }

        return null;
    }

    private static bool SameSite(string? left, string right) => string.Equals(JiraSettings.NormalizeSite(left), right, StringComparison.OrdinalIgnoreCase);

    private string? Token(JiraSettings settings)
        => settings.TokenVariable is { } named ? Value(named) : JiraSettings.TokenVariables.Select(Value).FirstOrDefault(static value => value is not null);

    private string? Email(JiraSettings settings) => settings.Email ?? JiraSettings.EmailVariables.Select(Value).FirstOrDefault(static value => value is not null);

    private string? Value(string variable) => _environment(variable) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    // Said once: a list that is read again does not repeat what the user was told.
    private void Announce(string key, string message)
    {
        lock (_announced)
        {
            if (!_announced.Add(key)) return;
        }

        Logger.Warn(message);
        Notify(message);
    }

    private void Notify(string message)
    {
        if (!Ui.HasInteractiveUi) return;
        // A notice that cannot be shown is no reason to fail what Jira was asked.
        _ = Task.Run(async () =>
        {
            try { await Ui.NotifyAsync(message, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException or OperationCanceledException) { }
        });
    }

    private async ValueTask<PluginCommandResult> SignInCommandAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        if (JiraSettings.Read(First(context.ProjectPath, context.Workspace.SelectedProjectPath, context.ScopeProjectPath)) is not { } settings)
            return PluginCommandResult.Message("This project does not use Jira. Add [plugins.jira] with site and project to its .alta/config.toml.");
        // The CLI asks which site to use once the browser is done: it is run in a terminal of the window, where the user
        // answers it. An application without terminals runs it itself, which is enough for an account of one site.
        if (_cli is { } cli && await cli.EnsureInstalledAsync(() => Notify("Jira: downloading the Atlassian CLI. This takes a moment."), cancellationToken).ConfigureAwait(false) is null
            && cli.ExecutablePath is { } executable && First(context.ProjectPath, context.Workspace.SelectedProjectPath, context.ScopeProjectPath) is { } folder)
        {
            var line = OperatingSystem.IsWindows() ? $"& '{executable.Replace("'", "''", StringComparison.Ordinal)}' jira auth login --web" : $"'{executable.Replace("'", "'\\''", StringComparison.Ordinal)}' jira auth login --web";
            try
            {
                var opened = await Services.Alta.InvokeAsync(["terminal", "create", "--cwd", folder, "--title", "Jira sign-in", "--command", line], cancellationToken: cancellationToken).ConfigureAwait(false);
                if (opened.ExitCode == 0)
                {
                    Invalidate();
                    lock (_announced) _announced.Clear();
                    return PluginCommandResult.Message($"Jira: finish the sign-in to {settings.Site} in the terminal that opened, then reload the Issues tab.");
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
            {
            }
        }

        // The browser takes the time the user takes: the command answers at once, and the result is a notice.
        Tasks.Run("jira-sign-in", async token =>
        {
            var problem = await SignInWithBrowserAsync(settings, token).ConfigureAwait(false);
            Notify(problem is null ? $"Jira: signed in to {settings.Site}." : "Jira: " + problem);
        });
        return PluginCommandResult.Message($"Jira: finish the sign-in to {settings.Site} in your browser.");
    }

    private async ValueTask<PluginCommandResult> StatusCommandAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        if (JiraSettings.Read(First(context.ProjectPath, context.Workspace.SelectedProjectPath, context.ScopeProjectPath)) is not { } settings)
            return PluginCommandResult.Message("This project does not use Jira. Add [plugins.jira] with site and project to its .alta/config.toml.");
        lock (_announced) _announced.Clear();
        Invalidate();
        var (problem, _) = await EnsureReadyAsync(settings, cancellationToken).ConfigureAwait(false);
        return PluginCommandResult.Message(problem ?? $"Jira is ready: project {settings.Project} on {settings.Site}.");
    }
}
