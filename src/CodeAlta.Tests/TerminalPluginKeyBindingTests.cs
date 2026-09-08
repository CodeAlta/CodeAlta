using System.Text;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using CodeAlta.Tui.Frontend.Commands;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI.Input;

namespace CodeAlta.Tests;

[TestClass]
public sealed class TerminalPluginKeyBindingTests
{
    [TestMethod]
    [DataRow(PluginKey.Enter, TerminalKey.Enter)]
    [DataRow(PluginKey.Escape, TerminalKey.Escape)]
    [DataRow(PluginKey.Backspace, TerminalKey.Backspace)]
    [DataRow(PluginKey.Tab, TerminalKey.Tab)]
    [DataRow(PluginKey.Space, TerminalKey.Space)]
    [DataRow(PluginKey.Up, TerminalKey.Up)]
    [DataRow(PluginKey.Down, TerminalKey.Down)]
    [DataRow(PluginKey.Left, TerminalKey.Left)]
    [DataRow(PluginKey.Right, TerminalKey.Right)]
    [DataRow(PluginKey.Home, TerminalKey.Home)]
    [DataRow(PluginKey.End, TerminalKey.End)]
    [DataRow(PluginKey.PageUp, TerminalKey.PageUp)]
    [DataRow(PluginKey.PageDown, TerminalKey.PageDown)]
    [DataRow(PluginKey.Insert, TerminalKey.Insert)]
    [DataRow(PluginKey.Delete, TerminalKey.Delete)]
    [DataRow(PluginKey.F1, TerminalKey.F1)]
    [DataRow(PluginKey.F2, TerminalKey.F2)]
    [DataRow(PluginKey.F3, TerminalKey.F3)]
    [DataRow(PluginKey.F4, TerminalKey.F4)]
    [DataRow(PluginKey.F5, TerminalKey.F5)]
    [DataRow(PluginKey.F6, TerminalKey.F6)]
    [DataRow(PluginKey.F7, TerminalKey.F7)]
    [DataRow(PluginKey.F8, TerminalKey.F8)]
    [DataRow(PluginKey.F9, TerminalKey.F9)]
    [DataRow(PluginKey.F10, TerminalKey.F10)]
    [DataRow(PluginKey.F11, TerminalKey.F11)]
    [DataRow(PluginKey.F12, TerminalKey.F12)]
    public void Mapping_NamedKeysRemainExplicit(PluginKey key, TerminalKey expected)
    {
        Assert.IsTrue(PluginTerminalKeyBindingMapper.TryMap(new PluginKeyBinding(new PluginKeyGesture(key)), out var gesture, out var sequence));
        Assert.AreEqual(new KeyGesture(expected), gesture!.Value);
        Assert.IsNull(sequence);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 2)]
    [DataRow(2, 4)]
    [DataRow(3, 6)]
    [DataRow(4, 1)]
    [DataRow(5, 3)]
    [DataRow(6, 5)]
    [DataRow(7, 7)]
    [DataRow(8, 8)]
    [DataRow(9, 10)]
    [DataRow(10, 12)]
    [DataRow(11, 14)]
    [DataRow(12, 9)]
    [DataRow(13, 11)]
    [DataRow(14, 13)]
    [DataRow(15, 15)]
    public void Mapping_MapsAllModifierCombinations(int neutral, int native)
    {
        Assert.IsTrue(PluginTerminalKeyBindingMapper.TryMap(new PluginKeyBinding(new PluginKeyGesture(PluginKey.F1, (PluginKeyModifiers)neutral)), out var gesture, out var sequence));
        Assert.AreEqual(new KeyGesture(TerminalKey.F1, (TerminalModifiers)native), gesture!.Value);
        Assert.IsNull(sequence);
    }

    [TestMethod]
    [DataRow('g', PluginKeyModifiers.Ctrl, '\u0007')]
    [DataRow('g', PluginKeyModifiers.None, 'G')]
    [DataRow('é', PluginKeyModifiers.None, 'É')]
    [DataRow('+', PluginKeyModifiers.Ctrl, '+')]
    [DataRow(' ', PluginKeyModifiers.None, ' ')]
    public void Mapping_CharactersUseTerminalEncoding(char input, PluginKeyModifiers modifiers, char expected)
    {
        Assert.IsTrue(PluginTerminalKeyBindingMapper.TryMap(new PluginKeyBinding(new PluginKeyGesture(input, modifiers)), out var gesture, out var sequence));
        Assert.AreEqual(new KeyGesture(expected, modifiers == PluginKeyModifiers.Ctrl ? TerminalModifiers.Ctrl : TerminalModifiers.None), gesture!.Value);
        Assert.IsNull(sequence);
        if (input == 'é')
        {
            Assert.IsTrue(PluginTerminalKeyBindingMapper.TryMap(new PluginKeyBinding(new PluginKeyGesture('\u2170')), out var nonLetter, out var nonLetterSequence));
            Assert.AreEqual(new KeyGesture('\u2170'), nonLetter!.Value);
            Assert.IsNull(nonLetterSequence);
        }
    }

    [TestMethod]
    [DataRow("only")]
    [DataRow("first")]
    [DataRow("last")]
    public void Mapping_RejectsWholeUnsupportedBinding(string row)
    {
        var unsupported = new PluginKeyGesture(new Rune(0x1F600));
        var supported = new PluginKeyGesture('G');
        var binding = row switch
        {
            "only" => new PluginKeyBinding(unsupported),
            "first" => new PluginKeyBinding(unsupported, supported),
            "last" => new PluginKeyBinding(supported, unsupported),
            _ => throw new AssertFailedException(row),
        };
        KeyGesture? gesture = new KeyGesture(TerminalKey.Enter);
        KeySequence? sequence = new KeySequence(new KeyGesture(TerminalKey.Enter), new KeyGesture(TerminalKey.Tab));
        Assert.IsFalse(PluginTerminalKeyBindingMapper.TryMap(binding, out gesture, out sequence));
        Assert.IsNull(gesture);
        Assert.IsNull(sequence);
        Assert.AreEqual(row == "only" ? 1 : 2, binding.Count);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    public void Mapping_PreservesSequenceOrder(int count)
    {
        PluginKeyGesture[] strokes =
        [
            new('g', PluginKeyModifiers.Ctrl),
            new('y', PluginKeyModifiers.Ctrl | PluginKeyModifiers.Meta),
            new(PluginKey.F1, PluginKeyModifiers.Alt),
            new(' ', PluginKeyModifiers.Shift),
        ];
        KeyGesture[] expected =
        [
            new('\u0007', TerminalModifiers.Ctrl),
            new('\u0019', TerminalModifiers.Ctrl | TerminalModifiers.Meta),
            new(TerminalKey.F1, TerminalModifiers.Alt),
            new(' ', TerminalModifiers.Shift),
        ];
        Assert.IsTrue(PluginTerminalKeyBindingMapper.TryMap(new PluginKeyBinding(strokes[..count]), out var gesture, out var sequence));
        if (count == 1)
        {
            Assert.AreEqual(expected[0], gesture!.Value);
            Assert.IsNull(sequence);
        }
        else
        {
            Assert.IsNull(gesture);
            Assert.AreEqual(count, sequence!.Value.Count);
            CollectionAssert.AreEqual(expected[..count], sequence.Value.Gestures.ToArray());
        }
    }

    [TestMethod]
    public void Mapping_RejectsNull()
        => Assert.ThrowsExactly<ArgumentNullException>(() => PluginTerminalKeyBindingMapper.TryMap(null!, out _, out _));

    [TestMethod]
    [DataRow("none")]
    [DataRow("supported")]
    [DataRow("unsupported")]
    [DataRow("null")]
    public void CommandAdaptation_UsesActualMappingWithoutAcquiringServices(string row)
    {
        if (row == "null")
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => PluginShellCommandAdapter.CreateCommand(null!));
            return;
        }
        var binding = row switch
        {
            "none" => null,
            "supported" => new PluginKeyBinding(new PluginKeyGesture('g', PluginKeyModifiers.Ctrl)),
            "unsupported" => new PluginKeyBinding(new PluginKeyGesture(new Rune(0x1F600))),
            _ => throw new AssertFailedException(row),
        };
        var contribution = new PluginCommandContribution
        {
            Name = "literal-command",
            Label = "Literal label",
            Description = "Literal description",
            SearchText = "Literal search",
            KeyBinding = binding,
            Placement = PluginCommandPlacement.ShellRoot | PluginCommandPlacement.PromptEditor | PluginCommandPlacement.WorkspaceRoot,
            ShowInCommandBar = false,
            ShowInCommandPalette = true,
            ShowInHelp = false,
            Handler = static (_, _) => throw new AssertFailedException("Original handler must remain inert."),
        };
        var originalHandler = contribution.Handler;
        var command = PluginShellCommandAdapter.CreateCommand(contribution);
        Assert.AreEqual("Plugin.literal-command", command.Id);
        Assert.AreEqual(contribution.Name, command.Name);
        Assert.AreEqual(contribution.Label, command.Label);
        Assert.AreEqual(contribution.Description, command.Description);
        Assert.AreEqual(contribution.SearchText, command.SearchText);
        Assert.AreEqual(ShellCommandHelpCategory.General, command.HelpCategory);
        Assert.AreEqual(ShellCommandPlacement.ShellRoot | ShellCommandPlacement.PromptEditor | ShellCommandPlacement.WorkspaceRoot, command.Placement);
        Assert.IsFalse(command.ShowInCommandBar);
        Assert.IsTrue(command.ShowInCommandPalette);
        Assert.IsFalse(command.ShowInHelp);
        Assert.IsNull(command.Sequence);
        if (row == "supported") Assert.AreEqual(new KeyGesture('\u0007', TerminalModifiers.Ctrl), command.Gesture!.Value);
        else Assert.IsNull(command.Gesture);
        Assert.AreSame(binding, contribution.KeyBinding);
        Assert.AreSame(originalHandler, contribution.Handler);
        // Inspect captured delegates only. Never call them, localization, controls, or services.
        Assert.IsNotNull(command.CanExecute);
        Assert.IsNotNull(command.ExecuteAsync);
        Assert.IsNull(command.IsVisible);
    }
}
