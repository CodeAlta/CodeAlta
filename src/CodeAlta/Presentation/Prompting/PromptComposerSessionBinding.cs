using XenoAtom.Terminal.UI;

namespace CodeAlta.Presentation.Prompting;

internal sealed record PromptComposerSessionBinding(
    Binding<string?> PromptText,
    PromptImageWorkspaceCallbacks? PromptImageCallbacks = null);
