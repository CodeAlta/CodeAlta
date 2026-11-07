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
    public async Task WithSeveralWindows_APromptGoesToTheWindowOfItsSession_AndADialogToTheWindowOfItsCommand()
    {
        using var ui = new DesktopPluginUi();
        var focused = "main";
        ui.SessionWindow = session => session switch { "s1" => "main", "s2" => "window-a1", _ => null };
        ui.FocusedWindow = () => focused;
        var main = new List<PluginUiEvent>();
        var second = new List<PluginUiEvent>();
        using var mainPage = ui.Watch("main", main.Add);
        // The second window watches last: before, every request went to its page.
        using var secondPage = ui.Watch("window-a1", second.Add);

        var toMain = ui.SendPromptToSessionAsync("s1", "for the main window").AsTask();
        Assert.AreEqual(("prompt", "s1"), (main.Single().Kind, main.Single().SessionId));
        Assert.AreEqual(0, second.Count);
        Assert.IsTrue(ui.Respond(Answer(main[^1], button: "ok")));
        Assert.IsTrue(await toMain);

        var toSecond = ui.SendPromptToSessionAsync("s2", "for the second window").AsTask();
        Assert.AreEqual(("prompt", "s2"), (second.Single().Kind, second.Single().SessionId));
        Assert.AreEqual(1, main.Count);
        Assert.IsTrue(ui.Respond(Answer(second[^1], button: "ok")));
        Assert.IsTrue(await toSecond);

        await Task.Run(async () =>
        {
            // A command started in the second window, on the session of the main one: its dialog and its notification go
            // where the user acted, its prompt to the composer of its session.
            ui.Enter(new DesktopPluginScope { SessionId = "s1", WindowLabel = "window-a1" });
            var confirm = ui.ConfirmAsync("Title", "Continue?").AsTask();
            Assert.AreEqual("ask", second[^1].Kind);
            ui.Respond(Answer(second[^1], button: "yes"));
            Assert.IsTrue(await confirm);
            await ui.NotifyAsync("done");
            Assert.AreEqual("done", second[^1].Message);
            var send = ui.SendPromptAsync("go").AsTask();
            Assert.AreEqual(("prompt", "s1"), (main[^1].Kind, main[^1].SessionId));
            ui.Respond(Answer(main[^1], button: "ok"));
            await send;
        });
        Assert.AreEqual(2, main.Count);
        Assert.AreEqual(3, second.Count);

        // Without a command or a session, the window used last.
        focused = "window-a1";
        await ui.NotifyAsync("background");
        Assert.AreEqual("background", second[^1].Message);
        Assert.AreEqual(2, main.Count);

        // What changes for every page goes to each.
        ui.InvalidateButtons();
        ui.Refresh();
        Assert.AreEqual("refresh", main[^1].Kind);
        Assert.AreEqual("refresh", second[^1].Kind);
        Assert.AreEqual("buttons", main[^2].Kind);
        Assert.AreEqual("buttons", second[^2].Kind);
    }

    [TestMethod]
    public async Task ADialogOfAWindowThatCloses_IsShownByTheMainWindow()
    {
        using var ui = new DesktopPluginUi();
        var open = true;
        ui.FocusedWindow = () => "window-a1";
        ui.WindowOpen = _ => open;
        var main = new List<PluginUiEvent>();
        var second = new List<PluginUiEvent>();
        using var mainPage = ui.Watch("main", main.Add);
        var secondPage = ui.Watch("window-a1", second.Add);

        var confirm = ui.ConfirmAsync("Title", "Continue?").AsTask();
        Assert.AreEqual("ask", second.Single().Kind);
        Assert.AreEqual(0, main.Count);
        open = false;
        secondPage.Dispose();
        Assert.AreEqual(second[0].RequestId, main.Single().RequestId);
        Assert.IsTrue(ui.Respond(Answer(main[0], button: "yes")));
        Assert.IsTrue(await confirm);

        // The window used last is closed: its request goes to the main window rather than nowhere.
        await ui.NotifyAsync("to a window that closed");
        Assert.AreEqual("to a window that closed", main[^1].Message);
        Assert.AreEqual(1, second.Count);
    }

    [TestMethod]
    public async Task APageThatReloads_KeepsTheRequestsOfItsWindow_UntilItsWindowCloses()
    {
        using var ui = new DesktopPluginUi();
        var open = true;
        ui.SessionWindow = session => session == "s2" ? "window-a1" : null;
        ui.FocusedWindow = () => "window-a1";
        ui.WindowOpen = _ => open;
        var main = new List<PluginUiEvent>();
        using var mainPage = ui.Watch("main", main.Add);

        // The page of the second window reloads: its dialog and the prompt for its session wait for it, not in the main window.
        var first = new List<PluginUiEvent>();
        var firstPage = ui.Watch("window-a1", first.Add);
        var confirm = ui.ConfirmAsync("Title", "Continue?").AsTask();
        firstPage.Dispose();
        var prompt = ui.SendPromptToSessionAsync("s2", "for the second window").AsTask();
        var reloaded = new List<PluginUiEvent>();
        var reloadedPage = ui.Watch("window-a1", reloaded.Add);
        Assert.AreEqual(0, main.Count);
        Assert.AreEqual(first.Single().RequestId, reloaded[0].RequestId);
        Assert.AreEqual(("prompt", "s2"), (reloaded[1].Kind, reloaded[1].SessionId));
        Assert.IsTrue(ui.Respond(Answer(reloaded[1], button: "ok")));
        Assert.IsTrue(await prompt);

        // Its page goes and then the window closes: the dialog goes to the main window.
        reloadedPage.Dispose();
        Assert.AreEqual(0, main.Count);
        open = false;
        ui.WindowsChanged();
        Assert.AreEqual(reloaded[0].RequestId, main.Single().RequestId);
        Assert.IsTrue(ui.Respond(Answer(main[0], button: "yes")));
        Assert.IsTrue(await confirm);
    }

    [TestMethod]
    public async Task WhatIsAskedBeforeThePagesWatch_GoesToThePageOfItsWindow()
    {
        using var ui = new DesktopPluginUi();
        ui.SessionWindow = session => session == "s2" ? "window-a1" : null;
        await Task.Run(async () =>
        {
            ui.Enter(new DesktopPluginScope { SessionId = "s2" });
            await ui.SetDraftTextAsync("for the second window");
        });
        await ui.NotifyAsync("for the main window");

        var second = new List<PluginUiEvent>();
        using var secondPage = ui.Watch("window-a1", second.Add);
        var main = new List<PluginUiEvent>();
        using var mainPage = ui.Watch("main", main.Add);
        Assert.AreEqual(("draft", "for the second window"), (second.Single().Kind, second.Single().Text));
        Assert.AreEqual("for the main window", main.Single().Message);
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
