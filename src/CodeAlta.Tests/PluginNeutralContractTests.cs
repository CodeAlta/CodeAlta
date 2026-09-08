using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

/// <summary>Inert contract tests; no concrete terminal controls, runtime services or native initialization.</summary>
[TestClass]
public sealed class PluginNeutralContractTests
{
    [TestMethod]
    public void NeutralDialogs_PreserveDataAndFactoryDefaults()
    {
        var request = new PluginDialogRequest { Title = "title" };
        Assert.AreEqual(PluginDialogKind.Custom, request.Kind);
        Assert.IsNull(request.Message);
        Assert.IsNull(request.InitialText);
        Assert.AreEqual(0, request.SelectionItems.Count);
        Assert.AreEqual(0, request.Buttons.Count);
        Assert.AreEqual(0, request.Metadata.Count);
        var notification = PluginUi.NotifyDialog("title", "message");
        Assert.AreEqual(PluginDialogKind.Notification, notification.Kind);
        Assert.AreEqual("message", notification.Message);
        var confirmation = PluginUi.ConfirmDialog("title", "message");
        Assert.AreEqual(PluginDialogKind.Confirmation, confirmation.Kind);
        Assert.AreEqual(2, confirmation.Buttons.Count);
        Assert.AreEqual(new PluginDialogButton { Name = "yes", Label = "Yes", IsDefault = true }, confirmation.Buttons[0]);
        Assert.AreEqual(new PluginDialogButton { Name = "no", Label = "No", IsCancel = true }, confirmation.Buttons[1]);
        var input = PluginUi.InputDialog("title");
        Assert.AreEqual(PluginDialogKind.Input, input.Kind);
        Assert.IsNull(input.InitialText);
        Assert.AreEqual("initial", PluginUi.InputDialog("title", "initial").InitialText);
        var editor = PluginUi.TextEditorDialog("title", "text");
        Assert.AreEqual(PluginDialogKind.TextEditor, editor.Kind);
        Assert.AreEqual("text", editor.InitialText);
        string[] items = ["first", "second"];
        var selection = PluginUi.SelectionDialog("title", items);
        Assert.AreEqual(PluginDialogKind.Selection, selection.Kind);
        Assert.AreSame(items, selection.SelectionItems);
        IReadOnlyDictionary<string, string> metadata = new Dictionary<string, string> { ["key"] = "value" };
        var detailed = request with { Message = "message", InitialText = "initial", SelectionItems = items, Buttons = confirmation.Buttons, Metadata = metadata };
        Assert.AreSame(metadata, detailed.Metadata);
        Assert.AreSame(confirmation.Buttons, detailed.Buttons);
        Assert.AreSame(items, detailed.SelectionItems);
        Assert.AreEqual("title", detailed.Title);
        Assert.AreEqual("message", detailed.Message);
        Assert.AreEqual("initial", detailed.InitialText);
        var response = new PluginDialogResponse { ButtonName = "yes", Cancelled = true, Text = "text", SelectedIndex = 1, Metadata = metadata };
        Assert.AreEqual("yes", response.ButtonName);
        Assert.IsTrue(response.Cancelled);
        Assert.AreEqual("text", response.Text);
        Assert.AreEqual(1, response.SelectedIndex);
        Assert.AreSame(metadata, response.Metadata);
    }

    [TestMethod]
    public void NeutralDialogs_PreserveValidationOrder()
    {
        Assert.AreEqual("title", Assert.ThrowsExactly<ArgumentException>(() => PluginUi.NotifyDialog(" ", "message")).ParamName);
        Assert.AreEqual("title", Assert.ThrowsExactly<ArgumentException>(() => PluginUi.ConfirmDialog(" ", "message")).ParamName);
        Assert.AreEqual("title", Assert.ThrowsExactly<ArgumentException>(() => PluginUi.InputDialog(" ")).ParamName);
        Assert.AreEqual("text", Assert.ThrowsExactly<ArgumentNullException>(() => PluginUi.TextEditorDialog(" ", null!)).ParamName);
        Assert.AreEqual("title", Assert.ThrowsExactly<ArgumentException>(() => PluginUi.TextEditorDialog(" ", "text")).ParamName);
        Assert.AreEqual("items", Assert.ThrowsExactly<ArgumentNullException>(() => PluginUi.SelectionDialog(" ", null!)).ParamName);
        // Selection, unlike the common Dialog helper, never validated its title.
        Assert.AreEqual(" ", PluginUi.SelectionDialog(" ", ["item"]).Title);
    }

    [TestMethod]
    public void TerminalDialogFactory_RejectsNullContent()
    {
        Assert.AreEqual("content", Assert.ThrowsExactly<ArgumentNullException>(() => PluginTui.CustomDialog("title", null!)).ParamName);
        Assert.AreEqual("content", Assert.ThrowsExactly<ArgumentNullException>(() => PluginTui.CustomDialog(null!, null!)).ParamName);
        PluginDialogRequest request = new PluginTerminalDialogRequest { Title = "title", Message = "message" };
        Assert.AreEqual(PluginDialogKind.Custom, request.Kind);
        Assert.AreEqual("message", request.Message);
        Assert.IsNull(((PluginTerminalDialogRequest)request).Content);
    }

    [TestMethod]
    public void PromptEditorFactory_PreservesMetadataAndDefersAttach()
    {
        var calls = 0;
        IAsyncDisposable? Attach(IPluginTerminalPromptEditorHost host) { calls++; return null; }
        var defaults = PluginTui.PromptEditor("name", Attach);
        var explicitValues = PluginTui.PromptEditor("other", Attach, "guidance", -3);
        Assert.AreEqual("name", defaults.Name);
        Assert.IsNull(defaults.PlaceholderText);
        Assert.AreEqual(0, defaults.Order);
        Assert.AreEqual("other", explicitValues.Name);
        Assert.AreEqual("guidance", explicitValues.PlaceholderText);
        Assert.AreEqual(-3, explicitValues.Order);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentNullException>(() => PluginTui.PromptEditor(null!, null!)).ParamName);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => PluginTui.PromptEditor(" ", null!)).ParamName);
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentException>(() => PluginTui.PromptEditor("", Attach)).ParamName);
        Assert.AreEqual("attach", Assert.ThrowsExactly<ArgumentNullException>(() => PluginTui.PromptEditor("name", null!)).ParamName);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void PromptEditorAttach_DeclinesUnsupportedHostWithoutInvocation()
    {
        var contribution = PluginTui.PromptEditor("name", static _ => throw new InvalidOperationException("must not attach"));
        Assert.IsNull(contribution.Attach(new NeutralHost()));
        Assert.AreEqual("host", Assert.ThrowsExactly<ArgumentNullException>(() => contribution.Attach(null!)).ParamName);
    }

    [TestMethod]
    public void PromptEditorAttach_ForwardsExactTerminalHostOnce()
    {
        var host = new TerminalHost();
        var calls = 0;
        var contribution = PluginTui.PromptEditor("name", actual => { Assert.AreSame(host, actual); calls++; return null; });
        Assert.IsNull(contribution.Attach(host));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void PromptEditorAttach_PreservesNullAndExceptionWithoutFallback()
    {
        var host = new TerminalHost();
        var calls = 0;
        var absent = PluginTui.PromptEditor("absent", _ => { calls++; return null; });
        Assert.IsNull(absent.Attach(host));
        Assert.AreEqual(1, calls);
        var error = new InvalidOperationException("attachment failed");
        var failing = PluginTui.PromptEditor("failing", _ => { calls++; throw error; });
        Assert.AreSame(error, Assert.ThrowsExactly<InvalidOperationException>(() => failing.Attach(host)));
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public void PromptEditorAttach_ReturnsAttachmentWithoutOwningDisposal()
    {
        var borrowed = new Attachment();
        var contribution = PluginTui.PromptEditor("name", _ => borrowed);
        var result = contribution.Attach(new TerminalHost());
        Assert.AreSame(borrowed, result);
        Assert.AreEqual(0, borrowed.Disposals);
        Assert.IsNull(contribution.Attach(new NeutralHost()));
        Assert.AreEqual(0, borrowed.Disposals);
        Assert.IsTrue(borrowed.DisposeAsync().IsCompletedSuccessfully);
        Assert.AreEqual(1, borrowed.Disposals);
    }

    [TestMethod]
    public void NoopDialogs_RemainUnsupportedAndDoNotReadContent()
    {
        // Parent-admitted stateless UI leaf only, never the aggregate no-op services.
        var service = new NoopPluginUiService();
        Assert.IsFalse(service.HasInteractiveUi);
        PluginDialogRequest[] requests = [PluginUi.NotifyDialog("title", "message"), new PluginTerminalDialogRequest { Title = "title" }];
        foreach (var request in requests)
        {
            Assert.IsTrue(service.ShowDialogAsync(request).IsCompletedSuccessfully);
            var result = service.ShowDialogForResultAsync(request);
            Assert.IsTrue(result.IsCompletedSuccessfully);
            Assert.IsNull(result.Result);
        }
        // The complete source leaf assertion additionally forbids reading request/native content.
    }

    [TestMethod]
    public void NoopDialogs_PreserveValidationBeforeCancellation()
    {
        var service = new NoopPluginUiService();
        var cancelled = new CancellationToken(canceled: true);
        Assert.AreEqual("request", Assert.ThrowsExactly<ArgumentNullException>(() => service.ShowDialogAsync(null!, cancelled)).ParamName);
        Assert.AreEqual("request", Assert.ThrowsExactly<ArgumentNullException>(() => service.ShowDialogForResultAsync(null!, cancelled)).ParamName);
        var request = new PluginDialogRequest { Title = "title" };
        Assert.AreEqual(cancelled, Assert.ThrowsExactly<OperationCanceledException>(() => service.ShowDialogAsync(request, cancelled)).CancellationToken);
        Assert.AreEqual(cancelled, Assert.ThrowsExactly<OperationCanceledException>(() => service.ShowDialogForResultAsync(request, cancelled)).CancellationToken);
    }

    [TestMethod]
    public void Layout_ResolvesSizesWithoutVisualConstruction()
    {
        Assert.AreEqual(new Size(80, 40), PluginDialogLayout.ResolveResponsiveSize(new Rectangle(0, 0, 100, 50), 40, 20));
        Assert.AreEqual(new Size(60, 18), PluginDialogLayout.ResolveResponsiveSize(null, 60, 18));
        Assert.AreEqual(new Size(3, 3), PluginDialogLayout.ResolveResponsiveSize(new Rectangle(0, 0, 5, 5), 1, 1, 0.5, 0.5));
        Assert.AreEqual(new Size(60, 18), PluginDialogLayout.ResolveResponsiveSize(new Rectangle(0, 0, 1, 1), 60, 18));
        Assert.AreEqual("minWidth", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PluginDialogLayout.ResolveResponsiveSize(null, 0, 0, 0, 0)).ParamName);
        Assert.AreEqual("minHeight", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PluginDialogLayout.ResolveResponsiveSize(null, 1, 0, 0, 0)).ParamName);
        Assert.AreEqual("widthFactor", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PluginDialogLayout.ResolveResponsiveSize(null, 1, 1, 0, 0)).ParamName);
        Assert.AreEqual("heightFactor", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PluginDialogLayout.ResolveResponsiveSize(null, 1, 1, 1, 2)).ParamName);
    }

    private class NeutralHost : IPluginPromptEditorHost
    {
        public event EventHandler? EditorStateChanged { add => throw new InvalidOperationException("event touched"); remove => throw new InvalidOperationException("event touched"); }
        public event EventHandler? Accepted { add => throw new InvalidOperationException("event touched"); remove => throw new InvalidOperationException("event touched"); }
        public string? ProjectPath => throw new InvalidOperationException("project touched");
        public string? Text { get => throw new InvalidOperationException("text touched"); set => throw new InvalidOperationException("text touched"); }
        public int CaretIndex { get => throw new InvalidOperationException("caret touched"); set => throw new InvalidOperationException("caret touched"); }
        public void FocusPromptEditor() => throw new InvalidOperationException("focus touched");
    }

    private sealed class TerminalHost : NeutralHost, IPluginTerminalPromptEditorHost
    {
        public Visual Visual => throw new InvalidOperationException("native anchor must not be touched");
    }

    private sealed class Attachment : IAsyncDisposable
    {
        internal int Disposals { get; private set; }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
