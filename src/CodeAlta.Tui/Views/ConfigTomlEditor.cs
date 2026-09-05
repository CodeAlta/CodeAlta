using CodeAlta.Tui.Presentation.Editing;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tui.Views;

internal static class ConfigTomlEditor
{
    public static CodeEditor Create(string text, string? fileName)
        => CodeEditorFactory.Create(
            text,
            new CodeEditorFactoryOptions
            {
                FileName = fileName,
                LanguageId = "toml",
                WordWrap = false,
            });
}
