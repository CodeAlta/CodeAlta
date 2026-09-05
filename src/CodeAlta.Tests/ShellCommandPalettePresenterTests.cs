using CodeAlta.Tui.Frontend.Commands;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellCommandPalettePresenterTests
{
    [TestMethod]
    public void CommandPalettePopupStyle_IsBottomCenteredHalfWidth()
    {
        var style = ShellCommandPalettePresenter.CommandPalettePopupStyle;

        Assert.AreEqual(50d, style.PopupWidthPercent.GetValueOrDefault());
        Assert.AreEqual(int.MaxValue, style.MaxWidth);
        Assert.AreEqual(Align.Center, style.PopupHorizontalAlignment);
        Assert.AreEqual(Align.End, style.PopupVerticalAlignment);
        Assert.AreEqual(-2, style.PopupOffsetY);
    }

    [TestMethod]
    public void DialogCommandPalettePopupStyle_IsCentered()
    {
        var style = ShellCommandPalettePresenter.DialogCommandPalettePopupStyle;

        Assert.AreEqual(50d, style.PopupWidthPercent.GetValueOrDefault());
        Assert.AreEqual(int.MaxValue, style.MaxWidth);
        Assert.AreEqual(Align.Center, style.PopupHorizontalAlignment);
        Assert.AreEqual(Align.Center, style.PopupVerticalAlignment);
        Assert.AreEqual(0, style.PopupOffsetY);
    }

    [TestMethod]
    public void ResolveCommandPalettePopupStyle_UsesCenteredStyleInsideDialog()
    {
        var editor = new TextBox();
        var dialog = new Dialog(editor);

        var style = ShellCommandPalettePresenter.ResolveCommandPalettePopupStyle(editor);

        Assert.AreSame(ShellCommandPalettePresenter.DialogCommandPalettePopupStyle, style);
        Assert.AreSame(dialog, editor.Parent);
    }
}
