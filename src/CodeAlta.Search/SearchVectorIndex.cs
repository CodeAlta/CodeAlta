using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CodeAlta.Search;

/// <summary>What a row of a <see cref="SearchVectorIndex"/> is filtered by: the columns of its item.</summary>
/// <param name="Kind">The kind of the item.</param>
/// <param name="Role">Who wrote it, for a message.</param>
/// <param name="At">When it was written.</param>
/// <param name="SpaceId">Its space, or null.</param>
/// <param name="ProjectId">Its project, or null.</param>
/// <param name="SessionId">Its session, or null.</param>
public readonly record struct SearchVectorRowInfo(SearchKind Kind, SearchRole Role, DateTimeOffset At, string? SpaceId, string? ProjectId, string? SessionId);

/// <summary>
/// The 8-bit vectors of one vector set in memory, kept as columns in insertion order: the codes of every row one after
/// the other, a scale per row, the chunk ids, and the columns a <see cref="SearchScope"/> filters on (kind, role, time,
/// and the space, project and session as interned numbers). A search applies the scope to a row before comparing it,
/// and keeps the best rows in a heap; beyond <see cref="ParallelThreshold"/> rows it runs in parallel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threads.</b> Writes (<see cref="Add(long, ReadOnlySpan{float}, in SearchVectorRowInfo)"/>, <see cref="Remove"/>,
/// <see cref="Compact"/>) take a lock and are meant for one writer; searches take no lock and run concurrently. A write
/// publishes an immutable snapshot (the arrays and the number of rows) that a search reads once: an addition writes
/// past the rows of the published snapshots, a growth or a compaction writes new arrays and publishes them, so a search
/// never sees a half-written row. A removal clears the kind of its row in place (kind 0 matches no scope): a search
/// running at that moment may or may not count the row, as if it had run just before or just after.
/// </para>
/// <para>
/// <b>Memory.</b> One byte per dimension, plus 31 bytes per row for the scale, the chunk id and the filter columns.
/// Removed rows stay as tombstones until they are more than a fifth of the rows, then the columns are compacted.
/// </para>
/// </remarks>
public sealed class SearchVectorIndex
{
    /// <summary>The rows beyond which a search runs in parallel.</summary>
    public const int ParallelThreshold = 50_000;

    private const int RowsPerPartition = 4_096;
    private const double CompactionRatio = 0.2;
    private const int Any = int.MinValue;

    private readonly Lock _writeLock = new();
    private readonly Interner _spaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Interner _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Interner _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _rows = new();
    private Columns _columns;
    private int _used;
    private int _tombstones;
    private Snapshot _snapshot;

    /// <summary>Creates an empty index.</summary>
    /// <param name="dimensions">The width of the vectors.</param>
    /// <param name="capacity">The rows to make room for at first.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dimensions"/> is not positive, or <paramref name="capacity"/> is negative.</exception>
    public SearchVectorIndex(int dimensions, int capacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Dimensions = dimensions;
        _columns = new Columns(dimensions, Math.Max(capacity, 16));
        _snapshot = new Snapshot(_columns, 0);
    }

    /// <summary>Gets the width of the vectors.</summary>
    public int Dimensions { get; }

    /// <summary>Gets the rows that are not removed.</summary>
    public int Count
    {
        get { lock (_writeLock) return _rows.Count; }
    }

    /// <summary>Gets the removed rows not yet compacted away.</summary>
    public int Tombstones
    {
        get { lock (_writeLock) return _tombstones; }
    }

    /// <summary>Gets the bytes of the columns, room for later rows included.</summary>
    public long MemoryBytes
    {
        get { lock (_writeLock) return _columns.Bytes; }
    }

    /// <summary>Gets whether a chunk has a row.</summary>
    /// <param name="chunkId">The chunk.</param>
    /// <returns>True when it has one.</returns>
    public bool Contains(long chunkId)
    {
        lock (_writeLock) return _rows.ContainsKey(chunkId);
    }

    /// <summary>Adds the vector of a chunk, quantized here; a chunk that has a row already gets a new one.</summary>
    /// <param name="chunkId">The chunk.</param>
    /// <param name="vector">Its vector, of <see cref="Dimensions"/> values.</param>
    /// <param name="info">The columns of its item.</param>
    /// <exception cref="ArgumentException">The vector has another width, or <paramref name="info"/> has no kind.</exception>
    public void Add(long chunkId, ReadOnlySpan<float> vector, in SearchVectorRowInfo info)
    {
        if (vector.Length != Dimensions) throw new ArgumentException($"The vector has {vector.Length} values instead of {Dimensions}.", nameof(vector));
        Span<sbyte> codes = Dimensions <= 1024 ? stackalloc sbyte[Dimensions] : new sbyte[Dimensions];
        var scale = SearchVectorKernels.Quantize(vector, codes);
        Add(chunkId, codes, scale, info);
    }

    /// <summary>Adds the 8-bit codes of a chunk; a chunk that has a row already gets a new one.</summary>
    /// <param name="chunkId">The chunk.</param>
    /// <param name="codes">Its codes, of <see cref="Dimensions"/> values.</param>
    /// <param name="scale">The scale of its codes.</param>
    /// <param name="info">The columns of its item.</param>
    /// <exception cref="ArgumentException">The codes have another width, or <paramref name="info"/> has no kind.</exception>
    public void Add(long chunkId, ReadOnlySpan<sbyte> codes, float scale, in SearchVectorRowInfo info)
    {
        if (codes.Length != Dimensions) throw new ArgumentException($"The codes have {codes.Length} values instead of {Dimensions}.", nameof(codes));
        if ((byte)info.Kind is 0 or >= 64) throw new ArgumentException("The row has no kind.", nameof(info));
        lock (_writeLock)
        {
            var replaced = _rows.TryGetValue(chunkId, out var existing);
            if (replaced) Bury(existing, chunkId);
            if (_used == _columns.Capacity)
            {
                var grown = new Columns(Dimensions, _columns.Capacity * 2);
                grown.CopyFrom(_columns, 0, 0, _used);
                _columns = grown;
            }

            var row = _used;
            var columns = _columns;
            codes.CopyTo(columns.Codes.AsSpan(row * Dimensions, Dimensions));
            columns.Scales[row] = scale;
            columns.ChunkIds[row] = chunkId;
            columns.Roles[row] = (byte)info.Role;
            columns.At[row] = info.At.ToUnixTimeMilliseconds();
            columns.Spaces[row] = _spaces.Intern(info.SpaceId);
            columns.Projects[row] = _projects.Intern(info.ProjectId);
            columns.Sessions[row] = _sessions.Intern(info.SessionId);
            columns.Kinds[row] = (byte)info.Kind;
            _rows[chunkId] = row;
            _used++;
            // Published last: a search reads the rows of its snapshot only, so it never sees this one half written.
            Volatile.Write(ref _snapshot, new Snapshot(columns, _used));
            if (replaced) CompactIfNeeded();
        }
    }

    /// <summary>Removes the row of a chunk; the columns are compacted once removed rows are more than a fifth of them.</summary>
    /// <param name="chunkId">The chunk.</param>
    /// <returns>True when it had a row.</returns>
    public bool Remove(long chunkId)
    {
        lock (_writeLock)
        {
            if (!_rows.TryGetValue(chunkId, out var row)) return false;
            Bury(row, chunkId);
            CompactIfNeeded();
            return true;
        }
    }

    private void Bury(int row, long chunkId)
    {
        _columns.Kinds[row] = 0;
        _rows.Remove(chunkId);
        _tombstones++;
    }

    private void CompactIfNeeded()
    {
        if (_tombstones > 0 && _tombstones > _used * CompactionRatio) CompactLocked();
    }

    /// <summary>Removes the tombstones now: the rows that remain keep their order.</summary>
    public void Compact()
    {
        lock (_writeLock) CompactLocked();
    }

    private void CompactLocked()
    {
        var live = _rows.Count;
        var compacted = new Columns(Dimensions, Math.Max(16, live + live / 4));
        var target = 0;
        for (var row = 0; row < _used; row++)
        {
            if (_columns.Kinds[row] == 0) continue;
            compacted.CopyFrom(_columns, row, target, 1);
            _rows[_columns.ChunkIds[row]] = target;
            target++;
        }

        _columns = compacted;
        _used = target;
        _tombstones = 0;
        Volatile.Write(ref _snapshot, new Snapshot(compacted, target));
    }

    /// <summary>Finds the rows closest to a query in a scope, the best first.</summary>
    /// <param name="query">The vector of the query, of <see cref="Dimensions"/> values.</param>
    /// <param name="scope">The scope; <see cref="SearchScope.CatalogFilter"/> is left to the caller.</param>
    /// <param name="count">The most rows.</param>
    /// <returns>The chunks and their similarity (the dot product of the vectors, as the codes give it).</returns>
    /// <exception cref="ArgumentException">The query has another width.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is null.</exception>
    public IReadOnlyList<(long ChunkId, float Similarity)> Search(ReadOnlySpan<float> query, SearchScope scope, int count)
        => Search(query, scope, count, Environment.ProcessorCount);

    /// <summary>As <see cref="Search(ReadOnlySpan{float}, SearchScope, int)"/>, with the most threads to use (1 for one thread).</summary>
    internal IReadOnlyList<(long ChunkId, float Similarity)> Search(ReadOnlySpan<float> query, SearchScope scope, int count, int parallelism)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (query.Length != Dimensions) throw new ArgumentException($"The query has {query.Length} values instead of {Dimensions}.", nameof(query));
        if (count <= 0) return [];
        var snapshot = Volatile.Read(ref _snapshot);
        // Built after the snapshot is read: every interned id of its rows is known to the filter.
        if (Filter.Create(scope, _spaces, _projects, _sessions) is not { } filter) return [];
        var codes = new sbyte[Dimensions];
        var queryScale = SearchVectorKernels.Quantize(query, codes);
        var rows = snapshot.Rows;
        var top = new TopRows(count);
        if (rows < ParallelThreshold || parallelism <= 1)
        {
            Scan(snapshot.Columns, codes, filter, 0, rows, top);
        }
        else
        {
            // Small parts taken by the threads as they are free, so that a slower core takes fewer; one heap per thread.
            var partitions = (rows + RowsPerPartition - 1) / RowsPerPartition;
            var merge = new Lock();
            Parallel.For(0, partitions, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, () => new TopRows(count),
                (part, _, local) =>
                {
                    Scan(snapshot.Columns, codes, filter, part * RowsPerPartition, Math.Min(rows, (part + 1) * RowsPerPartition), local);
                    return local;
                },
                local =>
                {
                    lock (merge) local.CopyTo(top);
                });
        }

        return top.ToResult(snapshot.Columns.ChunkIds, queryScale);
    }

    private void Scan(Columns columns, sbyte[] query, Filter filter, int start, int end, TopRows top)
    {
        var dimensions = Dimensions;
        ref var codes = ref MemoryMarshal.GetArrayDataReference(columns.Codes);
        ref var first = ref MemoryMarshal.GetArrayDataReference(query);
        var scales = columns.Scales;
        for (var row = start; row < end; row++)
        {
            if (!filter.Matches(columns, row)) continue;
            var score = SearchVectorKernels.Dot(ref Unsafe.Add(ref codes, (nint)row * dimensions), ref first, dimensions) * scales[row];
            if (score > top.Threshold) top.Add(score, row);
        }
    }

    private sealed record Snapshot(Columns Columns, int Rows);

    private sealed class Columns
    {
        public Columns(int dimensions, int capacity)
        {
            Dimensions = dimensions;
            Capacity = capacity;
            Codes = new sbyte[(long)dimensions * capacity];
            Scales = new float[capacity];
            ChunkIds = new long[capacity];
            Kinds = new byte[capacity];
            Roles = new byte[capacity];
            At = new long[capacity];
            Spaces = new int[capacity];
            Projects = new int[capacity];
            Sessions = new int[capacity];
        }

        public int Dimensions { get; }
        public int Capacity { get; }
        public sbyte[] Codes { get; }
        public float[] Scales { get; }
        public long[] ChunkIds { get; }
        public byte[] Kinds { get; }
        public byte[] Roles { get; }
        public long[] At { get; }
        public int[] Spaces { get; }
        public int[] Projects { get; }
        public int[] Sessions { get; }

        public long Bytes => (long)Capacity * (Dimensions + sizeof(float) + sizeof(long) + 2 + sizeof(long) + 3 * sizeof(int));

        public void CopyFrom(Columns source, int from, int to, int rows)
        {
            Array.Copy(source.Codes, (long)from * Dimensions, Codes, (long)to * Dimensions, (long)rows * Dimensions);
            Array.Copy(source.Scales, from, Scales, to, rows);
            Array.Copy(source.ChunkIds, from, ChunkIds, to, rows);
            Array.Copy(source.Kinds, from, Kinds, to, rows);
            Array.Copy(source.Roles, from, Roles, to, rows);
            Array.Copy(source.At, from, At, to, rows);
            Array.Copy(source.Spaces, from, Spaces, to, rows);
            Array.Copy(source.Projects, from, Projects, to, rows);
            Array.Copy(source.Sessions, from, Sessions, to, rows);
        }
    }

    /// <summary>Numbers for the strings of a column, 0 for null; read by searches while the writer adds, so it locks.</summary>
    private sealed class Interner(StringComparer comparer)
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, int> _ids = new(comparer);

        public int Count
        {
            get { lock (_lock) return _ids.Count + 1; }
        }

        public int Intern(string? value)
        {
            if (value is null) return 0;
            lock (_lock)
            {
                if (!_ids.TryGetValue(value, out var id)) _ids[value] = id = _ids.Count + 1;
                return id;
            }
        }

        // 0 for null, -1 for a string no row has.
        public int Find(string? value)
        {
            if (value is null) return 0;
            lock (_lock) return _ids.TryGetValue(value, out var id) ? id : -1;
        }

        public bool[]? Flags(IReadOnlySet<string>? values, bool nullValue)
        {
            if (values is null) return null;
            var flags = new bool[Count];
            flags[0] = nullValue;
            foreach (var value in values)
            {
                if (Find(value) is > 0 and var id && id < flags.Length) flags[id] = true;
            }

            return flags;
        }
    }

    /// <summary>A scope as numbers, compared with the columns of a row as SQL compares it with the columns of an item.</summary>
    private sealed class Filter
    {
        private ulong _kinds;
        private bool _simple;
        private byte _role;
        private long _since = long.MinValue;
        private long _until = long.MaxValue;
        private int _space = Any;
        private int _project = Any;
        private int _session = Any;
        private int _except = Any;
        private bool[]? _projects;
        private bool[]? _excludedProjects;
        private bool[]? _excludedSpaces;

        // Null when no row can match (a project or a session no row has).
        public static Filter? Create(SearchScope scope, Interner spaces, Interner projects, Interner sessions)
        {
            var filter = new Filter();
            if (scope.Kinds is { } kinds)
            {
                foreach (var kind in kinds)
                {
                    if ((byte)kind is > 0 and < 64) filter._kinds |= 1UL << (byte)kind;
                }
            }
            else
            {
                filter._kinds = ~1UL;
            }

            filter._role = (byte)scope.Role;
            if (scope.Since is { } since) filter._since = since.ToUnixTimeMilliseconds();
            if (scope.Until is { } until) filter._until = until.ToUnixTimeMilliseconds();
            if (scope.SpaceId is { } space) filter._space = spaces.Find(space);
            if (scope.ProjectId is { } project)
            {
                filter._project = projects.Find(project);
                if (filter._project < 0) return null;
            }
            else
            {
                filter._projects = projects.Flags(scope.Projects, nullValue: true);
            }

            if (scope.SessionId is { } session)
            {
                filter._session = sessions.Find(session);
                if (filter._session < 0) return null;
            }

            if (scope.ExceptSessionId is { } except) filter._except = sessions.Find(except);
            filter._excludedProjects = projects.Flags(scope.ExcludedProjects, nullValue: false);
            filter._excludedSpaces = spaces.Flags(scope.ExcludedSpaces, nullValue: false);
            filter._simple = filter._role == 0 && filter._since == long.MinValue && filter._until == long.MaxValue && filter._space == Any
                && filter._project == Any && filter._session == Any && filter._except <= 0 && filter._projects is null
                && filter._excludedProjects is null && filter._excludedSpaces is null;
            return filter;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Matches(Columns columns, int row)
        {
            // A removed row has kind 0, which no mask holds.
            if (((_kinds >> columns.Kinds[row]) & 1) == 0) return false;
            if (_simple) return true;
            if (_role != 0 && columns.Roles[row] != _role) return false;
            var at = columns.At[row];
            if (at < _since || at > _until) return false;
            var space = columns.Spaces[row];
            if (_space != Any && space != 0 && space != _space) return false;
            if (_excludedSpaces is { } excludedSpaces && space < excludedSpaces.Length && excludedSpaces[space]) return false;
            var project = columns.Projects[row];
            if (_project != Any && project != _project) return false;
            if (_projects is { } allowed && (project >= allowed.Length || !allowed[project])) return false;
            if (_excludedProjects is { } excludedProjects && project < excludedProjects.Length && excludedProjects[project]) return false;
            var session = columns.Sessions[row];
            if (_session != Any && session != _session) return false;
            if (_except > 0 && session == _except) return false;
            return true;
        }
    }

    /// <summary>The best rows seen: a heap whose root is the worst of them.</summary>
    private sealed class TopRows(int capacity)
    {
        private readonly float[] _scores = new float[capacity];
        private readonly int[] _rows = new int[capacity];
        private int _count;

        public float Threshold => _count < _scores.Length ? float.NegativeInfinity : _scores[0];

        public void Add(float score, int row)
        {
            if (_count < _scores.Length)
            {
                var i = _count++;
                _scores[i] = score;
                _rows[i] = row;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (_scores[parent] <= _scores[i]) break;
                    Swap(parent, i);
                    i = parent;
                }

                return;
            }

            if (score <= _scores[0]) return;
            _scores[0] = score;
            _rows[0] = row;
            var j = 0;
            while (true)
            {
                int left = 2 * j + 1, right = left + 1, smallest = j;
                if (left < _count && _scores[left] < _scores[smallest]) smallest = left;
                if (right < _count && _scores[right] < _scores[smallest]) smallest = right;
                if (smallest == j) break;
                Swap(smallest, j);
                j = smallest;
            }
        }

        private void Swap(int a, int b)
        {
            (_scores[a], _scores[b]) = (_scores[b], _scores[a]);
            (_rows[a], _rows[b]) = (_rows[b], _rows[a]);
        }

        public void CopyTo(TopRows other)
        {
            for (var i = 0; i < _count; i++) other.Add(_scores[i], _rows[i]);
        }

        public IReadOnlyList<(long ChunkId, float Similarity)> ToResult(long[] chunkIds, float queryScale)
        {
            var order = new int[_count];
            for (var i = 0; i < _count; i++) order[i] = i;
            Array.Sort(order, (a, b) => _scores[b].CompareTo(_scores[a]));
            var result = new (long, float)[_count];
            for (var i = 0; i < _count; i++) result[i] = (chunkIds[_rows[order[i]]], _scores[order[i]] * queryScale);
            return result;
        }
    }
}
