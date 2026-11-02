using System.Text.Json.Serialization;

namespace CodeAlta.Catalog.SessionMoves;

/// <summary>
/// The manifest of a session bundle: what a machine offers when it gives a session to another one, and what the target
/// checks the copy it receives against.
/// </summary>
/// <remarks>
/// A bundle is a folder: <c>manifest.json</c>, the journal (<see cref="JournalFile"/>), the attachments of the session
/// (<see cref="AttachmentsFolder"/>), what its provider keeps of it (<see cref="ProviderFolder"/>) and the git payload
/// (<see cref="SessionMoveRepository.Payload"/>). Every file of the bundle is listed in <see cref="Files"/> with its length
/// and its SHA-256, paths relative to the bundle with forward slashes.
/// </remarks>
public sealed record SessionMoveManifest
{
    /// <summary>The name of the manifest file in a bundle folder.</summary>
    public const string FileName = "manifest.json";

    /// <summary>The path of the journal in a bundle.</summary>
    public const string JournalFile = "journal.jsonl";

    /// <summary>The folder of the attachments in a bundle.</summary>
    public const string AttachmentsFolder = "attachments";

    /// <summary>The folder of what the provider keeps of the session in a bundle.</summary>
    public const string ProviderFolder = "provider";

    /// <summary>The folder of the git payload in a bundle.</summary>
    public const string GitFolder = "git";

    /// <summary>The version of the format this build writes and reads.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Gets the version of the format of the bundle.</summary>
    [JsonPropertyName("format")]
    public int Format { get; init; } = CurrentFormat;

    /// <summary>Gets the identifier of the move the bundle was made for.</summary>
    [JsonPropertyName("moveId")]
    public required string MoveId { get; init; }

    /// <summary>Gets the identifier of the session, kept on the target.</summary>
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    /// <summary>Gets when the session was created, which places its journal.</summary>
    [JsonPropertyName("sessionCreatedAt")]
    public DateTimeOffset SessionCreatedAt { get; init; }

    /// <summary>Gets the title of the session.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Gets the space of the session.</summary>
    [JsonPropertyName("spaceId")]
    public string? SpaceId { get; init; }

    /// <summary>Gets the name of the machine the session comes from.</summary>
    [JsonPropertyName("fromMachine")]
    public string? FromMachine { get; init; }

    /// <summary>Gets the ownership epoch of the session on the source; the move commits the next one.</summary>
    [JsonPropertyName("epoch")]
    public long Epoch { get; init; }

    /// <summary>Gets when the bundle was made.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Gets the length and the hash of the journal, as the source froze it.</summary>
    [JsonPropertyName("journal")]
    public required SessionMoveFile Journal { get; init; }

    /// <summary>Gets the folder the session worked in on the source, an absolute path of that machine.</summary>
    [JsonPropertyName("workingFolder")]
    public required string WorkingFolder { get; init; }

    /// <summary>Gets the provider the session runs with.</summary>
    [JsonPropertyName("providerKey")]
    public string? ProviderKey { get; init; }

    /// <summary>Gets the model of the session, when one was chosen.</summary>
    [JsonPropertyName("modelId")]
    public string? ModelId { get; init; }

    /// <summary>Gets the reasoning effort of the session, when one was chosen.</summary>
    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; init; }

    /// <summary>Gets the sandbox profile of the session (a profile name or <c>off</c>), when one was chosen.</summary>
    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    /// <summary>Gets the agent prompt of the session, when one was chosen.</summary>
    [JsonPropertyName("agentPromptId")]
    public string? AgentPromptId { get; init; }

    /// <summary>Gets the repository the session works in, and the work in progress shipped with it.</summary>
    [JsonPropertyName("repository")]
    public required SessionMoveRepository Repository { get; init; }

    /// <summary>Gets what the provider keeps of the session (Claude Code's transcript), when the bundle carries it.</summary>
    [JsonPropertyName("providerSession")]
    public SessionMoveProviderSession? ProviderSession { get; init; }

    /// <summary>Gets every file of the bundle but the manifest, with its length and its hash.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<SessionMoveFile> Files { get; init; } = [];
}

/// <summary>A file of a bundle.</summary>
/// <param name="Path">Its path in the bundle, with forward slashes.</param>
/// <param name="Length">Its length in bytes.</param>
/// <param name="Sha256">Its SHA-256, in lower-case hexadecimal.</param>
public sealed record SessionMoveFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>The repository of a moved session, where it was on the source, and its work in progress.</summary>
public sealed record SessionMoveRepository
{
    /// <summary>Gets the repository key (<see cref="RepositoryKey"/>), which the target finds its project by.</summary>
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    /// <summary>Gets the folder of the project inside the repository, with forward slashes; <c>.</c> for its root.</summary>
    [JsonPropertyName("relativeFolder")]
    public string RelativeFolder { get; init; } = ".";

    /// <summary>Gets the root of the checkout the session worked in on the source.</summary>
    [JsonPropertyName("sourceRoot")]
    public required string SourceRoot { get; init; }

    /// <summary>Gets whether that checkout was a linked worktree rather than the main checkout.</summary>
    [JsonPropertyName("worktree")]
    public bool Worktree { get; init; }

    /// <summary>Gets the branch checked out; <see langword="null"/> for a detached HEAD.</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    /// <summary>Gets the commit checked out.</summary>
    [JsonPropertyName("head")]
    public required string Head { get; init; }

    /// <summary>Gets the upstream of the branch (<c>origin/feature</c>), when it has one.</summary>
    [JsonPropertyName("upstream")]
    public string? Upstream { get; init; }

    /// <summary>Gets whether the working tree had changes (untracked files included).</summary>
    [JsonPropertyName("dirty")]
    public bool Dirty { get; init; }

    /// <summary>Gets the git payload: what the target needs beyond what the remote has.</summary>
    [JsonPropertyName("payload")]
    public required SessionMoveGitPayload Payload { get; init; }
}

/// <summary>
/// The git payload of a bundle: nothing when the branch is pushed and the tree clean; otherwise the commits no remote
/// has (a <c>git bundle</c>) and the changes of the working tree (a binary diff, untracked files included).
/// </summary>
public sealed record SessionMoveGitPayload
{
    /// <summary>Gets the number of commits no remote has.</summary>
    [JsonPropertyName("unpushedCommits")]
    public int UnpushedCommits { get; init; }

    /// <summary>Gets the path of the git bundle of those commits in the bundle; <see langword="null"/> when there are none.</summary>
    [JsonPropertyName("commits")]
    public string? CommitsFile { get; init; }

    /// <summary>Gets the path of the binary diff of the working tree in the bundle; <see langword="null"/> when it is clean.</summary>
    [JsonPropertyName("patch")]
    public string? PatchFile { get; init; }

    /// <summary>
    /// Gets the tree of the shipped state: HEAD with the shipped changes, as git writes it. The target checks the
    /// tree it gets against it.
    /// </summary>
    [JsonPropertyName("tree")]
    public required string Tree { get; init; }

    /// <summary>Gets the size of the payload in bytes.</summary>
    [JsonPropertyName("bytes")]
    public long Bytes { get; init; }

    /// <summary>Gets the changed or ignored files that stay behind, and why.</summary>
    [JsonPropertyName("leftBehind")]
    public IReadOnlyList<SessionMoveLeftBehind> LeftBehind { get; init; } = [];

    /// <summary>Gets how many ignored entries stay behind, of which <see cref="LeftBehind"/> lists the first ones.</summary>
    [JsonPropertyName("ignoredCount")]
    public int IgnoredCount { get; init; }

    /// <summary>Gets whether the payload is empty: the remote has everything.</summary>
    [JsonIgnore]
    public bool IsEmpty => CommitsFile is null && PatchFile is null;
}

/// <summary>A file that stays on the source.</summary>
/// <param name="Path">Its path in the repository, with forward slashes.</param>
/// <param name="Reason">Why: <see cref="Secret"/>, <see cref="Ignored"/> or <see cref="Submodule"/>.</param>
public sealed record SessionMoveLeftBehind(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("reason")] string Reason)
{
    /// <summary>A file whose name looks like a secret (<c>.env</c>, <c>*.pem</c>, <c>*.key</c>, <c>id_*</c>, <c>*.tfvars</c>).</summary>
    public const string Secret = "secret";

    /// <summary>A file git ignores.</summary>
    public const string Ignored = "ignored";

    /// <summary>A submodule or a nested repository, which a diff does not carry.</summary>
    public const string Submodule = "submodule";
}

/// <summary>What the provider of a session keeps of it, carried in the bundle.</summary>
/// <param name="ProviderKey">The provider.</param>
/// <param name="Folder">The folder of the bundle that holds it.</param>
public sealed record SessionMoveProviderSession(
    [property: JsonPropertyName("providerKey")] string ProviderKey,
    [property: JsonPropertyName("folder")] string Folder);

/// <summary>The record a moved session's journal gets on the target (<c>codealta.sessionMoved</c>).</summary>
public sealed record SessionMovedRecord
{
    /// <summary>The type of the journal record.</summary>
    public const string EventType = "codealta.sessionMoved";

    /// <summary>Gets the identifier of the move.</summary>
    [JsonPropertyName("moveId")]
    public required string MoveId { get; init; }

    /// <summary>Gets the machine the session comes from.</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    /// <summary>Gets the machine the session goes to.</summary>
    [JsonPropertyName("to")]
    public string? To { get; init; }

    /// <summary>Gets the folder the session worked in, on the source.</summary>
    [JsonPropertyName("oldWorkingFolder")]
    public required string OldWorkingFolder { get; init; }

    /// <summary>Gets the folder the session works in, on the target.</summary>
    [JsonPropertyName("newWorkingFolder")]
    public required string NewWorkingFolder { get; init; }

    /// <summary>Gets the ownership epoch the move committed.</summary>
    [JsonPropertyName("epoch")]
    public long Epoch { get; init; }

    /// <summary>Gets when the session moved.</summary>
    [JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }
}

// The JSON of the session moves: the manifest of a bundle, the moved record, the relinquishment beside a lease, the
// transaction records and the decisions of an arbiter.
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionMoveManifest))]
[JsonSerializable(typeof(SessionMovedRecord))]
[JsonSerializable(typeof(SessionRelinquishment))]
[JsonSerializable(typeof(SessionMoveTransaction))]
[JsonSerializable(typeof(SessionMoveDecision))]
internal sealed partial class SessionMoveJsonContext : JsonSerializerContext;
