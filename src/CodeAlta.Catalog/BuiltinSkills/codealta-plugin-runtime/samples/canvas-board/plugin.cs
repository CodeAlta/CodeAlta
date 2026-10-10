using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
// XenoAtom.CommandLine has a Command too: these two lines keep Command.Shell and name the other one.
using Command = CodeAlta.Plugins.Abstractions.Command;
using AltaCommand = XenoAtom.CommandLine.Command;

// A board drawn by a script of the package folder: ui/board.js is a React component that the window draws in the tab, with the Blueprint
// components of the application and a chart. The board itself lives here, in the plugin: the script reads it with `alta.rpc` (a call and a
// stream that the plugin pushes to), changes it with calls, and hears about the changes with an event. `alta board add` changes it from outside.
[Plugin("canvas-board", DisplayName = "Canvas board", Description = "A board of cards drawn by a script, whose data lives in the plugin.")]
public sealed class CanvasBoardPlugin : PluginBase
{
    private readonly BoardState _state = new();
    private readonly ConcurrentDictionary<string, PluginCanvasContext> _open = new(StringComparer.Ordinal);

    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "board", Title = "Board", Description = "A board of cards in columns, with a chart of how many each column holds.", Icon = "layers",
            Open = (canvas, _) =>
            {
                _open[canvas.InstanceId] = canvas;

                // The calls the script of this tab makes, registered before the view is returned. A request and a result are ordinary records,
                // written as JSON in camelCase. A PluginRpcException gives the script a code to test; any other exception reaches it as
                // `internal_error`, and its text stays in the log of the plugin.
                canvas.Rpc.Handle<GetBoard, BoardView>("board.get", (_, _) => ValueTask.FromResult(_state.Read()));
                canvas.Rpc.Handle<AddCard, BoardView>("board.add", async (request, _) =>
                {
                    var (view, message) = _state.Add(request);
                    await ChangedAsync(view, message);
                    return view;
                });
                canvas.Rpc.Handle<MoveCard, BoardView>("board.move", async (request, _) =>
                {
                    var (view, message) = _state.Move(request);
                    await ChangedAsync(view, message);
                    return view;
                });
                canvas.Rpc.Handle<GetBoard>("board.explode", static (_, _) => throw new InvalidOperationException("Not shown to the script."));
                // A stream: the board now, then the board after each change. The window takes an item when the script has taken the one before.
                canvas.Rpc.Stream<WatchBoard, BoardView>("board.watch", (_, cancellationToken) => _state.WatchAsync(cancellationToken));

                var view = PluginCanvasView.Html("<p class=\"alta-muted\">Loading the board…</p>") with { Script = PluginScript.File("ui/board.js"), Title = "Board" };
                return ValueTask.FromResult(view);
            },
            Closed = canvas =>
            {
                _open.TryRemove(canvas.InstanceId, out _);
                return ValueTask.CompletedTask;
            },
            Describe = (_, _) => ValueTask.FromResult<string?>(_state.Describe()),
        };
    }

    // `alta board open` asks the window for the tab; `alta board add "title"` adds a card, and the open tabs show it.
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution { Path = "board", Description = "Opens the board canvas and adds cards to it.", CreateCommandNode = CreateBoard };
    }

    private AltaCommand CreateBoard(PluginAltaCommandContext context)
    {
        var open = new AltaCommand("open", "Opens the board in a tab of the window.") { new CommandUsage(), new HelpOption() };
        open.Add(async (_, _) =>
        {
            var result = await Services.Canvases.OpenAsync("board", cancellationToken: CancellationToken.None);
            context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.board.open", status = result.Status.ToString(), instance = result.InstanceId, shown = result.Shown }));
            return result.Requested ? 0 : 1;
        });
        var title = "";
        var add = new AltaCommand("add", "Adds a card to the first column: the tabs that show the board change at once.") { new CommandUsage(), new HelpOption() };
        add.Add("title=", "The title of the card.", value => title = value ?? "");
        add.Add(async (_, _) =>
        {
            try
            {
                var (view, message) = _state.Add(new AddCard("To do", title));
                await ChangedAsync(view, message);
                context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.board.add", revision = view.Revision, message }));
                return 0;
            }
            catch (PluginRpcException error)
            {
                context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.board.error", code = error.Code, message = error.Message }));
                return 1;
            }
        });
        var root = new AltaCommand("board", "Board commands.") { new CommandUsage(), new HelpOption() };
        root.Add(open);
        root.Add(add);
        return root;
    }

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("board-open", "Opens the board canvas.", async (_, cancellationToken) =>
        {
            await Services.Canvases.OpenAsync("board", cancellationToken: cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Board: open" };
    }

    // Tells every script that listens: `alta.rpc.subscribe("board.changed", ...)`. An event is not kept, so a script reads the board with a call too.
    private async Task ChangedAsync(BoardView view, string message)
    {
        foreach (var canvas in _open.Values) await canvas.Rpc.PublishAsync("board.changed", new BoardChanged(view.Revision, message));
    }

    public sealed record GetBoard;

    public sealed record WatchBoard;

    public sealed record AddCard(string Column, string Title);

    public sealed record MoveCard(int Id, string Column);

    public sealed record BoardChanged(int Revision, string Message);

    public sealed record Card(int Id, string Title, string? File = null, int? Line = null);

    public sealed record Column(string Name, IReadOnlyList<Card> Cards);

    public sealed record BoardView(string Title, int Revision, IReadOnlyList<Column> Columns);

    // The state of the board, and the streams that follow it.
    private sealed class BoardState
    {
        private static readonly string[] Names = ["To do", "Doing", "Done"];

        private readonly Lock _gate = new();
        private readonly List<Channel<BoardView>> _watchers = [];
        private readonly List<List<Card>> _columns =
        [
            [new Card(1, "Write the notes", "doc/plugins.md", 1), new Card(2, "Check the samples")],
            [new Card(3, "Review the plugin API", "src/CodeAlta.Plugins.Abstractions/PluginScript.cs", 1)],
            [new Card(4, "Tag the release"), new Card(5, "Publish"), new Card(6, "Announce")],
        ];
        private int _nextId = 7;
        private int _revision = 1;

        public BoardView Read()
        {
            lock (_gate) return Snapshot();
        }

        public (BoardView View, string Message) Add(AddCard request)
        {
            var title = request.Title?.Trim() ?? string.Empty;
            if (title.Length is 0 or > 80) throw new PluginRpcException("invalid_card", "A card needs a title of 1 to 80 characters.");
            lock (_gate)
            {
                var column = Index(request.Column);
                _columns[column].Add(new Card(_nextId++, title));
                return (Publish(), $"Added \"{title}\" to {Names[column]}");
            }
        }

        public (BoardView View, string Message) Move(MoveCard request)
        {
            lock (_gate)
            {
                var to = Index(request.Column);
                foreach (var column in _columns)
                {
                    var card = column.Find(candidate => candidate.Id == request.Id);
                    if (card is null) continue;
                    column.Remove(card);
                    _columns[to].Add(card);
                    return (Publish(), $"Moved \"{card.Title}\" to {Names[to]}");
                }
            }

            throw new PluginRpcException("not_found", "No card has this number.");
        }

        public string Describe()
        {
            var view = Read();
            return "# Board\n\n" + string.Join("\n", view.Columns.Select(column => $"- {column.Name}: {column.Cards.Count} cards"));
        }

        // The board now, then the last board after each change: a reader that is slower than the changes sees the latest and skips the ones between.
        public async IAsyncEnumerable<BoardView> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var channel = Channel.CreateBounded<BoardView>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
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

        private int Index(string? name)
        {
            var index = Array.FindIndex(Names, candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 ? index : throw new PluginRpcException("unknown_column", "The columns are To do, Doing and Done.");
        }

        private BoardView Publish()
        {
            _revision++;
            var view = Snapshot();
            foreach (var watcher in _watchers) watcher.Writer.TryWrite(view);
            return view;
        }

        private BoardView Snapshot() => new("Release board", _revision, [.. _columns.Select((cards, index) => new Column(Names[index], [.. cards]))]);
    }
}
