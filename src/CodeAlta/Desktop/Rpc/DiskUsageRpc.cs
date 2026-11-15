using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Storage;
using CodeAlta.Catalog.Worktrees;
using CodeAlta.Core.IO;
using CodeAlta.Core.Processes;
using CodeAlta.Orchestration.Runtime;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The page's side of the disk usage: it measures every project of the catalog with its checkouts and its sessions, and
/// keeps the last complete measurement. The CodeAlta profile is not measured here: it has a view of its own (the storage
/// service). The one request that deletes is <see cref="CleanAsync"/>: build
/// folders of one checkout, confirmed against the measure of this run that the page showed.
/// </summary>
/// <remarks>
/// <para>
/// One scan runs at a time, on a worker the service owns and awaits when it is disposed; a clean is refused while it runs,
/// and a scan asked during a clean waits for it. A request while one runs joins
/// it: it receives the records the scan already produced, then the rest. The scan stops when the last request that
/// follows it goes away before it is kept; a stopped scan never replaces the last measurement. A worker that fails ends
/// every listener with a <c>failed</c> record.
/// </para>
/// <para>
/// Folders are compared physically, with the case rule of their volume (<see cref="CheckoutPaths"/>): a project named
/// through a link is the folder git lists. The projects that share one repository (the sub-projects of a monorepo, a
/// project inside another, a project that is a worktree of another) are measured once: the first of them carries the
/// checkouts, the others name it. A checkout gets its sessions, what protects it and its last use from the same join as
/// the window of the worktrees (<see cref="CheckoutSessions"/>).
/// </para>
/// <para>
/// Sizes are exclusive: a checkout is measured without the other checkouts and project folders of the scan that lie
/// inside it, and without the profile; the <c>done</c> record carries one total that counts every byte once.
/// </para>
/// </remarks>
[NeoRpcService("diskUsage", Version = 1)]
internal sealed class DiskUsageService : IAsyncDisposable
{
    /// <summary>The checkouts measured at the same time, across every project of a scan.</summary>
    internal const int MaximumParallelCheckouts = 3;

    /// <summary>The checkouts a project record holds at most; it says when git lists more.</summary>
    internal const int MaximumCheckouts = WorktreesService.MaximumCheckouts;

    /// <summary>The folders a clean would remove that a checkout lists at most; it says when there are more.</summary>
    internal const int MaximumPlannedEntries = 100;

    /// <summary>The sessions a project record names at most, the largest first; it counts them all.</summary>
    internal const int MaximumSessionsPerProject = 50;

    /// <summary>The version of the file of the last measurement; a file of another version is ignored (version 1 held the profile).</summary>
    internal const int FileVersion = 2;

    /// <summary>The name of the file of the last measurement, in the <c>cache</c> folder of the state root of the instance.</summary>
    internal const string FileName = "disk-usage.json";

    /// <summary>The largest file of the last measurement that is read: a larger one is not one this service wrote.</summary>
    internal const long MaximumFileBytesDefault = 64L * 1024 * 1024;

    private const int MaximumProblemLength = 600;

    // The longest path a clean is confirmed for.
    private const int MaximumConfirmedPathLength = 4096;

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly CatalogOptions? _options;
    private readonly GitWorktreeService? _worktrees;
    private readonly Func<IReadOnlyList<SessionWorkFolder>>? _busy;
    private readonly Func<IReadOnlyList<SessionWorkFolder>>? _working;
    private readonly Func<CancellationToken, Task<OwnedWorkspaceSnapshot>>? _workspace;
    private readonly string? _epoch;
    private readonly CheckoutUsageScanner? _checkouts;
    private readonly GitCheckoutStateReader? _states;
    private readonly BuildOutputCleaner? _cleaner;
    private readonly Lock _gate = new();
    private DiskUsageBroadcast? _running;
    private DiskUsageMeasurement? _last;
    // The measurement the file kept, while it is the last one: its checkouts have no measure of this run to clean against.
    private DiskUsageMeasurement? _fromFile;
    private IReadOnlyDictionary<string, KeptMeasure> _lastMeasures = new Dictionary<string, KeptMeasure>();
    private bool _fileRead;
    private bool _disposed;
    // The clean that runs; null when none does. One at a time, and never while a scan runs.
    private Task? _cleaning;
    // The one drain of the disposal, which every call awaits.
    private TaskCompletionSource? _disposal;

    /// <summary>Creates an unavailable service for launches without an owned host.</summary>
    internal DiskUsageService()
    {
    }

    /// <summary>Creates the service for an owned host.</summary>
    /// <param name="options">The options of the instance: its profile, its sessions folder and the root of its own state.</param>
    /// <param name="worktrees">The worktrees of the host: what git lists for a repository.</param>
    /// <param name="busy">The sessions that are at work, each with the folder it works in, as the window of the worktrees counts them.</param>
    /// <param name="working">Every folder a session works in now, whatever the work: what keeps a clean from going on.</param>
    /// <param name="workspace">Reads the projects of the catalog with the sessions and what each records of its project.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    internal DiskUsageService(CatalogOptions options, GitWorktreeService worktrees, Func<IReadOnlyList<SessionWorkFolder>> busy,
        Func<IReadOnlyList<SessionWorkFolder>> working, Func<CancellationToken, Task<OwnedWorkspaceSnapshot>> workspace, string epoch)
        : this(options, worktrees, busy, working, workspace, epoch, null)
    {
    }

    /// <summary>Creates the service for an owned host, measuring checkouts with the git <paramref name="run"/> starts.</summary>
    /// <param name="options">The options of the instance: its profile, its sessions folder and the root of its own state.</param>
    /// <param name="worktrees">The worktrees of the host: what git lists for a repository.</param>
    /// <param name="busy">The sessions that are at work, each with the folder it works in, as the window of the worktrees counts them.</param>
    /// <param name="working">Every folder a session works in now, whatever the work: what keeps a clean from going on.</param>
    /// <param name="workspace">Reads the projects of the catalog with the sessions and what each records of its project.</param>
    /// <param name="epoch">The host epoch that requests must name.</param>
    /// <param name="run">Runs git for the measure and the state of a checkout; null runs the git on the path.</param>
    internal DiskUsageService(CatalogOptions options, GitWorktreeService worktrees, Func<IReadOnlyList<SessionWorkFolder>> busy,
        Func<IReadOnlyList<SessionWorkFolder>> working, Func<CancellationToken, Task<OwnedWorkspaceSnapshot>> workspace, string epoch, GitRunner? run)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(worktrees);
        ArgumentNullException.ThrowIfNull(busy);
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        (_options, _worktrees, _busy, _working, _workspace, _epoch) = (options, worktrees, busy, working, workspace, epoch);
        _checkouts = new CheckoutUsageScanner(run);
        _states = new GitCheckoutStateReader(run);
        _cleaner = new BuildOutputCleaner(run);
    }

    /// <summary>
    /// The paths of the running application: its executable, and the folder the managed application runs from (on macOS
    /// the bundle's executable starts the files of the installed version, through a link). A checkout that holds one of
    /// them, physically, is the one the application was started from.
    /// </summary>
    internal IReadOnlyList<string?> ApplicationPaths { get; init; } = [Environment.ProcessPath, AppContext.BaseDirectory];

    /// <summary>Lists the executables of the programs that run now: a clean of a checkout that holds one is refused.</summary>
    internal Func<IReadOnlyCollection<string>> RunningExecutables { get; init; } = ProcessExecutables.List;

    /// <summary>Told each stage of a scan (<c>commit</c> right before it is decided to keep it, <c>done</c> after its end is given) and awaited: the tests hold a scan there.</summary>
    internal Func<string, CancellationToken, Task>? Stage { get; init; }

    /// <summary>The checkouts a project record holds at most; <see cref="MaximumCheckouts"/> but in the tests.</summary>
    internal int CheckoutBound { get; init; } = MaximumCheckouts;

    /// <summary>The largest file of the last measurement that is read.</summary>
    internal long MaximumFileBytes { get; init; } = MaximumFileBytesDefault;

    /// <summary>The worker of the scan that runs or ran last; null before the first.</summary>
    internal Task? Worker
    {
        get { lock (_gate) return _running?.Worker; }
    }

    /// <summary>The clean that runs; null when none does.</summary>
    internal Task? Cleaning
    {
        get { lock (_gate) return _cleaning; }
    }

    /// <summary>The file of the last measurement, under the state root of the instance (a developer instance has its own).</summary>
    internal string? StatePath => _options is null ? null : Path.Combine(Path.GetFullPath(_options.StateRoot), "cache", FileName);

    /// <summary>
    /// The measures of the checkouts of the last complete scan, by the full path of each checkout: what a clean of build
    /// output is confirmed against. Empty until a scan completed in this run.
    /// </summary>
    internal IReadOnlyDictionary<string, KeptMeasure> LastMeasures
    {
        get { lock (_gate) return _lastMeasures; }
    }

    /// <summary>
    /// Stops the scan that runs and awaits its worker: what it measured is not kept, and no file is written afterwards. A
    /// clean that runs is awaited, not stopped. Later requests are answered with <c>unavailable</c>.
    /// </summary>
    /// <returns>When the worker and the clean ended.</returns>
    public async ValueTask DisposeAsync()
    {
        DiskUsageBroadcast? running = null;
        Task? cleaning = null;
        TaskCompletionSource drained;
        var drains = false;
        lock (_gate)
        {
            if (_disposal is not null) drained = _disposal;
            else
            {
                _disposed = true;
                _disposal = drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                (running, cleaning, drains) = (_running, _cleaning, true);
            }
        }

        if (drains)
        {
            // Outside the lock: the worker and the clean take it as they end. The worker ends every listener itself and
            // never throws; a clean that runs is not stopped, it is awaited.
            running?.Stop();
            if (running?.Worker is { } worker) await worker.ConfigureAwait(false);
            if (cleaning is not null) await cleaning.ConfigureAwait(false);
            drained.SetResult();
        }

        await drained.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Measures every project of the catalog, or joins the measurement that runs. Streams a <c>progress</c> record a few
    /// times a second, a <c>project</c> record as each project is measured, and a final <c>done</c> record, or a
    /// <c>failed</c> one. Closing the last stream before the
    /// measurement is kept stops it.
    /// </summary>
    [NeoRpcMethod("scan")]
    public NeoRpcChannel<DiskUsageScanEvent> Scan(DiskUsageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(ScanAsync(request, cancellationToken), DesktopJsonContext.Default.DiskUsageScanEvent);
    }

    internal async IAsyncEnumerable<DiskUsageScanEvent> ScanAsync(DiskUsageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused)
        {
            yield return new(refused, null);
            yield break;
        }

        if (Follow() is not var (scan, listener))
        {
            yield return new("unavailable", null);
            yield break;
        }

        try
        {
            await foreach (var update in listener.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return update;
        }
        finally
        {
            scan.Leave(listener);
        }
    }

    /// <summary>
    /// Gives the last complete measurement, with the time it ended: the one of this run, else the one the file of the
    /// instance kept when it is whole. None when there is neither.
    /// </summary>
    [NeoRpcMethod("last")]
    public async Task<DiskUsageLastResponse> LastAsync(DiskUsageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return new(refused, null, false);
        bool read;
        lock (_gate) read = _fileRead || _last is not null;
        if (!read)
        {
            var kept = await ReadFileAsync(StatePath!, MaximumFileBytes, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                // A scan that completed meanwhile is newer than the file.
                if (_last is null) _last = _fromFile = kept;
                _fileRead = true;
            }
        }

        lock (_gate) return new("ok", _last, _running is { Ended: false }) { Current = _last is not null && !ReferenceEquals(_last, _fromFile) };
    }

    /// <summary>
    /// Removes build folders of one checkout that the user confirmed, and streams how far it is: a <c>running</c> record a
    /// few times a second at most, then one <c>done</c> record with the report (<see cref="CleanAsync"/>). A listener that
    /// leaves does not stop the clean: a clean may outlast a request.
    /// </summary>
    /// <param name="request">The checkout, the measure the user saw and the paths they confirmed.</param>
    /// <param name="cancellationToken">Ends the stream, not the clean.</param>
    /// <returns>The records of the clean.</returns>
    [NeoRpcMethod("clean")]
    public NeoRpcChannel<DiskUsageCleanEvent> Clean(DiskUsageCleanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new(CleanStreamAsync(request, cancellationToken), DesktopJsonContext.Default.DiskUsageCleanEvent);
    }

    internal async IAsyncEnumerable<DiskUsageCleanEvent> CleanStreamAsync(DiskUsageCleanRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // The latest report only: a listener that does not read holds one.
        var reports = System.Threading.Channels.Channel.CreateBounded<CleanProgress>(new System.Threading.Channels.BoundedChannelOptions(1)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest, SingleReader = true,
        });
        // Not the token of the listener: the clean goes on when it leaves.
        var cleaning = CleanAsync(request, new Reported(progress => reports.Writer.TryWrite(progress)));
        var clock = Stopwatch.StartNew();
        // The first report goes at once.
        var published = -ProgressInterval;
        while (!cleaning.IsCompleted)
        {
            var reading = reports.Reader.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(cleaning, reading).ConfigureAwait(false);
            if (reading.IsCompleted) await reading.ConfigureAwait(false);
            if (cleaning.IsCompleted) break;
            if (!reports.Reader.TryRead(out var progress)) continue;
            // A few records a second at most: the next one waits for its time, or for the end.
            var wait = published + ProgressInterval - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.WhenAny(cleaning, Task.Delay(wait, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (cleaning.IsCompleted) break;
                if (reports.Reader.TryRead(out var later)) progress = later;
            }

            published = clock.Elapsed;
            yield return new("running") { Progress = new(progress.Done, progress.Total) };
        }

        yield return new("done") { Result = await cleaning.ConfigureAwait(false) };
    }

    /// <summary>
    /// Removes build folders of one checkout that the user confirmed, against the measure the page showed, with
    /// <see cref="BuildOutputCleaner"/>; then measures the checkout again and puts it in the last measurement, in memory and
    /// in the file.
    /// </summary>
    /// <param name="request">The checkout, the measure the user saw and the paths they confirmed.</param>
    /// <returns>What the clean did, and the checkout measured again; a refusal with nothing else.</returns>
    /// <remarks>It takes no token: once asked, a clean is not stopped by its caller.</remarks>
    internal Task<DiskUsageCleanResponse> CleanAsync(DiskUsageCleanRequest request) => CleanAsync(request, null);

    private async Task<DiskUsageCleanResponse> CleanAsync(DiskUsageCleanRequest request, IProgress<CleanProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch) is { } refused) return Refusal(refused);
        if (request is not
            {
                ProjectId: { Length: > 0 and <= CheckoutSessions.MaximumIdLength } projectId, Checkout: { } checkoutPath,
                MeasureId: { Length: > 0 and <= CheckoutSessions.MaximumIdLength } measureId,
            }
            || !CheckoutSessions.ValidPath(checkoutPath) || ConfirmedPaths(request.Paths) is not { } confirmed)
            return Refusal("invalid_request");

        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DiskUsageMeasurement? last;
        KeptMeasure? kept;
        lock (_gate)
        {
            if (_disposed) return Refusal("unavailable");
            // A clean during a measure would give wrong figures, also while a stopped scan still ends; a scan asked during a
            // clean waits for it.
            if (_running is { } scan && (!scan.Ended || scan.Worker is { IsCompleted: false })) return Refusal("scanning");
            if (_cleaning is not null) return Refusal("cleaning");
            _cleaning = ended.Task;
            (last, kept) = (_last, _lastMeasures.GetValueOrDefault(checkoutPath));
        }

        try
        {
            return await CleanCheckoutAsync(projectId, checkoutPath, measureId, confirmed, last, kept, progress).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _cleaning = null;
            ended.SetResult();
        }
    }

    private async Task<DiskUsageCleanResponse> CleanCheckoutAsync(string projectId, string checkoutPath, string measureId, string[] confirmed, DiskUsageMeasurement? last,
        KeptMeasure? kept, IProgress<CleanProgress>? progress)
    {
        // The checkout as the page showed it: in the last measurement, with the measure the user confirmed paths of.
        if (last?.Projects.FirstOrDefault(project => project.Id == projectId) is not { } record
            || record.Checkouts.FirstOrDefault(checkout => checkout.Path == checkoutPath) is not { Missing: false } shown)
            return Refusal("not_found");
        if (kept is null || shown.Measure is null || shown.MeasureId is null) return Refusal("not_measured");
        if (!string.Equals(measureId, kept.Id, StringComparison.Ordinal) || !string.Equals(shown.MeasureId, kept.Id, StringComparison.Ordinal)) return Refusal("stale_measure");
        // Only what the record listed, exactly as it listed it.
        var listed = shown.Planned.Select(static entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        if (!confirmed.All(listed.Contains)) return Refusal("invalid_request");

        // What protects it now, from the workspace as it is: the checkouts git lists, the sessions at work, the folders
        // measured on their own, the programs that run.
        var snapshot = await _workspace!(CancellationToken.None).ConfigureAwait(false);
        if (snapshot.Projects.All(project => project.Id != projectId)) return Refusal("not_found");
        var plans = await PlanAsync(snapshot.Projects, CheckoutSessions.AtWork(_busy!()), snapshot, CancellationToken.None).ConfigureAwait(false);
        var (sameFolder, measured) = MeasuredFolders(plans);
        if (plans.Where(static plan => plan is { Status: "ok", Carrier: null }).SelectMany(static plan => plan.Checkouts)
                .FirstOrDefault(joined => joined.Worktree.Path == checkoutPath) is not { Worktree.Missing: false } current)
            return Refusal("not_found");
        if (sameFolder.ContainsKey(checkoutPath)) return Refusal("not_measured");
        var physical = CheckoutPaths.Physical(checkoutPath);
        var comparer = CheckoutPaths.ComparerOf(physical);
        if (current.InUse || WorksIn(physical, comparer)) return Refusal("busy");
        if (RunsFrom(physical, comparer) || ProgramsRunFrom(physical, comparer)) return Refusal("running_application");
        var excluded = ExcludedFrom(measured, physical);
        // The folders measured on their own inside it changed since (a project registered there, a worktree added): the
        // measurement would count their bytes twice or not at all. The user measures again.
        if (!SameFolders(excluded, kept.Usage.ExcludedFolders, comparer)) return Refusal("stale_measure");

        // Asked before each run of git: a session that started to work in the checkout stops what remains.
        CleanStop? Guard() => WorksIn(physical, comparer) ? new CleanStop("busy", "A session started to work in the checkout.") : null;
        BuildOutputCleanup cleanup;
        try
        {
            // Not the caller's token: once started, a clean is not stopped because the page went away.
            cleanup = await _cleaner!.CleanAsync(kept.Usage, confirmed, new BuildOutputCleanOptions(excluded) { Guard = Guard, Progress = progress }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFailure(exception))
        {
            // What it did is not known: nothing is said removed.
            cleanup = new(BuildOutputCleaner.Failed, Problem(exception), [.. confirmed.Select(static path => new CleanedEntry(path, CleanOutcome.Unknown, 0, 0))], 0,
                kept.Usage.IgnoredEntriesTruncated);
        }

        // Whatever happens after the deletion, the report of what was removed is answered; the checkout is then not measured.
        DiskUsageCheckout fresh;
        try
        {
            (fresh, var measure) = await MeasureAgainAsync(current, shown, kept, excluded).ConfigureAwait(false);
            // What it holds keeps the verdicts of the last measurement: a clean removes build output only, which no verdict reads.
            var verdicts = last.Projects.SelectMany(static project => project.Checkouts).Where(static checkout => checkout is { Missing: false, SameFolderAs: null })
                .GroupBy(static checkout => CheckoutPaths.Physical(checkout.Path), StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => VerdictOf(group.First().Verdict), StringComparer.Ordinal);
            fresh = WithHeld(fresh, folder => verdicts.GetValueOrDefault(folder, CheckoutVerdict.Unknown));
            await ReplaceAsync(last, record, shown, fresh, measure).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            fresh = NotMeasured(shown, Problem(exception) ?? exception.GetType().Name);
            Forget(last, record, shown, fresh);
        }

        return new(cleanup.Status, cleanup.Message,
            [.. cleanup.Entries.Select(static entry => new DiskUsageCleanedEntry(entry.Path, OutcomeName(entry.Outcome), entry.BytesBefore, entry.BytesFreed))],
            cleanup.BytesFreed, cleanup.MoreRemain || shown.PlannedTruncated, fresh);
    }

    private static bool SameFolders(IReadOnlyCollection<string> now, IReadOnlyCollection<string> measured, StringComparer comparer)
        => new HashSet<string>(now.Select(CheckoutPaths.Physical), comparer).SetEquals(measured.Select(CheckoutPaths.Physical));

    // Whether a session works in a folder or anywhere inside it now, whatever its work.
    private bool WorksIn(string physical, StringComparer comparer)
        => _working!().Any(session => CheckoutSessions.ValidPath(session.Folder)
            && (CheckoutPaths.IsWithin(session.Folder, physical, comparer) || CheckoutPaths.IsWithin(CheckoutPaths.Physical(session.Folder), physical, comparer)));

    // Whether a program that runs now was started from inside a folder: listed once for the request.
    private bool ProgramsRunFrom(string physical, StringComparer comparer)
        => RunningExecutables().Any(path => CheckoutPaths.IsWithin(path, physical, comparer) || CheckoutPaths.IsWithin(CheckoutPaths.Physical(path), physical, comparer));

    // The checkout measured again, with the folders measured on their own now, under a new name.
    private async Task<(DiskUsageCheckout Checkout, KeptMeasure? Measure)> MeasureAgainAsync(JoinedCheckout current, DiskUsageCheckout shown, KeptMeasure kept,
        string[] excluded)
    {
        var measures = new Dictionary<string, KeptMeasure>(StringComparer.Ordinal);
        using var slot = new SemaphoreSlim(1, 1);
        var fresh = await MeasureCheckoutAsync(current, shown.ProjectIds, null, _ => excluded, slot, null, measures, CancellationToken.None).ConfigureAwait(false);
        var measure = measures.GetValueOrDefault(shown.Path);
        // Git read it before the clean and not after: not a measure of the same checkout.
        if (kept.Usage.GitKnown && measure is { Usage.GitKnown: false }) return (NotMeasured(fresh, "Git could not read the checkout after the clean."), null);
        return (fresh, measure);
    }

    private static DiskUsageCheckout NotMeasured(DiskUsageCheckout checkout, string problem)
        => checkout with
        {
            Measure = null, Git = null, Verdict = VerdictName(CheckoutVerdict.Unknown), Planned = [], PlannedTruncated = false, MeasureId = null, Problem = problem,
        };

    // Replaces a checkout in the last measurement, its measure and the file, when that measurement is still the last one.
    private async Task ReplaceAsync(DiskUsageMeasurement last, DiskUsageProject record, DiskUsageCheckout shown, DiskUsageCheckout fresh, KeptMeasure? measure)
    {
        var projects = Replaced(last, record, shown, fresh);
        var (total, partial) = Total(projects);
        var replaced = last with { Projects = projects, TotalBytes = total, TotalPartial = partial };
        if (!Commit(last, replaced, fresh.Path, measure)) return;
        await Task.Run(() => WriteFile(StatePath!, replaced), CancellationToken.None).ConfigureAwait(false);
    }

    // After a failure: the checkout is not measured, in memory, and its old measure confirms nothing.
    private void Forget(DiskUsageMeasurement last, DiskUsageProject record, DiskUsageCheckout shown, DiskUsageCheckout fresh)
    {
        var projects = Replaced(last, record, shown, fresh);
        var (total, partial) = Total(projects);
        Commit(last, last with { Projects = projects, TotalBytes = total, TotalPartial = partial }, fresh.Path, null);
    }

    private static DiskUsageProject[] Replaced(DiskUsageMeasurement last, DiskUsageProject record, DiskUsageCheckout shown, DiskUsageCheckout fresh)
        => [.. last.Projects.Select(project => ReferenceEquals(project, record)
            ? project with { Checkouts = [.. project.Checkouts.Select(checkout => ReferenceEquals(checkout, shown) ? fresh : checkout)] }
            : project)];

    // Puts a measurement and the measure of a checkout in memory, when the measurement is still the last one: a scan waits
    // for the clean, so it is the one cleaned against; never replace a newer one.
    private bool Commit(DiskUsageMeasurement last, DiskUsageMeasurement replaced, string checkout, KeptMeasure? measure)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_last, last)) return false;
            _last = replaced;
            var measures = new Dictionary<string, KeptMeasure>(_lastMeasures, StringComparer.Ordinal);
            if (measure is null) measures.Remove(checkout);
            else measures[checkout] = measure;
            _lastMeasures = measures;
            return true;
        }
    }

    private static DiskUsageCleanResponse Refusal(string status) => new(status, null, [], 0, false, null);

    // Reports at once, on the thread that reports: the stream keeps the latest one.
    private sealed class Reported(Action<CleanProgress> report) : IProgress<CleanProgress>
    {
        public void Report(CleanProgress value) => report(value);
    }

    // The paths a clean is asked for, relative to the checkout with `/`, as a measure names them: a bounded number of
    // distinct ones, none rooted, empty, or with a `.` or `..` part; null when one is not.
    private static string[]? ConfirmedPaths(string[]? paths)
    {
        if (paths is not { Length: > 0 and <= MaximumPlannedEntries }) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path is not { Length: > 0 and <= MaximumConfirmedPathLength } || path.AsSpan().ContainsAnyInRange('\0', '\u001f') || Path.IsPathRooted(path)
                || path.Split('/', '\\').Any(static part => part is "" or "." or "..") || !seen.Add(path))
                return null;
        }

        return paths;
    }

    private static string OutcomeName(CleanOutcome outcome) => outcome switch
    {
        CleanOutcome.Removed => "removed",
        CleanOutcome.PartlyRemoved => "partly_removed",
        CleanOutcome.KeptHoldsRepository => "kept_holds_repository",
        CleanOutcome.KeptBelowLink => "kept_below_link",
        CleanOutcome.KeptNotBuildOutput => "kept_not_build_output",
        CleanOutcome.NotAttempted => "not_attempted",
        _ => "unknown",
    };


    // Joins the scan that runs, or starts one; null once the service is disposed. The scan that ended last is awaited by
    // the next one: one scan at a time.
    private (DiskUsageBroadcast Scan, DiskUsageBroadcast.Listener Listener)? Follow()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_running?.TryJoin() is { } joined) return (_running, joined);
            // The scan that ended last, and the clean that runs: the scan measures what the clean left.
            var previous = (_running?.Worker, _cleaning) switch
            {
                ({ } worker, { } cleaning) => Task.WhenAll(worker, cleaning),
                (var worker, var cleaning) => worker ?? cleaning,
            };
            var scan = new DiskUsageBroadcast();
            var listener = scan.TryJoin()!;
            _running = scan;
            scan.Worker = Task.Run(() => RunAsync(scan, previous));
            return (scan, listener);
        }
    }

    private async Task RunAsync(DiskUsageBroadcast scan, Task? previous)
    {
        var token = scan.Cancellation.Token;
        try
        {
            if (previous is not null) await previous.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var (measurement, measures) = await MeasureAsync(scan, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            if (Stage is not null) await Stage("commit", CancellationToken.None).ConfigureAwait(false);
            // Kept and stopped are one decision: a scan whose last listener left, or that the service stopped, is never kept.
            if (!scan.TryCommit()) return;
            lock (_gate)
            {
                _last = measurement;
                _lastMeasures = measures;
                _fileRead = true;
            }

            await Task.Run(() => WriteFile(StatePath!, measurement), CancellationToken.None).ConfigureAwait(false);
            scan.Publish(new("ok", "done")
            {
                Done = new(measurement.StartedAt, measurement.CompletedAt, measurement.Projects.Length, measurement.SessionsKnown, measurement.TotalBytes,
                    measurement.TotalPartial, measurement.Complete, measurement.Unreadable),
            });
            if (Stage is not null) await Stage("done", CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Nobody follows the scan any more: what it measured is dropped.
        }
        catch (Exception exception)
        {
            // Whatever ends the worker ends every listener with a record that says so: never a silent end. The last
            // measurement stays.
            scan.Publish(new("ok", "failed") { Problem = Problem(exception) ?? exception.GetType().Name });
        }
        finally
        {
            scan.End();
            scan.Cancellation.Dispose();
        }
    }

    private async Task<(DiskUsageMeasurement Measurement, IReadOnlyDictionary<string, KeptMeasure> Measures)> MeasureAsync(DiskUsageBroadcast scan, DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        var progress = new ScanProgress(scan);
        progress.Phase("sessions");
        var snapshot = await _workspace!(cancellationToken).ConfigureAwait(false);
        var working = _busy!();
        var running = new HashSet<string>(working.Select(static session => session.SessionId), StringComparer.Ordinal);

        // The sessions folder in one walk: what each session holds.
        SessionUsageSnapshot? sessionUsage = null;
        try
        {
            sessionUsage = await new SessionUsageScanner(_options!).ScanAsync(progress.Walk(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsFailure(exception))
        {
            // The sizes of the sessions are not known: each record says so.
        }

        var sessionBytes = (sessionUsage?.Sessions ?? []).ToDictionary(static usage => usage.SessionId, StringComparer.OrdinalIgnoreCase);
        var projects = snapshot.Projects;
        // Where each session belongs, as the list of sessions says it; the files of a session are named in any case.
        var scopes = new Dictionary<string, WorkspaceService.SessionScope>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in snapshot.Sessions) scopes.TryAdd(session.SessionId, WorkspaceService.ScopeOf(session, snapshot.SessionHeaders, projects));
        var sessionsOf = snapshot.Sessions.Where(session => scopes[session.SessionId].ProjectId is not null)
            .GroupBy(session => scopes[session.SessionId].ProjectId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);

        var plans = await PlanAsync(projects, CheckoutSessions.AtWork(working), snapshot, cancellationToken).ConfigureAwait(false);

        // The checkout each session works in, as the joins say.
        var checkoutOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var joined in plans.SelectMany(static plan => plan.Checkouts))
        {
            foreach (var session in joined.Sessions) checkoutOf.TryAdd(session.SessionId, joined.Worktree.Path);
        }

        // Every folder measured on its own: the checkouts the carriers hold.
        var (sameFolder, measured) = MeasuredFolders(plans);
        var sessionsInCheckouts = SessionsInCheckouts(measured);

        DiskUsageSessions SessionsOf(ProjectDescriptor project)
        {
            var own = sessionsOf.GetValueOrDefault(project.Id) ?? [];
            var sized = own.Select(session => (Session: session, Bytes: sessionBytes.TryGetValue(session.SessionId, out var usage) ? usage.TotalBytes : 0)).ToArray();
            var bytes = Sum(sized.Select(static pair => pair.Bytes));
            return new(own.Length, bytes, sessionsInCheckouts ? bytes : 0, sessionUsage is { IsPartial: false },
                [.. sized.OrderByDescending(static pair => pair.Bytes).ThenByDescending(static pair => pair.Session.UpdatedAt)
                    .ThenBy(static pair => pair.Session.SessionId, StringComparer.Ordinal).Take(MaximumSessionsPerProject)
                    .Select(pair => new DiskUsageSession(pair.Session.SessionId, CheckoutSessions.Title(pair.Session), pair.Bytes, pair.Session.UpdatedAt,
                        running.Contains(pair.Session.SessionId), checkoutOf.GetValueOrDefault(pair.Session.SessionId)))],
                own.Length > MaximumSessionsPerProject);
        }

        string[] ExcludedIn(string folder) => ExcludedFrom(measured, folder);

        var records = new DiskUsageProject[plans.Count];
        var measures = new Dictionary<string, KeptMeasure>(StringComparer.Ordinal);
        progress.Phase("projects");
        progress.SetTotal(measured.Length);

        // What needs no measure is complete at once: a folder that is gone, a project measured with another, a failure.
        foreach (var plan in plans.Where(static plan => plan.Status != "ok" || plan.Carrier is not null))
        {
            records[plan.Index] = Record(plan, [], SessionsOf(plan.Project));
            scan.Publish(new("ok", "project") { Project = records[plan.Index] });
        }

        // The own verdict of each folder measured on its own, told as soon as it is measured: a checkout that holds others
        // takes the worse of theirs before its record goes. Containment has no cycle, and a verdict is told before its
        // checkout waits for those it holds: no project waits for one that waits for it.
        var verdictsOf = measured.Distinct(StringComparer.Ordinal)
            .ToDictionary(static folder => folder, static _ => new TaskCompletionSource<CheckoutVerdict>(TaskCreationOptions.RunContinuationsAsynchronously), StringComparer.Ordinal);
        using var slots = new SemaphoreSlim(MaximumParallelCheckouts, MaximumParallelCheckouts);
        await Task.WhenAll(plans.Where(static plan => plan.Status == "ok" && plan.Carrier is null).Select(async plan =>
        {
            DiskUsageProject record;
            DiskUsageCheckout[] checkouts = [];
            try
            {
                checkouts = await Task.WhenAll(plan.Checkouts.Select(joined => MeasureCheckoutAsync(joined, ProjectsIn(plan, joined), sameFolder.GetValueOrDefault(joined.Worktree.Path), ExcludedIn,
                    slots, progress, measures, cancellationToken))).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                plan.Status = "failed";
                plan.Problem = Problem(exception);
            }
            finally
            {
                foreach (var joined in plan.Checkouts)
                {
                    if (!verdictsOf.TryGetValue(CheckoutPaths.Physical(joined.Worktree.Path), out var told)) continue;
                    told.TrySetResult(checkouts.FirstOrDefault(checkout => checkout.Path == joined.Worktree.Path) is { } measuredCheckout
                        ? VerdictOf(measuredCheckout.Verdict) : CheckoutVerdict.Unknown);
                }
            }

            if (plan.Status == "failed") record = Record(plan, [], SessionsOf(plan.Project));
            else
            {
                var held = new Dictionary<string, CheckoutVerdict>(StringComparer.Ordinal);
                foreach (var folder in checkouts.SelectMany(static checkout => checkout.HeldCheckouts).Distinct(StringComparer.Ordinal))
                    held[folder] = verdictsOf.TryGetValue(folder, out var told) ? await told.Task.WaitAsync(cancellationToken).ConfigureAwait(false) : CheckoutVerdict.Unknown;
                record = Record(plan, [.. checkouts.Select(checkout => WithHeld(checkout, folder => held.GetValueOrDefault(folder, CheckoutVerdict.Unknown)))],
                    SessionsOf(plan.Project));
            }

            records[plan.Index] = record;
            scan.Publish(new("ok", "project") { Project = record });
        })).ConfigureAwait(false);

        var (total, partial) = Total(records);
        var truncated = records.Any(static record => record.CheckoutsTruncated);
        string[] unreadable = [.. snapshot.UnreadableProjects.Select(static file => file.SourcePath)];
        return (new(started, DateTimeOffset.UtcNow, records, sessionUsage is not null, total, partial, unreadable.Length == 0 && !truncated, unreadable), measures);
    }

    // The projects of the catalog with what each is measured as: the carrier of its repository (or of its folder), or a
    // project measured with another.
    private async Task<List<ProjectPlan>> PlanAsync(IReadOnlyList<ProjectDescriptor> projects, CheckoutWork work, OwnedWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var plans = new List<ProjectPlan>(projects.Count);
        foreach (var (project, index) in projects.Select(static (project, index) => (project, index)))
        {
            var plan = new ProjectPlan(project, index);
            plans.Add(plan);
            try
            {
                plan.Physical = CheckoutPaths.Physical(project.ProjectPath);
                if (!Directory.Exists(plan.Physical))
                {
                    plan.Status = "missing";
                    continue;
                }

                plan.CheckoutRoot = GitWorktreeService.FindCheckoutRoot(plan.Physical);
                plan.Key = GitWorktreeService.FindCommonDirectory(plan.Physical) is { } common ? CheckoutPaths.Physical(common) : null;
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                (plan.Status, plan.Problem) = ("failed", Problem(exception));
            }
        }

        // The project in the main checkout first, then one at the top of a checkout, then one in a folder below, then a folder outside git.
        var carriers = new Dictionary<string, ProjectPlan>(StringComparer.Ordinal);
        foreach (var plan in plans.Where(static plan => plan.Status == "ok").OrderBy(static plan => plan.Rank).ThenBy(static plan => plan.Index))
        {
            var identity = Identity(plan.Key ?? plan.Physical!);
            if (carriers.TryGetValue(identity, out var carrier))
            {
                plan.Carrier = carrier;
                carrier.Members.Add(plan);
                continue;
            }

            carriers[identity] = plan;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var listed = plan.Key is null ? null : await _worktrees!.ListAsync(plan.Physical!, cancellationToken).ConfigureAwait(false);
                if (listed is null)
                {
                    // A folder git does not read is measured as a folder: the folder of the project, which is never removed.
                    plan.Checkouts = [new(new GitWorktree(plan.Physical!, Path.GetFileName(plan.Physical!), null, null, true, false, false), true, false, "main", [], 0, null)];
                    continue;
                }

                plan.Repository = true;
                IReadOnlyList<GitWorktree> worktrees = listed.Count > CheckoutBound ? [.. listed.Take(CheckoutBound)] : listed;
                plan.Truncated = listed.Count > CheckoutBound;
                // What the window of the worktrees says, with what the physical paths add: never less protection.
                var join = CheckoutSessions.WidenedJoin(worktrees, GitWorktreeService.FindCheckoutRoot(plan.Project.ProjectPath), work, snapshot.Sessions);
                // The folder of the project first, then the others as git lists them.
                plan.Checkouts = [.. join.Checkouts.Where(static joined => joined.Project), .. join.Checkouts.Where(static joined => !joined.Project)];
            }
            catch (Exception exception) when (IsFailure(exception))
            {
                (plan.Status, plan.Problem, plan.Checkouts) = ("failed", Problem(exception), []);
            }
        }

        return plans;
    }

    // The projects whose folder a checkout of a carrier is: the carrier, and the projects measured with it whose checkout it is.
    private static string[] ProjectsIn(ProjectPlan carrier, JoinedCheckout joined)
    {
        var path = CheckoutPaths.Physical(joined.Worktree.Path);
        var comparer = CheckoutPaths.ComparerOf(path);
        return [.. new[] { carrier }.Concat(carrier.Members)
            .Where(plan => (plan.CheckoutRoot ?? plan.Physical) is { } root && comparer.Equals(root, path))
            .Select(static plan => plan.Project.Id)];
    }

    // A path as a key of a dictionary, with the case rule of its volume.
    private static string Identity(string path)
        => ReferenceEquals(CheckoutPaths.ComparerOf(path), StringComparer.OrdinalIgnoreCase) ? path.ToUpperInvariant() : path;

    private async Task<DiskUsageCheckout> MeasureCheckoutAsync(JoinedCheckout joined, string[] projectIds, string? sameFolderAs, Func<string, string[]> excludedIn, SemaphoreSlim slots,
        ScanProgress? progress, Dictionary<string, KeptMeasure> measures, CancellationToken cancellationToken)
    {
        var worktree = joined.Worktree;
        var lastSession = joined.LastUsedAt;
        var physical = CheckoutPaths.Physical(worktree.Path);
        var comparer = CheckoutPaths.ComparerOf(physical);
        var runningApplication = RunsFrom(physical, comparer);
        // The folder of a project of the catalog is protected as the folder of a project is, whichever project carries it.
        var projectFolder = projectIds.Length > 0;
        DiskUsageCheckout Checkout(DiskUsageMeasure? measure, GitCheckoutState? state, CheckoutVerdict verdict, IReadOnlyList<IgnoredEntry> planned, bool plannedCut,
            string? problem)
        {
            var lastCommit = state?.LastCommit;
            var lastUsed = lastCommit is null ? lastSession : lastSession is null || lastCommit > lastSession ? lastCommit : lastSession;
            return new(worktree.Path, worktree.Name, worktree.Branch, worktree.Main || joined.Project || projectFolder, joined.Project, worktree.Locked, worktree.Missing,
                joined.InUse, projectFolder ? "main" : joined.Protection, projectIds, runningApplication, measure, state is null ? null : GitState(state), VerdictName(verdict),
                lastCommit, joined.SessionCount, lastUsed,
                [.. planned.Take(MaximumPlannedEntries).Select(static entry => new DiskUsagePlannedEntry(entry.Path, entry.Bytes, entry.Files, entry.LastWrite))],
                planned.Count > MaximumPlannedEntries || plannedCut, problem);
        }

        if (worktree.Missing) return Checkout(null, null, CheckoutVerdict.Unknown, [], false, null);
        // Measured as the registration it names; it keeps what protects it.
        if (sameFolderAs is not null) return Checkout(null, null, CheckoutVerdict.Unknown, [], false, null) with { SameFolderAs = sameFolderAs };
        var excluded = excludedIn(physical);
        // The checkouts measured on their own inside it (the profile apart): removing it deletes them; its verdict follows theirs.
        var profileRoot = CheckoutPaths.Physical(_options!.GlobalRoot);
        string[] held = [.. excluded.Where(folder => !comparer.Equals(folder, profileRoot))];
        await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Started(worktree.Path);
            var usage = await _checkouts!.ScanAsync(worktree.Path, excluded, progress?.Checkout(), cancellationToken).ConfigureAwait(false);
            var state = usage.GitKnown
                // The main checkout carries what belongs to the whole repository (stashes, other branches, tags), whichever
                // checkout the project is; the checkout of the project too.
                ? await _states!.ReadAsync(worktree.Path, worktree.Main || joined.Project ? GitCheckoutScope.Repository : GitCheckoutScope.Worktree, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            // A measure is named when it is made: a clean confirms this one, never one made after it.
            var id = Guid.NewGuid().ToString("N");
            lock (measures) measures[worktree.Path] = new(usage, id);
            var verdict = CheckoutVerdicts.Of(state, usage);
            // What a clean would remove is known whole only when the measure listed every ignored entry.
            return Checkout(new(usage.TotalBytes, usage.TotalFiles, usage.GitBytes, usage.BuildOutputBytes, usage.OtherIgnoredBytes, usage.OtherIgnoredCount,
                    usage.SkippedEntries, usage.GitKnown),
                state, verdict, BuildOutputCleaner.Planned(usage), usage.IgnoredEntriesTruncated, null) with { MeasureId = id, HeldCheckouts = held };
        }
        catch (Exception exception) when (IsFailure(exception))
        {
            // One checkout that cannot be read: its figures are not known, and the others are measured.
            return Checkout(null, null, CheckoutVerdict.Unknown, [], false, Problem(exception)) with { HeldCheckouts = held };
        }
        finally
        {
            slots.Release();
            progress?.Done();
        }
    }

    // The folders measured on their own, physically, each once: the checkouts the carriers hold. Git may register one folder
    // twice (a worktree replaced by a link to another): the first registration measures it, the others name that one.
    private static (Dictionary<string, string> SameFolder, string[] Measured) MeasuredFolders(List<ProjectPlan> plans)
    {
        var sameFolder = new Dictionary<string, string>(StringComparer.Ordinal);
        var firsts = new List<(string Physical, string Path, StringComparer Comparer)>();
        foreach (var joined in plans.Where(static plan => plan.Carrier is null).SelectMany(static plan => plan.Checkouts).Where(static joined => !joined.Worktree.Missing))
        {
            var physical = CheckoutPaths.Physical(joined.Worktree.Path);
            var comparer = CheckoutPaths.ComparerOf(physical);
            if (firsts.FirstOrDefault(first => comparer.Equals(first.Physical, physical)) is { Path: not null } first) sameFolder.TryAdd(joined.Worktree.Path, first.Path);
            else firsts.Add((physical, joined.Worktree.Path, comparer));
        }

        return (sameFolder, [.. firsts.Select(static first => first.Physical)]);
    }

    // The sessions folder is counted by the innermost folder measured on its own that holds it: a checkout counts the bytes
    // of the sessions of the projects already, unless the profile inside it is left out of it and holds them.
    private bool SessionsInCheckouts(string[] measured)
    {
        var (profileRoot, sessionsRoot) = (CheckoutPaths.Physical(_options!.GlobalRoot), CheckoutPaths.Physical(_options.SessionsRoot));
        return measured.Select(static folder => (Folder: folder, Checkout: true)).Append((Folder: profileRoot, Checkout: false))
            .Where(candidate => CheckoutPaths.IsWithin(sessionsRoot, candidate.Folder, CheckoutPaths.ComparerOf(candidate.Folder)))
            .OrderByDescending(static candidate => candidate.Folder.Length).ThenByDescending(static candidate => candidate.Checkout)
            .Select(static candidate => candidate.Checkout).FirstOrDefault();
    }

    // What a folder is measured without: the other folders measured on their own inside it, and the profile.
    private string[] ExcludedFrom(string[] measured, string folder)
    {
        var comparer = CheckoutPaths.ComparerOf(folder);
        bool Inside(string other) => !comparer.Equals(other, folder) && CheckoutPaths.IsWithin(other, folder, comparer);
        return [.. measured.Append(CheckoutPaths.Physical(_options!.GlobalRoot)).Where(Inside).Distinct(comparer)];
    }

    // What the records show, every byte once: every checkout measured on its own, and the sessions of the projects that no
    // checkout holds. A lower bound when something could not be measured or read whole, or a project failed, or git listed
    // more checkouts than a record holds.
    private static (long Total, bool Partial) Total(IReadOnlyList<DiskUsageProject> records)
    {
        long total = 0;
        var partial = false;
        foreach (var checkout in records.SelectMany(static record => record.Checkouts).Where(static checkout => checkout is { Missing: false, SameFolderAs: null }))
        {
            if (checkout.Measure is not { } measure)
            {
                partial = true;
                continue;
            }

            partial |= measure.SkippedEntries > 0;
            total = SaturatingAdd(total, measure.TotalBytes);
        }

        foreach (var record in records)
        {
            total = SaturatingAdd(total, record.Sessions.Bytes - record.Sessions.BytesInCheckouts);
            partial |= !record.Sessions.BytesKnown || record.CheckoutsTruncated || record.Status == "failed";
        }

        return (total, partial);
    }

    private static DiskUsageProject Record(ProjectPlan plan, DiskUsageCheckout[] checkouts, DiskUsageSessions sessions)
    {
        var project = plan.Project;
        var carrier = plan.Carrier ?? plan;
        return new(project.Id, project.DisplayName is { Length: > 0 } name ? name : project.Name, project.ProjectPath, [.. project.Spaces], project.Archived,
            plan.Status, plan.Problem, carrier.Repository, carrier.Repository ? carrier.Key : null, plan.Carrier?.Project.Id, [.. plan.Members.Select(static member => member.Project.Id)],
            checkouts, plan.Truncated, sessions);
    }

    private static DiskUsageGitState GitState(GitCheckoutState state)
        => new(state.HasRemote, state.ChangedFiles, state.UntrackedFiles, state.Branch, state.Detached,
            [.. state.UnpushedBranches.Select(static branch => new DiskUsageBranch(branch.Name, branch.Commits))], state.UnpushedBranchesTruncated,
            state.Stashes, state.DetachedCommits, [.. state.SubmodulesWithLocalWork], state.HiddenFiles, state.TagOnlyCommits,
            state.UnexaminedSubmoduleRepositories, state.OperationInProgress, state.HasLocalWork)
        {
            OtherWorktreeDetachedCommits = state.OtherWorktreeDetachedCommits, UnexaminedBranches = state.UnexaminedBranches, OtherRefs = state.OtherRefs,
            OtherRefNames = [.. state.OtherRefNames],
        };

    // A checkout that holds others: its verdict is the worse of its own and theirs, and it names those that made it worse.
    private static DiskUsageCheckout WithHeld(DiskUsageCheckout checkout, Func<string, CheckoutVerdict> verdictOf)
    {
        if (checkout.HeldCheckouts.Length == 0) return checkout;
        var own = VerdictOf(checkout.Verdict);
        var worst = own;
        var worse = new List<string>();
        foreach (var folder in checkout.HeldCheckouts)
        {
            var verdict = verdictOf(folder);
            if (Rank(verdict) <= Rank(own)) continue;
            worse.Add(folder);
            if (Rank(verdict) > Rank(worst)) worst = verdict;
        }

        return checkout with { Verdict = VerdictName(worst), WorseHeldCheckouts = [.. worse] };
    }

    // How bad a verdict is: nothing local, then ignored files only, then unknown, then local work.
    private static int Rank(CheckoutVerdict verdict) => verdict switch
    {
        CheckoutVerdict.NothingLocal => 0,
        CheckoutVerdict.IgnoredFilesOnly => 1,
        CheckoutVerdict.Unknown => 2,
        _ => 3,
    };

    private static CheckoutVerdict VerdictOf(string name) => name switch
    {
        "nothing_local" => CheckoutVerdict.NothingLocal,
        "ignored_files_only" => CheckoutVerdict.IgnoredFilesOnly,
        "has_local_work" => CheckoutVerdict.HasLocalWork,
        _ => CheckoutVerdict.Unknown,
    };

    private static string VerdictName(CheckoutVerdict verdict) => verdict switch
    {
        CheckoutVerdict.NothingLocal => "nothing_local",
        CheckoutVerdict.IgnoredFilesOnly => "ignored_files_only",
        CheckoutVerdict.HasLocalWork => "has_local_work",
        _ => "unknown",
    };

    private static long Sum(IEnumerable<long> values) => values.Aggregate(0L, SaturatingAdd);

    private static long SaturatingAdd(long sum, long value) => sum > long.MaxValue - value ? long.MaxValue : sum + value;

    private static bool IsFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or ArgumentException
            or NotSupportedException or Win32Exception or JsonException or System.Data.Common.DbException;

    private static string? Problem(Exception exception)
        => exception.Message is { Length: > 0 } message ? message.Length <= MaximumProblemLength ? message : message[..MaximumProblemLength] : null;

    // Whether the running application was started from inside a folder, physically.
    private bool RunsFrom(string physical, StringComparer comparer)
        => ApplicationPaths.Any(path => path is { Length: > 0 } && CheckoutPaths.IsWithin(CheckoutPaths.Physical(path), physical, comparer));

    private string? Refuse(string? expectedEpoch)
    {
        if (_options is null) return "unavailable";
        lock (_gate)
        {
            if (_disposed) return "unavailable";
        }

        return !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;
    }

    /// <summary>
    /// Reads the last measurement a file kept, as a reader of a file that is replaced whole, and no more than
    /// <paramref name="maximumBytes"/> of it. Null when there is none, it cannot be read, it is larger, it has another
    /// version, or it is not whole (<see cref="IsWhole(DiskUsageMeasurement)"/>).
    /// </summary>
    internal static async Task<DiskUsageMeasurement?> ReadFileAsync(string path, long maximumBytes, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await StagedFileReplacement.OpenReplacedFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (stream is null) return null;
            using var content = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                // The bound holds while reading: a file that grows is not read past it.
                if (content.Length + read > maximumBytes) return null;
                content.Write(buffer, 0, read);
            }

            var file = JsonSerializer.Deserialize(content.GetBuffer().AsSpan(0, (int)content.Length), DiskUsageFileJsonContext.Default.DiskUsageFile);
            return file is { Version: FileVersion, Measurement: { } measurement } && IsWhole(measurement) ? measurement : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Whether a measurement read from a file has every part a measurement has: its times and well-formed projects.</summary>
    internal static bool IsWhole(DiskUsageMeasurement measurement)
        => measurement is { Projects: { } projects, Unreadable: { } unreadable }
            && measurement.StartedAt != default && measurement.CompletedAt != default
            && unreadable.All(static path => path is not null)
            && projects.All(static project => project is { Id: not null, Name: not null, Folder: not null, Status: not null, Spaces: { } spaces, SharedWith: { } shared,
                    Checkouts: { } checkouts, Sessions.Largest: { } largest }
                && spaces.All(static space => space is not null) && shared.All(static id => id is not null)
                && largest.All(static session => session is { Id: not null, Title: not null })
                && checkouts.All(static checkout => checkout is { Path: not null, Name: not null, Verdict: not null, ProjectIds: { } ids, Planned: { } planned, HeldCheckouts: { } held,
                        WorseHeldCheckouts: { } worse }
                    && ids.All(static id => id is not null) && held.All(static path => path is not null) && worse.All(static path => path is not null) && planned.All(static entry => entry is { Path: not null })
                    && (checkout.Git is null || checkout.Git is { UnpushedBranches: { } branches, SubmodulesWithLocalWork: { } submodules, OtherRefNames: { } refs }
                        && branches.All(static branch => branch is { Name: not null }) && submodules.All(static path => path is not null)
                        && refs.All(static name => name is not null))));

    // Replaces the file at once: a reader finds the previous measurement or this one. A file that cannot be written
    // leaves the measurement in memory only.
    private static void WriteFile(string path, DiskUsageMeasurement measurement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SafeFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new DiskUsageFile { Version = FileVersion, Measurement = measurement },
                DiskUsageFileJsonContext.Default.DiskUsageFile));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The next run measures again.
        }
    }

    // A project of a scan, as it is planned before its checkouts are measured.
    private sealed class ProjectPlan(ProjectDescriptor project, int index)
    {
        public ProjectDescriptor Project { get; } = project;
        public int Index { get; } = index;
        public string Status { get; set; } = "ok";
        public string? Problem { get; set; }
        // The folder of the project, physically; the root of the checkout it is in; the git folder its repository shares.
        public string? Physical { get; set; }
        public string? CheckoutRoot { get; set; }
        public string? Key { get; set; }
        public bool Repository { get; set; }
        public bool Truncated { get; set; }
        public ProjectPlan? Carrier { get; set; }
        public List<ProjectPlan> Members { get; } = [];
        public IReadOnlyList<JoinedCheckout> Checkouts { get; set; } = [];

        // The order in which a project carries its repository: in the main checkout, at the top of a checkout, below, outside git.
        public int Rank => CheckoutRoot is null ? 3
            : !CheckoutPaths.ComparerOf(CheckoutRoot).Equals(Physical, CheckoutRoot) ? 2
            : Directory.Exists(Path.Combine(CheckoutRoot, ".git")) ? 0 : 1;
    }

    // What a scan is at, published a few times a second at most: the phase, the checkout started last, the checkouts
    // done of all, and the files counted in the phase.
    private sealed class ScanProgress(DiskUsageBroadcast scan)
    {
        private readonly Lock _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private TimeSpan _published = TimeSpan.MinValue;
        private string _phase = "sessions";
        private string? _current;
        private int _done;
        private int _total;
        private long _files;

        public void Phase(string phase)
        {
            lock (_gate)
            {
                (_phase, _current, _files) = (phase, null, 0);
                Publish(force: true);
            }
        }

        public void SetTotal(int total)
        {
            lock (_gate)
            {
                _total = total;
                Publish(force: true);
            }
        }

        public void Started(string checkout)
        {
            lock (_gate)
            {
                _current = checkout;
                Publish(force: false);
            }
        }

        public void Done()
        {
            lock (_gate)
            {
                _done++;
                Publish(force: false);
            }
        }

        // The files a measure of one checkout counted, added to the files of the phase as they grow.
        public IProgress<CheckoutUsageProgress> Checkout() => new CheckoutFiles(this);

        // The files the walk of the sessions folder counted so far.
        public IProgress<ProfileStorageProgress> Walk() => new ProfileFiles(this);

        private void Add(long files)
        {
            lock (_gate)
            {
                _files = SaturatingAdd(_files, files);
                Publish(force: false);
            }
        }

        private void Set(long files)
        {
            lock (_gate)
            {
                _files = files;
                Publish(force: false);
            }
        }

        private void Publish(bool force)
        {
            var now = _clock.Elapsed;
            if (!force && now - _published < ProgressInterval) return;
            _published = now;
            scan.Publish(new("ok", "progress") { Progress = new(_phase, _current, _done, _total, _files) });
        }

        private sealed class CheckoutFiles(ScanProgress owner) : IProgress<CheckoutUsageProgress>
        {
            private long _counted;

            public void Report(CheckoutUsageProgress value)
            {
                var before = Interlocked.Exchange(ref _counted, value.ProcessedFiles);
                if (value.ProcessedFiles > before) owner.Add(value.ProcessedFiles - before);
            }
        }

        private sealed class ProfileFiles(ScanProgress owner) : IProgress<ProfileStorageProgress>
        {
            public void Report(ProfileStorageProgress value) => owner.Set(value.ProcessedFiles);
        }
    }
}


/// <summary>Asks for the disk usage.</summary>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
internal sealed record DiskUsageRequest(string? ExpectedEpoch);

/// <summary>
/// One record of a measurement: <see cref="Kind"/> says which of its parts is set. A refused request gets one record,
/// with the status <c>unavailable</c> or <c>stale_epoch</c> and no kind. The last record of a measurement is <c>done</c>,
/// or <c>failed</c> when the measurement could not go on; nothing comes after it.
/// </summary>
/// <param name="Status"><c>ok</c>, or why the request was refused.</param>
/// <param name="Kind"><c>progress</c>, <c>project</c>, <c>done</c> or <c>failed</c>; null for a refusal.</param>
internal sealed record DiskUsageScanEvent(string Status, string? Kind)
{
    /// <summary>Gets how far the measurement is, for <c>progress</c>.</summary>
    public DiskUsageProgress? Progress { get; init; }

    /// <summary>Gets a project that was measured, for <c>project</c>.</summary>
    public DiskUsageProject? Project { get; init; }

    /// <summary>Gets the end of the measurement, for <c>done</c>.</summary>
    public DiskUsageDone? Done { get; init; }

    /// <summary>Gets why the measurement could not go on, for <c>failed</c>: a message of a few hundred characters at most.</summary>
    public string? Problem { get; init; }
}

/// <summary>How far a measurement is: not an estimate of what is left.</summary>
/// <param name="Phase"><c>sessions</c> (the sessions folder) or <c>projects</c> (the checkouts).</param>
/// <param name="Current">The checkout measured last started; null outside the <c>projects</c> phase.</param>
/// <param name="CheckoutsDone">The checkouts measured so far.</param>
/// <param name="CheckoutsTotal">The checkouts to measure; 0 until the projects are listed.</param>
/// <param name="Files">The files counted in the phase so far.</param>
internal sealed record DiskUsageProgress(string Phase, string? Current, int CheckoutsDone, int CheckoutsTotal,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Files);

/// <summary>The end of a measurement that completed.</summary>
/// <param name="StartedAt">When it started.</param>
/// <param name="CompletedAt">When it ended.</param>
/// <param name="Projects">The project records it produced.</param>
/// <param name="SessionsKnown">Whether the sizes of the sessions could be read.</param>
/// <param name="TotalBytes">
/// The projects, each byte once: their checkouts and project folders (a checkout is measured without the others inside it
/// and without the CodeAlta profile, which has a view of its own; a folder git registers twice is measured once), and
/// their sessions that no checkout holds. A project registered inside the sessions folder of the profile is a known
/// limit: its sessions are counted with it and again with the project.
/// </param>
/// <param name="TotalPartial">Whether something could not be measured (a checkout, a project, the sessions folder): the total is then a lower bound.</param>
/// <param name="Complete">Whether every project of the catalog was measured: false when some could not be read (<paramref name="Unreadable"/>).</param>
/// <param name="Unreadable">The files of the projects the catalog could not read.</param>
internal sealed record DiskUsageDone(DateTimeOffset StartedAt, DateTimeOffset CompletedAt, int Projects, bool SessionsKnown,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long TotalBytes,
    bool TotalPartial, bool Complete, string[] Unreadable);

/// <summary>A project of the catalog, as it was measured.</summary>
/// <param name="Id">The project.</param>
/// <param name="Name">The name it is shown with.</param>
/// <param name="Folder">Its folder.</param>
/// <param name="Spaces">The spaces it is in.</param>
/// <param name="Archived">Whether it is archived.</param>
/// <param name="Status"><c>ok</c>; <c>missing</c> when its folder is gone; <c>failed</c> when it could not be measured, <paramref name="Problem"/> says why.</param>
/// <param name="Problem">What went wrong, for <c>failed</c>.</param>
/// <param name="Repository">Whether its folder is in a git repository.</param>
/// <param name="RepositoryKey">The git folder its repository shares between its checkouts: the projects with the same one share the repository.</param>
/// <param name="MeasuredWith">The project that carries the checkouts of the repository, when it is another one: this one then has none.</param>
/// <param name="SharedWith">The other projects of the repository, which name this one; empty when none.</param>
/// <param name="Checkouts">The checkouts: the folder of the project first, then the worktrees git lists; for a folder git does not read, the folder alone.</param>
/// <param name="CheckoutsTruncated">Whether git lists more checkouts than the record holds.</param>
/// <param name="Sessions">The sessions of the project.</param>
internal sealed record DiskUsageProject(string Id, string Name, string Folder, string[] Spaces, bool Archived, string Status, string? Problem,
    bool Repository, string? RepositoryKey, string? MeasuredWith, string[] SharedWith, DiskUsageCheckout[] Checkouts, bool CheckoutsTruncated,
    DiskUsageSessions Sessions);

/// <summary>A checkout of a project's repository (or the folder of a project git does not read), as it was measured.</summary>
/// <param name="Path">Its folder.</param>
/// <param name="Name">The name of that folder.</param>
/// <param name="Branch">The branch it is on; null on a commit without a branch or outside git.</param>
/// <param name="Main">Whether it is the checkout the project lives in or the main one of the repository: neither is removed.</param>
/// <param name="Project">Whether it is the checkout the project lives in.</param>
/// <param name="Locked">Whether git keeps it from being removed.</param>
/// <param name="Missing">Whether its folder is gone while git still lists it: nothing was measured.</param>
/// <param name="InUse">Whether a session is at work in it.</param>
/// <param name="Protection">
/// Why it is not removed: <c>main</c> (the folder of a project, or the main checkout), <c>in_use</c> or <c>locked</c>; null
/// for a worktree that can go.
/// </param>
/// <param name="ProjectIds">The projects of the catalog whose folder this checkout is (one in a folder below its top included); empty for a worktree of none.</param>
/// <param name="RunningApplication">Whether the running application was started from inside it: its build output is not cleaned.</param>
/// <param name="Measure">What it holds, without the other checkouts, project folders and the profile inside it; null when it was not measured (gone, or <paramref name="Problem"/>).</param>
/// <param name="Git">What removing it would lose; null when git could not be read.</param>
/// <param name="Verdict"><c>nothing_local</c>, <c>ignored_files_only</c>, <c>has_local_work</c> or <c>unknown</c>.</param>
/// <param name="LastCommit">When the commit it is on was made; null when not known.</param>
/// <param name="SessionCount">How many sessions of the catalog work in it.</param>
/// <param name="LastUsedAt">The later of the last update of those sessions and of <paramref name="LastCommit"/>; null when neither is known.</param>
/// <param name="Planned">What a clean of its build output would remove, largest first, a bounded number.</param>
/// <param name="PlannedTruncated">Whether a clean may remove more folders than <paramref name="Planned"/> lists: more were found, or the measure listed only part of the ignored entries.</param>
/// <param name="Problem">Why it could not be measured; null otherwise.</param>
internal sealed record DiskUsageCheckout(string Path, string Name, string? Branch, bool Main, bool Project, bool Locked, bool Missing, bool InUse,
    string? Protection, string[] ProjectIds, bool RunningApplication, DiskUsageMeasure? Measure, DiskUsageGitState? Git, string Verdict, DateTimeOffset? LastCommit,
    int SessionCount, DateTimeOffset? LastUsedAt, DiskUsagePlannedEntry[] Planned, bool PlannedTruncated, string? Problem)
{
    /// <summary>
    /// Gets the checkout of the same scan that reaches the same folder (git registers both, one through a link): that one
    /// carries the measure, and this one none; null for a folder measured here.
    /// </summary>
    public string? SameFolderAs { get; init; }

    /// <summary>
    /// Gets the name of its measure, given when the measure was made: a clean names it to say which measure the user
    /// confirmed paths of. Null when it was not measured.
    /// </summary>
    public string? MeasureId { get; init; }

    /// <summary>
    /// Gets the checkouts measured on their own that lie inside its folder: removing it deletes them with it. Its verdict
    /// is the worse of its own and theirs.
    /// </summary>
    public string[] HeldCheckouts { get; init; } = [];

    /// <summary>
    /// Gets those of <see cref="HeldCheckouts"/> whose verdict is worse than its own and made its verdict worse (the reason
    /// <c>holds_checkout</c>); empty when they all say no worse than it does.
    /// </summary>
    public string[] WorseHeldCheckouts { get; init; } = [];
}

/// <summary>A measure of a checkout the service keeps for a clean, with the name the page knows it by.</summary>
/// <param name="Usage">The measure.</param>
/// <param name="Id">Its name, as <see cref="DiskUsageCheckout.MeasureId"/> carries it.</param>
internal sealed record KeptMeasure(CheckoutUsage Usage, string Id);

/// <summary>What a checkout holds: the sum of the lengths of its files, links not followed.</summary>
/// <param name="TotalBytes">Everything in its folder.</param>
/// <param name="TotalFiles">Its files.</param>
/// <param name="GitBytes">Its <c>.git</c> folder and those of its submodules; 0 for a worktree.</param>
/// <param name="BuildOutputBytes">What git ignores that is build output.</param>
/// <param name="OtherIgnoredBytes">What git ignores that is not build output: a clone does not bring it back.</param>
/// <param name="OtherIgnoredCount">The entries of <paramref name="OtherIgnoredBytes"/>.</param>
/// <param name="SkippedEntries">What could not be read: when not 0, the figures are a lower bound.</param>
/// <param name="GitKnown">Whether git could be read: when not, nothing is known as ignored.</param>
internal sealed record DiskUsageMeasure(
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long TotalBytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long TotalFiles,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long GitBytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long BuildOutputBytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long OtherIgnoredBytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long OtherIgnoredCount,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long SkippedEntries,
    bool GitKnown);

/// <summary>What git says of a checkout, read from the local repository as of the last fetch.</summary>
/// <param name="HasRemote">Whether the repository has a remote.</param>
/// <param name="ChangedFiles">Tracked files with changes.</param>
/// <param name="UntrackedFiles">Untracked entries.</param>
/// <param name="Branch">The branch of HEAD; null when detached.</param>
/// <param name="Detached">Whether HEAD is detached.</param>
/// <param name="UnpushedBranches">Local branches with commits on no remote.</param>
/// <param name="UnpushedBranchesTruncated">Whether some branches were not examined (<see cref="DiskUsageGitState.UnexaminedBranches"/> says how many).</param>
/// <param name="Stashes">The stashes; 0 for a worktree.</param>
/// <param name="DetachedCommits">Commits of its own detached HEAD on no branch and no remote (those of other worktrees: <see cref="DiskUsageGitState.OtherWorktreeDetachedCommits"/>).</param>
/// <param name="SubmodulesWithLocalWork">The submodules with local work.</param>
/// <param name="HiddenFiles">Files marked <c>assume-unchanged</c> or <c>skip-worktree</c>.</param>
/// <param name="TagOnlyCommits">Commits only tags hold.</param>
/// <param name="UnexaminedSubmoduleRepositories">Submodule repositories no checked-out submodule uses.</param>
/// <param name="OperationInProgress"><c>merge</c>, <c>rebase</c>, <c>cherry-pick</c>, <c>revert</c> or <c>bisect</c>; null when none.</param>
/// <param name="HasLocalWork">Whether one of these says that removing the checkout loses something.</param>
internal sealed record DiskUsageGitState(bool HasRemote, int ChangedFiles, int UntrackedFiles, string? Branch, bool Detached, DiskUsageBranch[] UnpushedBranches,
    bool UnpushedBranchesTruncated, int Stashes, int DetachedCommits, string[] SubmodulesWithLocalWork, int HiddenFiles, int TagOnlyCommits,
    int UnexaminedSubmoduleRepositories, string? OperationInProgress, bool HasLocalWork)
{
    /// <summary>Gets the commits that only the detached HEADs of the other worktrees hold; 0 for a worktree.</summary>
    public int OtherWorktreeDetachedCommits { get; init; }

    /// <summary>Gets the local branches not examined because there are more than the bound, while commits on no remote exist: they may hold some.</summary>
    public int UnexaminedBranches { get; init; }

    /// <summary>Gets the refs outside branches, tags, remotes and the stash (notes, for one) that go with the repository; 0 for a worktree.</summary>
    public int OtherRefs { get; init; }

    /// <summary>Gets the names of the first of <see cref="OtherRefs"/>, a few at most.</summary>
    public string[] OtherRefNames { get; init; } = [];
}

/// <summary>A local branch with commits on no remote.</summary>
/// <param name="Name">The branch.</param>
/// <param name="Commits">Its commits on no remote.</param>
internal sealed record DiskUsageBranch(string Name, int Commits);

/// <summary>A folder a clean of build output would remove.</summary>
/// <param name="Path">Its path from the checkout, with <c>/</c>: what a clean names.</param>
/// <param name="Bytes">What it holds.</param>
/// <param name="Files">Its files.</param>
/// <param name="LastWrite">The most recent write of its files; null when it holds none.</param>
internal sealed record DiskUsagePlannedEntry(string Path,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Bytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Files,
    DateTimeOffset? LastWrite);

/// <summary>The sessions of a project.</summary>
/// <param name="Count">How many it has.</param>
/// <param name="Bytes">What their journals and attachments hold.</param>
/// <param name="BytesInCheckouts">What of <paramref name="Bytes"/> a measured checkout holds too, as it holds the sessions folder: counted once, there.</param>
/// <param name="BytesKnown">Whether the sessions folder could be read whole: when not, <paramref name="Bytes"/> is a lower bound.</param>
/// <param name="Largest">The largest of them, a bounded number.</param>
/// <param name="Truncated">Whether it has more than <paramref name="Largest"/> names.</param>
internal sealed record DiskUsageSessions(int Count,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Bytes,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long BytesInCheckouts,
    bool BytesKnown, DiskUsageSession[] Largest, bool Truncated);

/// <summary>A session of a project.</summary>
/// <param name="Id">The session.</param>
/// <param name="Title">The title it is listed with.</param>
/// <param name="Bytes">What its journal and attachments hold.</param>
/// <param name="UpdatedAt">When it was last updated.</param>
/// <param name="Running">Whether it is at work.</param>
/// <param name="Checkout">The checkout it works in; null when it is in none of its repository.</param>
internal sealed record DiskUsageSession(string Id, string Title,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long Bytes,
    DateTimeOffset UpdatedAt, bool Running, string? Checkout);

/// <summary>A complete measurement.</summary>
/// <param name="StartedAt">When it started.</param>
/// <param name="CompletedAt">When it ended.</param>
/// <param name="Projects">The projects, in the order of the catalog.</param>
/// <param name="SessionsKnown">Whether the sizes of the sessions could be read.</param>
/// <param name="TotalBytes">Everything measured, each byte once, as <see cref="DiskUsageDone.TotalBytes"/>.</param>
/// <param name="TotalPartial">Whether the total is a lower bound.</param>
/// <param name="Complete">Whether every project of the catalog was measured.</param>
/// <param name="Unreadable">The files of the projects the catalog could not read.</param>
internal sealed record DiskUsageMeasurement(DateTimeOffset StartedAt, DateTimeOffset CompletedAt, DiskUsageProject[] Projects, bool SessionsKnown,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long TotalBytes,
    bool TotalPartial, bool Complete, string[] Unreadable);

/// <summary>The last complete measurement.</summary>
/// <param name="Status"><c>ok</c>, or why the request was refused: <c>unavailable</c>, <c>stale_epoch</c>.</param>
/// <param name="Measurement">The measurement; null when none was made yet.</param>
/// <param name="Running">Whether a measurement runs now.</param>
internal sealed record DiskUsageLastResponse(string Status, DiskUsageMeasurement? Measurement, bool Running)
{
    /// <summary>
    /// Gets whether the measurement was made in this run of the application: only its checkouts can be cleaned. False for
    /// the one the file kept from an earlier run, and when there is none.
    /// </summary>
    public bool Current { get; init; }
}

/// <summary>Asks to clean build folders of one checkout, as the page showed them.</summary>
/// <remarks>
/// <para>
/// The request carries no proof that a person confirmed it: anything that can run script in a window of the application
/// can send it, as it can send <c>worktrees.removeMany</c>. It is limited to the build folders listed for a checkout
/// measured in this run, and to the measure the page showed.
/// </para>
/// <para>
/// Known limits: the sessions of another CodeAlta instance are not seen; a session that starts to work in the checkout
/// between the check before a run of git and the end of that run is not stopped (the next run is).
/// </para>
/// </remarks>
/// <param name="ExpectedEpoch">The host epoch the page believes it is talking to.</param>
/// <param name="ProjectId">The project whose record holds the checkout in the last measurement.</param>
/// <param name="Checkout">The full path of the checkout, as the last measurement names it.</param>
/// <param name="MeasureId">The measure the page showed, as <see cref="DiskUsageCheckout.MeasureId"/> named it: another one is <c>stale_measure</c>.</param>
/// <param name="Paths">
/// The folders the user confirmed, each exactly one of the <see cref="DiskUsageCheckout.Planned"/> entries the record listed;
/// any other path makes the request <c>invalid_request</c>.
/// </param>
internal sealed record DiskUsageCleanRequest(string? ExpectedEpoch, string? ProjectId, string? Checkout, string? MeasureId, string[]? Paths);

/// <summary>What a clean did with one confirmed path.</summary>
/// <param name="Path">The path, as it was confirmed.</param>
/// <param name="Outcome">
/// <c>removed</c>, <c>partly_removed</c>, <c>kept_holds_repository</c>, <c>kept_below_link</c>, <c>kept_not_build_output</c> (also for a
/// folder that is no build output any more), <c>not_attempted</c> or <c>unknown</c>, as <see cref="CleanOutcome"/> names them.
/// </param>
/// <param name="BytesBefore">What it held when the clean measured it; 0 when that measure does not list it.</param>
/// <param name="BytesFreed">What it holds less after the clean.</param>
internal sealed record DiskUsageCleanedEntry(string Path, string Outcome,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long BytesBefore,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long BytesFreed);

/// <summary>What a clean did, or why it was refused.</summary>
/// <param name="Status">
/// The status of the cleaner (<see cref="BuildOutputCleaner.Ok"/>, <see cref="BuildOutputCleaner.Failed"/>, <see cref="BuildOutputCleaner.CheckoutChanged"/>,
/// <see cref="BuildOutputCleaner.NotRepository"/>, <see cref="BuildOutputCleaner.GitTooOld"/>, or <c>busy</c> when a session started to work in the
/// checkout between two runs of git), or a refusal: <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid_request</c>, <c>not_found</c>,
/// <c>not_measured</c>, <c>stale_measure</c>, <c>busy</c>, <c>running_application</c> (this application or another program runs from inside
/// the checkout), <c>scanning</c>, <c>cleaning</c>.
/// </param>
/// <param name="Message">What the cleaner said when it failed or refused; null otherwise.</param>
/// <param name="Entries">One entry per confirmed path, in their order; empty for a refusal.</param>
/// <param name="BytesFreed">What the entries hold less.</param>
/// <param name="MoreRemain">Whether build folders the record did not list (it shows a bounded number, or the measure listed part of them) were not cleaned.</param>
/// <param name="Checkout">The checkout measured again after the clean, as the last measurement now holds it; null for a refusal.</param>
internal sealed record DiskUsageCleanResponse(string Status, string? Message, DiskUsageCleanedEntry[] Entries,
    [property: NeoRpcInt64(NeoRpcInt64Policy.String), JsonConverter(typeof(NeoRpcInt64JsonConverter))] long BytesFreed,
    bool MoreRemain, DiskUsageCheckout? Checkout);

/// <summary>One record of a clean: where it is, then its report.</summary>
/// <param name="Kind"><c>running</c> with <see cref="Progress"/>, or <c>done</c> with <see cref="Result"/>, the last record.</param>
internal sealed record DiskUsageCleanEvent(string Kind)
{
    /// <summary>Gets how far the clean is, for <c>running</c>.</summary>
    public DiskUsageCleanProgress? Progress { get; init; }

    /// <summary>Gets what the clean did, or why it was refused, for <c>done</c>.</summary>
    public DiskUsageCleanResponse? Result { get; init; }
}

/// <summary>How far a clean is.</summary>
/// <param name="Done">The confirmed paths whose run of git is done.</param>
/// <param name="Total">The confirmed paths that go to git.</param>
internal sealed record DiskUsageCleanProgress(int Done, int Total);

/// <summary>The file of the last measurement of an instance.</summary>
internal sealed class DiskUsageFile
{
    /// <summary>Gets or sets the version of the format: <see cref="DiskUsageService.FileVersion"/>.</summary>
    public int Version { get; set; }

    /// <summary>Gets or sets the measurement.</summary>
    public DiskUsageMeasurement? Measurement { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DiskUsageFile))]
internal sealed partial class DiskUsageFileJsonContext : JsonSerializerContext;
