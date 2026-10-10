using System.Text.Json;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
// XenoAtom.CommandLine has a Command too: these two lines keep Command.Shell and name the other one.
using Command = CodeAlta.Plugins.Abstractions.Command;
using AltaCommand = XenoAtom.CommandLine.Command;

// A board drawn by a script of the package folder: ui/board.js is a React component that the window draws in the tab, with the Blueprint
// components of the application, a chart, and the `alta` object. The plugin gives the data as the input of the canvas; the script draws it.
[Plugin("canvas-board", DisplayName = "Canvas board", Description = "A board of cards drawn by a script, with Blueprint components and a chart.")]
public sealed class CanvasBoardPlugin : PluginBase
{
    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return new PluginCanvasContribution
        {
            Id = "board", Title = "Board", Description = "A board of cards in columns, with a chart of how many each column holds.", Icon = "layers",
            InputSchema = """{"type":"object","properties":{"title":{"type":"string"},"columns":{"type":"array","items":{"type":"object","properties":{"name":{"type":"string"},"cards":{"type":"array","items":{"type":"object","properties":{"title":{"type":"string"},"file":{"type":"string"},"line":{"type":"integer"}}}}}}}}}""",
            Open = static (canvas, _) =>
            {
                // The fragment is what the tab shows while the module loads, and for a window that cannot run it. The module itself is a file of the package.
                var view = PluginCanvasView.Html("<p class=\"alta-muted\">Loading the board…</p>") with { Script = PluginScript.File("ui/board.js") };
                return ValueTask.FromResult(view with { Title = canvas.Input is { ValueKind: JsonValueKind.Object } input && input.TryGetProperty("title", out var title) ? title.GetString() : null });
            },
            Describe = static (_, _) => ValueTask.FromResult<string?>("A board of cards in columns, drawn by a script. Open it with `alta board open`."),
        };
    }

    // `alta board open` asks the window for the tab and gives it the cards it draws.
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution { Path = "board", Description = "Opens the board canvas.", CreateCommandNode = CreateBoard };
    }

    private AltaCommand CreateBoard(PluginAltaCommandContext context)
    {
        var open = new AltaCommand("open", "Opens the board in a tab of the window, with a few cards.") { new CommandUsage(), new HelpOption() };
        open.Add(async (_, _) =>
        {
            var input = JsonSerializer.SerializeToElement(new
            {
                title = "Release board",
                columns = new object[]
                {
                    new { name = "To do", cards = new object[] { new { title = "Write the notes", file = "doc/plugins.md", line = 1 }, new { title = "Check the samples" } } },
                    new { name = "Doing", cards = new object[] { new { title = "Review the plugin API", file = "src/CodeAlta.Plugins.Abstractions/PluginScript.cs", line = 1 } } },
                    new { name = "Done", cards = new object[] { new { title = "Tag the release" }, new { title = "Publish" }, new { title = "Announce" } } },
                },
            });
            var result = await Services.Canvases.OpenAsync("board", new PluginCanvasOpenOptions { Input = input }, CancellationToken.None);
            context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.board.open", status = result.Status.ToString(), instance = result.InstanceId, shown = result.Shown }));
            return result.Requested ? 0 : 1;
        });
        var root = new AltaCommand("board", "Board commands.") { new CommandUsage(), new HelpOption() };
        root.Add(open);
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
}
