using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Presentation.Prompting;

internal sealed record ProjectFileAppearanceDescriptor(
    string Icon,
    Color Foreground,
    string? Category = null);
