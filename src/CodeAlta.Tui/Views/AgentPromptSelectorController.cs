namespace CodeAlta.Tui.Views;

internal sealed record AgentPromptSelectorController(Action<int> SelectPrompt, Action OpenPrompts)
{
    public static AgentPromptSelectorController Create(Action<int> selectPrompt)
        => Create(selectPrompt, static () => { });

    public static AgentPromptSelectorController Create(Action<int> selectPrompt, Action openPrompts)
    {
        ArgumentNullException.ThrowIfNull(selectPrompt);
        ArgumentNullException.ThrowIfNull(openPrompts);
        return new AgentPromptSelectorController(selectPrompt, openPrompts);
    }
}
