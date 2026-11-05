namespace CodeAlta.Fleet.NodeApi;

/// <summary>
/// The offer of a session to a target (<c>moves.offer</c>): what the target checks before it takes the bundle (its project
/// in the repository, the provider), and the length and hash of the archive it will receive.
/// </summary>
/// <param name="MoveId">The move.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Title">Its title.</param>
/// <param name="FromMachine">The name of the source.</param>
/// <param name="RepositoryKey">The repository key the target finds its project by.</param>
/// <param name="RelativeFolder">The folder of the project in the repository; <c>.</c> for its root.</param>
/// <param name="Branch">The branch of the session; null for a detached HEAD.</param>
/// <param name="RemoteUrl">The URL of its remote, which a target without the repository may clone.</param>
/// <param name="ProviderKey">The provider of the session.</param>
/// <param name="ArchiveLength">The length of the archive of the bundle; 0 for a probe.</param>
/// <param name="ArchiveSha256">Its SHA-256 in lower-case hexadecimal; empty for a probe.</param>
/// <param name="Probe">Checks only: nothing is recorded, nothing is cloned.</param>
/// <param name="CloneRepository">Clones the repository on the target when no project of it is there.</param>
/// <param name="Via">The machine a relay forwards the call to: the target as the device in the middle names it.</param>
public sealed record FleetMoveOffer(string MoveId, string SessionId, string? Title, string? FromMachine, string RepositoryKey, string RelativeFolder,
    string? Branch, string? RemoteUrl, string? ProviderKey, long ArchiveLength, string ArchiveSha256, bool Probe = false, bool CloneRepository = false,
    string? Via = null);

/// <summary>What a target answers to an offer.</summary>
/// <param name="Status">
/// <c>accepted</c> (send the archive from <paramref name="ReceivedBytes"/>), the state of a move it knows already
/// (<c>prepared</c>, <c>committed</c>, <c>finished</c>, <c>aborted</c>), or <c>refused</c>.
/// </param>
/// <param name="Code">Why it refused: <c>project_missing</c>, <c>session_exists</c>, <c>receive_disabled</c>, <c>too_large</c>, <c>clone_failed</c>...</param>
/// <param name="Message">The refusal in words.</param>
/// <param name="ReceivedBytes">How much of the archive the target holds.</param>
/// <param name="ProjectId">The project of the target the session goes to.</param>
/// <param name="ProjectName">Its name.</param>
/// <param name="ProjectPath">Its folder on the target.</param>
/// <param name="CanClone">Whether the target can clone the repository when it has no project of it.</param>
/// <param name="Warnings">What the target says without refusing (a provider it lacks).</param>
public sealed record FleetMoveOfferResult(string Status, string? Code, string? Message, long ReceivedBytes, string? ProjectId, string? ProjectName,
    string? ProjectPath, bool CanClone, IReadOnlyList<string> Warnings);

/// <summary>A chunk of the archive of a bundle (<c>moves.transfer</c>).</summary>
/// <param name="MoveId">The move.</param>
/// <param name="Offset">Where the chunk goes in the archive: the length the target said it holds.</param>
/// <param name="Data">The bytes, base64; at most <see cref="FleetMoveLimits.ChunkBytes"/> of them.</param>
/// <param name="Via">The machine a relay forwards the call to.</param>
public sealed record FleetMoveChunk(string MoveId, long Offset, string Data, string? Via = null);

/// <summary>What a target holds of an archive after a chunk.</summary>
/// <param name="ReceivedBytes">Its length.</param>
public sealed record FleetMoveChunkResult(long ReceivedBytes);

/// <summary>A step of a move on a target (<c>moves.prepare</c>, <c>moves.commit</c>, <c>moves.complete</c>).</summary>
/// <param name="MoveId">The move.</param>
/// <param name="Via">The machine a relay forwards the call to.</param>
public sealed record FleetMoveRef(string MoveId, string? Via = null);

/// <summary>What a step of a move did.</summary>
/// <param name="Status">
/// The state of the move on the machine (<c>prepared</c>, <c>committed</c>, <c>finished</c>, <c>aborted</c>, <c>offered</c>), or
/// why the step was refused (<c>bundle_invalid</c>, <c>project_missing</c>, <c>missing</c>...).
/// </param>
/// <param name="Epoch">The ownership epoch the move gives the session.</param>
/// <param name="WorkFolder">The folder the session works in on the target.</param>
/// <param name="Branch">The branch it works on there.</param>
/// <param name="Warnings">What went wrong without stopping the step.</param>
public sealed record FleetMoveStepResult(string Status, long Epoch, string? WorkFolder, string? Branch, IReadOnlyList<string> Warnings);

/// <summary>The parameters of <c>moves.status</c>.</summary>
/// <param name="MoveId">The move.</param>
/// <param name="Resolve">
/// Asks the arbiter for the outcome, as a source does after a lost connection: a move the arbiter never committed is
/// aborted then, and a later commit refused.
/// </param>
/// <param name="Via">The machine a relay forwards the call to.</param>
public sealed record FleetMoveStatusParams(string MoveId, bool Resolve = false, string? Via = null);

/// <summary>A record of a move on a machine.</summary>
/// <param name="MoveId">The move.</param>
/// <param name="Role"><c>source</c> or <c>target</c>.</param>
/// <param name="State">Its state.</param>
/// <param name="SessionId">The session.</param>
/// <param name="FromMachine">The machine it comes from.</param>
/// <param name="ToMachine">The machine it goes to.</param>
/// <param name="Epoch">The epoch the move gives the session.</param>
/// <param name="WorkFolder">The folder the session works in on the target.</param>
/// <param name="Branch">Its branch there.</param>
/// <param name="Reason">Why it was undone.</param>
/// <param name="UpdatedAt">When the state was recorded.</param>
/// <param name="ToDeviceId">
/// On a source, the device the move is bound to: the one that drives it on the channel (the target, or the relay that
/// forwards to <paramref name="Via"/>). Only that device goes on with the move.
/// </param>
/// <param name="Via">On a source, the device the relay forwards the move to; null when <paramref name="ToDeviceId"/> is the target.</param>
public sealed record FleetMoveRecord(string MoveId, string Role, string State, string SessionId, string? FromMachine, string? ToMachine, long Epoch,
    string? WorkFolder, string? Branch, string? Reason, DateTimeOffset UpdatedAt, string? ToDeviceId = null, string? Via = null);

/// <summary>The outcome of a move as the arbiter knows it, and the records of the machine.</summary>
/// <param name="MoveId">The move.</param>
/// <param name="Outcome"><c>committed</c>, <c>aborted</c>, or null while the arbiter decided nothing.</param>
/// <param name="Epoch">The epoch a commit gave the session.</param>
/// <param name="Records">The records of the move on the machine.</param>
public sealed record FleetMoveStatus(string MoveId, string? Outcome, long Epoch, IReadOnlyList<FleetMoveRecord> Records);

/// <summary>The moves a machine recorded (<c>moves.list</c>).</summary>
/// <param name="Moves">The records, the latest first.</param>
public sealed record FleetMoveList(IReadOnlyList<FleetMoveRecord> Moves);

/// <summary>
/// The parameters of <c>moves.start</c>: the node gives one of its sessions to the caller, or, through the caller, to a
/// machine the caller reaches. The node is then the source and calls the methods of the target back on the same channel.
/// </summary>
/// <param name="SessionId">The session of the node.</param>
/// <param name="MoveId">The move; the same id again resumes it.</param>
/// <param name="TargetName">The name of the target, as the session will say it moved to.</param>
/// <param name="Via">The machine the caller forwards the target's methods to; null when the caller is the target.</param>
/// <param name="Probe">Only says what would move and what stays (<see cref="FleetMoveResult.Preview"/>): nothing is frozen.</param>
/// <param name="CloneRepository">Asks the target to clone the repository when it has no project of it.</param>
public sealed record FleetMoveStartParams(string SessionId, string MoveId, string? TargetName = null, string? Via = null, bool Probe = false, bool CloneRepository = false);

/// <summary>What a source says of a session before a move: what would stop it, what moves and what stays.</summary>
/// <param name="Status"><c>ok</c>, or why the session cannot move (<c>session_missing</c>, <c>not_movable</c>, <c>no_remote</c>, <c>not_repository</c>...).</param>
/// <param name="SessionId">The session.</param>
/// <param name="Title">Its title.</param>
/// <param name="Busy">What keeps it busy now (<c>working</c>, <c>waiting</c>, <c>background_running</c>); null when it is idle.</param>
/// <param name="RepositoryKey">Its repository key.</param>
/// <param name="RelativeFolder">The folder of its project in the repository.</param>
/// <param name="Branch">Its branch.</param>
/// <param name="RemoteUrl">The URL of its remote.</param>
/// <param name="UnpushedCommits">How many commits no remote has (they move).</param>
/// <param name="Dirty">Whether the working tree has changes (they move).</param>
/// <param name="Secrets">Changed files that look like secrets: they stay.</param>
/// <param name="Jobs">The background jobs of the session that run: they do not move.</param>
/// <param name="ProviderKey">Its provider.</param>
/// <param name="Message">What git said of a failure.</param>
public sealed record FleetMovePreview(string Status, string SessionId, string? Title, string? Busy, string? RepositoryKey, string RelativeFolder, string? Branch,
    string? RemoteUrl, int UnpushedCommits, bool Dirty, IReadOnlyList<string> Secrets, IReadOnlyList<string> Jobs, string? ProviderKey, string? Message);

/// <summary>The outcome of a move as its source drove it.</summary>
/// <param name="Status">
/// <c>finished</c> (the session runs on the target), <c>aborted</c> (it stays here), <c>offered</c> (the target could not be
/// reached after the session was frozen: start the move again with the same id to end it), <c>refused</c>, or
/// <c>preview</c> for a probe.
/// </param>
/// <param name="Code">Why it was refused or undone (<c>busy</c>, <c>queued</c>, <c>project_missing</c>, <c>forbidden</c>...).</param>
/// <param name="Message">The refusal in words.</param>
/// <param name="MoveId">The move.</param>
/// <param name="SessionId">The session.</param>
/// <param name="Epoch">The epoch the move gave the session.</param>
/// <param name="WorkFolder">The folder the session works in on the target.</param>
/// <param name="Warnings">What went wrong without stopping the move.</param>
/// <param name="Preview">What a probe found.</param>
public sealed record FleetMoveResult(string Status, string? Code, string? Message, string MoveId, string SessionId, long Epoch, string? WorkFolder,
    IReadOnlyList<string> Warnings, FleetMovePreview? Preview = null);

/// <summary>The bounds of the transfers of moves.</summary>
public static class FleetMoveLimits
{
    /// <summary>The most bytes of one chunk of an archive: well under the frame limit once in base64.</summary>
    public const int ChunkBytes = 512 * 1024;

    /// <summary>The largest archive a target takes: 4 GiB.</summary>
    public const long MaximumArchiveBytes = 4L * 1024 * 1024 * 1024;
}
