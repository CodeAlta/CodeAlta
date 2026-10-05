using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugins.Abstractions.Tests;

/// <summary>The parts of the plugin contract that the desktop application shows: HTML fragments, dialogs with actions and prompt pickers.</summary>
[TestClass]
public sealed class PluginDesktopContractTests
{
    [TestMethod]
    public void HtmlHelpers_EncodeTextAndWriteTheAttributesTheWindowActsOn()
    {
        Assert.AreEqual("a &lt;b&gt; &amp; &quot;c&quot;", PluginHtml.Encode("a <b> & \"c\""));
        Assert.AreEqual(string.Empty, PluginHtml.Encode(null));
        Assert.AreEqual("<button type=\"button\" data-alta-command=\"notes\">Open &amp; edit</button>", PluginHtml.CommandButton("notes", "Open & edit"));
        Assert.AreEqual("<button type=\"button\" class=\"alta-primary\" data-alta-action=\"save\">Save</button>", PluginHtml.ActionButton("save", "Save", primary: true));
        Assert.ThrowsExactly<ArgumentException>(() => PluginHtml.CommandButton(" ", "label"));
        Assert.ThrowsExactly<ArgumentException>(() => PluginHtml.ActionButton("save", ""));
    }

    [TestMethod]
    public void RenderResult_CarriesAnHtmlFragmentWithItsPlainText()
    {
        var result = PluginRenderResult.FromHtml("<b>3</b> notes", "3 notes");

        Assert.AreEqual("<b>3</b> notes", result.Html);
        Assert.AreEqual("3 notes", result.Text);
        Assert.IsNull(result.Markdown);
        Assert.IsNull(PluginRenderResult.FromHtml("<i>x</i>").Text);
        Assert.ThrowsExactly<ArgumentException>(() => PluginRenderResult.FromHtml(" "));
    }

    [TestMethod]
    public async Task HtmlDialog_HasItsFragmentButtonsAndActionResults()
    {
        var ok = new PluginDialogButton { Name = "ok", Label = "Create", IsDefault = true };
        var request = PluginUi.HtmlDialog("New note", "<input name=\"title\">", ok) with
        {
            OnAction = static (action, _) => ValueTask.FromResult(action.Name == "close"
                ? PluginDialogActionResult.CloseDialog("done")
                : PluginDialogActionResult.Update($"<b>{action.Values["title"]}</b>")),
        };

        Assert.AreEqual(PluginDialogKind.Custom, request.Kind);
        Assert.AreEqual("New note", request.Title);
        Assert.AreEqual("<input name=\"title\">", request.Html);
        Assert.AreSame(ok, request.Buttons.Single());
        var updated = await request.OnAction!(new PluginDialogAction { Name = "preview", Values = new Dictionary<string, string> { ["title"] = "Fix" } }, default);
        Assert.AreEqual(("<b>Fix</b>", false, (string?)null), (updated.Html, updated.Close, updated.ButtonName));
        var closed = await request.OnAction(new PluginDialogAction { Name = "close" }, default);
        Assert.AreEqual(((string?)null, true, "done"), (closed.Html, closed.Close, closed.ButtonName));
        Assert.IsFalse(PluginDialogActionResult.KeepOpen.Close);
        Assert.IsNull(PluginDialogActionResult.KeepOpen.Html);
        Assert.AreEqual(0, new PluginDialogResponse().Values.Count);
        Assert.ThrowsExactly<ArgumentException>(() => PluginUi.HtmlDialog("Title", ""));
    }

    [TestMethod]
    public void PromptPicker_TakesAPunctuationCharacter()
    {
        PluginPromptPickerSearchHandler search = static (_, _) => ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>([]);

        var picker = PluginUi.PromptPicker("notes", '!', "Notes", search, "Search notes", order: 3);

        Assert.AreEqual(("notes", '!', "Notes", "Search notes", 3), (picker.Name, picker.Trigger, picker.Title, picker.PlaceholderText, picker.Order));
        Assert.AreSame(search, picker.SearchAsync);
        foreach (var trigger in new[] { 'a', '7', ' ', '\n' })
        {
            Assert.ThrowsExactly<ArgumentException>(() => PluginUi.PromptPicker("notes", trigger, "Notes", search), trigger.ToString());
        }

        Assert.AreEqual(0, new EmptyPlugin().GetPromptPickers().Count());
    }

    [TestMethod]
    public void SessionEvents_AndStatusItems_CarryWhatTheDesktopShows()
    {
        var card = new PluginDerivedSessionEvent
        {
            EventId = "notes:1", Markdown = "**Notes** · 3", Html = "<b>3</b>",
            DetailSections = [new PluginDerivedSessionEventDetailSection { Header = "All", Markdown = "- a", Html = "<ul><li>a</li></ul>" }],
        };

        Assert.AreEqual("<b>3</b>", card.Html);
        Assert.AreEqual("<ul><li>a</li></ul>", card.DetailSections.Single().Html);
        Assert.IsNull(new PluginDerivedSessionEvent { EventId = "plain" }.Html);
        Assert.AreEqual("open-notes", new PluginStatusItem { Label = "Notes", Text = "3", Command = "open-notes" }.Command);
        Assert.AreEqual(PluginFrontends.All, new PluginAttribute("notes").Frontends);
        Assert.AreEqual(PluginFrontends.None, new PluginHostInfo { ApplicationName = "Test", Version = "1", HostApiVersion = "1", UserDataDirectory = "." }.Frontend);
    }

    private sealed class EmptyPlugin : PluginBase;
}
