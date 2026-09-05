using CodeAlta.Tui.Presentation.Editing;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Extensions.CodeEditor.TextMateSharp;

namespace CodeAlta.Tests;

[TestClass]
public sealed class CodeEditorFactoryTests
{
    [TestMethod]
    public void Create_AppliesSharedEditorDefaultsAndSyntaxHighlighter()
    {
        var editor = CodeEditorFactory.Create(
            "alpha",
            new CodeEditorFactoryOptions
            {
                FileName = "sample.cs",
                AutoFocus = false,
                MinHeight = 8,
            });

        Assert.IsTrue(editor.WordWrap);
        Assert.IsTrue(editor.ShowLineNumbers);
        Assert.IsTrue(editor.HighlightCurrentLine);
        Assert.AreEqual(8, editor.MinHeight);
        Assert.AreEqual("alpha", CodeEditorFactory.GetText(editor));

        var highlighter = Assert.IsInstanceOfType<TextMateCodeEditorSyntaxHighlighter>(editor.SyntaxHighlighter);
        Assert.AreEqual("sample.cs", highlighter.Options.FileName);
    }

    [TestMethod]
    public void Create_BindsWordWrapToProvidedState()
    {
        var wordWrap = new State<bool>(false);
        var editor = CodeEditorFactory.Create(
            "alpha",
            new CodeEditorFactoryOptions
            {
                WordWrapState = wordWrap,
            });

        Assert.IsFalse(editor.WordWrap);

        wordWrap.Value = true;

        Assert.IsTrue(editor.WordWrap);
    }
}
