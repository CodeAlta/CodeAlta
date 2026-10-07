namespace CodeAlta.LiveTool;

/// <summary>
/// How the window looks, for the <c>alta appearance</c> commands. The settings are the user's and an agent only
/// reads them; what an agent changes is the view of one session, its own by default, so that two sessions never
/// compete for a setting of the window. A host without a window does not have the service, and then the commands
/// do not exist.
/// </summary>
public interface IAltaAppearance
{
    /// <summary>The least width of a conversation, as a percentage of the space of a session.</summary>
    public const int MinimumSessionWidth = 40;

    /// <summary>The width of a conversation that takes the whole space of a session.</summary>
    public const int DefaultSessionWidth = 100;

    /// <summary>Gets the user's setting: how much of the space of a session its timeline and its prompt take, in percent.</summary>
    int SessionWidth { get; }

    /// <summary>Gets the width one session is shown with instead of the user's setting.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The width in percent; null when the session follows the user's setting.</returns>
    int? GetSessionWidth(string sessionId);

    /// <summary>Changes the width one session is shown with, until the application exits or the user resizes that session.</summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="percent">
    /// The width, between <see cref="MinimumSessionWidth"/> and <see cref="DefaultSessionWidth"/>; null to follow the
    /// user's setting again.
    /// </param>
    /// <returns>False when the width is out of range or the session is not named: nothing changes.</returns>
    bool SetSessionWidth(string sessionId, int? percent);
}
