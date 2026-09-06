using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tui.App;

internal sealed class RuntimeEventPump : IAsyncDisposable
{
    private readonly SessionRuntimeService _runtimeService;
    private readonly ISessionRuntimeEventProjector _runtimeEventProjector;
    private readonly CancellationTokenSource _disposeCts = new();
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    public RuntimeEventPump(
        SessionRuntimeService runtimeService,
        ISessionRuntimeEventProjector runtimeEventProjector)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(runtimeEventProjector);

        _runtimeService = runtimeService;
        _runtimeEventProjector = runtimeEventProjector;
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (_pumpTask is not null)
        {
            return;
        }

        _pumpCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        // Runtime event streaming is a background pump. Delivery back into the shell happens via
        // the shell controller's explicit UI dispatch path.
        _pumpTask = Task.Run(
            () => RunAsync(_pumpCts.Token),
            CancellationToken.None);
        global::CodeAlta.Tui.CodeAltaTaskMonitor.Observe(_pumpTask, "Runtime event pump");
    }

    public async ValueTask DisposeAsync()
    {
        var pumpTask = _pumpTask;
        var pumpCts = _pumpCts;
        await DisposePumpAsync(
            pumpTask,
            _disposeCts.Cancel,
            () => pumpCts?.Cancel(),
            () => pumpCts?.Dispose(),
            _disposeCts.Dispose).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var runtimeEvent in _runtimeService.StreamEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                _runtimeEventProjector.QueueRuntimeEvent(runtimeEvent, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Attempts the pump's existing cancellation, original-task join and source-release stages in order.
    /// </summary>
    /// <remarks>
    /// Validate all four mandatory callbacks synchronously, then start the local core inline.
    /// Support never-started pumps and serialized Start/first disposal without changing admission,
    /// scheduling, RunAsync or fatal task monitoring. The caller snapshots its original task and linked
    /// source before callbacks; the readonly disposal source remains the same instance-owned resource.
    /// Independently attempt disposal cancellation, pump cancellation, the original task join, linked
    /// source release and disposal-source release. Even after earlier errors, release follows actual
    /// terminal completion of the original task. Only the join suppresses OCE unconditionally, including
    /// an awaited faulted OCE; callback/release OCE remains a failure and aggregates are never unwrapped.
    /// Preserve a lone retained failure through EDI and multiple direct references in execution order,
    /// without flattening or deduplication. A pending join avoids captured frontend context; completed
    /// awaits may stay inline, so there is no promised thread switch. The frontend's own plain await
    /// retains its later context. No cache, retry, timeout or repeated/concurrent disposal guarantee is added.
    /// These are exception channels, not proof that current channel callbacks or ordinary CTS release throw.
    /// The original successful join-before-release did not establish an updater-style released-source
    /// token-read race. Joining covers enumeration, synchronous queue calls and enumerator cleanup, not
    /// queued UI events/wakes, publishers, runtime/stream/controller/plugin termination or external Cancel
    /// traversal completion. External cancellation callers own their callback errors; this operation
    /// retains errors from its own calls. Unchanged fatal monitoring may terminate the process, and a
    /// noncooperative callback/task can prevent later stages indefinitely. Hidden construction/publication,
    /// Program/early admission and other lower-owner cleanup remain separate work.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A mandatory callback is null.</exception>
    /// <exception cref="Exception">A lone retained original failure is rethrown through EDI.</exception>
    /// <exception cref="OperationCanceledException">A cancellation/release callback supplies the sole retained OCE.</exception>
    /// <exception cref="AggregateException">Multiple direct failures are reported in order, or an original aggregate is retained.</exception>
    internal static Task DisposePumpAsync(
        Task? pumpTask,
        Action cancelDisposal,
        Action cancelPump,
        Action disposePumpCancellation,
        Action disposeDisposalCancellation)
    {
        ArgumentNullException.ThrowIfNull(cancelDisposal);
        ArgumentNullException.ThrowIfNull(cancelPump);
        ArgumentNullException.ThrowIfNull(disposePumpCancellation);
        ArgumentNullException.ThrowIfNull(disposeDisposalCancellation);
        return CoreAsync();

        async Task CoreAsync()
        {
            List<Exception>? failures = null;
            try
            {
                cancelDisposal();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                cancelPump();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (pumpTask is not null)
            {
                try
                {
                    await pumpTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
                }
            }

            try
            {
                disposePumpCancellation();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            try
            {
                disposeDisposalCancellation();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }

            if (failures is { Count: 1 })
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures is { Count: > 1 })
            {
                throw new AggregateException(failures);
            }
        }
    }
}
