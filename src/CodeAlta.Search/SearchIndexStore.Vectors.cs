using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Search;

/// <summary>What makes the vectors of a model comparable: a vector set is kept per identity.</summary>
/// <param name="Model">The model.</param>
/// <param name="Revision">Its revision (a commit of its files).</param>
/// <param name="Dimensions">The width of its vectors.</param>
/// <param name="Prompts">The version of the prompts given to the model before a query and a document.</param>
/// <param name="Runtime">The version of the runtime that runs it: two versions do not give the same vectors.</param>
public sealed record SearchVectorSetIdentity(string Model, string Revision, int Dimensions, string Prompts, string Runtime);

/// <summary>Where a vector set is.</summary>
public enum SearchVectorSetState : byte
{
    /// <summary>Being filled in the background while another set answers.</summary>
    Building = 1,

    /// <summary>The set queries use; there is at most one.</summary>
    Active = 2,

    /// <summary>Replaced by another set; kept until it is deleted.</summary>
    Retired = 3,
}

/// <summary>A vector set of the index.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Identity">What its vectors are made with.</param>
/// <param name="State">Where it is.</param>
/// <param name="CreatedAt">When it was created.</param>
public sealed record SearchVectorSet(long Id, SearchVectorSetIdentity Identity, SearchVectorSetState State, DateTimeOffset CreatedAt);

/// <summary>A vector made for a text.</summary>
/// <param name="EmbedHash">The hash of the text, as <see cref="SearchEmbeddingWork.EmbedHash"/> gave it.</param>
/// <param name="Vector">The vector, of the width of its set.</param>
public readonly record struct SearchVectorInput(byte[] EmbedHash, ReadOnlyMemory<float> Vector);

/// <summary>A text that has no vector yet in a set.</summary>
/// <param name="ChunkId">A chunk that holds it.</param>
/// <param name="EmbedHash">The hash of the title and the text: the key of its vector.</param>
/// <param name="Title">The title given to the model with the text.</param>
/// <param name="Text">The text.</param>
public sealed record SearchEmbeddingWork(long ChunkId, byte[] EmbedHash, string Title, string Text);

/// <summary>How many chunks of the index have a vector in a set.</summary>
/// <param name="Chunks">The chunks.</param>
/// <param name="Embedded">The chunks whose text has a vector.</param>
public readonly record struct SearchVectorCoverage(long Chunks, long Embedded)
{
    /// <summary>Gets the share of chunks that have a vector, from 0 to 1; 1 when there are no chunks.</summary>
    public double Ratio => Chunks == 0 ? 1 : (double)Embedded / Chunks;
}

/// <summary>A row read for a <see cref="SearchVectorIndex"/>.</summary>
internal readonly record struct SearchVectorRow(long ChunkId, float Scale, sbyte[] Codes, SearchVectorRowInfo Info);

/// <summary>A vector set loaded in memory, and where the log of changes was when it was read.</summary>
internal sealed record SearchVectorLoad(SearchVectorSet Set, SearchVectorIndex Index, string Epoch, long Cursor, long Chunks);

/// <summary>What changed for the vectors of the active set since a cursor of the log.</summary>
internal sealed record SearchVectorChanges
{
    /// <summary>Gets the active set, or null when there is none.</summary>
    public SearchVectorSet? Active { get; init; }

    /// <summary>Gets whether the set must be loaded again: another set, another file, or changes that are no longer all in the log.</summary>
    public bool Reload { get; init; }

    /// <summary>Gets the cursor after these changes.</summary>
    public long Cursor { get; init; }

    /// <summary>Gets the chunks that were added, removed or moved: their rows are made again from <see cref="Rows"/>.</summary>
    public IReadOnlyList<long> Touched { get; init; } = [];

    /// <summary>Gets the rows of the touched chunks that have a vector, and of the chunks whose vector came.</summary>
    public IReadOnlyList<SearchVectorRow> Rows { get; init; } = [];

    /// <summary>Gets the chunks of the index, or -1 when nothing changed.</summary>
    public long Chunks { get; init; } = -1;
}

// The vectors of the index. A vector set holds one vector per text (by the hash of what is embedded), so a chunk whose
// text did not change keeps its vector when its item is written again, and a text repeated in several chunks has one.
// The log of changes (search_change) is filled by triggers: a chunk added or removed, an item whose filter columns
// changed, a vector added. The in-memory index follows it from a cursor instead of reading everything again.
public sealed partial class SearchIndexStore
{
    private const string ChangeEpochKey = "change_epoch";
    private const string ChangeFloorKey = "change_floor";
    private const long KeptChanges = 100_000;
    private const string VectorSetColumns = "id, model, revision, dims, prompts, runtime, state, created_at";

    private static async ValueTask CreateChangeLogAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE search_change (seq INTEGER PRIMARY KEY AUTOINCREMENT, chunk_id INTEGER, set_id INTEGER, embed_hash BLOB);
            CREATE TRIGGER search_chunk_added AFTER INSERT ON search_chunk
              BEGIN INSERT INTO search_change(chunk_id) VALUES (NEW.id); END;
            CREATE TRIGGER search_chunk_removed AFTER DELETE ON search_chunk
              BEGIN INSERT INTO search_change(chunk_id) VALUES (OLD.id); END;
            CREATE TRIGGER search_item_moved AFTER UPDATE OF kind, role, at, space_id, project_id, session_id ON search_item
              WHEN OLD.kind IS NOT NEW.kind OR OLD.role IS NOT NEW.role OR OLD.at IS NOT NEW.at OR OLD.space_id IS NOT NEW.space_id
                OR OLD.project_id IS NOT NEW.project_id OR OLD.session_id IS NOT NEW.session_id
              BEGIN INSERT INTO search_change(chunk_id) SELECT id FROM search_chunk WHERE item_id = NEW.id; END;
            CREATE TRIGGER search_vector_added AFTER INSERT ON search_vector
              BEGIN INSERT INTO search_change(set_id, embed_hash) VALUES (NEW.set_id, NEW.embed_hash); END;
            INSERT INTO search_meta(key, value) VALUES ('change_epoch', lower(hex(randomblob(8))));
            """, cancellationToken).ConfigureAwait(false);
    }

    // The log keeps its last changes only; a reader whose cursor is older than what was trimmed loads again.
    private static async ValueTask TrimChangesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        long last;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT coalesce(max(seq), 0) FROM search_change";
            last = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (last - KeptChanges > 0) await DeleteChangesAsync(connection, last - KeptChanges, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask DeleteChangesAsync(SqliteConnection connection, long through, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM search_change WHERE seq <= $through;
            INSERT INTO search_meta(key, value) VALUES ($key, $through)
              ON CONFLICT(key) DO UPDATE SET value = CAST(max(CAST(value AS INTEGER), CAST(excluded.value AS INTEGER)) AS TEXT);
            """;
        command.Parameters.AddWithValue("$through", through);
        command.Parameters.AddWithValue("$key", ChangeFloorKey);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists the vector sets.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The sets, the oldest first.</returns>
    public async ValueTask<IReadOnlyList<SearchVectorSet>> GetVectorSetsAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<IReadOnlyList<SearchVectorSet>>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {VectorSetColumns} FROM search_vector_set ORDER BY id";
            var list = new List<SearchVectorSet>();
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) list.Add(ReadVectorSet(reader));
            return list;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the set queries use, or null.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The active set.</returns>
    public async ValueTask<SearchVectorSet?> GetActiveVectorSetAsync(CancellationToken cancellationToken = default)
        => (await GetVectorSetsAsync(cancellationToken).ConfigureAwait(false)).LastOrDefault(static set => set.State == SearchVectorSetState.Active);

    /// <summary>
    /// Gets the set of an identity, creating it when there is none. A new set is active when no set is, and is built
    /// in the background otherwise, while the active set keeps answering until <see cref="CompleteVectorSetAsync"/>
    /// makes it the active one. A retired set of the same identity comes back with its vectors.
    /// </summary>
    /// <param name="identity">What its vectors are made with.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The set.</returns>
    /// <exception cref="ArgumentException">A part of the identity is empty or its width is not positive.</exception>
    public async ValueTask<SearchVectorSet> OpenVectorSetAsync(SearchVectorSetIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrEmpty(identity.Model);
        ArgumentNullException.ThrowIfNull(identity.Revision);
        ArgumentNullException.ThrowIfNull(identity.Prompts);
        ArgumentNullException.ThrowIfNull(identity.Runtime);
        ArgumentOutOfRangeException.ThrowIfLessThan(identity.Dimensions, 1);
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(Owner, async (connection, token) =>
        {
            SearchVectorSet? found = null;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT {VectorSetColumns} FROM search_vector_set
                    WHERE model = $model AND revision = $revision AND dims = $dims AND prompts = $prompts AND runtime = $runtime
                    ORDER BY id DESC LIMIT 1
                    """;
                AddIdentity(command, identity);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false)) found = ReadVectorSet(reader);
            }

            bool hasActive;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT EXISTS (SELECT 1 FROM search_vector_set WHERE state = 'active')";
                hasActive = (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))! != 0;
            }

            if (found is { State: SearchVectorSetState.Active } || found is { State: SearchVectorSetState.Building } && hasActive) return found;
            var state = hasActive ? SearchVectorSetState.Building : SearchVectorSetState.Active;
            if (found is not null)
            {
                await SetStateAsync(connection, found.Id, state, token).ConfigureAwait(false);
                return found with { State = state };
            }

            var createdAt = DateTimeOffset.UtcNow;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO search_vector_set(model, revision, dims, prompts, runtime, state, created_at)
                    VALUES ($model, $revision, $dims, $prompts, $runtime, $state, $created) RETURNING id
                    """;
                AddIdentity(command, identity);
                command.Parameters.AddWithValue("$state", StateName(state));
                command.Parameters.AddWithValue("$created", createdAt.ToUnixTimeMilliseconds());
                var id = (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
                return new SearchVectorSet(id, identity, state, DateTimeOffset.FromUnixTimeMilliseconds(createdAt.ToUnixTimeMilliseconds()));
            }
        }, recoverDamagedFile: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes a set the active one when every chunk has its vector in it; the set it replaces is retired.</summary>
    /// <param name="setId">The set.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>True when the set is active now.</returns>
    public async ValueTask<bool> CompleteVectorSetAsync(long setId, CancellationToken cancellationToken = default)
        => await ActivateAsync(setId, onlyWhenComplete: true, cancellationToken).ConfigureAwait(false);

    /// <summary>Makes a set the active one now, complete or not; the set it replaces is retired.</summary>
    /// <param name="setId">The set.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>False when there is no such set.</returns>
    public async ValueTask<bool> ActivateVectorSetAsync(long setId, CancellationToken cancellationToken = default)
        => await ActivateAsync(setId, onlyWhenComplete: false, cancellationToken).ConfigureAwait(false);

    private async ValueTask<bool> ActivateAsync(long setId, bool onlyWhenComplete, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(Owner, async (connection, token) =>
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT state FROM search_vector_set WHERE id = $set";
                command.Parameters.AddWithValue("$set", setId);
                var state = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
                if (state is null) return false;
                if (state == "active") return true;
            }

            if (onlyWhenComplete)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT EXISTS (SELECT 1 FROM search_chunk c
                      WHERE NOT EXISTS (SELECT 1 FROM search_vector v WHERE v.set_id = $set AND v.embed_hash = c.embed_hash))
                    """;
                command.Parameters.AddWithValue("$set", setId);
                if ((long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))! != 0) return false;
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE search_vector_set SET state = 'retired' WHERE state = 'active'";
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await SetStateAsync(connection, setId, SearchVectorSetState.Active, token).ConfigureAwait(false);
            return true;
        }, recoverDamagedFile: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a set and its vectors.</summary>
    /// <param name="setId">The set.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>False when there was no such set.</returns>
    public async ValueTask<bool> DeleteVectorSetAsync(long setId, CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM search_vector WHERE set_id = $set; DELETE FROM search_vector_set WHERE id = $set;";
            command.Parameters.AddWithValue("$set", setId);
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
        }, recoverDamagedFile: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores vectors in a set, quantized to 8 bits, in one transaction; a vector already there is replaced.</summary>
    /// <param name="setId">The set.</param>
    /// <param name="vectors">The vectors, each keyed by the hash of its text.</param>
    /// <param name="cancellationToken">Cancels before the write.</param>
    /// <returns>The vectors stored.</returns>
    /// <exception cref="ArgumentException">There is no such set, a vector has another width, or a hash is empty.</exception>
    public async ValueTask<int> PutVectorsAsync(long setId, IReadOnlyList<SearchVectorInput> vectors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        if (vectors.Count == 0) return 0;
        foreach (var vector in vectors)
        {
            if (vector.EmbedHash is not { Length: > 0 }) throw new ArgumentException("A vector has no hash.", nameof(vectors));
        }

        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(Owner, async (connection, token) =>
        {
            int dimensions;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT dims FROM search_vector_set WHERE id = $set";
                command.Parameters.AddWithValue("$set", setId);
                dimensions = await command.ExecuteScalarAsync(token).ConfigureAwait(false) is long dims ? (int)dims : throw new ArgumentException($"There is no vector set {setId}.", nameof(setId));
            }

            var codes = new sbyte[dimensions];
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT OR REPLACE INTO search_vector(set_id, embed_hash, scale, code) VALUES ($set, $hash, $scale, $code)";
            insert.Parameters.AddWithValue("$set", setId);
            var hash = insert.Parameters.Add("$hash", SqliteType.Blob);
            var scale = insert.Parameters.Add("$scale", SqliteType.Real);
            var code = insert.Parameters.Add("$code", SqliteType.Blob);
            foreach (var vector in vectors)
            {
                if (vector.Vector.Length != dimensions) throw new ArgumentException($"A vector has {vector.Vector.Length} values instead of {dimensions}.", nameof(vectors));
                hash.Value = vector.EmbedHash;
                scale.Value = (double)SearchVectorKernels.Quantize(vector.Vector.Span, codes);
                code.Value = MemoryMarshal.AsBytes(codes.AsSpan()).ToArray();
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            return vectors.Count;
        }, recoverDamagedFile: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts the chunks that have a vector in a set.</summary>
    /// <param name="setId">The set.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The coverage.</returns>
    public async ValueTask<SearchVectorCoverage> GetVectorCoverageAsync(long setId, CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*), count(v.embed_hash) FROM search_chunk c
                LEFT JOIN search_vector v ON v.set_id = $set AND v.embed_hash = c.embed_hash
                """;
            command.Parameters.AddWithValue("$set", setId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            await reader.ReadAsync(token).ConfigureAwait(false);
            return new SearchVectorCoverage(reader.GetInt64(0), reader.GetInt64(1));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists texts that have no vector yet in a set, one per hash: the catalog kinds first (tool search needs them),
    /// then the newest items. The page is then ordered by length, so that a batch pads little.
    /// </summary>
    /// <param name="setId">The set.</param>
    /// <param name="limit">The most chunks to read.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The texts to embed.</returns>
    public async ValueTask<IReadOnlyList<SearchEmbeddingWork>> ListMissingVectorsAsync(long setId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        var list = await _database.ReadAsync<List<SearchEmbeddingWork>>(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT c.id, c.embed_hash, coalesce(c.embed_title, c.title), c.text
                FROM search_chunk c JOIN search_item i ON i.id = c.item_id
                WHERE NOT EXISTS (SELECT 1 FROM search_vector v WHERE v.set_id = $set AND v.embed_hash = c.embed_hash)
                ORDER BY i.kind IN ({string.Join(',', SearchNames.CatalogKinds.Select(static kind => (int)kind))}) DESC, i.at DESC, c.id
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$set", setId);
            command.Parameters.AddWithValue("$limit", limit);
            var work = new List<SearchEmbeddingWork>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var hash = (byte[])reader[1];
                if (seen.Add(Convert.ToHexString(hash))) work.Add(new SearchEmbeddingWork(reader.GetInt64(0), hash, reader.GetString(2), reader.GetString(3)));
            }

            return work;
        }, cancellationToken).ConfigureAwait(false);
        list.Sort(static (left, right) => (left.Title.Length + left.Text.Length).CompareTo(right.Title.Length + right.Text.Length));
        return list;
    }

    /// <summary>Removes the vectors of every set whose text no chunk holds any more.</summary>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The vectors removed.</returns>
    public async ValueTask<int> CompactVectorsAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.WriteAsync(Owner, async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM search_vector WHERE NOT EXISTS (SELECT 1 FROM search_chunk c WHERE c.embed_hash = search_vector.embed_hash)";
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, recoverDamagedFile: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the vectors of a set's chunks into memory, with the columns of their items, in the order of the chunks.</summary>
    /// <param name="setId">The set.</param>
    /// <param name="cancellationToken">Cancels.</param>
    /// <returns>The index, or null when there is no such set.</returns>
    public async ValueTask<SearchVectorIndex?> LoadVectorIndexAsync(long setId, CancellationToken cancellationToken = default)
        => (await LoadVectorsAsync(setId, cancellationToken).ConfigureAwait(false))?.Index;

    /// <summary>Loads a set, or the active set when <paramref name="setId"/> is null, with the cursor of the log read before its rows.</summary>
    internal async ValueTask<SearchVectorLoad?> LoadVectorsAsync(long? setId, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync<SearchVectorLoad?>(Owner, async (connection, token) =>
        {
            // The cursor is read first: the changes written while the rows are read are applied again, which does no harm.
            var (epoch, _, cursor) = await ReadLogStateAsync(connection, token).ConfigureAwait(false);
            SearchVectorSet? set = null;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = setId is null
                    ? $"SELECT {VectorSetColumns} FROM search_vector_set WHERE state = 'active' ORDER BY id DESC LIMIT 1"
                    : $"SELECT {VectorSetColumns} FROM search_vector_set WHERE id = $set";
                command.Parameters.AddWithValue("$set", setId ?? 0);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false)) set = ReadVectorSet(reader);
            }

            if (set is null) return null;
            long chunks;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT count(*) FROM search_chunk";
                chunks = (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            }

            var index = new SearchVectorIndex(set.Identity.Dimensions, (int)Math.Min(chunks, int.MaxValue / set.Identity.Dimensions));
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"{VectorRowSelect} ORDER BY c.id";
                command.Parameters.AddWithValue("$set", set.Id);
                await ReadVectorRowsAsync(command, set.Identity.Dimensions, (long chunkId, ReadOnlySpan<sbyte> codes, float scale, in SearchVectorRowInfo info) => index.Add(chunkId, codes, scale, info), token).ConfigureAwait(false);
            }

            return new SearchVectorLoad(set, index, epoch, cursor, chunks);
        }, cancellationToken).ConfigureAwait(false);
    }

    private const string VectorRowSelect = """
        SELECT c.id, i.kind, i.role, i.at, i.space_id, i.project_id, i.session_id, v.scale, v.code
        FROM search_chunk c JOIN search_item i ON i.id = c.item_id
        JOIN search_vector v ON v.set_id = $set AND v.embed_hash = c.embed_hash
        """;

    private delegate void VectorRowSink(long chunkId, ReadOnlySpan<sbyte> codes, float scale, in SearchVectorRowInfo info);

    private static async ValueTask ReadVectorRowsAsync(SqliteCommand command, int dimensions, VectorRowSink add, CancellationToken cancellationToken)
    {
        var bytes = new byte[dimensions];
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // A code of another width cannot be compared: it is left out (a damaged row).
            if (reader.GetBytes(8, 0, null, 0, 0) != dimensions) continue;
            reader.GetBytes(8, 0, bytes, 0, dimensions);
            var info = new SearchVectorRowInfo((SearchKind)reader.GetInt32(1), (SearchRole)reader.GetInt32(2), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6));
            if ((byte)info.Kind is 0 or >= 64) continue;
            add(reader.GetInt64(0), MemoryMarshal.Cast<byte, sbyte>(bytes.AsSpan()), (float)reader.GetDouble(7), info);
        }
    }

    /// <summary>Reads what changed for the vectors of a set since a cursor of the log.</summary>
    /// <param name="setId">The set the reader holds.</param>
    /// <param name="epoch">The epoch of the log the reader's cursor belongs to.</param>
    /// <param name="since">The cursor.</param>
    /// <param name="maxChanges">The most changes worth applying one by one; beyond, the reader loads again.</param>
    /// <param name="cancellationToken">Cancels.</param>
    internal async ValueTask<SearchVectorChanges> ReadVectorChangesAsync(long setId, string epoch, long since, int maxChanges, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await _database.ReadAsync(Owner, async (connection, token) =>
        {
            var (currentEpoch, floor, last) = await ReadLogStateAsync(connection, token).ConfigureAwait(false);
            SearchVectorSet? active = null;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT {VectorSetColumns} FROM search_vector_set WHERE state = 'active' ORDER BY id DESC LIMIT 1";
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (await reader.ReadAsync(token).ConfigureAwait(false)) active = ReadVectorSet(reader);
            }

            if (active is null || active.Id != setId || currentEpoch != epoch || since < floor || since > last)
                return new SearchVectorChanges { Active = active, Reload = active is not null, Cursor = last };
            if (since == last) return new SearchVectorChanges { Active = active, Cursor = last };

            long count;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT count(*) FROM search_change WHERE seq > $since AND seq <= $last";
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$last", last);
                count = (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            }

            if (count > maxChanges) return new SearchVectorChanges { Active = active, Reload = true, Cursor = last };

            var touched = new List<long>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT DISTINCT chunk_id FROM search_change WHERE seq > $since AND seq <= $last AND chunk_id IS NOT NULL";
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$last", last);
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) touched.Add(reader.GetInt64(0));
            }

            var rows = new List<SearchVectorRow>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    {VectorRowSelect}
                    WHERE c.id IN (SELECT chunk_id FROM search_change WHERE seq > $since AND seq <= $last AND chunk_id IS NOT NULL)
                       OR c.embed_hash IN (SELECT embed_hash FROM search_change WHERE seq > $since AND seq <= $last AND set_id = $set)
                    ORDER BY c.id
                    """;
                command.Parameters.AddWithValue("$set", setId);
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$last", last);
                await ReadVectorRowsAsync(command, active.Identity.Dimensions,
                    (long chunkId, ReadOnlySpan<sbyte> codes, float scale, in SearchVectorRowInfo info) => rows.Add(new SearchVectorRow(chunkId, scale, codes.ToArray(), info)), token).ConfigureAwait(false);
            }

            long chunks;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT count(*) FROM search_chunk";
                chunks = (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
            }

            return new SearchVectorChanges { Active = active, Cursor = last, Touched = touched, Rows = rows, Chunks = chunks };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes the changes a reader applied: a reader whose cursor is older loads again.</summary>
    internal async ValueTask PruneVectorChangesAsync(long through, CancellationToken cancellationToken)
    {
        await OpenAsync(cancellationToken).ConfigureAwait(false);
        await _database.WriteAsync(Owner, async (connection, token) => await DeleteChangesAsync(connection, through, token).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    // The epoch changes when the tables are made again; the floor is the last change trimmed; the last is the counter of the log.
    private static async ValueTask<(string Epoch, long Floor, long Last)> ReadLogStateAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT value FROM search_meta WHERE key = $epoch),
              coalesce((SELECT CAST(value AS INTEGER) FROM search_meta WHERE key = $floor), 0),
              coalesce((SELECT seq FROM sqlite_sequence WHERE name = 'search_change'), 0)
            """;
        command.Parameters.AddWithValue("$epoch", ChangeEpochKey);
        command.Parameters.AddWithValue("$floor", ChangeFloorKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.IsDBNull(0) ? "" : reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static void AddIdentity(SqliteCommand command, SearchVectorSetIdentity identity)
    {
        command.Parameters.AddWithValue("$model", identity.Model);
        command.Parameters.AddWithValue("$revision", identity.Revision);
        command.Parameters.AddWithValue("$dims", identity.Dimensions);
        command.Parameters.AddWithValue("$prompts", identity.Prompts);
        command.Parameters.AddWithValue("$runtime", identity.Runtime);
    }

    private static async ValueTask SetStateAsync(SqliteConnection connection, long setId, SearchVectorSetState state, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE search_vector_set SET state = $state WHERE id = $set";
        command.Parameters.AddWithValue("$state", StateName(state));
        command.Parameters.AddWithValue("$set", setId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string StateName(SearchVectorSetState state) => state switch
    {
        SearchVectorSetState.Active => "active",
        SearchVectorSetState.Retired => "retired",
        _ => "building",
    };

    private static SearchVectorSet ReadVectorSet(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        new SearchVectorSetIdentity(reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5)),
        reader.GetString(6) switch { "active" => SearchVectorSetState.Active, "retired" => SearchVectorSetState.Retired, _ => SearchVectorSetState.Building },
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)));
}
