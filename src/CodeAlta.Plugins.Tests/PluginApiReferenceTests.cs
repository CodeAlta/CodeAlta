namespace CodeAlta.Plugins.Tests;

[TestClass]
public sealed class PluginApiReferenceTests
{
    private static readonly PluginApiReference Reference = new();

    [TestMethod]
    public void ARecord_ReadsAsItIsWritten()
    {
        var request = Reference.Find("PluginDialogRequest").Single();

        Assert.AreEqual("record", request.Kind);
        Assert.AreEqual("public record PluginDialogRequest", request.Declaration);
        Assert.AreEqual("CodeAlta.Plugins.Abstractions", request.Namespace);
        var members = Signatures(request);
        CollectionAssert.Contains(members, "required string Title { get; init; }");
        CollectionAssert.Contains(members, "string? Html { get; init; }");
        CollectionAssert.Contains(members, "IReadOnlyList<PluginDialogButton> Buttons { get; init; }");
        CollectionAssert.Contains(members, "PluginDialogActionHandler? OnAction { get; init; }");
        // What the compiler adds to a record is not part of what an author writes.
        Assert.IsFalse(members.Any(static member => member.Contains("Clone", StringComparison.Ordinal) || member.Contains("EqualityContract", StringComparison.Ordinal) || member.Contains("Equals", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void AContext_HasTheMembersOfItsBase()
    {
        var context = Reference.Find("PluginCommandContext").Single();

        Assert.AreEqual("public sealed class PluginCommandContext : PluginOperationContext", context.Declaration);
        var members = Signatures(context);
        CollectionAssert.Contains(members, "IPluginUiService Ui { get; }");
        CollectionAssert.Contains(members, "string? ProjectPath { get; init; }");
        CollectionAssert.Contains(members, "string? SessionId { get; init; }");
        CollectionAssert.Contains(Reference.Find("PluginOperationContext").Single().DerivedTypes.ToArray(), "PluginCommandContext");
    }

    [TestMethod]
    public void ADelegate_AnEnum_AndAFactory_ReadAsTheyAreWritten()
    {
        Assert.AreEqual(
            "public delegate ValueTask<PluginCommandResult> PluginCommandHandler(PluginCommandContext context, CancellationToken cancellationToken)",
            Reference.Find("PluginCommandHandler").Single().Declaration);

        var region = Reference.Find("PluginUiRegion").Single();
        Assert.AreEqual("enum", region.Kind);
        CollectionAssert.AreEqual(new[] { "CommandBar = 0", "SessionFooter = 1", "SessionStatus = 2" }, Signatures(region));

        var factory = Reference.Find("PluginUi").First(static type => type.Name == "PluginUi");
        Assert.AreEqual("public static class PluginUi", factory.Declaration);
        var members = Signatures(factory);
        CollectionAssert.Contains(members, "static PluginDialogRequest HtmlDialog(string title, string html, params PluginDialogButton[] buttons)");
        CollectionAssert.Contains(members, "static PluginStatusContribution SessionStatus(string label, string text, PluginStatusTone tone = PluginStatusTone.Info, string? iconMarkup = null, string? name = null, int order = 0)");
    }

    [TestMethod]
    public void AnInterface_HasItsGenericMethods()
    {
        var store = Reference.Find("IPluginStateStore").Single();

        Assert.AreEqual("interface", store.Kind);
        CollectionAssert.Contains(Signatures(store), "ValueTask<T?> ReadJsonAsync<T>(PluginStateScope scope, string name, CancellationToken cancellationToken = default)");
        CollectionAssert.Contains(Signatures(store), "string GetDirectory(PluginStateScope scope)");
    }

    [TestMethod]
    public void TheTypesOfTheAgentThatTheApiNames_ArePartOfIt()
    {
        var result = Reference.Find("AgentToolResult").Single();
        CollectionAssert.Contains(Signatures(result), "new AgentToolResult(bool Success, IReadOnlyList<AgentToolResultItem> Items, string? Error = null)");

        // A type is found with the types nested in it.
        var items = Reference.Find("AgentToolResultItem").Select(static type => type.Name).ToArray();
        CollectionAssert.Contains(items, "AgentToolResultItem");
        CollectionAssert.Contains(items, "AgentToolResultItem.Text");
        CollectionAssert.Contains(Signatures(Reference.Find("AgentToolResultItem.Text").Single()), "new AgentToolResultItem.Text(string Value)");

        CollectionAssert.Contains(Reference.Find("AgentEvent").First(static type => type.Name == "AgentEvent").DerivedTypes.ToArray(), "AgentActivityEvent");
        Assert.AreEqual("enum", Reference.Find("AgentActivityPhase").Single().Kind);
        // The rest of the agent runtime is not what a plugin is written against.
        Assert.AreEqual(0, Reference.Find("AgentSession").Count(static type => type.Name == "AgentSession"));
    }

    [TestMethod]
    public void ANamePart_FindsTypes_AndAMemberName_FindsItsTypes()
    {
        var dialogs = Reference.Find("dialog");
        Assert.IsTrue(dialogs.Count > 4);
        Assert.IsTrue(dialogs.All(static type => type.Name.Contains("Dialog", StringComparison.OrdinalIgnoreCase)));

        var owners = Reference.Find("SelectedProjectPath").Select(static type => type.Name).ToArray();
        CollectionAssert.Contains(owners, "IPluginWorkspaceService");

        Assert.AreEqual(0, Reference.Find("NoSuchThingAnywhere").Count);
        Assert.IsTrue(Reference.Types.Count > 100);
        CollectionAssert.AllItemsAreUnique(Reference.Types.Select(static type => type.Namespace + "." + type.Name).ToArray());
    }

    [TestMethod]
    public void Summaries_ComeFromTheDocumentationBesideTheAssemblies()
    {
        var documented = Reference.Find("PluginDialogRequest").Single();
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "CodeAlta.Plugins.Abstractions.xml")))
        {
            Assert.IsNull(documented.Summary);
            return;
        }

        Assert.AreEqual("Describes a dialog request supplied by a plugin.", documented.Summary);
        Assert.AreEqual("Gets the dialog title.", documented.Members.Single(static member => member.Name == "Title").Summary);
        // A reference to another member reads as its name.
        StringAssert.Contains(documented.Members.Single(static member => member.Name == "OnAction").Summary, "in Html.");

        using var temp = new TestTempDirectory();
        var bare = new PluginApiReference(temp.Path).Find("PluginDialogRequest").Single();
        Assert.IsNull(bare.Summary);
        CollectionAssert.AreEqual(Signatures(documented), Signatures(bare));
    }

    [TestMethod]
    public void ANameThatIsNotInTheApi_IsAnsweredWithTheNamesThatAreCloseToIt()
    {
        // A name an agent guessed: no type and no member has it.
        Assert.AreEqual(0, Reference.Find("PluginUiContext").Count);

        var near = Reference.Suggest("PluginUiContext");

        // The types that have its last word: the one that was meant is among them.
        CollectionAssert.Contains(near.ToArray(), "PluginStatusContext");
        CollectionAssert.Contains(near.ToArray(), "PluginCommandContext");
        Assert.IsTrue(near.All(static name => name.Contains("Context", StringComparison.Ordinal)), string.Join(", ", near));
        Assert.IsTrue(near.Count is > 12 and <= 32, near.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(3, Reference.Suggest("PluginUiContext", 3).Count);

        // Without a type for the last word, a word before it; "Plugin" alone and unknown words name nothing.
        Assert.IsTrue(Reference.Suggest("DialogZzzqq").All(static name => name.Contains("Dialog", StringComparison.Ordinal)));
        Assert.IsTrue(Reference.Suggest("DialogZzzqq").Count > 0);
        Assert.AreEqual(0, Reference.Suggest("Plugin").Count);
        Assert.AreEqual(0, Reference.Suggest("Zzzqq").Count);
        Assert.AreEqual(0, Reference.Suggest("PluginUiContext", 0).Count);
        Assert.ThrowsExactly<ArgumentException>(() => Reference.Suggest(" "));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Reference.Suggest("Context", -1));
    }

    private static string[] Signatures(PluginApiType type) => [.. type.Members.Select(static member => member.Signature)];
}
