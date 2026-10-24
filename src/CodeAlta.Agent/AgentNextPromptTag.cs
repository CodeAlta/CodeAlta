namespace CodeAlta.Agent;

/// <summary>
/// The <c>&lt;next_prompt&gt;</c> tag a model ends its final answer of a turn with, to suggest what the user may write
/// next. Like the title tag, it is removed from every text that is shown (<see cref="AgentSessionTitleTag.Filter"/>);
/// the session tells its content as an <see cref="AgentPromptSuggestionEvent"/> once the turn ended.
/// </summary>
public static class AgentNextPromptTag
{
    /// <summary>The opening tag.</summary>
    public const string Opening = "<next_prompt>";

    /// <summary>The closing tag.</summary>
    public const string Closing = "</next_prompt>";

    /// <summary>
    /// The longest content of a tag: a longer one is ordinary text. It is longer than a suggestion may be
    /// (<see cref="AgentPromptSuggestionEvent.MaximumLength"/>), so that a suggestion too long to be told is still not shown.
    /// </summary>
    public const int MaxContentLength = 1000;
}
