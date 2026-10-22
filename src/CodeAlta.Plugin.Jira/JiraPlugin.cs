using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Jira;

/// <summary>
/// Built-in plugin for projects whose issues are in Jira. A project names its Atlassian site and its Jira
/// project, or several of them, in <c>[jira]</c> of its <c>.alta/issues.toml</c> (see <see cref="JiraSettings"/>); the plugin then lists
/// and reads their issues for the Issues tab, and gives agents the <c>alta jira</c> commands. Jira is reached
/// through the Atlassian CLI (<c>acli</c>), which the plugin downloads the first time a project needs it.
/// </summary>
/// <remarks>
/// The space of a session can give the site, the email and the token variable of its account. The CLI keeps one
/// login for the whole machine: each use of Jira holds the account from the moment it is checked (and switched or
/// signed in) until its commands are done, so that sessions of two spaces never work with each other's account.
/// </remarks>
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
    // Held by one use of Jira at a time: the CLI has one login, which a use of another account would switch.
    private readonly SemaphoreSlim _account = new(1, 1);
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private IJiraCli? _cli;
    private JiraClient? _client;
    private (string Site, string? Email, long Checked)? _ready;

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
        _account.Dispose();
        return base.DisposeAsync();
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, CancellationToken cancellationToken)
        => GetTrackersAsync(projectPath, null, cancellationToken);

    /// <summary>Finds the Jira of the project in a folder, with the account of the space of the session, or of the window.</summary>
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IIssueTracker>> GetTrackersAsync(string projectPath, string? sessionId, CancellationToken cancellationToken)
    {
        if (_client is null || JiraSettings.Read(projectPath, await IdentityAsync(sessionId, cancellationToken).ConfigureAwait(false)) is not { } settings) return [];
        return [new JiraTracker(settings, _client, token => UseAccountAsync(settings, token))];
    }

    /// <inheritdoc />
    public async ValueTask<TrackedEventPage?> ReadEventsAsync(string projectPath, string service, TrackedEventKind kind, TimeSpan window, CancellationToken cancellationToken)
    {
        if (service != "jira" || _client is not { } client) return null;
        // An automation works for no session: with the account of the space its runs go to.
        if (JiraSettings.Read(projectPath, await ProjectIdentityAsync(projectPath, cancellationToken).ConfigureAwait(false)) is not { } settings) return null;
        var page = new TrackedEventPage("Jira", string.Join(", ", settings.Projects), []);
        using var use = await UseAccountAsync(settings, cancellationToken).ConfigureAwait(false);
        if (use.Problem is { } problem) return page with { Problem = problem };
        // Jira reads a date as the account's own time: a number of minutes means the same everywhere.
        var minutes = Math.Clamp((int)Math.Ceiling(window.TotalMinutes) + 1, 2, 7 * 24 * 60);
        var field = kind == TrackedEventKind.Created ? "created" : "updated";
        // A project the account does not see is left out: what happens in the others still starts the automation.
        var (found, _) = await client.SearchAsync(settings, settings.Projects,
            projects => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{JiraClient.Projects(projects)} AND {field} >= -{minutes}m ORDER BY {field} ASC"),
            MaximumEvents, cancellationToken).ConfigureAwait(false);
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
        yield return Prompt.Dynamic(PluginPromptChannel.Developer, async (context, cancellationToken) =>
        {
            var identity = await IdentityAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
            var settings = JiraSettings.Read(context.ProjectPath ?? context.Services.Workspace.SelectedProjectPath, identity);
            return settings is null ? null : Guidance(settings);
        }, title: "Jira", kind: PluginPromptPartKind.ToolGuidance, order: 60);
    }

    /// <summary>
    /// What an agent is told of the Jira of its project: where the issues are, how to read and change them, and,
    /// with several Jira projects, which one a new issue goes to.
    /// </summary>
    internal static string Guidance(JiraSettings settings)
    {
        if (!settings.HasSeveralProjects)
        {
            return $"This project keeps its issues in Jira: project `{settings.Project}` on `{settings.Site}`. Read them with `alta issue list --tracker jira` and "
                   + $"`alta issue show {settings.Project}-<n>`. Change them with `alta jira` (`create`, `comment`, `transition`, `assign`), only when the user asks for it. "
                   + $"An issue key such as `{settings.Project}-12` names an issue of that project.";
        }

        var other = settings.Projects[1];
        return $"This project keeps its issues in Jira on `{settings.Site}`, in several projects: {string.Join(", ", settings.Projects.Select(static project => $"`{project}`"))}. "
               + $"Read them with `alta issue list --tracker jira`, which lists the issues of all these projects together (`--part {other}` for one of them), and `alta issue show <KEY>-<n>`. "
               + "Change them with `alta jira` (`create`, `comment`, `transition`, `assign`), only when the user asks for it. "
               + $"A new issue goes to `{settings.Project}`; it goes to another of these projects only when the user names that project: `alta jira create --jira-project {other}`. "
               + $"The key of an issue starts with the key of its project: `{other}-12` is an issue of `{other}`. A number alone names an issue of `{settings.Project}`.";
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
        if (IsReady(settings) && cli.IsInstalled) return (null, false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady(settings) && cli.IsInstalled) return (null, false);
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

            // The account of the site is the one of the space when the space names it: another one is signed out of.
            if (!account.SignedIn || !SameSite(account.Site, settings.Site) || !SameEmail(account.Email, settings.Email))
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

            _ready = (settings.Site, settings.Email, Environment.TickCount64);
            return (null, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Holds the account of the CLI for one use of Jira, made ready for the site and the account of the settings: the
    /// use runs its commands, then disposes of what this returns, and no other use switches the account meanwhile.
    /// </summary>
    /// <returns>The use, with what is missing when Jira is not ready; it is disposed of in every case.</returns>
    internal async ValueTask<JiraAccountUse> UseAccountAsync(JiraSettings settings, CancellationToken cancellationToken)
    {
        await _account.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (problem, needsSignIn) = await EnsureReadyAsync(settings, cancellationToken).ConfigureAwait(false);
            return new JiraAccountUse(_account, problem, needsSignIn);
        }
        catch
        {
            _account.Release();
            throw;
        }
    }

    /// <summary>Gets the identity of the work of a session, or of the window for null; the usual one outside a host.</summary>
    internal async ValueTask<PluginIdentity> IdentityAsync(string? sessionId, CancellationToken cancellationToken)
    {
        IPluginIdentityService identities;
        try { identities = Services.Identity; }
        catch (InvalidOperationException) { return PluginIdentity.Usual; }
        return await identities.GetIdentityAsync(string.IsNullOrWhiteSpace(sessionId) ? null : sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Signs in through the browser, then checks that the account is the one of the site of the project.</summary>
    internal async Task<string?> SignInWithBrowserAsync(JiraSettings settings, CancellationToken cancellationToken)
    {
        // The login of the CLI changes: no other use of Jira runs meanwhile.
        await _account.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SignInWithBrowserCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _account.Release();
        }
    }

    private async Task<string?> SignInWithBrowserCoreAsync(JiraSettings settings, CancellationToken cancellationToken)
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

    // An account the CLI does not name, or settings that name none, are not told apart by their email.
    private static bool SameEmail(string? account, string? wanted)
        => account is null || wanted is null || string.Equals(account.Trim(), wanted.Trim(), StringComparison.OrdinalIgnoreCase);

    private bool IsReady(JiraSettings settings)
        => _ready is { } ready && ready.Site == settings.Site && SameEmail(ready.Email, settings.Email)
            && Environment.TickCount64 - ready.Checked < AccountLifetime.TotalMilliseconds;

    // The identity of work on a project that no session does; the usual one outside a host.
    private async ValueTask<PluginIdentity> ProjectIdentityAsync(string projectPath, CancellationToken cancellationToken)
    {
        IPluginIdentityService identities;
        try { identities = Services.Identity; }
        catch (InvalidOperationException) { return PluginIdentity.Usual; }
        return await identities.GetProjectIdentityAsync(projectPath, cancellationToken).ConfigureAwait(false);
    }

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
        if (JiraSettings.Read(First(context.ProjectPath, context.Workspace.SelectedProjectPath, context.ScopeProjectPath),
                await IdentityAsync(context.SessionId, cancellationToken).ConfigureAwait(false)) is not { } settings)
            return PluginCommandResult.Message(JiraSettings.Problem(First(context.ProjectPath, context.Workspace.SelectedProjectPath, context.ScopeProjectPath)) ?? JiraSettings.NotConfiguredMessage);
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
        if (JiraSettings.Read(First(context.ProjectPath, context.Workspace.SelectedProjectPath, context.ScopeProjectPath),
                await IdentityAsync(context.SessionId, cancellationToken).ConfigureAwait(false)) is not { } settings)
            return PluginCommandResult.Message(JiraSettings.Problem(First(context.ProjectPath, context.Workspace.SelectedProjectPath, context.ScopeProjectPath)) ?? JiraSettings.NotConfiguredMessage);
        lock (_announced) _announced.Clear();
        Invalidate();
        string? problem;
        using (var use = await UseAccountAsync(settings, cancellationToken).ConfigureAwait(false)) problem = use.Problem;
        var said = problem ?? (settings.HasSeveralProjects
            ? $"Jira is ready: projects {string.Join(", ", settings.Projects)} on {settings.Site}. New issues go to {settings.Project}."
            : $"Jira is ready: project {settings.Project} on {settings.Site}.");
        return PluginCommandResult.Message(settings.Legacy ? said + " " + JiraSettings.LegacyNote : said);
    }
}

/// <summary>
/// One use of the account of the Atlassian CLI (<see cref="JiraPlugin.UseAccountAsync"/>): it holds the account until
/// it is disposed of, and says what is missing when Jira is not ready.
/// </summary>
internal sealed class JiraAccountUse : IDisposable
{
    private SemaphoreSlim? _account;

    internal JiraAccountUse(SemaphoreSlim account, string? problem, bool needsSignIn)
        => (_account, Problem, NeedsSignIn) = (account, problem, needsSignIn);

    /// <summary>Gets what is missing for Jira to be used; null when it is ready.</summary>
    public string? Problem { get; }

    /// <summary>Gets whether signing in is what it takes.</summary>
    public bool NeedsSignIn { get; }

    /// <summary>Lets the next use of Jira run.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _account, null) is not { } account) return;
        try { account.Release(); }
        catch (ObjectDisposedException) { }
    }
}
