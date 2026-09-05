using XenoAtom.Terminal.UI;

namespace CodeAlta.Tui.Presentation.Prompting;

internal sealed record ProjectFileAppearance(
    string Icon,
    Color IconForeground,
    Style NameStyle,
    string? Category = null);
