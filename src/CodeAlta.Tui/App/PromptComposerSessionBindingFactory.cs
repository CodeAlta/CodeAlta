using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Prompting;

namespace CodeAlta.Tui.App;

internal static class PromptComposerSessionBindingFactory
{
    public static Func<string, SessionState?, PromptComposerSessionBinding> Create(
        PromptDraftUiCoordinator promptDrafts,
        PromptImageCapabilityContext imageCapabilities,
        Action<string, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(promptDrafts);
        ArgumentNullException.ThrowIfNull(imageCapabilities);
        ArgumentNullException.ThrowIfNull(setStatus);

        return (tabId, session) => new PromptComposerSessionBinding(
            promptDrafts.GetPromptTextBinding(tabId, session),
            PromptImageWorkspaceCallbackFactory.Create(
                promptDrafts,
                tabId,
                session,
                imageCapabilities,
                setStatus));
    }
}
