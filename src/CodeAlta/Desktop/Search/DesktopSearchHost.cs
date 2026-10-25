using CodeAlta.Catalog;
using CodeAlta.Catalog.Search;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Search;

namespace CodeAlta.Desktop.Search;

/// <summary>
/// What the window tells search: its settings (read again when <c>config.toml</c> changes), the projects of its
/// catalog and the sessions it lists, each kept a few seconds so that a burst of signals reads them once.
/// </summary>
internal sealed class DesktopSearchHost : ISearchHost, IMeaningSettings
{
    private static readonly TimeSpan ListLifetime = TimeSpan.FromSeconds(10);

    private readonly CodeAltaHost _host;
    private readonly CodeAltaConfigStore _config;
    private readonly string _configPath;
    private readonly Lock _gate = new();
    private readonly Lock _update = new();
    private SearchSettings _settings = SearchSettings.Default;
    private (long Length, long Ticks) _settingsStamp = (-1, -1);
    private IReadOnlyList<SearchProjectInfo> _projects = [];
    private DateTimeOffset _projectsAt = DateTimeOffset.MinValue;
    private IReadOnlyDictionary<string, SearchSessionInfo> _sessions = new Dictionary<string, SearchSessionInfo>();
    private DateTimeOffset _sessionsAt = DateTimeOffset.MinValue;
    private readonly Dictionary<string, (bool Excluded, DateTimeOffset At)> _projectExclusions = new(StringComparer.OrdinalIgnoreCase);

    private readonly DesktopSkillUsage? _skillUsage;

    public DesktopSearchHost(CodeAltaHost host, DesktopSkillUsage? skillUsage = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _skillUsage = skillUsage;
        _config = new CodeAltaConfigStore(host.CatalogOptions);
        _configPath = host.CatalogOptions.ConfigPath;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, double>? GetSkillUsage(string? projectId, string spaceId) => _skillUsage?.ScoresFor(projectId, spaceId);

    /// <summary>Raised when the settings read from the file changed.</summary>
    public event Action<SearchSettings, SearchSettings>? SettingsChanged;

    /// <summary>Gets the projects as last read, for the list of sources.</summary>
    public IReadOnlyList<SearchProjectInfo> LastProjects
    {
        get { lock (_gate) return _projects; }
    }

    /// <inheritdoc />
    public SearchSettings GetSettings()
    {
        (long, long) stamp;
        try
        {
            var info = new FileInfo(_configPath);
            stamp = info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (0, 0);
        }
        catch (IOException)
        {
            lock (_gate) return _settings;
        }

        SearchSettings previous, current;
        lock (_gate)
        {
            if (stamp == _settingsStamp) return _settings;
            previous = _settings;
        }

        try
        {
            current = _config.LoadGlobalSearchSettings();
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // A configuration that cannot be read keeps the last settings: search does not turn itself off.
            current = previous;
        }

        lock (_gate)
        {
            _settings = current;
            _settingsStamp = stamp;
        }

        if (!Equals(previous, current) || !previous.ExcludedProjects.SequenceEqual(current.ExcludedProjects) || !previous.ExcludedSpaces.SequenceEqual(current.ExcludedSpaces))
            SettingsChanged?.Invoke(previous, current);
        return current;
    }

    /// <summary>
    /// Changes the settings in one step: they are read and the change is saved under one lock, so that two changes made at
    /// once (the page, the landing card) do not lose one another.
    /// </summary>
    /// <param name="change">Makes the new settings from the current ones.</param>
    /// <returns>The settings saved.</returns>
    public SearchSettings UpdateSettings(Func<SearchSettings, SearchSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_update)
        {
            var saved = change(GetSettings());
            SaveSettings(saved);
            return saved;
        }
    }

    /// <summary>Saves the settings and makes them current.</summary>
    public void SaveSettings(SearchSettings settings)
    {
        _config.SaveGlobalSearchSettings(settings);
        lock (_gate) _settingsStamp = (-1, -1);
        _ = GetSettings();
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SearchProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _projectsAt < ListLifetime) return _projects;
        }

        var projects = await _host.ProjectCatalog.LoadAsync(cancellationToken).ConfigureAwait(false);
        var list = projects.Where(static project => !string.IsNullOrWhiteSpace(project.ProjectPath))
            .Select(static project => new SearchProjectInfo(project.Id, string.IsNullOrWhiteSpace(project.DisplayName) ? project.Name : project.DisplayName, project.ProjectPath, project.Archived)
            {
                Spaces = [.. project.Spaces],
            }).ToList();
        lock (_gate)
        {
            _projects = list;
            _projectsAt = DateTimeOffset.UtcNow;
        }

        return list;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, SearchSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _sessionsAt < ListLifetime) return _sessions;
        }

        var snapshot = await _host.WorkspaceReads.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, SearchSessionInfo>(snapshot.Sessions.Count, StringComparer.Ordinal);
        foreach (var session in snapshot.Sessions)
        {
            snapshot.SessionHeaders.TryGetValue(session.SessionId, out var header);
            // The project the list of sessions shows it in, when the one it records is no project of the catalog.
            var projectId = header is null ? null
                : ProjectCatalog.ResolveSessionProject(header.ProjectRef, header.WorkingDirectory, snapshot.Projects)?.Id ?? header.ProjectRef;
            map[session.SessionId] = new SearchSessionInfo(session.SessionId, SessionRuntimeService.ListedTitle(session, null, null),
                projectId, header?.SpaceId ?? session.SpaceId, session.ViewState?.Archived == true);
        }

        lock (_gate)
        {
            _sessions = map;
            _sessionsAt = DateTimeOffset.UtcNow;
        }

        return map;
    }

    /// <summary>Forgets the sessions read, after a rename or a move, so that the next read sees them.</summary>
    public void InvalidateSessions()
    {
        lock (_gate) _sessionsAt = DateTimeOffset.MinValue;
    }

    /// <inheritdoc />
    public bool IsProjectExcludedByItself(SearchProjectInfo project)
    {
        ArgumentNullException.ThrowIfNull(project);
        lock (_gate)
        {
            if (_projectExclusions.TryGetValue(project.Path, out var known) && DateTimeOffset.UtcNow - known.At < ListLifetime * 3) return known.Excluded;
        }

        var excluded = _config.IsProjectExcludedFromSearch(project.Path);
        lock (_gate) _projectExclusions[project.Path] = (excluded, DateTimeOffset.UtcNow);
        return excluded;
    }
}
