using System.Text;
using CodeAlta.Agent.Runtime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Catalog.Secrets;

/// <summary>A use of a value the fleet gave this machine: which run used which value at which version, never the value.</summary>
/// <param name="Source">The server link the value came through, which the use is published to.</param>
/// <param name="SecretId">The id of the value at the server.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">The version used.</param>
/// <param name="RunId">The run.</param>
/// <param name="UsedAt">When it was used.</param>
public sealed record SecretUse(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("id")] string SecretId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("run")] string RunId,
    [property: JsonPropertyName("at")] DateTimeOffset UsedAt);

/// <summary>
/// The uses a machine records of the values the fleet gave it (<c>doc/fleet/secrets.md</c>, audit), one line each in
/// <c>~/.alta/secrets/uses.jsonl</c>, until they are published to the server they came through. A run that uses the same
/// version of a value twice is recorded once.
/// </summary>
/// <remarks>The processes of one profile share the file: each write holds the lock of the file.</remarks>
public sealed class SecretUseLog
{
    /// <summary>The most uses kept while they wait to be published; the oldest go first.</summary>
    public const int MaximumKept = 10_000;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _recorded = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="SecretUseLog"/> class for the profile of a root.</summary>
    /// <param name="root">The root of the profile, <c>~/.alta</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="root"/> is empty.</exception>
    public SecretUseLog(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        FilePath = Path.Combine(root, "secrets", "uses.jsonl");
    }

    /// <summary>Gets the file of the log.</summary>
    public string FilePath { get; }

    /// <summary>Records the use of a value by a run, once per run and version.</summary>
    /// <param name="value">The value, as the store knows it.</param>
    /// <param name="runId">The run.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="runId"/> is empty.</exception>
    public void Record(FleetSecretCopyInfo value, string runId)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        lock (_gate)
        {
            if (!_recorded.Add($"{value.Source}\n{value.SecretId}\n{value.Version}\n{runId}")) return;
            try
            {
                using var held = ConfigFileLock.Acquire(FilePath);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var line = JsonSerializer.Serialize(new SecretUse(value.Source, value.SecretId, value.Name, value.Version, runId, DateTimeOffset.UtcNow),
                    SecretUseJsonContext.Default.SecretUse);
                File.AppendAllText(FilePath, line + "\n", Encoding.UTF8);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The run goes on: a use that cannot be written is not published.
                _recorded.Remove($"{value.Source}\n{value.SecretId}\n{value.Version}\n{runId}");
            }
        }
    }

    /// <summary>Takes the uses of one server link out of the log, at most a number of them, to publish them.</summary>
    /// <param name="source">The link.</param>
    /// <param name="most">The most uses taken.</param>
    /// <param name="publish">Publishes the uses; they leave the log only once it returns true.</param>
    /// <returns>The uses published.</returns>
    /// <exception cref="ArgumentException"><paramref name="source"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publish"/> is null.</exception>
    public async Task<IReadOnlyList<SecretUse>> PublishAsync(string source, int most, Func<IReadOnlyList<SecretUse>, Task<bool>> publish)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(publish);
        var taken = Read().Where(use => use.Source == source).Take(Math.Max(1, most)).ToArray();
        if (taken.Length == 0 || !await publish(taken).ConfigureAwait(false)) return [];
        var published = taken.ToHashSet();
        Rewrite(uses => [.. uses.Where(use => !published.Contains(use))]);
        return taken;
    }

    /// <summary>Takes away the uses of a server link: the link is gone.</summary>
    /// <param name="source">The link.</param>
    public void Forget(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Rewrite(uses => [.. uses.Where(use => use.Source != source)]);
    }

    /// <summary>Reads the uses that wait to be published.</summary>
    /// <returns>The uses, the oldest first.</returns>
    public IReadOnlyList<SecretUse> Read()
    {
        lock (_gate)
        {
            try
            {
                using var held = ConfigFileLock.Acquire(FilePath);
                return ReadFile();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    private void Rewrite(Func<IReadOnlyList<SecretUse>, IReadOnlyList<SecretUse>> change)
    {
        lock (_gate)
        {
            try
            {
                using var held = ConfigFileLock.Acquire(FilePath);
                var kept = change(ReadFile());
                if (kept.Count > MaximumKept) kept = [.. kept.Skip(kept.Count - MaximumKept)];
                var text = new StringBuilder();
                foreach (var use in kept) text.Append(JsonSerializer.Serialize(use, SecretUseJsonContext.Default.SecretUse)).Append('\n');
                var temporary = FilePath + ".tmp";
                File.WriteAllText(temporary, text.ToString(), Encoding.UTF8);
                StagedFileReplacement.MoveOverWithoutGap(temporary, FilePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Published again later: the server keeps a use once.
            }
        }
    }

    private List<SecretUse> ReadFile()
    {
        if (!File.Exists(FilePath)) return [];
        var uses = new List<SecretUse>();
        foreach (var line in File.ReadLines(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, SecretUseJsonContext.Default.SecretUse) is { Source.Length: > 0, SecretId.Length: > 0, RunId.Length: > 0 } use) uses.Add(use);
            }
            catch (JsonException)
            {
                // A line cut by a crash is dropped.
            }
        }

        return uses;
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SecretUse))]
internal sealed partial class SecretUseJsonContext : JsonSerializerContext;
