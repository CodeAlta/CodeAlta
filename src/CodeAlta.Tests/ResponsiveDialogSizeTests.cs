using CodeAlta.Frontend.Commands;
using CodeAlta.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ResponsiveDialogSizeTests
{
    [TestMethod]
    public void Resolve_UsesResponsiveScalingWithinBounds()
    {
        var size = ResponsiveDialogSize.Resolve(new Rectangle(0, 0, 100, 50), minWidth: 40, minHeight: 20);

        Assert.AreEqual(80, size.Width);
        Assert.AreEqual(40, size.Height);
    }

    [TestMethod]
    public void Resolve_UsesMinimumSizeWhenBoundsAreSmallerOrMissing()
    {
        var missingBounds = ResponsiveDialogSize.Resolve(bounds: null, minWidth: 60, minHeight: 18);
        var smallBounds = ResponsiveDialogSize.Resolve(new Rectangle(0, 0, 40, 10), minWidth: 60, minHeight: 18);

        Assert.AreEqual(60, missingBounds.Width);
        Assert.AreEqual(18, missingBounds.Height);
        Assert.AreEqual(60, smallBounds.Width);
        Assert.AreEqual(18, smallBounds.Height);
    }

    [TestMethod]
    public void Apply_AssignsMinimumDialogDimensions()
    {
        var dialog = new Dialog();

        ResponsiveDialogSize.Apply(dialog, new Rectangle(0, 0, 120, 60), minWidth: 60, minHeight: 18);

        Assert.AreEqual(60, dialog.MinWidth);
        Assert.AreEqual(18, dialog.MinHeight);
    }

    [TestMethod]
    public void ConfigRecoveryDialog_UsesTerminalSizeWhenRootBoundsAreInitialMinimum()
    {
        var bounds = ConfigRecoveryDialog.ResolveDialogBounds(
            new Rectangle(0, 0, 80, 24),
            new TerminalSize(200, 60));

        var size = ResponsiveDialogSize.Resolve(bounds, minWidth: 80, minHeight: 24, widthFactor: 0.8, heightFactor: 0.8);

        Assert.AreEqual(160, size.Width);
        Assert.AreEqual(48, size.Height);
    }

    [TestMethod]
    public void SessionWorkspaceView_UsesF6ForExpandedPromptShortcut()
        => Assert.AreEqual("F6", SessionWorkspaceView.ExpandPromptShortcutKey.ToString());

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlSForFocusSidebarShortcut()
    {
        var sequence = BuiltinShellCommands.FocusSidebarShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlS, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlPForFocusPromptShortcut()
    {
        var sequence = BuiltinShellCommands.FocusPromptShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlP, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlOForModelsShortcut()
    {
        var sequence = BuiltinShellCommands.ModelsShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlO, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void SessionWorkspaceView_UsesCtrlGCtrlRForModelProvidersShortcut()
    {
        var sequence = SessionWorkspaceView.ModelProvidersShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlR, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlNForPluginsShortcut()
    {
        var sequence = BuiltinShellCommands.PluginsShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlN, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlWForWorkspaceSettingsShortcut()
    {
        var sequence = BuiltinShellCommands.WorkspaceSettingsShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlW, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlGForToggleNavigatorShortcut()
    {
        var sequence = BuiltinShellCommands.ToggleNavigatorShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlLForApplicationLogsShortcut()
    {
        var sequence = BuiltinShellCommands.ApplicationLogsShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlL, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlKForSkillsShortcut()
    {
        var sequence = BuiltinShellCommands.SkillsShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlK, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlGCtrlAForAboutShortcut()
    {
        var sequence = BuiltinShellCommands.AboutShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlA, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void SessionWorkspaceView_UsesCtrlGCtrlTForSessionInfoShortcut()
    {
        var sequence = SessionWorkspaceView.SessionInfoShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlT, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void SessionWorkspaceView_UsesCtrlGCtrlUForSessionUsageShortcut()
    {
        var sequence = SessionWorkspaceView.SessionUsageShortcutSequence;

        Assert.AreEqual(2, sequence.Count);
        Assert.AreEqual(TerminalChar.CtrlG, sequence[0].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[0].Modifiers);
        Assert.AreEqual(TerminalChar.CtrlU, sequence[1].Char);
        Assert.AreEqual(TerminalModifiers.Ctrl, sequence[1].Modifiers);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlAltLeftForTabLeftShortcut()
    {
        var gesture = BuiltinShellCommands.TabLeft.Gesture;

        Assert.AreEqual(new KeyGesture(TerminalKey.Left, TerminalModifiers.Ctrl | TerminalModifiers.Alt), gesture);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesCtrlAltRightForTabRightShortcut()
    {
        var gesture = BuiltinShellCommands.TabRight.Gesture;

        Assert.AreEqual(new KeyGesture(TerminalKey.Right, TerminalModifiers.Ctrl | TerminalModifiers.Alt), gesture);
    }

    [TestMethod]
    public void BuiltinShellCommands_UsesFunctionKeysForMessageNavigation()
    {
        Assert.AreEqual(new KeyGesture(TerminalKey.F3), BuiltinShellCommands.MessagePrevious.Gesture);
        Assert.AreEqual(new KeyGesture(TerminalKey.F4), BuiltinShellCommands.MessageNext.Gesture);
        Assert.AreEqual(new KeyGesture(TerminalKey.F3, TerminalModifiers.Ctrl), BuiltinShellCommands.MessageFirst.Gesture);
        Assert.AreEqual(new KeyGesture(TerminalKey.F4, TerminalModifiers.Ctrl), BuiltinShellCommands.MessageLast.Gesture);
    }
}
