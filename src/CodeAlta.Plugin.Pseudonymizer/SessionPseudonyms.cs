using System.Text.Json;
using System.Text.Json.Serialization;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Core.IO;
using CodeAlta.Pseudonymization;

namespace CodeAlta.Plugin.Pseudonymizer;

/// <summary>
/// The pseudonyms of one session and what its texts were sent as, kept in one file encrypted with the key of the user
/// on this machine (DPAPI, the Keychain, libsecret). It is written after each request that changed it.
/// </summary>
internal sealed class SessionPseudonyms
{
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly Lock _gate = new();
    private (int Table, int Cache) _saved;

    private SessionPseudonyms(string path, ISecretProtector protector, PseudonymTable table, PseudonymCache cache)
    {
        _path = path;
        _protector = protector;
        Table = table;
        Cache = cache;
        _saved = (table.Version, cache.Version);
    }

    public PseudonymTable Table { get; }

    public PseudonymCache Cache { get; }

    /// <summary>Reads the file of a session, or starts empty when it has none.</summary>
    /// <exception cref="AgentProviderBoundaryException">The file exists and cannot be read: the session sends nothing rather than number its pseudonyms again.</exception>
    public static SessionPseudonyms Load(string path, ISecretProtector protector)
    {
        if (!File.Exists(path)) return new SessionPseudonyms(path, protector, new PseudonymTable(), new PseudonymCache());
        try
        {
            var json = protector.Unprotect(File.ReadAllBytes(path));
            var document = JsonSerializer.Deserialize(json, SessionPseudonymsJsonContext.Default.SessionPseudonymsDocument)
                ?? throw new JsonException("The file is empty.");
            var table = PseudonymTable.FromJson(Convert.FromBase64String(document.Table ?? throw new JsonException("The file has no table.")));
            var cache = document.Cache is null ? new PseudonymCache() : PseudonymCache.FromJson(Convert.FromBase64String(document.Cache));
            return new SessionPseudonyms(path, protector, table, cache);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or SecretStoreUnavailableException)
        {
            throw new AgentProviderBoundaryException("The pseudonyms of this confidential session cannot be read on this machine: nothing was sent.", exception);
        }
    }

    /// <summary>Makes the boundary of a request: it shares the table and the cache of the session, and saves them once it changed them.</summary>
    public PseudonymizingBoundary Boundary(PiiAnalyzer analyzer, PseudonymizerOptions options)
    {
        var boundary = new PseudonymizingBoundary(analyzer, Table, Cache, options);
        boundary.RequestPrepared += _ => Save();
        boundary.ResponseRestored += _ => Save();
        return boundary;
    }

    // A file that cannot be written fails the request: an answer whose pseudonyms are not kept could not be read again.
    private void Save()
    {
        lock (_gate)
        {
            var versions = (Table.Version, Cache.Version);
            if (versions == _saved) return;
            var document = new SessionPseudonymsDocument
            {
                Table = Convert.ToBase64String(Table.ToJson()),
                Cache = Convert.ToBase64String(Cache.ToJson()),
            };
            try
            {
                var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(document, SessionPseudonymsJsonContext.Default.SessionPseudonymsDocument));
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var staged = StagedFileReplacement.StagedPathOf(_path);
                File.WriteAllBytes(staged, bytes);
                StagedFileReplacement.MoveOver(staged, _path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecretStoreUnavailableException)
            {
                throw new AgentProviderBoundaryException("The pseudonyms of this confidential session could not be saved: nothing more is sent until they can be.", exception);
            }

            _saved = versions;
        }
    }
}

/// <summary>The file of the pseudonyms of a session, before it is encrypted.</summary>
internal sealed class SessionPseudonymsDocument
{
    [JsonPropertyName("table")]
    public string? Table { get; set; }

    [JsonPropertyName("cache")]
    public string? Cache { get; set; }
}

[JsonSerializable(typeof(SessionPseudonymsDocument))]
internal sealed partial class SessionPseudonymsJsonContext : JsonSerializerContext;
