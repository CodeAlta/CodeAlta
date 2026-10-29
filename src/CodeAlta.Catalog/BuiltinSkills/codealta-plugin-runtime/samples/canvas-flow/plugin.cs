using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
// XenoAtom.CommandLine has a Command too: these two lines keep Command.Shell and name the other one.
using Command = CodeAlta.Plugins.Abstractions.Command;
using AltaCommand = XenoAtom.CommandLine.Command;

// A graph drawn by a script of the package folder: ui/flow.js draws the steps of a release pipeline with React Flow (`@xyflow/react`, the one
// the window runs), in the colors of the window. The pipeline lives here, in the plugin: the script follows it with a stream, and changes it
// with calls when a step is dragged, linked, started or finished. `alta flow status --step tests --status done` changes it from outside.
[Plugin("canvas-flow", DisplayName = "Canvas flow", Description = "A release pipeline drawn with React Flow, whose steps and links live in the plugin.")]
public sealed class CanvasFlowPlugin : PluginBase
{
    private readonly FlowState _state = new();
    private readonly ConcurrentDictionary<string, PluginCanvasContext> _open = new(StringComparer.Ordinal);

    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "flow", Title = "Pipeline", Description = "The steps of a release pipeline and their links, as a graph that pans, zooms and drags.", Icon = "workflow",
            Open = (canvas, _) =>
            {
                _open[canvas.InstanceId] = canvas;

                // Each call answers with the pipeline after the change; the stream shows it to every open tab as well.
                canvas.Rpc.Handle<GetFlow, FlowView>("flow.get", (_, _) => ValueTask.FromResult(_state.Read()));
                canvas.Rpc.Stream<WatchFlow, FlowView>("flow.watch", (_, cancellationToken) => _state.WatchAsync(cancellationToken));
                canvas.Rpc.Handle<MoveStep, FlowView>("flow.move", (request, _) => ValueTask.FromResult(_state.Move(request)));
                canvas.Rpc.Handle<SetStatus, FlowView>("flow.status", (request, _) => ValueTask.FromResult(_state.SetStatus(request.Step, request.Status)));
                canvas.Rpc.Handle<Connect, FlowView>("flow.connect", (request, _) => ValueTask.FromResult(_state.Connect(request)));
                canvas.Rpc.Handle<Disconnect, FlowView>("flow.disconnect", (request, _) => ValueTask.FromResult(_state.Disconnect(request)));
                canvas.Rpc.Handle<AddStep, FlowView>("flow.add", (request, _) => ValueTask.FromResult(_state.Add(request)));
                canvas.Rpc.Handle<ResetFlow, FlowView>("flow.reset", (_, _) => ValueTask.FromResult(_state.Reset()));

                var view = PluginCanvasView.Html("<p class=\"alta-muted\">Loading the pipeline…</p>") with { Script = PluginScript.File("ui/flow.js"), Title = "Pipeline" };
                return ValueTask.FromResult(view);
            },
            Closed = canvas =>
            {
                _open.TryRemove(canvas.InstanceId, out _);
                return ValueTask.CompletedTask;
            },
            // What an agent reads with `alta canvas read`: the steps and where each one is.
            Describe = (_, _) => ValueTask.FromResult<string?>(_state.Describe()),
        };
    }

    // `alta flow open` asks the window for the tab; `alta flow status --step <id> --status <status>` moves a step on, and the tabs show it.
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution { Path = "flow", Description = "Opens the pipeline canvas and changes the status of its steps.", CreateCommandNode = CreateFlow };
    }

    private AltaCommand CreateFlow(PluginAltaCommandContext context)
    {
        var open = new AltaCommand("open", "Opens the pipeline in a tab of the window.") { new CommandUsage(), new HelpOption() };
        open.Add(async (_, _) =>
        {
            var result = await Services.Canvases.OpenAsync("flow", cancellationToken: CancellationToken.None);
            context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.flow.open", status = result.Status.ToString(), instance = result.InstanceId, shown = result.Shown }));
            return result.Requested ? 0 : 1;
        });

        var step = "";
        var status = "";
        var set = new AltaCommand("status", "Sets the status of a step: waiting, running, done or failed.") { new CommandUsage(), new HelpOption() };
        set.Add("step=", "The id of the step, such as `tests`.", value => step = value ?? "");
        set.Add("status=", "waiting, running, done or failed.", value => status = value ?? "");
        set.Add((_, _) =>
        {
            try
            {
                var view = _state.SetStatus(step, status);
                context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.flow.status", revision = view.Revision, step, status }));
                return ValueTask.FromResult(0);
            }
            catch (PluginRpcException error)
            {
                context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.flow.error", code = error.Code, message = error.Message }));
                return ValueTask.FromResult(1);
            }
        });

        var root = new AltaCommand("flow", "Pipeline commands.") { new CommandUsage(), new HelpOption() };
        root.Add(open);
        root.Add(set);
        return root;
    }

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("flow-open", "Opens the pipeline canvas.", async (_, cancellationToken) =>
        {
            await Services.Canvases.OpenAsync("flow", cancellationToken: cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Pipeline: open" };
    }

    public sealed record GetFlow;

    public sealed record WatchFlow;

    public sealed record ResetFlow;

    public sealed record MoveStep(string Id, double X, double Y);

    public sealed record SetStatus(string Step, string Status);

    public sealed record Connect(string From, string To);

    public sealed record Disconnect(string Id);

    public sealed record AddStep(string Title, string Kind);

    /// <summary>A step: <c>Kind</c> is source, build, test or publish; <c>Status</c> is waiting, running, done or failed.</summary>
    public sealed record Step(string Id, string Title, string Detail, string Kind, string Status, double X, double Y);

    public sealed record Link(string Id, string From, string To);

    public sealed record FlowView(string Title, int Revision, IReadOnlyList<Step> Steps, IReadOnlyList<Link> Links);

    // The pipeline, and the streams that follow it.
    private sealed class FlowState
    {
        private static readonly string[] Kinds = ["source", "build", "test", "publish"];
        private static readonly string[] Statuses = ["waiting", "running", "done", "failed"];

        private readonly Lock _gate = new();
        private readonly List<Channel<FlowView>> _watchers = [];
        private List<Step> _steps = [];
        private List<Link> _links = [];
        private int _next;
        private int _revision;

        public FlowState() => Fill();

        public FlowView Read()
        {
            lock (_gate) return Snapshot();
        }

        public FlowView Move(MoveStep request)
        {
            lock (_gate)
            {
                var index = IndexOf(request.Id);
                _steps[index] = _steps[index] with { X = Math.Round(request.X), Y = Math.Round(request.Y) };
                return Publish();
            }
        }

        public FlowView SetStatus(string id, string status)
        {
            var value = status?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!Statuses.Contains(value)) throw new PluginRpcException("unknown_status", "A status is waiting, running, done or failed.");
            lock (_gate)
            {
                var index = IndexOf(id);
                _steps[index] = _steps[index] with { Status = value };
                return Publish();
            }
        }

        public FlowView Connect(Connect request)
        {
            lock (_gate)
            {
                IndexOf(request.From);
                IndexOf(request.To);
                if (request.From == request.To) throw new PluginRpcException("invalid_link", "A step does not lead to itself.");
                if (!_links.Any(link => link.From == request.From && link.To == request.To)) _links.Add(new Link($"{request.From}-{request.To}", request.From, request.To));
                return Publish();
            }
        }

        public FlowView Disconnect(Disconnect request)
        {
            lock (_gate)
            {
                _links.RemoveAll(link => link.Id == request.Id);
                return Publish();
            }
        }

        public FlowView Add(AddStep request)
        {
            var title = request.Title?.Trim() ?? string.Empty;
            if (title.Length is 0 or > 40) throw new PluginRpcException("invalid_step", "A step needs a title of 1 to 40 characters.");
            var kind = Kinds.Contains(request.Kind) ? request.Kind : "build";
            lock (_gate)
            {
                // Below the lowest step, so that it is seen at once and dragged where it belongs.
                var y = _steps.Count == 0 ? 0 : _steps.Max(step => step.Y) + 120;
                _steps.Add(new Step($"step-{++_next}", title, "Added from the canvas", kind, "waiting", 0, y));
                return Publish();
            }
        }

        public FlowView Reset()
        {
            lock (_gate)
            {
                Fill();
                return Publish();
            }
        }

        public string Describe()
        {
            var view = Read();
            return "# Release pipeline\n\n" + string.Join("\n", view.Steps.Select(step => $"- `{step.Id}` {step.Title}: {step.Status}")) +
                "\n\nChange a step with `alta flow status --step <id> --status waiting|running|done|failed`.";
        }

        // The pipeline now, then the last one after each change: a reader that is slower than the changes sees the latest.
        public async IAsyncEnumerable<FlowView> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var channel = Channel.CreateBounded<FlowView>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            lock (_gate)
            {
                _watchers.Add(channel);
                channel.Writer.TryWrite(Snapshot());
            }

            try
            {
                await foreach (var view in channel.Reader.ReadAllAsync(cancellationToken)) yield return view;
            }
            finally
            {
                lock (_gate) _watchers.Remove(channel);
            }
        }

        // The same pipeline at every start, so the canvas looks the same each time it is opened.
        private void Fill()
        {
            _steps =
            [
                new("commit", "Commit", "main, pushed", "source", "done", 0, 150),
                new("dotnet-build", ".NET build", "dotnet build -c Release", "build", "done", 320, 40),
                new("frontend-build", "Frontend build", "vite build", "build", "done", 320, 260),
                new("dotnet-tests", ".NET tests", "5,037 tests", "test", "running", 640, 0),
                new("frontend-tests", "Frontend tests", "2,402 tests", "test", "done", 640, 150),
                new("website", "Website", "lunet build", "test", "running", 640, 300),
                new("packages", "NuGet packages", "alta · altatui", "publish", "waiting", 960, 75),
                new("release", "GitHub release", "tag 3.1.0", "publish", "waiting", 1280, 150),
            ];
            _links =
            [
                new("commit-dotnet-build", "commit", "dotnet-build"),
                new("commit-frontend-build", "commit", "frontend-build"),
                new("dotnet-build-dotnet-tests", "dotnet-build", "dotnet-tests"),
                new("frontend-build-frontend-tests", "frontend-build", "frontend-tests"),
                new("frontend-build-website", "frontend-build", "website"),
                new("dotnet-tests-packages", "dotnet-tests", "packages"),
                new("frontend-tests-packages", "frontend-tests", "packages"),
                new("packages-release", "packages", "release"),
                new("website-release", "website", "release"),
            ];
        }

        private int IndexOf(string? id)
        {
            var index = _steps.FindIndex(step => step.Id == id);
            return index >= 0 ? index : throw new PluginRpcException("not_found", $"No step has the id '{id}'.");
        }

        private FlowView Publish()
        {
            _revision++;
            var view = Snapshot();
            foreach (var watcher in _watchers) watcher.Writer.TryWrite(view);
            return view;
        }

        private FlowView Snapshot() => new("Release pipeline", _revision, [.. _steps], [.. _links]);
    }
}
