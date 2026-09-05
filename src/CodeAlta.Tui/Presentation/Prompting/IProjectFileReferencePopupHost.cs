using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Presentation.Prompting;

internal interface IProjectFileReferencePopupHost
{
    string? Text { get; set; }

    int CaretIndex { get; set; }

    Visual Visual { get; }

    void FocusPromptEditor();
}
