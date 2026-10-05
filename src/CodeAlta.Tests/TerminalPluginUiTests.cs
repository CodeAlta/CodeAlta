using System.Reflection;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using CodeAlta.Tui.Plugins;
using CodeAlta.Tui.Threading;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.Backends;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;
using XenoAtom.Terminal.UI.Hosting;

namespace CodeAlta.Tests;

/// <summary>The dialogs, the selected session and the prompt that the terminal application gives its plugins.</summary>
[TestClass]
public sealed class TerminalPluginUiTests
{
    [TestMethod]
    public async Task WithoutAWindow_NothingIsShownAndQuestionsHaveNoAnswer()
    {
        var ui = new TerminalPluginUi();

        Assert.IsFalse(ui.HasInteractiveUi);
        await ui.NotifyAsync("message");
        Assert.IsFalse(await ui.ConfirmAsync("Title", "Continue?"));
        Assert.IsNull(await ui.InputAsync("Title", "text"));
        Assert.IsNull(await ui.EditTextAsync("Title", "text"));
        Assert.IsNull(await ui.SelectAsync("Title", [new PluginSelectItem<string> { Label = "a", Value = "a" }]));
        Assert.IsNull(await ui.ShowDialogForResultAsync(PluginUi.NotifyDialog("Title", "Message")));
        Assert.IsNull(ui.SelectedSessionId);
        Assert.IsFalse(ui.IsSelectedSessionBusy);
        Assert.IsNull(ui.DraftText);
        await ui.SendPromptAsync("prompt");
        await ui.EnqueuePromptAsync("prompt");
        await ui.SetDraftTextAsync("draft");
        Assert.IsFalse(await ui.TrySteerAsync("prompt"));
        Assert.IsFalse(await ui.RequestCompactionAsync());
    }

    [TestMethod]
    public void CreateModel_ShowsTheNativeFormOfARequest()
    {
        var input = TerminalPluginUi.CreateModel(PluginUi.InputDialog("Name", "Ada"))!;
        Assert.AreEqual((PluginDialogKind.Input, "Name", "Ada"), (input.Kind, input.Title, input.Text));
        CollectionAssert.AreEqual(new[] { "cancel", "ok" }, input.Buttons.Select(static button => button.Name).ToArray());

        var selection = TerminalPluginUi.CreateModel(PluginUi.SelectionDialog("Pick", ["one", "two"]))!;
        CollectionAssert.AreEqual(new[] { "one", "two" }, selection.Choices.Select(static choice => choice.Label).ToArray());

        var confirm = TerminalPluginUi.CreateModel(PluginUi.ConfirmDialog("Sure", "Continue?"))!;
        Assert.AreEqual(PluginDialogKind.Confirmation, confirm.Kind);
        Assert.IsTrue(confirm.Buttons.Count >= 2);

        // A custom dialog shows the plugin's own controls and keeps its buttons.
        var content = new TextBlock("native");
        var save = new PluginDialogButton { Name = "save", Label = "Save", IsDefault = true };
        var custom = TerminalPluginUi.CreateModel(PluginTui.CustomDialog("Form", content) with { Html = "<b>html</b>", Buttons = [save] })!;
        Assert.AreEqual(PluginDialogKind.Custom, custom.Kind);
        Assert.AreSame(content, custom.Content);
        Assert.AreSame(save, custom.Buttons.Single());

        // A fragment is for the desktop application: here its message is shown, and nothing without one.
        var message = TerminalPluginUi.CreateModel(PluginUi.HtmlDialog("Form", "<b>html</b>") with { Message = "Plain text" })!;
        Assert.AreEqual((PluginDialogKind.Notification, "Plain text"), (message.Kind, message.Message));
        Assert.AreEqual("close", message.Buttons.Single().Name);
        Assert.IsNull(TerminalPluginUi.CreateModel(PluginUi.HtmlDialog("Form", "<b>html</b>")));
    }

    [TestMethod]
    public void Dialog_AnswersOnceWithWhatTheUserEntered()
    {
        using var terminal = new TerminalFixture();
        var answers = new List<PluginDialogResponse>();
        PluginRequestDialog Open(PluginRequestDialogModel model)
        {
            var dialog = new PluginRequestDialog(model, () => new Rectangle(0, 0, 120, 40), () => terminal.Root, answers.Add);
            dialog.Show();
            terminal.Tick();
            Assert.IsTrue(dialog.IsOpen);
            return dialog;
        }

        var input = Open(TerminalPluginUi.CreateModel(PluginUi.InputDialog("Name", "Ada"))!);
        input.Accept();
        input.Cancel();
        terminal.Tick();
        Assert.IsFalse(input.IsOpen);
        Assert.AreEqual(("ok", false, "Ada"), (answers.Single().ButtonName, answers[0].Cancelled, answers[0].Text));

        var edit = Open(TerminalPluginUi.CreateModel(PluginUi.TextEditorDialog("Note", "line 1\nline 2"))!);
        edit.Accept();
        Assert.AreEqual("line 1\nline 2", answers[^1].Text!.ReplaceLineEndings("\n"));

        var selection = Open(new PluginRequestDialogModel
        {
            Kind = PluginDialogKind.Selection, Title = "Pick", SelectedIndex = 1,
            Choices = [new("one", null), new("two", "second")],
            Buttons = [new() { Name = "cancel", Label = "Cancel", IsCancel = true }, new() { Name = "ok", Label = "OK", IsDefault = true }],
        });
        selection.Accept();
        Assert.AreEqual(1, answers[^1].SelectedIndex);

        var cancelled = Open(TerminalPluginUi.CreateModel(PluginUi.SelectionDialog("Pick", ["one"]))!);
        cancelled.Cancel();
        Assert.AreEqual((true, (int?)null, (string?)null), (answers[^1].Cancelled, answers[^1].SelectedIndex, answers[^1].ButtonName));

        // A withdrawn request closes without an answer.
        var withdrawn = Open(TerminalPluginUi.CreateModel(PluginUi.NotifyDialog("Title", "Message"))!);
        var count = answers.Count;
        withdrawn.Close();
        withdrawn.Accept();
        terminal.Tick();
        Assert.IsFalse(withdrawn.IsOpen);
        Assert.AreEqual(count, answers.Count);
    }

    [TestMethod]
    public async Task Questions_AreAnsweredFromTheKeyboard()
    {
        using var terminal = new TerminalFixture();
        var host = new Host(terminal.Root);
        var ui = new TerminalPluginUi();
        ui.Attach(host);
        Assert.IsTrue(ui.HasInteractiveUi);

        var refused = ui.ConfirmAsync("Title", "Continue?").AsTask();
        terminal.Press(TerminalKey.Escape);
        Assert.IsFalse(await refused.WaitAsync(TimeSpan.FromSeconds(30)));

        var accepted = ui.ConfirmAsync("Title", "Continue?").AsTask();
        terminal.Press(TerminalKey.Enter);
        Assert.IsTrue(await accepted.WaitAsync(TimeSpan.FromSeconds(30)));

        var name = ui.InputAsync("Name", "Ada").AsTask();
        terminal.Press(TerminalKey.Enter);
        Assert.AreEqual("Ada", await name.WaitAsync(TimeSpan.FromSeconds(30)));

        var items = new[] { new PluginSelectItem<int> { Label = "one", Value = 1 }, new PluginSelectItem<int> { Label = "two", Value = 2, IsSelected = true } };
        var picked = ui.SelectAsync("Pick", items).AsTask();
        terminal.Press(TerminalKey.Enter);
        Assert.AreEqual(2, await picked.WaitAsync(TimeSpan.FromSeconds(30)));

        var dismissed = ui.SelectAsync("Pick", items).AsTask();
        terminal.Press(TerminalKey.Escape);
        Assert.AreEqual(0, await dismissed.WaitAsync(TimeSpan.FromSeconds(30)));

        using var cancellation = new CancellationTokenSource();
        var withdrawn = ui.EditTextAsync("Note", "text", cancellation.Token).AsTask();
        terminal.Tick();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => withdrawn.WaitAsync(TimeSpan.FromSeconds(30)));

        ui.Detach();
        Assert.IsFalse(ui.HasInteractiveUi);
        Assert.IsFalse(await ui.ConfirmAsync("Title", "Continue?"));
    }

    [TestMethod]
    public async Task ThePromptAndTheSelectedSession_AreThoseOfTheWindow()
    {
        using var terminal = new TerminalFixture();
        var host = new Host(terminal.Root) { PromptText = "typed" };
        var ui = new TerminalPluginUi();
        ui.Attach(host);

        await ui.NotifyAsync("hello");
        CollectionAssert.AreEqual(new[] { "hello" }, host.Messages);
        Assert.AreEqual("typed", ui.DraftText);
        await ui.SetDraftTextAsync("replaced");
        Assert.AreEqual("replaced", host.PromptText);

        // Without a session a queued prompt starts one, and there is nothing to steer or compact.
        Assert.IsNull(ui.SelectedSessionId);
        await ui.EnqueuePromptAsync("first");
        Assert.IsFalse(await ui.TrySteerAsync("steer"));
        Assert.IsFalse(await ui.RequestCompactionAsync());
        CollectionAssert.AreEqual(new[] { "send:first" }, host.Prompts);

        host.SelectedSessionId = "session";
        host.IsSelectedSessionBusy = true;
        Assert.AreEqual("session", ui.SelectedSessionId);
        Assert.IsTrue(ui.IsSelectedSessionBusy);
        await ui.EnqueuePromptAsync("second");
        Assert.IsTrue(await ui.TrySteerAsync("third"));
        await ui.SendPromptAsync("fourth");
        Assert.IsTrue(await ui.RequestCompactionAsync());
        CollectionAssert.AreEqual(new[] { "send:first", "queue:second", "steer:third", "send:fourth", "compact" }, host.Prompts);

        host.IsSelectedSessionBusy = false;
        Assert.IsFalse(await ui.TrySteerAsync("idle"));
    }

    [TestMethod]
    public void PickerToken_IsTheTriggerAtAWordStartAndTheTextTypedAfterIt()
    {
        Assert.AreEqual(new PromptPickerToken(0, 3, "bu"), PromptPickerToken.Find('!', "!bu", 3));
        Assert.AreEqual(new PromptPickerToken(5, 11, "bu"), PromptPickerToken.Find('!', "see (!bug-1, then", 8));
        Assert.AreEqual(new PromptPickerToken(4, 5, ""), PromptPickerToken.Find('!', "see !", 5));
        Assert.IsNull(PromptPickerToken.Find('!', "wow!bu", 6), "inside a word");
        Assert.IsNull(PromptPickerToken.Find('!', "!a b", 4), "the token ended");
        Assert.IsNull(PromptPickerToken.Find('!', "!!", 1), "a trigger follows the caret");
        Assert.IsNull(PromptPickerToken.Find('!', "nothing", 7));
        Assert.IsNull(PromptPickerToken.Find('!', "!x", 0));
        Assert.IsNull(PromptPickerToken.Find('!', "!x", 9));
        Assert.AreEqual(("see BUG-12  now", 11), new PromptPickerToken(4, 7, "bu").Replace("see !bu now", "BUG-12 "));
    }

    [TestMethod]
    public async Task Picker_OpensOnItsTrigger_SearchesThePlugin_AndInsertsTheChosenItem()
    {
        using var terminal = new TerminalFixture();
        var editor = new PromptEditor(terminal.Root);
        var queries = new List<string>();
        var picker = PluginUi.PromptPicker("fruit", '!', "Fruits", static (_, _) => ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>([]));
        string[] fruits = ["banana", "blueberry", "cherry"];
        await using var attachment = new TerminalPromptPickerAttachment(editor, picker, (query, _) =>
        {
            queries.Add(query);
            return Task.FromResult<IReadOnlyList<PluginPromptPickerItem>?>(query == "fail" ? null
                : [.. fruits.Where(fruit => fruit.StartsWith(query, StringComparison.Ordinal)).Select(static fruit => new PluginPromptPickerItem { Label = fruit, InsertText = $"fruit:{fruit} " })]);
        });

        editor.Type("see ", 4);
        Assert.IsFalse(attachment.IsOpen);

        editor.Type("see !b", 6);
        terminal.Tick();
        Assert.IsTrue(attachment.IsOpen);
        CollectionAssert.AreEqual(new[] { "b" }, queries);
        CollectionAssert.AreEqual(new[] { "banana", "blueberry" }, attachment.Dialog!.Items.Select(static item => item.Label).ToArray());
        Assert.AreEqual(0, attachment.Dialog.SelectedIndex);

        // Arrow down then Enter, typed in the query of the dialog.
        terminal.Press(TerminalKey.Down);
        Assert.AreEqual(1, attachment.Dialog.SelectedIndex);
        terminal.Press(TerminalKey.Enter);
        Assert.IsFalse(attachment.IsOpen);
        Assert.AreEqual(("see fruit:blueberry ", 20, 1), (editor.Text, editor.CaretIndex, editor.Focused));

        // Escape leaves the prompt as typed, and the picker stays closed until the prompt changes.
        editor.Type("!", 1);
        terminal.Tick();
        Assert.IsTrue(attachment.IsOpen);
        Assert.AreEqual(3, attachment.Dialog!.Items.Count);
        terminal.Press(TerminalKey.Escape);
        Assert.IsFalse(attachment.IsOpen);
        Assert.AreEqual("!", editor.Text);
        editor.Type("!", 1);
        Assert.IsFalse(attachment.IsOpen);

        editor.Type("!fail", 5);
        terminal.Tick();
        Assert.IsTrue(attachment.IsOpen);
        Assert.AreEqual(0, attachment.Dialog!.Items.Count);
        Assert.AreNotEqual(string.Empty, attachment.Dialog.Status);
        Assert.IsFalse(attachment.Dialog.AcceptSelected());
    }

    private sealed class PromptEditor(Visual visual) : IPluginTerminalPromptEditorHost
    {
        public event EventHandler? EditorStateChanged;
#pragma warning disable CS0067 // The picker does not use it.
        public event EventHandler? Accepted;
#pragma warning restore CS0067
        public Visual Visual { get; } = visual;
        public string? ProjectPath => null;
        public string? Text { get; set; }
        public int CaretIndex { get; set; }
        public int Focused { get; private set; }
        public void FocusPromptEditor() => Focused++;

        public void Type(string text, int caret)
        {
            Text = text;
            CaretIndex = caret;
            EditorStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Host(Visual root) : ITerminalPluginUiHost
    {
        public List<string> Messages { get; } = [];
        public List<string> Prompts { get; } = [];
        public IUiDispatcher Dispatcher { get; } = new InlineDispatcher();
        public Rectangle? GetDialogBounds() => new Rectangle(0, 0, 120, 40);
        public Visual? GetFocusTarget() => root;
        public void ShowMessage(string message, bool warning) => Messages.Add(message);
        public string? SelectedSessionId { get; set; }
        public bool IsSelectedSessionBusy { get; set; }
        public string? PromptText { get; set; }
        public Task SendPromptAsync(string text, bool steer) { Prompts.Add((steer ? "steer:" : "send:") + text); return Task.CompletedTask; }
        public bool EnqueuePrompt(string text) { if (SelectedSessionId is null) return false; Prompts.Add("queue:" + text); return true; }
        public Task<bool> CompactAsync() { if (SelectedSessionId is null) return Task.FromResult(false); Prompts.Add("compact"); return Task.FromResult(true); }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    }

    // A fullscreen application over an in-memory terminal, ticked by hand.
    private sealed class TerminalFixture : IDisposable
    {
        private readonly TerminalSession _session;
        private readonly TerminalApp _app;

        public TerminalFixture()
        {
            _session = Terminal.Open(new InMemoryTerminalBackend(new TerminalSize(120, 40)), new TerminalOptions { ImplicitStartInput = true }, force: true);
            Root = new Button("Root");
            _app = new TerminalApp(Root, _session.Instance, new TerminalAppOptions { HostKind = TerminalHostKind.Fullscreen });
            Invoke("BeginRun");
            _app.Focus(Root);
        }

        public Button Root { get; }

        public void Tick() => typeof(TerminalApp).GetMethod("Tick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_app, [null]);

        public void Press(TerminalKey key)
        {
            Tick();
            ((InMemoryTerminalBackend)_session.Instance.Backend).PushEvent(new TerminalKeyEvent { Key = key });
            Tick();
        }

        public void Dispose()
        {
            Invoke("EndRun");
            _session.Dispose();
        }

        private void Invoke(string method) => typeof(TerminalApp).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_app, null);
    }
}
