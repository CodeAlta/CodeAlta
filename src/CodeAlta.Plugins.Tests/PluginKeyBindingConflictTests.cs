using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginKeyBindingConflictTests
{
    [TestMethod]
    [DataRow("same", true)]
    [DataRow("key", false)]
    [DataRow("modifier", false)]
    [DataRow("order", false)]
    [DataRow("kind", false)]
    [DataRow("length", false)]
    [DataRow("case", true)]
    public void ConflictKey_UsesStructuralIdentity(string row, bool equal)
    {
        var left = new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl));
        var right = row switch
        {
            "same" => new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl)),
            "key" => new PluginKeyBinding(new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl)),
            "modifier" => new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl | PluginKeyModifiers.Meta)),
            "order" => new PluginKeyBinding(new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl), new PluginKeyGesture('G', PluginKeyModifiers.Ctrl)),
            "kind" => new PluginKeyBinding(new PluginKeyGesture(PluginKey.Space)),
            "length" => new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl), new PluginKeyGesture('G', PluginKeyModifiers.Ctrl)),
            "case" => new PluginKeyBinding(new PluginKeyGesture('g', PluginKeyModifiers.Ctrl)),
            _ => throw new AssertFailedException(row),
        };
        if (row == "order") left = new PluginKeyBinding(new PluginKeyGesture('G', PluginKeyModifiers.Ctrl), new PluginKeyGesture('Y', PluginKeyModifiers.Ctrl));
        if (row == "kind") left = new PluginKeyBinding(new PluginKeyGesture(' '));
        var leftKey = PluginContributionRegistry.GetConflictKeys(Registration(left)).Last();
        var rightKey = PluginContributionRegistry.GetConflictKeys(Registration(right)).Last();
        Assert.AreEqual(equal, string.Equals(leftKey.Value, rightKey.Value, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual("keybinding", leftKey.Kind);
        Assert.AreEqual(left.ToString(), leftKey.DisplayName);
        if (row is "same" or "case") Assert.AreEqual("keybinding:1:U000047/01;", leftKey.Value);
        if (row == "modifier") Assert.AreEqual("keybinding:1:U000047/09;", rightKey.Value);
        if (row == "kind") Assert.AreEqual("keybinding:1:N000005/00;", rightKey.Value);
        if (row == "order") Assert.AreEqual("keybinding:2:U000047/01;U000059/01;", leftKey.Value);
        if (row == "length") Assert.AreEqual("keybinding:2:U000047/01;U000047/01;", rightKey.Value);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void ConflictKeys_PreserveCommandKeyAndTypedBindingOrder(int count)
    {
        var binding = count == 0 ? null : new PluginKeyBinding(Enumerable.Repeat(new PluginKeyGesture('X', PluginKeyModifiers.Ctrl), count).ToArray());
        var keys = PluginContributionRegistry.GetConflictKeys(Registration(binding)).ToArray();
        Assert.AreEqual(count == 0 ? 1 : 2, keys.Length);
        Assert.AreEqual("command", keys[0].Kind);
        Assert.AreEqual("command:literal-command", keys[0].Value);
        Assert.AreEqual("literal-command", keys[0].DisplayName);
        if (binding is not null)
        {
            Assert.AreEqual("keybinding", keys[1].Kind);
            Assert.AreEqual(count == 1 ? "keybinding:1:U000058/01;" : "keybinding:2:U000058/01;U000058/01;", keys[1].Value);
            Assert.AreEqual(binding.ToString(), keys[1].DisplayName);
        }
    }

    [TestMethod]
    public void ConflictKeys_RejectNullRegistration()
    {
        var keys = PluginContributionRegistry.GetConflictKeys(null!);
        Assert.IsNotNull(keys);
        using var enumerator = keys.GetEnumerator();
        Assert.ThrowsExactly<NullReferenceException>(() => enumerator.MoveNext());
    }

    private static PluginContributionRegistration Registration(PluginKeyBinding? binding)
        => new()
        {
            Handle = new PluginContributionHandle
            {
                PluginRuntimeKey = "literal-plugin",
                PluginTypeName = "LiteralPlugin",
                Point = PluginPoint.Command,
                RuntimeContributionKey = "literal-registration",
                NaturalName = "literal-command",
            },
            Scope = PluginScope.Global,
            Contribution = new PluginCommandContribution
            {
                Name = "literal-command",
                KeyBinding = binding,
                Handler = static (_, _) => throw new AssertFailedException("Command handler must remain inert."),
            },
        };
}
