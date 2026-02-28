using System.Security.Cryptography;
using System.Text;
using CodeAlta.Persistence;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Search;

/// <summary>
/// Provides document indexing and retrieval queries over SQLite.
/// </summary>
public sealed class DocumentIndexStore
{
    private readonly CodeAltaDb _db;

    /// <summary>
    /// Initializes a new instance of the <see cref="DocumentIndexStore"/> class.
    /// </summary>
    /// <param name="db">Persistence database accessor.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="db"/> is <see langword="null"/>.</exception>
    public DocumentIndexStore(CodeAltaDb db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>
    /// Upserts documents, FTS rows, and embeddings.
    /// </summary>
    /// <param name="documents">Documents to index.</param>
    /// <param name="embedder">Embedding provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Indexed document ids in input order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when required arguments are <see langword="null"/>.</exception>
    public async Task<IReadOnlyList<long>> UpsertDocumentsAsync(
        IReadOnlyList<DocumentInput> documents,
        IEmbedder embedder,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(embedder);
        if (documents.Count == 0)
        {
            return [];
        }

        return await _db.ExecuteWriteAsync(
            async (connection, ct) =>
            {
                var embeddings = await embedder.EmbedAsync(
                    documents.Select(x => x.Text).ToArray(),
                    ct).ConfigureAwait(false);

                var documentIds = new List<long>(documents.Count);
                for (var index = 0; index < documents.Count; index++)
                {
                    var input = documents[index];
                    ValidateDocument(input);
                    var textHash = ComputeTextHash(input.Text);
                    var now = DateTimeOffset.UtcNow.ToString("O");

                    var existing = await FindDocumentAsync(connection, input.SourceKind, input.SourceId, ct)
                        .ConfigureAwait(false);

                    long documentId;
                    if (existing is null)
                    {
                        await using var insert = connection.CreateCommand();
                        insert.CommandText =
                            """
                            INSERT INTO documents(
                                source_kind,
                                source_id,
                                workspace_id,
                                project_id,
                                title,
                                mime_type,
                                text,
                                text_hash,
                                created_at,
                                updated_at)
                            VALUES (
                                $source_kind,
                                $source_id,
                                $workspace_id,
                                $project_id,
                                $title,
                                $mime_type,
                                $text,
                                $text_hash,
                                $created_at,
                                $updated_at);
                            SELECT last_insert_rowid();
                            """;
                        insert.Parameters.AddWithValue("$source_kind", input.SourceKind);
                        insert.Parameters.AddWithValue("$source_id", input.SourceId);
                        insert.Parameters.AddWithValue("$workspace_id", (object?)input.WorkspaceId ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$project_id", (object?)input.ProjectId ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$title", (object?)input.Title ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$mime_type", (object?)input.MimeType ?? DBNull.Value);
                        insert.Parameters.AddWithValue("$text", input.Text);
                        insert.Parameters.AddWithValue("$text_hash", textHash);
                        insert.Parameters.AddWithValue("$created_at", now);
                        insert.Parameters.AddWithValue("$updated_at", now);
                        documentId = (long)(await insert.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
                    }
                    else
                    {
                        documentId = existing.DocumentId;
                        if (!string.Equals(existing.TextHash, textHash, StringComparison.Ordinal) ||
                            !string.Equals(existing.Title, input.Title, StringComparison.Ordinal))
                        {
                            await using var update = connection.CreateCommand();
                            update.CommandText =
                                """
                                UPDATE documents
                                SET workspace_id = $workspace_id,
                                    project_id = $project_id,
                                    title = $title,
                                    mime_type = $mime_type,
                                    text = $text,
                                    text_hash = $text_hash,
                                    updated_at = $updated_at
                                WHERE document_id = $document_id;
                                """;
                            update.Parameters.AddWithValue("$workspace_id", (object?)input.WorkspaceId ?? DBNull.Value);
                            update.Parameters.AddWithValue("$project_id", (object?)input.ProjectId ?? DBNull.Value);
                            update.Parameters.AddWithValue("$title", (object?)input.Title ?? DBNull.Value);
                            update.Parameters.AddWithValue("$mime_type", (object?)input.MimeType ?? DBNull.Value);
                            update.Parameters.AddWithValue("$text", input.Text);
                            update.Parameters.AddWithValue("$text_hash", textHash);
                            update.Parameters.AddWithValue("$updated_at", now);
                            update.Parameters.AddWithValue("$document_id", documentId);
                            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                    }

                    await UpsertFtsAsync(connection, documentId, input.Title, input.Text, ct).ConfigureAwait(false);
                    await UpsertEmbeddingAsync(
                        connection,
                        documentId,
                        embedder.GetType().Name,
                        embeddings[index],
                        ct).ConfigureAwait(false);

                    documentIds.Add(documentId);
                }

                return (IReadOnlyList<long>)documentIds;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes an FTS query.
    /// </summary>
    /// <param name="query">Search query options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>FTS results ordered by BM25 ranking.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when query text is empty.</exception>
    public Task<IReadOnlyList<SearchResult>> QueryFtsAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Text))
        {
            throw new ArgumentException("Query text is required.", nameof(query));
        }

        var limit = query.PrefilterLimit <= 0 ? 50 : query.PrefilterLimit;
        return _db.ExecuteReadAsync<IReadOnlyList<SearchResult>>(
            async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT d.document_id,
                           d.source_kind,
                           d.source_id,
                           d.title,
                           snippet(documents_fts, 2, '[', ']', '...', 12) AS snippet_text,
                           bm25(documents_fts) AS score
                    FROM documents_fts
                    INNER JOIN documents d ON d.document_id = documents_fts.document_id
                    WHERE documents_fts MATCH $query
                      AND ($workspace_id IS NULL OR d.workspace_id = $workspace_id)
                      AND ($project_id IS NULL OR d.project_id = $project_id)
                    ORDER BY score ASC
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$query", query.Text);
                command.Parameters.AddWithValue("$workspace_id", (object?)query.WorkspaceId ?? DBNull.Value);
                command.Parameters.AddWithValue("$project_id", (object?)query.ProjectId ?? DBNull.Value);
                command.Parameters.AddWithValue("$limit", limit);

                var results = new List<SearchResult>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var sourceKind = reader.GetString(1);
                    var sourceId = reader.GetString(2);
                    var bm25Score = reader.GetDouble(5);
                    results.Add(
                        new SearchResult
                        {
                            DocumentId = reader.GetInt64(0),
                            SourceKind = sourceKind,
                            SourceId = sourceId,
                            LinkUri = BuildSourceLink(sourceKind, sourceId),
                            Title = reader.IsDBNull(3) ? null : reader.GetString(3),
                            Snippet = reader.IsDBNull(4) ? null : reader.GetString(4),
                            FtsScore = bm25Score,
                            CombinedScore = -bm25Score,
                        });
                }

                return results;
            },
            cancellationToken);
    }

    /// <summary>
    /// Loads embeddings for a document id set.
    /// </summary>
    /// <param name="documentIds">Document identifiers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Map of document id to embedding vector.</returns>
    public Task<Dictionary<long, float[]>> GetEmbeddingsAsync(
        IReadOnlyCollection<long> documentIds,
        CancellationToken cancellationToken = default)
    {
        if (documentIds.Count == 0)
        {
            return Task.FromResult(new Dictionary<long, float[]>());
        }

        return _db.ExecuteReadAsync(
            async (connection, ct) =>
            {
                await using var command = connection.CreateCommand();
                var inParameters = new List<string>(documentIds.Count);
                var parameterIndex = 0;
                foreach (var id in documentIds)
                {
                    var parameterName = $"$id{parameterIndex++}";
                    command.Parameters.AddWithValue(parameterName, id);
                    inParameters.Add(parameterName);
                }

                command.CommandText =
                    $"""
                    SELECT document_id, embedding_blob
                    FROM document_embeddings
                    WHERE document_id IN ({string.Join(", ", inParameters)});
                    """;

                var results = new Dictionary<long, float[]>();
                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var documentId = reader.GetInt64(0);
                    var bytes = (byte[])reader.GetValue(1);
                    results[documentId] = DeserializeEmbedding(bytes);
                }

                return results;
            },
            cancellationToken);
    }

    private static void ValidateDocument(DocumentInput input)
    {
        if (string.IsNullOrWhiteSpace(input.SourceKind))
        {
            throw new ArgumentException("Document source kind is required.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.SourceId))
        {
            throw new ArgumentException("Document source id is required.", nameof(input));
        }

        if (string.IsNullOrWhiteSpace(input.Text))
        {
            throw new ArgumentException("Document text is required.", nameof(input));
        }
    }

    private static string BuildSourceLink(string sourceKind, string sourceId)
    {
        return sourceKind switch
        {
            "artifact" => sourceId,
            "file" => $"file://{sourceId}",
            "task" => $"task://{sourceId}",
            _ => $"{sourceKind}://{sourceId}",
        };
    }

    private static string ComputeTextHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }

    private static async Task<DocumentRow?> FindDocumentAsync(
        SqliteConnection connection,
        string sourceKind,
        string sourceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT document_id, title, text_hash
            FROM documents
            WHERE source_kind = $source_kind AND source_id = $source_id;
            """;
        command.Parameters.AddWithValue("$source_kind", sourceKind);
        command.Parameters.AddWithValue("$source_id", sourceId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DocumentRow(
            reader.GetInt64(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2));
    }

    private static async Task UpsertFtsAsync(
        SqliteConnection connection,
        long documentId,
        string? title,
        string text,
        CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM documents_fts WHERE document_id = $document_id;";
        delete.Parameters.AddWithValue("$document_id", documentId);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO documents_fts(document_id, title, text)
            VALUES ($document_id, $title, $text);
            """;
        insert.Parameters.AddWithValue("$document_id", documentId);
        insert.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
        insert.Parameters.AddWithValue("$text", text);
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertEmbeddingAsync(
        SqliteConnection connection,
        long documentId,
        string modelId,
        float[] embedding,
        CancellationToken cancellationToken)
    {
        var bytes = SerializeEmbedding(embedding);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO document_embeddings(document_id, model_id, dimension, embedding_blob)
            VALUES ($document_id, $model_id, $dimension, $embedding_blob)
            ON CONFLICT(document_id) DO UPDATE SET
                model_id = excluded.model_id,
                dimension = excluded.dimension,
                embedding_blob = excluded.embedding_blob;
            """;
        command.Parameters.AddWithValue("$document_id", documentId);
        command.Parameters.AddWithValue("$model_id", modelId);
        command.Parameters.AddWithValue("$dimension", embedding.Length);
        command.Parameters.AddWithValue("$embedding_blob", bytes);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * sizeof(float)];
        Buffer.BlockCopy(embedding, srcOffset: 0, bytes, dstOffset: 0, bytes.Length);
        return bytes;
    }

    private static float[] DeserializeEmbedding(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, srcOffset: 0, values, dstOffset: 0, bytes.Length);
        return values;
    }

    private sealed record DocumentRow(long DocumentId, string? Title, string TextHash);
}
