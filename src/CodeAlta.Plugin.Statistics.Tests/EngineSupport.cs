using System.Runtime.CompilerServices;
using CodeAlta.Agent;
using CodeAlta.Plugin.Statistics.History;
using CodeAlta.Plugin.Statistics.Store;

namespace CodeAlta.Plugin.Statistics.Tests;

/// <summary>
/// A clock the tests set by hand. Its timers are its own too: a delay that waits on this clock (<c>Task.Delay(span, time)</c>) ends
/// when <see cref="Advance"/> carries the clock past it, never with the time of the machine.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;
    private long _ticks;

    public ManualTime(DateTimeOffset now) => _now = now;

    /// <summary>Gets the number of timers that wait for the clock to move.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count(static timer => timer.Due is not null);
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _ticks;
        }
    }

    public override long TimestampFrequency => 1000;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock, and runs the timers that came due, in the order of their time, before it returns.</summary>
    public void Advance(TimeSpan span)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + span;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.Due is { } due && due <= target).MinBy(static timer => timer.Due);
                if (next is null)
                {
                    _ticks += (long)(target - _now).TotalMilliseconds;
                    _now = target;
                    return;
                }

                var due = next.Due!.Value;
                if (due > _now)
                {
                    _ticks += (long)(due - _now).TotalMilliseconds;
                    _now = due;
                }

                next.Due = next.Period is { } period ? _now + period : null;
            }

            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }

        public TimeSpan? Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this))
                {
                    return false;
                }

                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The journals of a session store, in memory: a file is a byte array and a time, and a reader gets a snapshot.</summary>
internal sealed class FakeJournalCatalog : ISessionJournalCatalog
{
    private readonly Dictionary<string, (byte[] Bytes, DateTimeOffset LastWrite)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _failAfterBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public int Opens { get; private set; }

    /// <summary>Gets how many times the files were listed.</summary>
    public int Lists { get; private set; }

    /// <summary>Gets or sets what a test does when a file is about to be opened, with the session and the offset: a file that changes between two readings.</summary>
    public Action<string, long>? Opening { get; set; }

    /// <summary>Gets the sum of the lengths of the files.</summary>
    public long TotalBytes
    {
        get
        {
            lock (_gate)
            {
                return _files.Values.Sum(static file => (long)file.Bytes.Length);
            }
        }
    }

    public void Set(string sessionId, byte[] bytes, DateTimeOffset lastWrite)
    {
        lock (_gate)
        {
            _files[sessionId] = (bytes, lastWrite);
        }
    }

    public void Set(JournalBuilder builder, DateTimeOffset lastWrite) => Set(builder.SessionId, builder.ToBytes(), lastWrite);

    public void Append(string sessionId, byte[] more, DateTimeOffset lastWrite)
    {
        lock (_gate)
        {
            _files[sessionId] = ([.. _files[sessionId].Bytes, .. more], lastWrite);
        }
    }

    public void Remove(string sessionId)
    {
        lock (_gate)
        {
            _files.Remove(sessionId);
        }
    }

    /// <summary>Makes the reading of a session fail after some bytes: a disk that goes away in the middle of a file.</summary>
    public void FailAfter(string sessionId, long bytes)
    {
        lock (_gate)
        {
            _failAfterBytes[sessionId] = bytes;
        }
    }

    public void StopFailing()
    {
        lock (_gate)
        {
            _failAfterBytes.Clear();
        }
    }

    public async IAsyncEnumerable<SessionJournalFile> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SessionJournalFile[] files;
        lock (_gate)
        {
            Lists++;
            files = [.. _files.Select(static pair => new SessionJournalFile(pair.Key, "memory://" + pair.Key, pair.Value.Bytes.Length, pair.Value.LastWrite))
                .OrderByDescending(static file => file.LastWriteUtc).ThenByDescending(static file => file.SessionId, StringComparer.Ordinal)];
        }

        foreach (var file in files)
        {
            yield return file;
        }

        await Task.CompletedTask;
    }

    public ValueTask<SessionJournalFile?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_files.TryGetValue(sessionId, out var file)
                ? new SessionJournalFile(sessionId, "memory://" + sessionId, file.Bytes.Length, file.LastWrite)
                : null);
        }
    }

    public ValueTask<Stream?> OpenAsync(string sessionId, long offset = 0, CancellationToken cancellationToken = default)
    {
        Opening?.Invoke(sessionId, offset);
        lock (_gate)
        {
            Opens++;
            if (!_files.TryGetValue(sessionId, out var file))
            {
                return ValueTask.FromResult<Stream?>(null);
            }

            Stream stream = new MemoryStream(file.Bytes, writable: false);
            if (_failAfterBytes.TryGetValue(sessionId, out var limit))
            {
                stream = new FailingStream(stream, limit);
            }

            stream.Position = offset;
            return ValueTask.FromResult<Stream?>(stream);
        }
    }

    private sealed class FailingStream(Stream inner, long limit) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (inner.Position >= limit)
            {
                throw new IOException("The disk went away.");
            }

            return inner.Read(buffer, offset, (int)Math.Min(count, Math.Max(1, limit - inner.Position)));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A store, an engine and a fake session store, with the clock of the tests.</summary>
internal sealed class EngineHarness : IAsyncDisposable
{
    /// <summary>The moment of the tests: a Friday noon in UTC.</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private EngineHarness(StoreHarness store)
    {
        Store = store;
    }

    public StoreHarness Store { get; }

    public FakeJournalCatalog Journals { get; } = new();

    public ManualTime Time { get; } = new(Now);

    public StatisticsEngine Engine { get; private set; } = null!;

    public static async Task<EngineHarness> CreateAsync(TimeZoneInfo? timeZone = null, Action<StatisticsEngineOptionsBuilder>? configure = null)
    {
        var harness = new EngineHarness(await StoreHarness.CreateAsync(timeZone));
        harness.Engine = harness.CreateEngine(configure);
        return harness;
    }

    public StatisticsEngine CreateEngine(Action<StatisticsEngineOptionsBuilder>? configure = null)
    {
        var builder = new StatisticsEngineOptionsBuilder();
        configure?.Invoke(builder);
        return new StatisticsEngine(Store.Store, Journals, new StatisticsEngineOptions
        {
            Time = Time,
            StartDelay = TimeSpan.Zero,
            FlowDebounce = builder.FlowDebounce,
            ChunkBytes = builder.ChunkBytes,
            DeadAfter = builder.DeadAfter,
            RescanInterval = builder.RescanInterval,
            ResolveProjectNames = builder.ResolveProjectNames,
            ProjectDirectory = builder.Directory,
        });
    }

    /// <summary>A restart of the application: the database and the engine are made again, the sessions stay.</summary>
    public async Task RestartAsync(Action<StatisticsEngineOptionsBuilder>? configure = null)
    {
        await Engine.DisposeAsync();
        await Store.ReopenAsync();
        Engine = CreateEngine(configure);
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Store.DisposeAsync();
    }

    /// <summary>A small journal: a header, and runs of a prompt, a request, an answer and the end.</summary>
    public static JournalBuilder Session(string id, DateTimeOffset start, int runs = 2, string provider = "codex", bool endLastRun = true, long input = 1000, string tag = "run")
    {
        var builder = new JournalBuilder(id, provider);
        builder.Header(start).State(start);
        return AddRuns(builder, start, runs, endLastRun, input, tag);
    }

    /// <summary>Adds runs to a journal: a prompt, a request, an answer and the end of each, ten minutes apart.</summary>
    public static JournalBuilder AddRuns(JournalBuilder builder, DateTimeOffset start, int runs, bool endLastRun = true, long input = 1000, string tag = "run")
    {
        for (var run = 0; run < runs; run++)
        {
            var at = start.AddMinutes(run * 10);
            var runId = $"{builder.SessionId}-{tag}{run}";
            builder.User(at.AddSeconds(1), runId, "a prompt of some words")
                .Usage(at.AddSeconds(5), runId, input: input * (run + 1), output: 100 + run)
                .Assistant(at.AddSeconds(10), runId);
            if (run < runs - 1 || endLastRun)
            {
                builder.Idle(at.AddSeconds(20), runId);
            }
        }

        return builder;
    }

    /// <summary>The lines a session writes when it goes on: runs of another tag, to be appended to its journal.</summary>
    public static byte[] More(string id, DateTimeOffset start, int runs = 1, string tag = "more")
        => AddRuns(new JournalBuilder(id), start, runs, true, 1000, tag).ToBytes();

    public static DateTimeOffset EndOf(JournalBuilder builder, DateTimeOffset start, int runs = 2) => start.AddMinutes((runs - 1) * 10).AddSeconds(20);

    public async Task<long> SumAsync(string table, string column)
        => await Store.Store.ReadAsync(sql => sql.ScalarLong($"SELECT COALESCE(SUM({column}), 0) FROM {Store.Store.Prefix}{table}"));
}

/// <summary>The settings the tests change.</summary>
internal sealed class StatisticsEngineOptionsBuilder
{
    public TimeSpan FlowDebounce { get; set; } = TimeSpan.Zero;

    public long ChunkBytes { get; set; } = 64L * 1024 * 1024;

    public TimeSpan DeadAfter { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan RescanInterval { get; set; } = TimeSpan.FromMinutes(5);

    public Func<CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>>? ResolveProjectNames { get; set; }

    public CodeAlta.Plugin.Statistics.Query.IProjectDirectory? Directory { get; set; }
}
