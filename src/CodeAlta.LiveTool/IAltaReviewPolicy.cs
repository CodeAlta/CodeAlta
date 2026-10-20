namespace CodeAlta.LiveTool;

/// <summary>Whether a session reviews changes before they are committed, and what decides it.</summary>
/// <param name="On">Whether the user reviews the changes of the session before the agent commits them.</param>
/// <param name="Source"><c>session</c> when the session has its own value, <c>settings</c> when the setting applies.</param>
public sealed record AltaReviewBeforeCommit(bool On, string Source);

/// <summary>Tells <c>alta review status</c> whether a session's changes wait for the user's review before a commit.</summary>
public interface IAltaReviewPolicy
{
    /// <summary>Gets the state of a session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>Whether the session reviews before commit, and where that comes from.</returns>
    AltaReviewBeforeCommit GetBeforeCommit(string sessionId);
}
