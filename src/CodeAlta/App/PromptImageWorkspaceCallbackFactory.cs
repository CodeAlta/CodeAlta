using CodeAlta.Models;
using CodeAlta.Presentation.Prompting;

namespace CodeAlta.App;

internal static class PromptImageWorkspaceCallbackFactory
{
    public static PromptImageWorkspaceCallbacks Create(
        PromptDraftUiCoordinator promptDrafts,
        PromptImageCapabilityContext capabilities,
        Action<string, StatusTone> setStatus)
    {
        ArgumentNullException.ThrowIfNull(promptDrafts);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(setStatus);

        return new PromptImageWorkspaceCallbacks(
            () => promptDrafts.CurrentPromptImages,
            promptDrafts.GetNextImageTitle,
            promptDrafts.AddPromptImage,
            (imageId, title) => _ = promptDrafts.RenamePromptImage(imageId, title),
            imageId => _ = promptDrafts.DeletePromptImage(imageId),
            capabilities.CurrentPromptModelSupportsImageInput,
            capabilities.BuildCurrentPromptImageUnsupportedMessage,
            setStatus);
    }
}
