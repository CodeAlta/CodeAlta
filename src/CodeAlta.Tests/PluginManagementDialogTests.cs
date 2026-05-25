using System.Reflection;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Views;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PluginManagementDialogTests
{
    [TestMethod]
    public void PluginListItemMarkup_DoesNotDimSelectedRowDetails()
    {
        var entry = new PluginManagementEntry
        {
            Key = "github",
            PluginId = "github",
            DisplayName = "GitHub",
            LoadUnitKind = PluginLoadUnitKind.BuiltIn,
            Scope = PluginScope.Global,
            State = PluginManagementState.Enabled,
            Enabled = true,
            Metadata = new Dictionary<string, string>
            {
                ["Description"] = "Adds a GitHub issue prompt picker.",
            },
        };

        var selectedMarkup = BuildPluginListItemMarkup(entry, selected: true);
        var unselectedMarkup = BuildPluginListItemMarkup(entry, selected: false);

        StringAssert.Contains(selectedMarkup, "built-in · enabled · Adds a GitHub issue prompt picker.");
        Assert.IsFalse(selectedMarkup.Contains("[dim]built-in", StringComparison.Ordinal));
        StringAssert.Contains(unselectedMarkup, "[dim]built-in · enabled · Adds a GitHub issue prompt picker.[/]");
    }

    private static string BuildPluginListItemMarkup(PluginManagementEntry entry, bool selected)
    {
        var method = typeof(PluginManagementDialog).GetMethod("BuildPluginListItemMarkup", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return (string)method.Invoke(null, [entry, selected])!;
    }
}
