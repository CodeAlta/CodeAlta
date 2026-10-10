using System.Text.Json;

namespace CodeAlta.Plugins.Abstractions.Tests;

[TestClass]
public sealed class PluginCanvasContractTests
{
    [TestMethod]
    public async Task HtmlView_CarriesItsFragmentAndItsActionHandler()
    {
        PluginCanvasActionHandler handler = (_, action, _) => ValueTask.FromResult(PluginCanvasActionResult.Update("<p>" + action.Name + "</p>"));

        var view = PluginCanvasView.Html("<p>hello</p>", handler);

        Assert.AreEqual("<p>hello</p>", view.Fragment);
        Assert.IsNull(view.Renderer);
        Assert.AreSame(handler, view.OnAction);
        Assert.IsNull(view.Title);
        var result = await view.OnAction!(new Context(), new PluginCanvasAction { Name = "tick" }, CancellationToken.None);
        Assert.AreEqual("<p>tick</p>", result.Html);
        Assert.IsFalse(result.Close);
        Assert.IsNull(PluginCanvasView.Html(string.Empty).OnAction);
        Assert.ThrowsExactly<ArgumentNullException>(() => PluginCanvasView.Html(null!));
    }

    [TestMethod]
    public async Task RenderedView_WritesItsFragmentWithTheHandler()
    {
        var view = PluginCanvasView.Rendered((canvas, _) => ValueTask.FromResult("<b>" + canvas.CanvasId + "</b>"));

        Assert.AreEqual(string.Empty, view.Fragment);
        Assert.AreEqual("<b>list</b>", await view.Renderer!(new Context(), CancellationToken.None));
        Assert.ThrowsExactly<ArgumentNullException>(() => PluginCanvasView.Rendered(null!));
    }

    [TestMethod]
    public void ActionResults_SayWhatTheTabDoesNext()
    {
        Assert.IsNull(PluginCanvasActionResult.KeepOpen.Html);
        Assert.IsFalse(PluginCanvasActionResult.KeepOpen.Close);
        Assert.AreEqual("<i>x</i>", PluginCanvasActionResult.Update("<i>x</i>").Html);
        Assert.IsTrue(PluginCanvasActionResult.CloseCanvas().Close);
        Assert.ThrowsExactly<ArgumentNullException>(() => PluginCanvasActionResult.Update(null!));
    }

    [TestMethod]
    public void Contribution_DefaultsToAnApplicationCanvasWithoutActions()
    {
        var canvas = new PluginCanvasContribution { Id = "list", Title = "List", Open = static (_, _) => ValueTask.FromResult(PluginCanvasView.Html("")) };

        Assert.AreEqual(PluginCanvasScope.Application, canvas.Scope);
        Assert.AreEqual(0, canvas.Actions.Count);
        Assert.IsNull(canvas.Describe);
        Assert.IsNull(canvas.Closed);
        Assert.IsNull(canvas.InputSchema);
        Assert.AreEqual(0, canvas.Order);
    }

    [TestMethod]
    public void PluginBase_DeclaresNoCanvasByDefault()
    {
        Assert.AreEqual(0, new Bare().GetCanvases().Count());
        Assert.IsTrue(Enum.IsDefined(PluginPoint.Canvas));
    }

    [TestMethod]
    public async Task HostWithoutAWindow_SaysNothingCanBeShown()
    {
        var service = NoopPluginCanvasService.Instance;

        Assert.IsFalse(service.HasInteractiveUi);
        var result = await service.OpenAsync("list", new PluginCanvasOpenOptions { Key = "k" });
        Assert.AreEqual(PluginCanvasOpenStatus.Unavailable, result.Status);
        Assert.IsFalse(result.Requested);
        Assert.IsNull(result.InstanceId);
        Assert.IsFalse(await service.CloseAsync("instance"));
        await service.InvalidateAsync("list");
        Assert.AreEqual(0, service.GetOpen().Count);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await service.OpenAsync(" "));
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await service.CloseAsync(""));
    }

    [TestMethod]
    public void Services_GiveTheNoopCanvasServiceToAHostThatDoesNotKnowCanvases()
    {
        // A host written before canvases existed implements IPluginServices without this member.
        IPluginServices old = new OldHost();
        var noop = NoopPluginServices.Create();

        Assert.AreSame(NoopPluginCanvasService.Instance, old.Canvases);
        Assert.AreSame(NoopPluginCanvasService.Instance, noop.Canvases);
    }

    [TestMethod]
    public void OpenOptions_FocusTheTabByDefault()
    {
        var options = new PluginCanvasOpenOptions { Input = JsonDocument.Parse("{\"a\":1}").RootElement };

        Assert.IsTrue(options.Focus);
        Assert.AreEqual(1, options.Input!.Value.GetProperty("a").GetInt32());
        Assert.IsNull(options.SpaceId);
    }

    [TestMethod]
    public void AContextWithoutATab_HasARegistryThatRegistersNothing_OfItsOwn()
    {
        var one = new Context();
        var other = new Context();

        var first = one.Rpc;
        Assert.AreSame(first, one.Rpc);
        Assert.AreNotSame(one.Rpc, other.Rpc, "a registry holds its options: each context has its own, and no state is shared");
        one.Rpc.Handle<JsonElement>("board.get", static (_, _) => ValueTask.CompletedTask);
        Assert.ThrowsExactly<ArgumentException>(() => one.Rpc.Handle<JsonElement>("Board", static (_, _) => ValueTask.CompletedTask));
    }

    private sealed class Bare : PluginBase
    {
    }

    private sealed class OldHost : IPluginServices
    {
        private readonly NoopPluginServices _inner = NoopPluginServices.Create();

        public XenoAtom.Logging.Logger Logger => _inner.Logger;

        public IPluginUiService Ui => _inner.Ui;

        public IPluginStateStore State => _inner.State;

        public IPluginWorkspaceService Workspace => _inner.Workspace;

        public IPluginSessionService Sessions => _inner.Sessions;

        public IPluginPromptService Prompts => _inner.Prompts;

        public IPluginAgentService Agents => _inner.Agents;

        public IPluginTaskService Tasks => _inner.Tasks;

        public IPluginAltaService Alta => _inner.Alta;

        public IPluginDatabase Database => _inner.Database;
    }

    private sealed class Context : PluginCanvasContext
    {
        public override string InstanceId => "instance";

        public override string CanvasId => "list";

        public override string? SpaceId => null;

        public override string? ProjectId => null;

        public override string? SessionId => null;

        public override string? Key => null;

        public override JsonElement? Input => null;

        public override bool IsVisible => false;

        public override bool IsOpen => false;

        public override CancellationToken Closed => CancellationToken.None;

        public override event Action<bool>? VisibilityChanged
        {
            add { }
            remove { }
        }

        public override ValueTask SetTitleAsync(string? title, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask SetStatusAsync(string? status, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask UpdateAsync(string html, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public override ValueTask InvalidateAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
