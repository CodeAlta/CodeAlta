using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp;
using XenoAtom.Terminal.UI.Text;

namespace CodeAlta.Tui.Presentation.Editing;

internal static class CodeEditorFactory
{
    public static CodeEditor Create(string? text, CodeEditorFactoryOptions? options = null)
    {
        options ??= new CodeEditorFactoryOptions();
        var editor = options.Config is null ? new CodeEditor() : new CodeEditor(options.Config);

        editor.AutoFocus(options.AutoFocus)
            .ShowLineNumbers(options.ShowLineNumbers)
            .HighlightCurrentLine(options.HighlightCurrentLine)
            .MinHeight(options.MinHeight);

        if (options.WordWrapState is not null)
        {
            editor.WordWrap(options.WordWrapState);
        }
        else
        {
            editor.WordWrap(options.WordWrap);
        }

        editor.TextDocument = new TextDocument(text ?? string.Empty);
        editor.SyntaxHighlighter = CreateSyntaxHighlighter(options.FileName, options.LanguageId);
        return editor;
    }

    public static ScrollViewer CreateScrollViewer(CodeEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);

        return new ScrollViewer(editor.Stretch(), focusable: false)
            .IsTabStop(false)
            .HorizontalAlignment(Align.Stretch)
            .VerticalAlignment(Align.Stretch);
    }

    public static CodeEditorSyntaxHighlighter? CreateSyntaxHighlighter(string? fileName, string? languageId = null)
    {
        if (string.IsNullOrWhiteSpace(fileName) && string.IsNullOrWhiteSpace(languageId))
        {
            return null;
        }

        try
        {
            return new TextMateCodeEditorSyntaxHighlighter(
                new TextMateCodeEditorOptions
                {
                    FileName = fileName,
                    LanguageId = languageId,
                });
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string GetText(CodeEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        return GetText(editor.TextDocument);
    }

    public static string GetText(ITextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var snapshot = document.CurrentSnapshot;
        if (snapshot.Length == 0)
        {
            return string.Empty;
        }

        return string.Create(snapshot.Length, snapshot, static (span, currentSnapshot) => currentSnapshot.CopyTo(0, span));
    }
}

internal sealed record CodeEditorFactoryOptions
{
    public CodeEditorConfig? Config { get; init; }

    public string? FileName { get; init; }

    public string? LanguageId { get; init; }

    public bool AutoFocus { get; init; } = true;

    public bool WordWrap { get; init; } = true;

    public State<bool>? WordWrapState { get; init; }

    public bool ShowLineNumbers { get; init; } = true;

    public bool HighlightCurrentLine { get; init; } = true;

    public int MinHeight { get; init; } = 12;
}
