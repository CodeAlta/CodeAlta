using System.Runtime.ExceptionServices;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Views;

internal enum CodeAltaUpdateCheckStatus
{
    NotStarted,
    Checking,
    Latest,
    UpdateAvailable,
    PackageNotFound,
    Failed,
}

internal sealed record CodeAltaUpdateCheckSnapshot(
    CodeAltaUpdateCheckStatus Status,
    string PackageId,
    string CurrentVersionText,
    string? LatestVersionText,
    bool LatestVersionIsPrerelease,
    bool IncludePrerelease,
    string? ErrorMessage)
{
    public bool HasNewerVersion => Status == CodeAltaUpdateCheckStatus.UpdateAvailable && !string.IsNullOrWhiteSpace(LatestVersionText);

    public bool IsCompleted => Status is CodeAltaUpdateCheckStatus.Latest or CodeAltaUpdateCheckStatus.UpdateAvailable or CodeAltaUpdateCheckStatus.PackageNotFound or CodeAltaUpdateCheckStatus.Failed;

    public string UpdateCommand => LatestVersionIsPrerelease
        ? $"dotnet tool update -g {PackageId} --prerelease"
        : $"dotnet tool update -g {PackageId}";

    public static CodeAltaUpdateCheckSnapshot CreateNotStarted()
        => new(
            CodeAltaUpdateCheckStatus.NotStarted,
            CodeAltaUpdateChecker.PackageId,
            CodeAltaApplicationInfo.GetVersionInfo().PackageVersion,
            LatestVersionText: null,
            LatestVersionIsPrerelease: false,
            IncludePrerelease: false,
            ErrorMessage: null);
}

internal sealed class CodeAltaUpdateService : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly State<int> _uiRefreshVersion = new(0);
    private CodeAltaUpdateCheckSnapshot _snapshot = CodeAltaUpdateCheckSnapshot.CreateNotStarted();
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _checkTask;
    private readonly Lazy<Task> _disposeTask;
    private bool _startRequested;
    private bool _stopRequested;
    private int _generation;
    private int _observedGeneration;

    public CodeAltaUpdateService()
    {
        _disposeTask = CreateUpdateDisposal(
            stopCheck: () => _stopRequested = true,
            disposeCore: DisposeCoreAsync);
    }

    public State<int> UiRefreshVersion => _uiRefreshVersion;

    public CodeAltaUpdateCheckSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public void Start()
    {
        StartUpdateCheck(
            ref _startRequested,
            _stopRequested,
            ref _cancellationTokenSource,
            ref _checkTask,
            () => SetSnapshot(Snapshot with
            {
                Status = CodeAltaUpdateCheckStatus.Checking
            }),
            token => Task.Run(() => CheckAsync(token)));
    }

    public void SynchronizeUiState()
    {
        var generation = Volatile.Read(ref _generation);
        if (generation == _observedGeneration)
        {
            return;
        }

        _observedGeneration = generation;
        _uiRefreshVersion.Value++;
    }

    public ValueTask DisposeAsync() => new(_disposeTask.Value);

    private Task DisposeCoreAsync()
    {
        // The lazy initializer stopped admission before any cancellation callback can run.
        var checkTask = _checkTask;
        var cancellation = _cancellationTokenSource;
        return DisposeUpdateCheckAsync(
            checkTask,
            cancelCheck: () => cancellation?.Cancel(),
            disposeCancellation: () => cancellation?.Dispose());
    }

    /// <summary>
    /// Admits one update check, captures its owned token and retains the original scheduled task.
    /// </summary>
    /// <remarks>
    /// Start and first disposal are serialized. Validate both operations before admission checks;
    /// stopped admission takes precedence over an already admitted no-op. Latch before allocation,
    /// publication and scheduling so a failed start cannot retry. Store the source before callbacks;
    /// the caller remains responsible for disposal even if publication or scheduling throws.
    /// The mandatory scheduling operation must return a non-null task without replacing ownership.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory operation is null.</exception>
    /// <exception cref="ObjectDisposedException">Disposal has stopped admission.</exception>
    internal static void StartUpdateCheck(
        ref bool startRequested,
        bool stopRequested,
        ref CancellationTokenSource? cancellationTokenSource,
        ref Task? checkTask,
        Action publishChecking,
        Func<CancellationToken, Task> startCheck)
    {
        ArgumentNullException.ThrowIfNull(publishChecking);
        ArgumentNullException.ThrowIfNull(startCheck);

        if (stopRequested)
        {
            throw new ObjectDisposedException(nameof(CodeAltaUpdateService));
        }

        if (startRequested)
        {
            return;
        }

        startRequested = true;
        cancellationTokenSource = new CancellationTokenSource();
        var token = cancellationTokenSource.Token;
        publishChecking();
        checkTask = startCheck(token);
    }

    /// <summary>
    /// Creates the single cached updater disposal, stopping admission before its core starts inline.
    /// </summary>
    /// <remarks>
    /// The stop callback must be a nonthrowing assignment. Default execution-and-publication shares
    /// pending and terminal outcomes among repeated/concurrent nonreentrant disposers without retry.
    /// First access belongs to the owning frontend cleanup context. Concurrent start/first disposal
    /// and same-owner recursive disposal are unsupported; recursive disposal can throw or deadlock.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory callback is null.</exception>
    internal static Lazy<Task> CreateUpdateDisposal(
        Action stopCheck,
        Func<Task> disposeCore)
    {
        ArgumentNullException.ThrowIfNull(stopCheck);
        ArgumentNullException.ThrowIfNull(disposeCore);

        return new Lazy<Task>(async () =>
        {
            stopCheck();
            await disposeCore();
        });
    }

    /// <summary>
    /// Requests cancellation, joins the original check task and then releases its cancellation source.
    /// </summary>
    /// <remarks>
    /// Capture task/source after stopping admission and before cancellation callbacks. Every stage
    /// is attempted after earlier failures; all escaped check failures are retained without filtering.
    /// Normal requested cancellation and result publication remain the checking operation's policy.
    /// Plain awaits preserve the caller's cleanup context. No timeout, abandonment or guarantee of
    /// noncooperative callback, composed network operation or transport termination is provided.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory operation is null.</exception>
    /// <exception cref="Exception">One original failure is rethrown through EDI.</exception>
    /// <exception cref="OperationCanceledException">The sole retained failure is cancellation.</exception>
    /// <exception cref="AggregateException">Multiple direct failures are reported in order without flattening.</exception>
    internal static Task DisposeUpdateCheckAsync(
        Task? checkTask,
        Action cancelCheck,
        Action disposeCancellation)
    {
        ArgumentNullException.ThrowIfNull(cancelCheck);
        ArgumentNullException.ThrowIfNull(disposeCancellation);
        return CoreAsync();

        async Task CoreAsync()
        {
            List<Exception>? failures = null;
            try
            {
                cancelCheck();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (checkTask is not null)
            {
                try
                {
                    await checkTask;
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }

            try
            {
                disposeCancellation();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (failures is { Count: 1 })
            {
                ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(failures);
            }
        }
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await CodeAltaUpdateChecker.CheckCurrentAssemblyAsync(cancellationToken: cancellationToken);
            SetSnapshot(new CodeAltaUpdateCheckSnapshot(
                result.HasNewerVersion
                    ? CodeAltaUpdateCheckStatus.UpdateAvailable
                    : result.PackageFound
                        ? CodeAltaUpdateCheckStatus.Latest
                        : CodeAltaUpdateCheckStatus.PackageNotFound,
                result.PackageId,
                result.CurrentVersionText,
                result.LatestVersionText,
                result.LatestVersion?.IsPrerelease ?? false,
                result.IncludePrerelease,
                ErrorMessage: null));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetSnapshot(Snapshot with
            {
                Status = CodeAltaUpdateCheckStatus.Failed,
                ErrorMessage = ex.Message,
            });
        }
    }

    private void SetSnapshot(CodeAltaUpdateCheckSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
            _generation++;
        }
    }
}
