using System.Text;

namespace CodeAlta.Plugins.Abstractions.Tests;

[TestClass]
public sealed class PluginKeyBindingTests
{
    [TestMethod]
    [DataRow("named")]
    [DataRow("ASCII")]
    [DataRow("BMP")]
    [DataRow("supplementary")]
    [DataRow("None")]
    [DataRow("undefined")]
    [DataRow("control")]
    [DataRow("modifiers")]
    [DataRow("default Rune")]
    [DataRow("surrogate")]
    public void Gesture_ValidatesIdentityAndModifiers(string row)
    {
        Assert.IsFalse(default(PluginKeyGesture).IsValid);
        switch (row)
        {
            case "None":
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PluginKeyGesture(PluginKey.None));
                return;
            case "undefined":
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PluginKeyGesture((PluginKey)999));
                return;
            case "control":
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PluginKeyGesture('\t'));
                return;
            case "modifiers":
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PluginKeyGesture('A', (PluginKeyModifiers)16));
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PluginKeyGesture(PluginKey.Enter, (PluginKeyModifiers)(-1)));
                return;
            case "default Rune":
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PluginKeyGesture(default(Rune)));
                return;
            case "surrogate":
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PluginKeyGesture('\uD800'));
                return;
        }

        var gesture = row switch
        {
            "named" => new PluginKeyGesture(PluginKey.Enter, PluginKeyModifiers.Meta),
            "ASCII" => new PluginKeyGesture('A', PluginKeyModifiers.Meta),
            "BMP" => new PluginKeyGesture('É', PluginKeyModifiers.Meta),
            "supplementary" => new PluginKeyGesture(new Rune(0x1F600), PluginKeyModifiers.Meta),
            _ => throw new AssertFailedException(row),
        };
        Assert.IsTrue(gesture.IsValid);
        Assert.AreEqual(PluginKeyModifiers.Meta, gesture.Modifiers);
        Assert.AreEqual(row == "named" ? PluginKey.Enter : PluginKey.None, gesture.Key);
        Assert.AreEqual(row == "named", gesture.Character is null);
        if (row != "named")
            Assert.AreEqual(row == "ASCII" ? 65 : row == "BMP" ? 201 : 0x1F600, gesture.Character!.Value.Value);
    }

    [TestMethod]
    [DataRow("ASCII")]
    [DataRow("BMP")]
    [DataRow("Space")]
    [DataRow("Ctrl")]
    [DataRow("non-letter")]
    public void Gesture_NormalizesLettersWithoutConflatingNamedKeys(string row)
    {
        switch (row)
        {
            case "ASCII":
                Assert.AreEqual(new PluginKeyGesture('A'), new PluginKeyGesture('a'));
                break;
            case "BMP":
                Assert.AreEqual(new PluginKeyGesture('É'), new PluginKeyGesture('é'));
                break;
            case "Space":
                Assert.AreNotEqual(new PluginKeyGesture(PluginKey.Space), new PluginKeyGesture(' '));
                break;
            case "Ctrl":
                var logical = new PluginKeyGesture('i', PluginKeyModifiers.Ctrl);
                Assert.AreEqual(new Rune('I'), logical.Character!.Value);
                Assert.AreNotEqual(new PluginKeyGesture(PluginKey.Tab), logical);
                Assert.AreNotEqual(new PluginKeyGesture(PluginKey.Enter), new PluginKeyGesture('m', PluginKeyModifiers.Ctrl));
                break;
            case "non-letter":
                Assert.AreNotEqual(new PluginKeyGesture('\u2170'), new PluginKeyGesture('\u2160'));
                Assert.AreEqual(new Rune(0x2170), new PluginKeyGesture('\u2170').Character!.Value);
                break;
            default: throw new AssertFailedException(row);
        }
    }

    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(-2)]
    public void Binding_ValidatesLengthAndCopiesInput(int length)
    {
        if (length == -1)
        {
            Assert.ThrowsExactly<ArgumentNullException>(() => new PluginKeyBinding(null!));
            return;
        }
        if (length == -2)
        {
            Assert.ThrowsExactly<ArgumentException>(() => new PluginKeyBinding(default(PluginKeyGesture)));
            return;
        }
        var input = Enumerable.Repeat(new PluginKeyGesture('A'), length).ToArray();
        if (length is 0 or 5)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PluginKeyBinding(input));
            return;
        }
        var binding = new PluginKeyBinding(input);
        input[0] = new PluginKeyGesture('B');
        Assert.AreEqual(length, binding.Count);
        Assert.AreEqual(length, binding.Gestures.Length);
        Assert.AreEqual(new PluginKeyGesture('A'), binding.Gestures[0]);
    }

    [TestMethod]
    [DataRow("plain", "Enter")]
    [DataRow("modifiers", "Ctrl+Alt+Shift+Meta+G")]
    [DataRow("sequence", "Ctrl+G Ctrl+Y")]
    [DataRow("supplementary", "😀")]
    public void Formatting_UsesTypedValuesNotIdentityStrings(string row, string expected)
    {
        var binding = row switch
        {
            "plain" => new PluginKeyBinding(new PluginKeyGesture(PluginKey.Enter)),
            "modifiers" => new PluginKeyBinding(new PluginKeyGesture('g', PluginKeyModifiers.Ctrl | PluginKeyModifiers.Alt | PluginKeyModifiers.Shift | PluginKeyModifiers.Meta)),
            "sequence" => new PluginKeyBinding(new PluginKeyGesture('g', PluginKeyModifiers.Ctrl), new PluginKeyGesture('y', PluginKeyModifiers.Ctrl)),
            "supplementary" => new PluginKeyBinding(new PluginKeyGesture(new Rune(0x1F600))),
            _ => throw new AssertFailedException(row),
        };
        Assert.AreEqual(expected, binding.ToString());
        if (binding.Count == 1) Assert.AreEqual(expected, binding.Gestures[0].ToString());
        Assert.IsTrue(binding.Gestures[0].IsValid);
    }
}
