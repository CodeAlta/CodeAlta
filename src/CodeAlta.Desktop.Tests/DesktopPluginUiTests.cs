using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>The broker between the plugins and the page: what is asked, what is answered, and what is kept for a page that is not there.</summary>
[TestClass]
public sealed class DesktopPluginUiTests
{
    [TestMethod]
    public async Task Notifications_AreKeptForThePageThatArrives_NewestOnly()
    {
        using var ui = new DesktopPluginUi();
        for (var index = 0; index < DesktopPluginUi.MaximumBacklog + 3; index++) await ui.NotifyAsync("message " + index);
        ui.NotifyProblem("a problem");

        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);
        await ui.NotifyAsync("live");

        Assert.AreEqual(DesktopPluginUi.MaximumBacklog + 1, seen.Count);
        Assert.AreEqual("message 4", seen[0].Message);
        Assert.AreEqual("warning", seen[^2].Tone);
        Assert.AreEqual("live", seen[^1].Message);
        Assert.IsTrue(seen.All(static item => item.Kind == "notify"));
    }

    [TestMethod]
    public async Task Dialogs_ReturnWhatThePageAnswers()
    {
        using var ui = new DesktopPluginUi();
        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);

        var confirm = ui.ConfirmAsync("Title", "Continue?").AsTask();
        Assert.AreEqual("confirm", seen[^1].Dialog);
        Assert.AreEqual("Continue?", seen[^1].Message);
        Assert.IsTrue(ui.Respond(Answer(seen[^1], button: "yes")));
        Assert.IsTrue(await confirm);
        Assert.IsFalse(ui.Respond(Answer(seen[^1], button: "yes")), "a request is answered once");

        var refused = ui.ConfirmAsync("Title", "Continue?").AsTask();
        ui.Respond(Answer(seen[^1], button: "no", cancelled: true));
        Assert.IsFalse(await refused);

        var input = ui.InputAsync("Name", "Ada").AsTask();
        Assert.AreEqual("Ada", seen[^1].Text);
        ui.Respond(Answer(seen[^1], button: "ok", text: "Grace"));
        Assert.AreEqual("Grace", await input);

        var edit = ui.EditTextAsync("Note", "text").AsTask();
        Assert.AreEqual("edit", seen[^1].Dialog);
        ui.Respond(Answer(seen[^1], cancelled: true));
        Assert.IsNull(await edit);

        var items = new[] { new PluginSelectItem<int> { Label = "one", Value = 1 }, new PluginSelectItem<int> { Label = "two", Value = 2, Description = "second", IsSelected = true } };
        var select = ui.SelectAsync("Pick", items).AsTask();
        CollectionAssert.AreEqual(new[] { new PluginUiChoice("one", null, false), new PluginUiChoice("two", "second", true) }, seen[^1].Items);
        ui.Respond(Answer(seen[^1], button: "ok", selectedIndex: 1));
        Assert.AreEqual(2, await select);

        var outside = ui.SelectAsync("Pick", items).AsTask();
        ui.Respond(Answer(seen[^1], button: "ok", selectedIndex: 7));
        Assert.AreEqual(0, await outside);
        Assert.AreEqual(0, await ui.SelectAsync("Pick", Array.Empty<PluginSelectItem<int>>()));
    }

    [TestMethod]
    public async Task HtmlDialog_RunsItsActions_AndClosesWithTheFieldValues()
    {
        using var ui = new DesktopPluginUi();
        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);
        var count = 0;
        var request = PluginUi.HtmlDialog("Form", "<b>0</b>", new PluginDialogButton { Name = "ok", Label = "Create", IsDefault = true }) with
        {
            OnAction = (action, _) => action.Name switch
            {
                "count" => ValueTask.FromResult(PluginDialogActionResult.Update($"<b>{++count}</b>")),
                "finish" => ValueTask.FromResult(PluginDialogActionResult.CloseDialog("done")),
                "throw" => throw new InvalidOperationException("secret"),
                _ => ValueTask.FromResult(PluginDialogActionResult.KeepOpen),
            },
        };

        var dialog = ui.ShowDialogForResultAsync(request).AsTask();
        var shown = seen[^1];
        Assert.AreEqual("html", shown.Dialog);
        Assert.AreEqual("<b>0</b>", shown.Html);
        Assert.IsTrue(shown.Actions);
        Assert.AreEqual(new PluginUiButton("ok", "Create", true, false), shown.Buttons!.Single());

        Assert.AreEqual(Result("ok", "<b>1</b>", false), await ui.ActionAsync(shown.RequestId, "count", null, null, default));
        Assert.AreEqual(Result("ok", null, false), await ui.ActionAsync(shown.RequestId, "other", null, null, default));
        Assert.AreEqual(Result("failed", null, false), await ui.ActionAsync(shown.RequestId, "throw", null, null, default));
        Assert.AreEqual(Result("unknown", null, false), await ui.ActionAsync("ui-999", "count", null, null, default));
        Assert.IsFalse(dialog.IsCompleted);

        var closed = await ui.ActionAsync(shown.RequestId, "finish", "v", new Dictionary<string, string> { ["title"] = "Fix" }, default);
        Assert.AreEqual(Result("ok", null, true), closed);
        var response = await dialog;
        Assert.AreEqual("done", response!.ButtonName);
        Assert.IsFalse(response.Cancelled);
        Assert.AreEqual("Fix", response.Values["title"]);

        // A dialog without a handler has no action; its buttons return the field values.
        var plain = ui.ShowDialogForResultAsync(PluginUi.HtmlDialog("Plain", "<input name=\"a\">")).AsTask();
        Assert.IsFalse(seen[^1].Actions);
        Assert.AreEqual(Result("unsupported", null, false), await ui.ActionAsync(seen[^1].RequestId, "count", null, null, default));
        ui.Respond(Answer(seen[^1], button: "close", values: new() { ["a"] = "1" }));
        Assert.AreEqual("1", (await plain)!.Values["a"]);
    }

    [TestMethod]
    public async Task OpenDialogs_AreGivenAgainToAPageThatReloads_AndClosedWhenCancelled()
    {
        using var ui = new DesktopPluginUi();
        var first = new List<PluginUiEvent>();
        var watch = ui.Watch(first.Add);
        using var cancellation = new CancellationTokenSource();
        var confirm = ui.ConfirmAsync("Title", "Continue?", cancellation.Token).AsTask();
        watch.Dispose();

        var second = new List<PluginUiEvent>();
        using var reloaded = ui.Watch(second.Add);
        Assert.AreEqual(first.Single().RequestId, second.Single().RequestId);

        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => confirm);
        Assert.AreEqual("close", second[^1].Kind);
        Assert.AreEqual(first[0].RequestId, second[^1].RequestId);
        Assert.IsFalse(ui.Respond(Answer(first[0], button: "yes")));
    }

    [TestMethod]
    public async Task Dispose_EndsOpenDialogsAsCancelled()
    {
        var ui = new DesktopPluginUi();
        var confirm = ui.ConfirmAsync("Title", "Continue?").AsTask();
        var dialog = ui.ShowDialogForResultAsync(PluginUi.NotifyDialog("Title", "Message")).AsTask();

        ui.Dispose();

        Assert.IsFalse(await confirm);
        Assert.IsNull(await dialog);
        Assert.IsFalse(await ui.ConfirmAsync("Title", "After"));
    }

    [TestMethod]
    public async Task ThePaneOfAnOperation_NamesItsSessionAndDraft_AndTakesItsPrompts()
    {
        using var ui = new DesktopPluginUi();
        var seen = new List<PluginUiEvent>();
        using var watch = ui.Watch(seen.Add);
        Assert.IsNull(ui.SelectedSessionId);
        Assert.IsNull(ui.DraftText);

        await Task.Run(async () =>
        {
            ui.Enter(new DesktopPluginScope { ProjectId = "project", SessionId = "session", SessionBusy = true, DraftText = "draft" });
            Assert.AreEqual("session", ui.SelectedSessionId);
            Assert.IsTrue(ui.IsSelectedSessionBusy);
            Assert.AreEqual("draft", ui.DraftText);

            await ui.SetDraftTextAsync("new draft");
            Assert.AreEqual("new draft", ui.DraftText);
            Assert.AreEqual(("draft", "new draft", "session", "project"), (seen[^1].Kind, seen[^1].Text, seen[^1].SessionId, seen[^1].ProjectId));

            var steer = ui.TrySteerAsync("go on").AsTask();
            Assert.AreEqual(("prompt", "steer", "go on", "session"), (seen[^1].Kind, seen[^1].Mode, seen[^1].Text, seen[^1].SessionId));
            ui.Respond(Answer(seen[^1], button: "ok"));
            Assert.IsTrue(await steer);

            var refused = ui.RequestCompactionAsync().AsTask();
            Assert.AreEqual("compact", seen[^1].Mode);
            ui.Respond(Answer(seen[^1], cancelled: true));
            Assert.IsFalse(await refused);
        });

        // The pane belongs to the flow that entered it.
        Assert.IsNull(ui.SelectedSessionId);
    }

    [TestMethod]
    public void Cut_NeverSplitsASurrogatePair()
    {
        Assert.AreEqual("ab", DesktopPluginUi.Cut("abc", 2));
        Assert.AreEqual("a", DesktopPluginUi.Cut("a\U0001F600b", 2));
        Assert.AreEqual("abc", DesktopPluginUi.Cut("abc", 8));
    }

    private static (string Status, string? Html, bool Closed) Result(string status, string? html, bool closed) => (status, html, closed);

    private static PluginUiAnswer Answer(PluginUiEvent request, string? button = null, bool cancelled = false, string? text = null, int? selectedIndex = null,
        Dictionary<string, string>? values = null)
        => new(request.RequestId, button, cancelled, text, selectedIndex, values);
}
