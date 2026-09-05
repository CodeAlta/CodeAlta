using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Presentation.Prompting;

internal sealed record PromptComposerSessionBinding(
    Binding<string?> PromptText,
    PromptImageWorkspaceCallbacks? PromptImageCallbacks = null);
