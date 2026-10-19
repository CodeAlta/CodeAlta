using XenoAtom.Logging;

namespace CodeAlta.Desktop;

/// <summary>
/// The thread pool of the desktop application. Every call of the page runs its handler on the pool, and on Windows a
/// child process that writes to a redirected pipe holds a pool thread for as long as it runs (git, background jobs,
/// ACP agents, the workers of speech and search, MCP servers). With its default minimum (the number of cores) the pool
/// then adds threads slowly, about one or two a second, and the calls of the page wait behind them. A higher minimum
/// lets it add the threads it needs at once; it does not create them before they are needed.
/// </summary>
internal static class DesktopThreadPool
{
    /// <summary>The fewest worker threads the pool adds without waiting.</summary>
    internal static int MinimumWorkers(int processorCount) => Math.Max(64, processorCount * 8);

    /// <summary>Raises the minimum of the pool, never lowers it, and logs what it set.</summary>
    internal static void Configure()
    {
        ThreadPool.GetMinThreads(out var workers, out var completion);
        var wanted = MinimumWorkers(Environment.ProcessorCount);
        if (workers >= wanted && completion >= wanted) return;
        var applied = ThreadPool.SetMinThreads(Math.Max(workers, wanted), Math.Max(completion, wanted));
        try
        {
            LogManager.GetLogger("CodeAlta.Desktop").Info(applied
                ? $"Thread pool: at least {Math.Max(workers, wanted)} threads without waiting (was {workers})."
                : $"Thread pool: the minimum of {wanted} threads was refused; it stays {workers}.");
        }
        catch
        {
            // Logging never stops the start.
        }
    }
}
