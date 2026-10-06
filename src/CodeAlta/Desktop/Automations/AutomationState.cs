using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Desktop.Automations;

/// <summary>One time an automation was started, and the session it started.</summary>
/// <param name="Id">The identifier of the run.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="Trigger">What started it: <c>manual</c>, or the kind of the trigger.</param>
internal sealed record AutomationRun(string Id, string AutomationId, DateTimeOffset StartedAt, string Trigger)
{
    /// <summary>The run is in progress.</summary>
    internal const string Running = "running";

    /// <summary>The session answered.</summary>
    internal const string Completed = "completed";

    /// <summary>The run could not start, or its session failed.</summary>
    internal const string Failed = "failed";

    /// <summary>The run was stopped.</summary>
    internal const string Cancelled = "cancelled";

    /// <summary>The application stopped while the run was in progress.</summary>
    internal const string Interrupted = "interrupted";

    /// <summary>A trigger was due while the previous run was still in progress.</summary>
    internal const string Skipped = "skipped";

    /// <summary>The name of the automation when it ran: a run outlives a rename, and its automation.</summary>
    public string? Name { get; init; }

    /// <summary>The session the run started; null when it could not start one.</summary>
    public string? SessionId { get; init; }

    /// <summary>The project the session belongs to; null for a chat.</summary>
    public string? ProjectId { get; init; }

    /// <summary>What the trigger was about, such as the issue that was opened.</summary>
    public string? Detail { get; init; }

    /// <summary>One of the status constants of this type.</summary>
    public string Status { get; init; } = Running;

    /// <summary>Why the run failed or was skipped.</summary>
    public string? Message { get; init; }

    /// <summary>When the run ended.</summary>
    public DateTimeOffset? EndedAt { get; init; }
}

/// <summary>What an event trigger has already seen of a repository.</summary>
internal sealed record AutomationWatermark
{
    /// <summary>Items created before this moment do not start the automation.</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>
    /// Items numbered up to this one do not start the automation either: they were handled, and
    /// <see cref="Seen"/> no longer lists them. A provider numbers its items in the order they are opened.
    /// </summary>
    public long Floor { get; init; }

    /// <summary>The items that already started it, newest last: an item is handled once.</summary>
    public List<string> Seen { get; init; } = [];

    /// <summary>The last commit seen of each open pull request, by its number.</summary>
    public Dictionary<string, string> Heads { get; init; } = [];
}

/// <summary>
/// What this instance of the application remembers of the automations: whether they are paused, when it last
/// looked at the schedules, the runs it started, what the event triggers have seen and which automations of a
/// project the user allowed. It is kept in one file of the instance, apart from the definitions, which are shared
/// with every instance and with the repository.
/// </summary>
internal sealed class AutomationStateStore
{
    /// <summary>The most runs kept for one automation.</summary>
    internal const int MaximumRunsPerAutomation = 100;

    /// <summary>The most runs kept in all.</summary>
    internal const int MaximumRuns = 2000;

    private const int MaximumFileBytes = 8 * 1024 * 1024;
    private const int MaximumSeen = 256;
    private const int MaximumHeads = 512;
    private const int MaximumAllowed = 1024;
    private static readonly TimeSpan AliveSavedEvery = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly string _path;
    private StateFile _state;
    private DateTimeOffset? _aliveSavedAt;

    /// <summary>
    /// Opens the state kept in a file. A file that is missing starts an empty state. A file that is there and
    /// cannot be read starts an empty state too, with the automations paused: what it said is lost, and whether
    /// the user had paused them is part of it.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="pausedByDefault">Whether the automations are paused until the user starts them, when nothing was kept yet.</param>
    internal AutomationStateStore(string path, bool pausedByDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _state = Load(path, out var unreadable) ?? new StateFile { Paused = pausedByDefault || unreadable };
        _aliveSavedAt = _state.AliveAt;
        // What was in progress when the application stopped did not finish.
        for (var index = 0; index < _state.Runs.Count; index++)
        {
            if (_state.Runs[index].Status == AutomationRun.Running)
                _state.Runs[index] = _state.Runs[index] with { Status = AutomationRun.Interrupted, EndedAt = _state.Runs[index].EndedAt ?? _state.AliveAt };
        }
    }

    /// <summary>Gets or sets whether the triggers start nothing.</summary>
    internal bool Paused
    {
        get { lock (_gate) return _state.Paused; }
        set { lock (_gate) { if (_state.Paused == value) return; _state.Paused = value; Save(); } }
    }

    /// <summary>Gets the last moment the schedules were looked at, by this run of the application or an earlier one.</summary>
    internal DateTimeOffset? AliveAt
    {
        get { lock (_gate) return _state.AliveAt; }
    }

    /// <summary>Records that the schedules were looked at. It is written to the file once a minute at most.</summary>
    internal void Alive(DateTimeOffset now)
    {
        lock (_gate)
        {
            _state.AliveAt = now;
            if (_aliveSavedAt is not { } saved || now - saved >= AliveSavedEvery || now < saved) Save();
        }
    }

    /// <summary>Writes what was not written yet: the last moment the schedules were looked at, when the application closes.</summary>
    internal void Flush()
    {
        lock (_gate)
        {
            if (_state.AliveAt != _aliveSavedAt) Save();
        }
    }

    /// <summary>The runs, newest first.</summary>
    /// <param name="automationId">The automation whose runs are asked; null for all.</param>
    /// <param name="limit">The most runs returned.</param>
    internal IReadOnlyList<AutomationRun> Runs(string? automationId, int limit)
    {
        lock (_gate)
        {
            var runs = new List<AutomationRun>();
            for (var index = _state.Runs.Count - 1; index >= 0 && runs.Count < limit; index--)
            {
                if (automationId is null || _state.Runs[index].AutomationId == automationId) runs.Add(_state.Runs[index]);
            }

            return runs;
        }
    }

    /// <summary>The run that started a session, or null.</summary>
    internal AutomationRun? RunOfSession(string sessionId)
    {
        lock (_gate) return _state.Runs.LastOrDefault(run => run.SessionId == sessionId);
    }

    /// <summary>Adds a run, forgetting the oldest ones of its automation beyond what is kept.</summary>
    internal void Add(AutomationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            _state.Runs.Add(run);
            var kept = _state.Runs.Count(candidate => candidate.AutomationId == run.AutomationId);
            for (var index = 0; index < _state.Runs.Count && kept > MaximumRunsPerAutomation; index++)
            {
                if (_state.Runs[index].AutomationId != run.AutomationId || _state.Runs[index].Status == AutomationRun.Running) continue;
                _state.Runs.RemoveAt(index--);
                kept--;
            }

            for (var index = 0; index < _state.Runs.Count && _state.Runs.Count > MaximumRuns; index++)
            {
                if (_state.Runs[index].Status != AutomationRun.Running) _state.Runs.RemoveAt(index--);
            }

            Save();
        }
    }

    /// <summary>Replaces a run by its identifier.</summary>
    /// <returns>The run as it is now, or null when it is no longer kept.</returns>
    internal AutomationRun? Update(string runId, Func<AutomationRun, AutomationRun> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var index = _state.Runs.FindLastIndex(run => run.Id == runId);
            if (index < 0) return null;
            _state.Runs[index] = change(_state.Runs[index]);
            Save();
            return _state.Runs[index];
        }
    }

    /// <summary>Whether the user allowed an automation of a project as it is now.</summary>
    /// <param name="automationId">The automation.</param>
    /// <param name="fingerprint">The fingerprint of its definition: what was allowed is that definition, not a later one.</param>
    internal bool IsAllowed(string automationId, string fingerprint)
    {
        lock (_gate) return _state.Allowed.TryGetValue(automationId, out var allowed) && allowed == fingerprint;
    }

    /// <summary>Records that the user allowed an automation of a project as it is now.</summary>
    internal void Allow(string automationId, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(automationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        lock (_gate)
        {
            if (_state.Allowed.TryGetValue(automationId, out var allowed) && allowed == fingerprint) return;
            // More than anyone allows: what is forgotten is asked again.
            if (_state.Allowed.Count >= MaximumAllowed) _state.Allowed.Clear();
            _state.Allowed[automationId] = fingerprint;
            Save();
        }
    }

    /// <summary>What an event trigger of an automation has seen; null before its first look.</summary>
    internal AutomationWatermark? Watermark(string automationId, string triggerKey)
    {
        lock (_gate) return _state.Watermarks.GetValueOrDefault(automationId + "|" + triggerKey);
    }

    /// <summary>
    /// Records what an event trigger has seen. Only the latest items are kept by their number: the older ones
    /// are remembered as everything up to the highest of them.
    /// </summary>
    internal void SetWatermark(string automationId, string triggerKey, AutomationWatermark watermark)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        lock (_gate)
        {
            if (watermark.Seen.Count > MaximumSeen)
            {
                var dropped = watermark.Seen.Count - MaximumSeen;
                var floor = watermark.Floor;
                foreach (var number in watermark.Seen.Take(dropped))
                {
                    if (long.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) && value > floor) floor = value;
                }

                watermark = watermark with { Floor = floor, Seen = watermark.Seen.GetRange(dropped, MaximumSeen) };
            }

            // The pull requests with the lowest numbers are the oldest ones.
            foreach (var old in watermark.Heads.Keys.OrderByDescending(static key => key.Length).ThenByDescending(static key => key, StringComparer.Ordinal).Skip(MaximumHeads).ToArray())
                watermark.Heads.Remove(old);
            _state.Watermarks[automationId + "|" + triggerKey] = watermark;
            Save();
        }
    }

    /// <summary>Forgets what the event triggers of automations that no longer exist have seen.</summary>
    internal void KeepWatermarks(IReadOnlySet<string> keys)
    {
        lock (_gate)
        {
            var gone = _state.Watermarks.Keys.Where(key => !keys.Contains(key)).ToArray();
            if (gone.Length == 0) return;
            foreach (var key in gone) _state.Watermarks.Remove(key);
            Save();
        }
    }

    private static StateFile? Load(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;
            unreadable = true;
            if (file.Length > MaximumFileBytes) return null;
            using var stream = file.OpenRead();
            if (JsonSerializer.Deserialize(stream, AutomationStateJsonContext.Default.StateFile) is not { Version: 1 } state) return null;
            // A file written by hand, or cut short, may hold nothing where a list is expected.
            state.Runs = [.. (state.Runs ?? []).Where(static run => run is { Id.Length: > 0, AutomationId.Length: > 0, Trigger: not null, Status: not null })];
            state.Watermarks = (state.Watermarks ?? []).Where(static pair => pair.Value is { Seen: not null, Heads: not null }).ToDictionary(static pair => pair.Key, static pair => pair.Value);
            state.Allowed = (state.Allowed ?? []).Where(static pair => !string.IsNullOrEmpty(pair.Value)).ToDictionary(static pair => pair.Key, static pair => pair.Value);
            unreadable = false;
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    // Under the gate. A state that cannot be written is still the state of this run of the application.
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var staged = _path + ".tmp";
            using (var stream = File.Create(staged)) JsonSerializer.Serialize(stream, _state, AutomationStateJsonContext.Default.StateFile);
            File.Move(staged, _path, overwrite: true);
            _aliveSavedAt = _state.AliveAt;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed class StateFile
    {
        public int Version { get; set; } = 1;

        public bool Paused { get; set; }

        public DateTimeOffset? AliveAt { get; set; }

        public List<AutomationRun> Runs { get; set; } = [];

        public Dictionary<string, AutomationWatermark> Watermarks { get; set; } = [];

        /// <summary>The automations of project files the user allowed, each with the fingerprint of what was allowed.</summary>
        public Dictionary<string, string> Allowed { get; set; } = [];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AutomationStateStore.StateFile))]
internal sealed partial class AutomationStateJsonContext : JsonSerializerContext;
