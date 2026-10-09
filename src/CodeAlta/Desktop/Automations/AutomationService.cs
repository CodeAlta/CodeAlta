using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CodeAlta.Catalog;

namespace CodeAlta.Desktop.Automations;

/// <summary>The automations as they are known at one moment.</summary>
/// <param name="Entries">The automations that were read, by name.</param>
/// <param name="Faults">What could not be read as an automation.</param>
/// <param name="Scanned">Whether the configuration files were read at least once.</param>
internal sealed record AutomationSnapshot(IReadOnlyList<AutomationEntry> Entries, IReadOnlyList<AutomationFault> Faults, bool Scanned)
{
    internal static readonly AutomationSnapshot Empty = new([], [], false);

    /// <summary>The automation with an identifier, or null.</summary>
    internal AutomationEntry? Find(string id) => Entries.FirstOrDefault(entry => entry.Id == id);
}

/// <summary>
/// The automations of the application. It reads them from the configuration of the user and of each project,
/// away from the thread that asks; it runs the ones whose schedule is due, whose event happened in the
/// repository of their project or whose command succeeded; and it keeps what each run started.
/// </summary>
/// <remarks>
/// A schedule is read on the clock of the machine. A time that passed while the application was not running, or
/// while the computer slept, is not run later, unless the automation asks to catch up: it then runs once.
/// <para>
/// The configuration of a project comes with its repository: other people write it. The triggers of an automation
/// kept there start it only once the user allowed that automation here, as it is and where it is: a change of its
/// prompt, model or triggers asks again, and so does the same automation in another folder. What is saved through
/// this application is allowed by that. Running by hand needs no allowance: the user sees what they run. For the
/// same reason a configuration file of a project that is a link is neither read nor written: it may name any file.
/// </para>
/// </remarks>
internal sealed partial class AutomationService : IAsyncDisposable
{
    /// <summary>How late a schedule may be and still run without having asked to catch up.</summary>
    internal static readonly TimeSpan Late = TimeSpan.FromMinutes(2);

    /// <summary>How often the configuration files are looked at again for changes made outside the application.</summary>
    internal static readonly TimeSpan RescanEvery = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(30);
    private static readonly byte[] Utf8Mark = [0xEF, 0xBB, 0xBF];
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const string NoSuchAutomation = "There is no such automation.";

    private readonly Lock _gate = new();
    private readonly string _globalConfigPath;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> _projects;
    private readonly AutomationStateStore _state;
    private readonly IAutomationRunner _runner;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly CancellationTokenSource _stop = new();
    // One change of a configuration file at a time: each reads the file, changes one table and writes it back.
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Dictionary<string, FileRead> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Id, string Trigger), DateTimeOffset?> _due = [];
    private readonly Dictionary<string, int> _running = new(StringComparer.Ordinal);
    private readonly List<Task> _observers = [];
    // The fingerprint of a definition is asked at every look at the schedules: it is computed once for each one read.
    private readonly ConditionalWeakTable<AutomationDefinition, string> _fingerprints = new();
    private readonly DateTimeOffset? _aliveBefore;
    private AutomationSnapshot _snapshot = AutomationSnapshot.Empty;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _scan;
    private bool _scanning;
    private bool _scanAgain;
    private Task? _loop;
    private DateTimeOffset _scannedAt;
    private bool _closed;

    /// <summary>Creates the service. Nothing is read or run before <see cref="Start"/>.</summary>
    /// <param name="globalConfigPath">The configuration file of the user.</param>
    /// <param name="projects">Gives the projects of the application.</param>
    /// <param name="state">What this instance remembers of the automations.</param>
    /// <param name="runner">Starts the session of a run.</param>
    /// <param name="time">The clock.</param>
    /// <param name="zone">The time zone the schedules are read in.</param>
    /// <param name="feed">Reads the repositories for the event triggers, and is disposed with the service; null when they are not watched.</param>
    /// <param name="trackers">Gives the plugins that say what happened in a tracker, as they are active when asked; null when there are none.</param>
    /// <param name="commands">Runs the commands of the command triggers; null when they are not run.</param>
    internal AutomationService(string globalConfigPath, Func<CancellationToken, Task<IReadOnlyList<ProjectDescriptor>>> projects,
        AutomationStateStore state, IAutomationRunner runner, TimeProvider time, TimeZoneInfo zone, IAutomationFeed? feed = null,
        Func<IReadOnlyList<CodeAlta.Plugins.Abstractions.IIssueEventSource>>? trackers = null, IAutomationCommands? commands = null)
    {
        _trackers = trackers;
        _commands = commands;
        ArgumentException.ThrowIfNullOrWhiteSpace(globalConfigPath);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(zone);
        (_globalConfigPath, _projects, _state, _runner, _time, _zone, _feed) = (globalConfigPath, projects, state, runner, time, zone, feed);
        _aliveBefore = state.AliveAt;
    }

    /// <summary>Raised, on any thread, when the automations, their runs or their state changed.</summary>
    internal event Action? Changed;

    /// <summary>Gets the automations as they were last read.</summary>
    internal AutomationSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>Gets or sets whether the triggers start nothing. Running an automation by hand still works.</summary>
    internal bool Paused
    {
        get => _state.Paused;
        set
        {
            if (_state.Paused == value) return;
            _state.Paused = value;
            lock (_gate)
            {
                // What happens in a repository while paused is not an event once resumed.
                _looked.Clear();
                _tags.Clear();
                _lookedAt = null;
            }

            Wake();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Whether the triggers of an automation may start it: always for one kept with the user, and for one kept
    /// with a project once the user allowed it as it is now, in that file.
    /// </summary>
    internal bool IsAllowed(AutomationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Source.IsGlobal || _state.IsAllowed(entry.Id, Allowance(entry));
    }

    /// <summary>Allows the triggers of an automation of a project to start it, as it is now.</summary>
    /// <returns>Whether there is such an automation.</returns>
    internal bool Allow(string id)
    {
        if (Snapshot.Find(id) is not { } entry) return false;
        if (!entry.Source.IsGlobal) _state.Allow(entry.Id, Allowance(entry));
        lock (_gate)
        {
            // Its schedules count from now, its event triggers look at their repository soon, and its commands are started.
            foreach (var key in _due.Keys.Where(key => key.Id == id).ToArray()) _due.Remove(key);
            Reschedule(_time.GetUtcNow(), first: false);
            _lookSoon = true;
        }

        Wake();
        Changed?.Invoke();
        return true;
    }

    // Whether the triggers of an automation start it: it is enabled, can run as defined and is allowed.
    private bool Armed(AutomationEntry entry) => entry is { Definition.Enabled: true, Problem: null } && IsAllowed(entry);

    // What is allowed is what the automation does and the file it is in: the same table in another folder is another one.
    private string Allowance(AutomationEntry entry)
    {
        var content = _fingerprints.GetValue(entry.Definition, static definition => definition.Fingerprint());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content + "\n" + PathKey(entry.Source.FilePath))));
    }

    /// <summary>Starts reading the configuration files and watching the schedules, without waiting for either.</summary>
    internal void Start()
    {
        lock (_gate)
        {
            if (_closed || _loop is not null) return;
            _loop = Task.Run(() => LoopAsync(_stop.Token));
        }

        _ = RefreshAsync();
    }

    /// <summary>Reads the configuration files again. Calls made while a reading is in progress share the next one.</summary>
    /// <returns>A task that completes when what was on disk at the call has been read.</returns>
    internal Task RefreshAsync()
    {
        lock (_gate)
        {
            if (_closed) return Task.CompletedTask;
            if (_scanning)
            {
                _scanAgain = true;
                return _scan!;
            }

            _scanning = true;
            return _scan = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        await ScanAsync(_stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        // Closing, or a reading that failed as a whole: what was read before stays, and the next one tries again.
                        lock (_gate) (_scanning, _scanAgain) = (false, false);
                        return;
                    }

                    // Decided under the gate: a caller that comes after this either joins a reading or starts one.
                    lock (_gate)
                    {
                        if (!_scanAgain)
                        {
                            _scanning = false;
                            return;
                        }

                        _scanAgain = false;
                    }
                }
            });
        }
    }

    /// <summary>The next time a schedule of an automation is due; null without one, when it is disabled or while paused.</summary>
    internal DateTimeOffset? NextDue(string id)
    {
        lock (_gate)
        {
            if (_state.Paused || _snapshot.Find(id) is not { } entry || !Armed(entry)) return null;
            DateTimeOffset? next = null;
            foreach (var (key, due) in _due)
            {
                if (key.Id == id && due is { } time && (next is null || time < next)) next = time;
            }

            return next;
        }
    }

    /// <summary>
    /// The times the schedules are due in the hours to come, soonest first: nothing while paused. The window is cut
    /// in slots and an automation is listed once in a slot, with its first time there: a schedule that is due every
    /// minute then shows as due all along, not as a thousand times.
    /// </summary>
    /// <param name="window">How far ahead to look.</param>
    /// <param name="slots">In how many slots the window is cut.</param>
    /// <param name="maximum">The most times returned.</param>
    internal IReadOnlyList<(string Id, DateTimeOffset At)> Upcoming(TimeSpan window, int slots, int maximum)
    {
        AutomationSnapshot snapshot;
        lock (_gate) snapshot = _snapshot;
        if (_state.Paused || slots < 1) return [];
        var now = _time.GetUtcNow();
        var until = now + window;
        var times = new List<(string Id, DateTimeOffset At)>();
        foreach (var entry in snapshot.Entries)
        {
            if (!Armed(entry)) continue;
            var own = new SortedDictionary<int, DateTimeOffset>();
            foreach (var trigger in entry.Definition.Triggers)
            {
                if (Schedule(trigger) is not { } schedule) continue;
                for (var (at, count) = (schedule.Next(now, _zone), 0); at is { } time && time <= until && count < 2000; (at, count) = (schedule.Next(time, _zone), count + 1))
                {
                    var slot = Math.Min(slots - 1, (int)((time - now).Ticks * slots / window.Ticks));
                    if (!own.TryGetValue(slot, out var first) || time < first) own[slot] = time;
                }
            }

            times.AddRange(own.Values.Select(time => (entry.Id, time)));
        }

        times.Sort(static (left, right) => left.At.CompareTo(right.At));
        return times.Count <= maximum ? times : times.GetRange(0, maximum);
    }

    /// <summary>Whether a run of an automation is in progress.</summary>
    internal bool IsRunning(string id)
    {
        lock (_gate) return _running.GetValueOrDefault(id) > 0;
    }

    /// <summary>The runs, newest first.</summary>
    internal IReadOnlyList<AutomationRun> Runs(string? id, int limit) => _state.Runs(id, limit);

    /// <summary>The run that started a session, or null.</summary>
    internal AutomationRun? RunOfSession(string sessionId) => _state.RunOfSession(sessionId);

    /// <summary>The next times a trigger is due, for the user who writes it.</summary>
    /// <returns>The times, or what is wrong with the trigger.</returns>
    internal (IReadOnlyList<DateTimeOffset> Times, string? Problem) Preview(AutomationTrigger trigger, int count)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        if (!AutomationSchedule.TryCreate(trigger, out var schedule, out var problem)) return ([], problem);
        var times = new List<DateTimeOffset>();
        var after = _time.GetUtcNow();
        while (times.Count < count && schedule!.Next(after, _zone) is { } next)
        {
            times.Add(next);
            after = next;
        }

        return (times, null);
    }

    /// <summary>
    /// Writes an automation in the configuration of the user, or of a project. Nothing is written unless the
    /// file, with the automation in it, reads back with that automation. What is written in the file of a
    /// project is allowed by that: the user, or a session of theirs, wrote it here.
    /// </summary>
    /// <param name="definition">The automation.</param>
    /// <param name="projectId">The project whose configuration holds it; null for the configuration of the user.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>What refused the automation, or null once it is written and read back.</returns>
    internal async Task<string?> SaveAsync(AutomationDefinition definition, string? projectId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition = definition with
        {
            Name = definition.Name.Trim(),
            Prompt = definition.Prompt.ReplaceLineEndings("\n").Trim(),
            // A project's own file runs its automations in that project.
            Project = projectId is null ? definition.Project : null,
        };
        if (AutomationConfig.Validate(definition) is { } invalid) return invalid;
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = await SourceAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (target is null) return "Its project is no longer one of the projects of CodeAlta.";
            var (before, mark) = await ReadTextAsync(target, cancellationToken).ConfigureAwait(false);
            var after = AutomationConfig.Write(before, definition);
            if (ReadBack(after, definition.Id, target) is { } unreadable) return unreadable;
            if (target.IsGlobal && CodeAltaConfigStore.ValidateGlobalConfigContent(after, target.FilePath) is { IsValid: false } refused)
                return "The configuration file would no longer be valid: " + refused.Message;
            await WriteTextAsync(target, after, mark, cancellationToken).ConfigureAwait(false);
            // An automation has one definition: saved in another file, it leaves the one it was in.
            if (Snapshot.Find(definition.Id) is { } previous && !SamePath(previous.Source.FilePath, target.FilePath) && File.Exists(previous.Source.FilePath))
            {
                var (other, otherMark) = await ReadTextAsync(previous.Source, CancellationToken.None).ConfigureAwait(false);
                await WriteTextAsync(previous.Source, AutomationConfig.Remove(other, definition.Id), otherMark, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FirstLine(exception.Message);
        }
        finally
        {
            _writes.Release();
        }

        await RefreshAsync().ConfigureAwait(false);
        // What was read back is what is allowed: the definition as the file now holds it.
        if (projectId is not null) Allow(definition.Id);
        return null;
    }

    /// <summary>
    /// Turns the triggers of an automation on or off, in the file as it is now: what was changed there since it
    /// was last read stays. It does not allow an automation: what it does was not read for that.
    /// </summary>
    /// <returns>What refused, or null.</returns>
    internal async Task<string?> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken)
    {
        if (Snapshot.Find(id) is not { } entry) return NoSuchAutomation;
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (before, mark) = await ReadTextAsync(entry.Source, cancellationToken).ConfigureAwait(false);
            if (AutomationConfig.Read(before, entry.Source).Definitions.FirstOrDefault(definition => definition.Id == id) is not { } current) return NoSuchAutomation;
            if (current.Enabled != enabled)
            {
                var after = AutomationConfig.Write(before, current with { Enabled = enabled });
                if (ReadBack(after, id, entry.Source) is { } unreadable) return unreadable;
                await WriteTextAsync(entry.Source, after, mark, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FirstLine(exception.Message);
        }
        finally
        {
            _writes.Release();
        }

        await RefreshAsync().ConfigureAwait(false);
        return null;
    }

    /// <summary>Removes an automation from its configuration file. Its runs and their sessions stay.</summary>
    /// <returns>What refused, or null.</returns>
    internal async Task<string?> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (Snapshot.Find(id) is not { } entry) return NoSuchAutomation;
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (before, mark) = await ReadTextAsync(entry.Source, cancellationToken).ConfigureAwait(false);
            var after = AutomationConfig.Remove(before, id);
            if (!ReferenceEquals(after, before)) await WriteTextAsync(entry.Source, after, mark, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FirstLine(exception.Message);
        }
        finally
        {
            _writes.Release();
        }

        await RefreshAsync().ConfigureAwait(false);
        return null;
    }

    /// <summary>Runs an automation now, whatever its triggers, and whether or not it is enabled.</summary>
    /// <returns>The run once its session exists or it failed to start; null when there is no such automation.</returns>
    internal async Task<AutomationRun?> RunAsync(string id, CancellationToken cancellationToken)
    {
        if (Snapshot.Find(id) is not { } entry) return null;
        if (entry.Problem is { } problem)
        {
            var refused = new AutomationRun(NewRunId(), id, _time.GetUtcNow(), "manual") { Name = entry.Definition.Name, Status = AutomationRun.Failed, Message = problem, EndedAt = _time.GetUtcNow() };
            _state.Add(refused);
            Changed?.Invoke();
            return refused;
        }

        return await BeginAsync(entry, "manual", null, entry.Definition.Prompt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the schedules, ends the commands of the triggers and waits for what was being read or started.
    /// Sessions that are running keep going.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        List<IAutomationCommand> commands;
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            commands = EndCommands();
            pending = [.. new[] { _loop, _scan, _look }.OfType<Task>(), .. _observers];
        }

        foreach (var command in commands) command.Kill();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // What was being read or started ended badly, or late: closing the application does not wait for it.
        }

        // The next start tells from this what was missed while the application was closed.
        _state.Flush();
        (_feed as IDisposable)?.Dispose();
        _stop.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Task wake;
            lock (_gate) wake = _wake.Task;
            var wait = LongestWait;
            try
            {
                var now = _time.GetUtcNow();
                wait = Tick(now);
                if (UntilLook(now) is { } look)
                {
                    if (look <= TimeSpan.Zero) StartLook(now);
                    else if (look < wait) wait = look;
                }

                if (_time.GetUtcNow() - _scannedAt >= RescanEvery) _ = RefreshAsync();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not OperationCanceledException)
            {
                // One look at the schedules that failed is one look: the next one is not lost with it.
            }

            try
            {
                await Task.WhenAny(wake, Task.Delay(wait, _time, cancellationToken)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (wake.IsCompleted)
            {
                lock (_gate) if (ReferenceEquals(_wake.Task, wake)) _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    /// <summary>
    /// Starts what is due at a moment, starts and ends the commands of the triggers as the automations are now,
    /// and tells how long to wait until something else is due.
    /// </summary>
    internal TimeSpan Tick(DateTimeOffset now)
    {
        var starts = new List<(AutomationEntry Entry, AutomationTrigger Trigger)>();
        DateTimeOffset? next = null;
        lock (_gate)
        {
            _state.Alive(now);
            var paused = _state.Paused;
            foreach (var entry in _snapshot.Entries)
            {
                AutomationTrigger? fire = null;
                foreach (var trigger in entry.Definition.Triggers)
                {
                    var key = (entry.Id, trigger.Key);
                    if (!_due.TryGetValue(key, out var due) || due is not { } time) continue;
                    if (time <= now)
                    {
                        // A time that passed long ago was missed: it runs only for an automation that catches up.
                        if (now - time <= Late || entry.Definition.CatchUp) fire ??= trigger;
                        _due[key] = time = Schedule(trigger)?.Next(now, _zone) ?? DateTimeOffset.MaxValue;
                    }

                    if (next is null || time < next) next = time;
                }

                if (fire is not null && !paused && Armed(entry)) starts.Add((entry, fire));
            }
        }

        foreach (var (entry, trigger) in starts) Trigger(entry, trigger.KindName, null, entry.Definition.Prompt);
        if (LookAtCommands(now) is { } restart && (next is null || restart < next)) next = restart;
        var wait = next is { } due2 ? due2 - now : LongestWait;
        return wait < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : wait > LongestWait ? LongestWait : wait;
    }

    /// <summary>
    /// Starts a run for a trigger, unless the previous run of the automation is still in progress, the automations
    /// were paused meanwhile or the application is closing.
    /// </summary>
    /// <param name="entry">The automation.</param>
    /// <param name="trigger">The kind of the trigger.</param>
    /// <param name="detail">What the trigger was about.</param>
    /// <param name="prompt">What is sent to the session.</param>
    /// <param name="waits">
    /// Whether what triggers can wait for the run in progress to end, as an event does: nothing is then recorded.
    /// A time that is due does not wait: it is recorded as skipped.
    /// </param>
    /// <returns>Whether a run was started.</returns>
    internal bool Trigger(AutomationEntry entry, string trigger, string? detail, string prompt, bool waits = false)
    {
        bool busy;
        lock (_gate)
        {
            if (_closed) return false;
            busy = _running.GetValueOrDefault(entry.Id) > 0;
        }

        // A look at a repository that was in progress when the user paused starts nothing more.
        if (_state.Paused || busy && waits) return false;
        if (busy)
        {
            var now = _time.GetUtcNow();
            _state.Add(new AutomationRun(NewRunId(), entry.Id, now, trigger)
            {
                Name = entry.Definition.Name, ProjectId = entry.ProjectId, Detail = detail, Status = AutomationRun.Skipped,
                Message = "The previous run was still in progress.", EndedAt = now,
            });
            Changed?.Invoke();
            return false;
        }

        var start = BeginAsync(entry, trigger, detail, prompt, _stop.Token);
        lock (_gate)
        {
            _observers.RemoveAll(static task => task.IsCompleted);
            _observers.Add(start);
        }

        return true;
    }

    // Records the run, starts its session and returns once the session exists; what follows is observed apart.
    private async Task<AutomationRun?> BeginAsync(AutomationEntry entry, string trigger, string? detail, string prompt, CancellationToken cancellationToken)
    {
        var run = new AutomationRun(NewRunId(), entry.Id, _time.GetUtcNow(), trigger) { Name = entry.Definition.Name, ProjectId = entry.ProjectId, Detail = detail };
        lock (_gate)
        {
            if (_closed) return null;
            _running[entry.Id] = _running.GetValueOrDefault(entry.Id) + 1;
        }

        _state.Add(run);
        Changed?.Invoke();
        AutomationStart start;
        try
        {
            start = await _runner.StartAsync(entry, run.Id, prompt, detail, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            start = new(null, exception is OperationCanceledException ? "The run was cancelled before it started." : FirstLine(exception.Message), null);
        }

        if (start.Completion is null)
            return End(run.Id, entry.Id, new(AutomationRun.Failed, start.Problem ?? "The run did not start."), start.SessionId);
        var started = _state.Update(run.Id, current => current with { SessionId = start.SessionId }) ?? run with { SessionId = start.SessionId };
        Changed?.Invoke();
        var observer = ObserveAsync(run.Id, entry.Id, start.Completion);
        lock (_gate)
        {
            _observers.RemoveAll(static task => task.IsCompleted);
            _observers.Add(observer);
        }

        return started;
    }

    private async Task ObserveAsync(string runId, string automationId, Task<AutomationOutcome> completion)
    {
        AutomationOutcome outcome;
        try
        {
            outcome = await completion.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The application is closing: the run is marked as interrupted when its state is opened again.
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome = new(AutomationRun.Failed, FirstLine(exception.Message));
        }

        End(runId, automationId, outcome, null);
    }

    private AutomationRun? End(string runId, string automationId, AutomationOutcome outcome, string? sessionId)
    {
        bool waiting;
        lock (_gate)
        {
            var count = _running.GetValueOrDefault(automationId) - 1;
            if (count > 0) _running[automationId] = count;
            else _running.Remove(automationId);
            // An event that waited for this run is looked for again soon.
            waiting = _waiting;
            _lookSoon |= waiting;
            // A command that succeeded meanwhile starts its run at the next look.
            waiting |= HoldsCommand(automationId);
        }

        var ended = _state.Update(runId, run => run with { Status = outcome.Status, Message = outcome.Message, EndedAt = _time.GetUtcNow(), SessionId = sessionId ?? run.SessionId });
        if (waiting) Wake();
        Changed?.Invoke();
        return ended;
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var faults = new List<AutomationFault>();
        IReadOnlyList<ProjectDescriptor> projects;
        try
        {
            projects = await _projects(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            projects = [];
            faults.Add(new(new(_globalConfigPath, null, null), string.Empty, "The projects could not be read: " + FirstLine(exception.Message)));
        }

        var live = projects.Where(static project => !project.Archived && !string.IsNullOrWhiteSpace(project.ProjectPath)).ToArray();
        AutomationSource[] sources =
        [
            new(_globalConfigPath, null, null),
            .. live.Select(static project => new AutomationSource(ConfigPath(project.ProjectPath), project.Id, project.ProjectPath)),
        ];
        var reads = new FileRead[sources.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, sources.Length), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (index, token) => reads[index] = await ReadAsync(sources[index], token).ConfigureAwait(false)).ConfigureAwait(false);

        var entries = new List<AutomationEntry>();
        var places = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < sources.Length; index++)
        {
            var source = sources[index];
            faults.AddRange(reads[index].Faults);
            foreach (var definition in reads[index].Definitions)
            {
                AutomationEntry entry;
                if (!source.IsGlobal) entry = new(definition, source, source.ProjectId, source.ProjectPath, null);
                else if (definition.Project is null) entry = new(definition, source, null, null, null);
                else if (live.FirstOrDefault(project => SamePath(project.ProjectPath, definition.Project)) is { } project) entry = new(definition, source, project.Id, project.ProjectPath, null);
                else entry = new(definition, source, null, null, $"'{definition.Project}' is not the folder of a project of CodeAlta.");

                if (!places.TryGetValue(definition.Id, out var place))
                {
                    places[definition.Id] = entries.Count;
                    entries.Add(entry);
                }
                else if (!IsAllowed(entries[place]) && IsAllowed(entry))
                {
                    // Two files hold the same identifier: the one the user allowed is the automation.
                    faults.Add(new(entries[place].Source, definition.Id, $"The automation '{entries[place].Definition.Name}' has the identifier of another one, which is the one that was allowed."));
                    entries[place] = entry;
                }
                else
                {
                    faults.Add(new(source, definition.Id, $"The automation '{definition.Name}' has the identifier of another one, read before it."));
                }
            }
        }

        entries.Sort(static (left, right) => string.Compare(left.Definition.Name, right.Definition.Name, StringComparison.CurrentCultureIgnoreCase) is var order and not 0
            ? order : string.CompareOrdinal(left.Id, right.Id));
        bool changed;
        lock (_gate)
        {
            foreach (var gone in _files.Keys.Where(path => !sources.Any(source => SamePath(source.FilePath, path))).ToArray()) _files.Remove(gone);
            var first = !_snapshot.Scanned;
            changed = first || !Same(_snapshot, entries, faults);
            _snapshot = new(entries, faults, true);
            _scannedAt = now;
            Reschedule(now, first);
            // A trigger that was just written watches from now, not from the next look.
            _lookSoon |= changed;
        }

        // A file that could not be read says nothing of its automations: what their triggers have seen is kept for
        // when it is read again.
        if (!faults.Any(static fault => fault.Key.Length == 0))
            _state.KeepWatermarks(entries.SelectMany(static entry => entry.Definition.Triggers.Select(trigger => entry.Id + "|" + trigger.Key)).ToHashSet(StringComparer.Ordinal));
        Wake();
        if (changed) Changed?.Invoke();
    }

    // Under the gate. A schedule already waited for keeps its time; a new one starts from now, or, at the start of
    // the application and for an automation that catches up, from when the schedules were last looked at.
    private void Reschedule(DateTimeOffset now, bool first)
    {
        var keys = new HashSet<(string, string)>();
        foreach (var entry in _snapshot.Entries)
        {
            foreach (var trigger in entry.Definition.Triggers)
            {
                if (Schedule(trigger) is not { } schedule) continue;
                var key = (entry.Id, trigger.Key);
                keys.Add(key);
                if (_due.ContainsKey(key)) continue;
                var from = first && entry.Definition.CatchUp && _aliveBefore is { } alive && alive < now ? alive : now;
                _due[key] = schedule.Next(from, _zone);
            }
        }

        foreach (var key in _due.Keys.Where(key => !keys.Contains(key)).ToArray()) _due.Remove(key);
    }

    private async ValueTask<FileRead> ReadAsync(AutomationSource source, CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(source.FilePath);
            if (!file.Exists)
            {
                lock (_gate) _files.Remove(source.FilePath);
                return FileRead.None;
            }

            if (LinkProblem(source) is { } link) return new(default, source, [], [new(source, string.Empty, link)]);
            var stamp = (file.Length, file.LastWriteTimeUtc);
            lock (_gate)
            {
                if (_files.TryGetValue(source.FilePath, out var known) && known.Stamp == stamp && known.Source == source) return known;
            }

            FileRead read;
            if (file.Length > MaximumFileBytes)
            {
                read = new(stamp, source, [], [new(source, string.Empty, "The configuration file is too large to read its automations.")]);
            }
            else
            {
                var (definitions, faults) = AutomationConfig.Read(await File.ReadAllTextAsync(source.FilePath, cancellationToken).ConfigureAwait(false), source);
                read = new(stamp, source, definitions, faults);
            }

            lock (_gate) _files[source.FilePath] = read;
            return read;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // Whatever a file holds, reading it fails for that file only.
            return new(default, source, [], [new(source, string.Empty, "The configuration file could not be read: " + FirstLine(exception.Message))]);
        }
    }

    private async Task<AutomationSource?> SourceAsync(string? projectId, CancellationToken cancellationToken)
    {
        if (projectId is null) return new(_globalConfigPath, null, null);
        var projects = await _projects(cancellationToken).ConfigureAwait(false);
        return projects.FirstOrDefault(project => project.Id == projectId && !project.Archived) is { } found && Directory.Exists(found.ProjectPath)
            ? new(ConfigPath(found.ProjectPath), found.Id, found.ProjectPath) : null;
    }

    // What makes the file of a project one that is neither read nor written: it, or its folder, is a link. The
    // repository gives that link, and it may name any file of the user.
    private static string? LinkProblem(AutomationSource source)
    {
        if (source.IsGlobal) return null;
        var file = new FileInfo(source.FilePath);
        return file.LinkTarget is not null || file.Directory is { Exists: true, LinkTarget: not null }
            ? "The configuration file of the project is a link: its automations are not read, and it is not written."
            : null;
    }

    // The text of a file and whether it starts with the mark of UTF-8, which is written back with it.
    private static async Task<(string Text, bool Mark)> ReadTextAsync(AutomationSource source, CancellationToken cancellationToken)
    {
        if (LinkProblem(source) is { } link) throw new InvalidDataException(link);
        if (!File.Exists(source.FilePath)) return (string.Empty, false);
        var bytes = await File.ReadAllBytesAsync(source.FilePath, cancellationToken).ConfigureAwait(false);
        var mark = bytes.AsSpan().StartsWith(Utf8Mark);
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes, mark ? Utf8Mark.Length : 0, bytes.Length - (mark ? Utf8Mark.Length : 0)), mark);
    }

    // Staged beside the file under a name nothing else has, then moved over it: a reader sees the old text or the
    // new one, and nothing that was put there under a name known in advance is written through.
    private static async Task WriteTextAsync(AutomationSource source, string content, bool mark, CancellationToken cancellationToken)
    {
        if (LinkProblem(source) is { } link) throw new InvalidDataException(link);
        var path = source.FilePath;
        // The user's own file may be a link to where they keep their settings: the file it names is the one replaced.
        if (new FileInfo(path) is { Exists: true, LinkTarget: not null } linked && linked.ResolveLinkTarget(returnFinalTarget: true) is { } final) path = final.FullName;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var staged = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                if (mark) await stream.WriteAsync(Utf8Mark, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content), cancellationToken).ConfigureAwait(false);
            }

            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(staged)) File.Delete(staged);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // What keeps the text of a file from being written: with it, the automation would not be read back.
    private static string? ReadBack(string content, string id, AutomationSource source)
    {
        var (definitions, faults) = AutomationConfig.Read(content, source);
        if (definitions.Any(definition => definition.Id == id)) return null;
        var fault = faults.FirstOrDefault(fault => fault.Key == id) ?? faults.FirstOrDefault(static fault => fault.Key.Length == 0);
        return "The automation would not be read back from its file: " + (fault?.Message ?? "the file holds its automations in a form that cannot be added to.");
    }

    private void Wake()
    {
        lock (_gate) _wake.TrySetResult();
    }

    private static AutomationSchedule? Schedule(AutomationTrigger trigger)
        => AutomationSchedule.TryCreate(trigger, out var schedule, out _) ? schedule : null;

    private static bool Same(AutomationSnapshot snapshot, List<AutomationEntry> entries, List<AutomationFault> faults)
        => snapshot.Faults.SequenceEqual(faults) && snapshot.Entries.Count == entries.Count && snapshot.Entries.Zip(entries).All(static pair =>
            pair.First with { Definition = pair.Second.Definition } == pair.Second
            && pair.First.Definition with { Triggers = pair.Second.Definition.Triggers } == pair.Second.Definition
            && pair.First.Definition.Triggers.Select(static trigger => trigger.Key).SequenceEqual(pair.Second.Definition.Triggers.Select(static trigger => trigger.Key)));

    private static string ConfigPath(string projectPath) => Path.Combine(projectPath, ".alta", "config.toml");

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NewRunId() => Guid.CreateVersion7().ToString("N");

    private static string FirstLine(string message)
    {
        var line = message.ReplaceLineEndings("\n").Split('\n')[0].Trim();
        return line.Length <= 300 ? line : line[..300];
    }

    private sealed record FileRead((long Length, DateTime Written) Stamp, AutomationSource? Source, IReadOnlyList<AutomationDefinition> Definitions, IReadOnlyList<AutomationFault> Faults)
    {
        internal static readonly FileRead None = new(default, null, [], []);
    }
}
