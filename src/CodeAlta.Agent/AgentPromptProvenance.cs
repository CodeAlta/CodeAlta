using System.Collections.Frozen;

namespace CodeAlta.Agent;

/// <summary>
/// The provenance of a user prompt that nobody typed in the composer: a prompt the host sent to a session (a reminder that
/// fired, the end of a background job, a review, the answer to a question, a message of another session...). It is
/// recorded with the user message as a stable identifier (<c>provenance</c> in the journal), so that the timelines
/// and the statistics tell it from a prompt of the user. A prompt the user typed has none.
/// </summary>
/// <remarks>
/// The identifiers are strings, not an enumeration: a new kind of provenance is a new identifier, which every reader
/// shows by its <see cref="GetDisplayName"/> fallback until it learns it. An identifier never changes once a session
/// records it; its display name can.
/// </remarks>
public static class AgentPromptProvenance
{
    /// <summary>The answer of the user to a question an agent asked (<c>alta ask</c>).</summary>
    public const string UserAnswer = "user-answer";

    /// <summary>A review round of the user: the comments and the verdict on changes or on a file.</summary>
    public const string UserReview = "user-review";

    /// <summary>A reminder that fired.</summary>
    public const string Reminder = "reminder";

    /// <summary>The end of a background job of the session.</summary>
    public const string BackgroundJob = "background-job";

    /// <summary>A prompt or a message of another agent session: a parent, a sub-agent or a peer.</summary>
    public const string AgentSession = "agent-session";

    /// <summary>The prompt of an automation.</summary>
    public const string Automation = "automation";

    /// <summary>The prompt that continues a session on another model after its model failed.</summary>
    public const string ModelFallback = "model-fallback";

    /// <summary>A prompt a plugin sent.</summary>
    public const string Plugin = "plugin";

    /// <summary>The message of a turn the provider started by itself (a background command of an agent CLI ended).</summary>
    public const string Provider = "provider";

    /// <summary>The result of a Code Mode script that went on in the background after its tool call.</summary>
    public const string Workflow = "workflow";

    /// <summary>The prompt that asks a session on Adaptive to check the change of its turn against the request.</summary>
    public const string AdaptiveRecheck = "adaptive-recheck";

    /// <summary>The prompt that continues on a stronger model a turn of a session on Adaptive that ended in visible trouble.</summary>
    public const string AdaptiveEscalation = "adaptive-escalation";

    /// <summary>The longest identifier, in characters.</summary>
    public const int MaxLength = 64;

    private static readonly FrozenDictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [UserAnswer] = "User Answer",
        [UserReview] = "User Review",
        [Reminder] = "Reminder",
        [BackgroundJob] = "Background Job",
        [AgentSession] = "Agent Session",
        [Automation] = "Automation",
        [ModelFallback] = "Model Fallback",
        [Plugin] = "Plugin",
        [Provider] = "Provider",
        [Workflow] = "Workflow",
        [AdaptiveRecheck] = "Adaptive Recheck",
        [AdaptiveEscalation] = "Adaptive Escalation",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets the identifiers this version knows, with a display name of their own.</summary>
    public static IEnumerable<string> KnownIds => DisplayNames.Keys;

    /// <summary>Gets a value indicating whether an identifier is one this version knows.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>True for a known identifier.</returns>
    public static bool IsKnown(string? id) => id is not null && DisplayNames.ContainsKey(id);

    /// <summary>
    /// Gets a value indicating whether a text is a well-formed identifier: lowercase ASCII letters and digits in words
    /// separated by single hyphens, at most <see cref="MaxLength"/> characters.
    /// </summary>
    /// <param name="id">The text.</param>
    /// <returns>True for a well-formed identifier, known or not.</returns>
    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength || id[0] == '-' || id[^1] == '-') return false;
        for (var index = 0; index < id.Length; index++)
        {
            var c = id[index];
            if (c == '-' ? id[index - 1] == '-' : !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)) return false;
        }

        return true;
    }

    /// <summary>Gets the identifier a text names, trimmed, or null for a blank or malformed text.</summary>
    /// <param name="id">The text.</param>
    /// <returns>The identifier, or null.</returns>
    public static string? Normalize(string? id)
    {
        var trimmed = id?.Trim();
        return IsValid(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// Gets the English name a timeline shows for an identifier: its own for a known one, the words of the identifier
    /// for another (<c>code-review</c> is <c>Code Review</c>).
    /// </summary>
    /// <param name="id">The identifier.</param>
    /// <returns>The display name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a well-formed identifier (see <see cref="IsValid"/>).</exception>
    public static string GetDisplayName(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (DisplayNames.TryGetValue(id, out var name)) return name;
        if (!IsValid(id)) throw new ArgumentException($"'{id}' is not a well-formed prompt provenance.", nameof(id));
        return string.Join(' ', id.Split('-').Select(static word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}
