using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.App;

internal static class PromptComposerSessionBindingFactory
{
    public static Func<string, ThreadSessionState?, PromptComposerSessionBinding> Create(
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
