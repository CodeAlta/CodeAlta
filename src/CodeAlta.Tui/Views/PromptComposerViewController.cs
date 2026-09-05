namespace CodeAlta.Tui.Views;

internal sealed record PromptComposerViewController(
    Action<string> AcceptPrompt,
    Action SendPrompt,
    Action AbortSession,
    Action OpenHelp,
    Action OpenCommandPalette)
{
    public static PromptComposerViewController Create(
        Action<string> acceptPrompt,
        Action sendPrompt,
        Action abortSession,
        Action openHelp,
        Action openCommandPalette)
    {
        ArgumentNullException.ThrowIfNull(acceptPrompt);
        ArgumentNullException.ThrowIfNull(sendPrompt);
        ArgumentNullException.ThrowIfNull(abortSession);
        ArgumentNullException.ThrowIfNull(openHelp);
        ArgumentNullException.ThrowIfNull(openCommandPalette);
        return new PromptComposerViewController(acceptPrompt, sendPrompt, abortSession, openHelp, openCommandPalette);
    }
}
