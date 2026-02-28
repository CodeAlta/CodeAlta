using System.Threading.Channels;

namespace CodeAlta.Search;

/// <summary>
/// Provides a channel-backed indexing queue.
/// </summary>
public sealed class IndexingQueue
{
    private readonly Channel<IndexingJob> _channel;
    private int _depth;

    /// <summary>
    /// Initializes a new instance of the <see cref="IndexingQueue"/> class.
    /// </summary>
    /// <param name="capacity">The bounded queue capacity.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="capacity"/> is not positive.</exception>
    public IndexingQueue(int capacity = 128)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        _channel = Channel.CreateBounded<IndexingJob>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
    }

    /// <summary>
    /// Gets current queue depth.
    /// </summary>
    public int Depth => Volatile.Read(ref _depth);

    /// <summary>
    /// Enqueues an indexing job.
    /// </summary>
    /// <param name="job">Job to enqueue.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="job"/> is <see langword="null"/>.</exception>
    public async Task EnqueueAsync(IndexingJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        await _channel.Writer.WriteAsync(job, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _depth);
    }

    /// <summary>
    /// Dequeues an indexing job.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The next queued job.</returns>
    public async Task<IndexingJob> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var job = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Decrement(ref _depth);
        return job;
    }

    /// <summary>
    /// Completes the queue writer.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();
}
