using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// What admitted a run to an environment of the fleet (<c>doc/fleet/secrets.md</c>, environments): the environment by its
/// name and id, the version of its rules that held, and the approval that was taken for it, when the rules required one.
/// </summary>
/// <param name="Environment">The name of the environment.</param>
/// <param name="EnvironmentId">Its id at the server; null for an environment named without one (a host that keeps no rules).</param>
/// <param name="Version">The version of its rules that admitted the run; 0 when unknown.</param>
/// <param name="ApprovalId">The approval taken for the run; null when the rules required none.</param>
public sealed record EnvironmentAdmission(string Environment, string? EnvironmentId, long Version, string? ApprovalId);

/// <summary>Whether a run was admitted to an environment, and by what.</summary>
/// <param name="Problem">Why the run may not start; null when it was admitted.</param>
/// <param name="Admission">What admitted it; null when it was refused.</param>
public sealed record EnvironmentAdmissionResult(string? Problem, EnvironmentAdmission? Admission)
{
    /// <summary>Gets a refusal.</summary>
    /// <param name="problem">Why.</param>
    /// <returns>The result.</returns>
    public static EnvironmentAdmissionResult Refused(string problem) => new(problem, null);
}

/// <summary>The admission of one session, as the host keeps it.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Admission">What admitted it.</param>
/// <param name="AdmittedAt">When.</param>
public sealed record SessionEnvironmentAdmission(
    [property: JsonPropertyName("session")] string SessionId,
    [property: JsonPropertyName("admission")] EnvironmentAdmission Admission,
    [property: JsonPropertyName("at")] DateTimeOffset AdmittedAt);

/// <summary>
/// The environments (<c>doc/fleet/secrets.md</c>, environments) the sessions of this host were admitted to, by session:
/// the host that admits a run (its protection rules held, its approval verified and taken) records it before the session
/// attaches, and the processes of that session then get the values of that environment, also after a restart of the host:
/// the admission is kept in a file of the profile, bound to the id of the environment, the version of its rules and the
/// approval taken. The approval is not asked nor taken again; the admission holds while the host's verified rules of that
/// environment are still that version (<see cref="Validate"/>), and a session admitted to rules that changed since gets
/// nothing of the environment until it is admitted again.
/// </summary>
/// <remarks>The processes of one profile share the file; one writer at a time within this instance.</remarks>
public sealed class SessionSecretEnvironments
{
    /// <summary>The most admissions kept; the oldest go first.</summary>
    public const int MaximumKept = 2000;

    private readonly Lock _gate = new();
    private readonly string? _path;
    private Dictionary<string, SessionEnvironmentAdmission>? _admissions;

    /// <summary>Initializes a new instance of the <see cref="SessionSecretEnvironments"/> class that keeps nothing across a restart.</summary>
    public SessionSecretEnvironments() : this(null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SessionSecretEnvironments"/> class kept in a file.</summary>
    /// <param name="path">The file; null keeps the admissions in memory only.</param>
    public SessionSecretEnvironments(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    /// <summary>
    /// Gets or sets what tells whether an admission still holds: the host that verifies the rules of environments (a
    /// node) says no once the environment it holds is another one or another version; null holds every admission.
    /// </summary>
    public Func<SessionEnvironmentAdmission, bool>? Validate { get; set; }

    /// <summary>Records the environment a session was admitted to, by its name alone.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="environment">The name of the environment.</param>
    /// <exception cref="ArgumentException">An argument is empty.</exception>
    public void Admit(string sessionId, string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);
        Admit(sessionId, new EnvironmentAdmission(environment, null, 0, null));
    }

    /// <summary>Records what admitted a session to an environment, and keeps it.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="admission">What admitted it.</param>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="admission"/> is null.</exception>
    public void Admit(string sessionId, EnvironmentAdmission admission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentException.ThrowIfNullOrWhiteSpace(admission.Environment);
        lock (_gate)
        {
            var admissions = Load();
            admissions[sessionId] = new SessionEnvironmentAdmission(sessionId, admission, DateTimeOffset.UtcNow);
            Save(admissions);
        }
    }

    /// <summary>Gets what admitted a session to an environment, when it still holds.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The admission, or null.</returns>
    public SessionEnvironmentAdmission? Find(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        SessionEnvironmentAdmission? held;
        lock (_gate) held = Load().GetValueOrDefault(sessionId);
        return held is not null && (Validate?.Invoke(held) ?? true) ? held : null;
    }

    /// <summary>Gets the environment a session was admitted to, when the admission still holds.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The name of the environment, or null.</returns>
    public string? Get(string sessionId) => Find(sessionId)?.Admission.Environment;

    /// <summary>Forgets the environment of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>Whether it had one.</returns>
    public bool Remove(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);
        lock (_gate)
        {
            var admissions = Load();
            if (!admissions.Remove(sessionId)) return false;
            Save(admissions);
            return true;
        }
    }

    // Under the gate: the file read once, then what this instance wrote.
    private Dictionary<string, SessionEnvironmentAdmission> Load()
    {
        if (_admissions is { } known) return known;
        var admissions = new Dictionary<string, SessionEnvironmentAdmission>(StringComparer.Ordinal);
        if (_path is not null)
        {
            try
            {
                foreach (var admission in JsonSerializer.Deserialize(File.ReadAllBytes(_path), SessionEnvironmentJson.Default.ListSessionEnvironmentAdmission) ?? [])
                {
                    if (admission is { SessionId.Length: > 0, Admission.Environment.Length: > 0 }) admissions[admission.SessionId] = admission;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException or IOException or UnauthorizedAccessException)
            {
                // A file that does not read admits nothing: the runs are admitted again.
            }
        }

        return _admissions = admissions;
    }

    private void Save(Dictionary<string, SessionEnvironmentAdmission> admissions)
    {
        if (admissions.Count > MaximumKept)
        {
            foreach (var old in admissions.Values.OrderBy(static admission => admission.AdmittedAt).Take(admissions.Count - MaximumKept).ToArray()) admissions.Remove(old.SessionId);
        }

        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var staging = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(staging, JsonSerializer.SerializeToUtf8Bytes([.. admissions.Values.OrderBy(static admission => admission.AdmittedAt)],
                SessionEnvironmentJson.Default.ListSessionEnvironmentAdmission));
            File.Move(staging, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Kept in memory meanwhile: the admission holds until the host restarts.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<SessionEnvironmentAdmission>))]
internal sealed partial class SessionEnvironmentJson : JsonSerializerContext;
