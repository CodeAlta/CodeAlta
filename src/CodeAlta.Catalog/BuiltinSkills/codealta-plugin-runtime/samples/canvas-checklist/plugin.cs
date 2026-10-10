using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.CommandLine;
// XenoAtom.CommandLine has a Command too: these two lines keep Command.Shell and name the other one.
using Command = CodeAlta.Plugins.Abstractions.Command;
using AltaCommand = XenoAtom.CommandLine.Command;

// A checklist in three canvases: one of the application, one for each project and one for each session. The plugin
// holds the lists; a tab is a view of one. Items are ticked from the page, from a command, or by an agent, and every
// open tab of the list shows the change at once.
[Plugin("canvas-checklist", DisplayName = "Canvas checklist", Description = "A checklist shown in canvases of the application, of a project and of a session.")]
public sealed class CanvasSamplePlugin : PluginBase
{
    private readonly Dictionary<string, List<Item>> _lists = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginCanvasContext> _open = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        var saved = await Services.State.ReadJsonAsync<Dictionary<string, List<Item>>>(PluginStateScope.User, "lists", cancellationToken);
        if (saved is not null) foreach (var pair in saved) _lists[pair.Key] = pair.Value;
    }

    // Buttons in the window. The first one opens the application canvas with no code at all, the second runs a command, and both
    // show how many items are left. The third is a line in the menu of every project row, read for that row.
    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return PluginUi.Button(PluginButtonPlace.TitleBar, "checklist", icon: "list-checks", label: "Checklist") with
        {
            Canvas = "checklist",
            GetState = _ => new PluginButtonState { Badge = Left("app") },
        };
        yield return PluginUi.Button(PluginButtonPlace.TitleBar, "tick", icon: "check", label: "Tick the next item", order: 1) with
        {
            Command = "checklist-tick",
            GetState = _ => new PluginButtonState { Badge = Left("app"), Tone = Left("app") == 0 ? PluginStatusTone.Success : PluginStatusTone.Info, Disabled = Left("app") == 0 },
        };
        yield return PluginUi.Button(PluginButtonPlace.ProjectMenu, "project", icon: "briefcase", label: "Project checklist") with
        {
            Canvas = "project-checklist",
            GetState = button => new PluginButtonState { Badge = Left("project:" + button.ProjectId) },
        };
    }

    public override IEnumerable<PluginCanvasContribution> GetCanvases()
    {
        yield return Canvas("checklist", "Checklist", "A checklist of the application.", "star", PluginCanvasScope.Application);
        yield return Canvas("project-checklist", "Project checklist", "A checklist of a project.", "briefcase", PluginCanvasScope.Project);
        yield return Canvas("session-checklist", "Session checklist", "A checklist of a session.", "bot", PluginCanvasScope.Session);
    }

    private PluginCanvasContribution Canvas(string id, string title, string description, string icon, PluginCanvasScope scope)
        => new()
        {
            Id = id, Title = title, Description = description, Icon = icon, Scope = scope,
            Open = (canvas, cancellationToken) =>
            {
                _open[canvas.InstanceId] = canvas;
                // The tab stays in the list of open instances until the instance is closed.
                canvas.Closed.Register(() => _open.TryRemove(canvas.InstanceId, out _));
                // What the tab shows first, status included: a push before the view is returned is not kept.
                return ValueTask.FromResult(PluginCanvasView.Rendered((c, _) => ValueTask.FromResult(Render(c)), OnActionAsync) with { Status = Summary(KeyOf(canvas)) });
            },
            Describe = (canvas, _) => ValueTask.FromResult<string?>(Markdown(KeyOf(canvas))),
            Actions =
            [
                new PluginCanvasActionContribution
                {
                    Name = "add", Description = "Adds an item to the checklist. Input: { \"text\": \"...\" }.", InputSchema = """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""",
                    Handler = async (canvas, input, cancellationToken) =>
                    {
                        var text = input?.GetProperty("text").GetString() ?? throw new ArgumentException("The item has no text.");
                        await ChangeAsync(KeyOf(canvas), list => list.Add(new Item(Guid.NewGuid().ToString("N")[..6], text.Trim(), false)), cancellationToken);
                        return JsonSerializer.SerializeToElement(new { items = Items(KeyOf(canvas)).Count });
                    },
                },
                new PluginCanvasActionContribution
                {
                    Name = "tick", Description = "Ticks an item by its text or its id. Input: { \"item\": \"...\" }.", InputSchema = """{"type":"object","properties":{"item":{"type":"string"}},"required":["item"]}""",
                    Handler = async (canvas, input, cancellationToken) =>
                    {
                        var wanted = input?.GetProperty("item").GetString() ?? string.Empty;
                        var found = false;
                        await ChangeAsync(KeyOf(canvas), list =>
                        {
                            var index = list.FindIndex(item => item.Id == wanted || string.Equals(item.Text, wanted, StringComparison.OrdinalIgnoreCase));
                            if (index >= 0) { list[index] = list[index] with { Done = true }; found = true; }
                        }, cancellationToken);
                        return JsonSerializer.SerializeToElement(new { ticked = found });
                    },
                },
            ],
        };

    // `alta checklist open --canvas project-checklist --project <id>` asks the window for a tab: how an agent shows the canvas.
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution { Path = "checklist", Description = "Opens a checklist canvas, and ticks the next item.", CreateCommandNode = CreateChecklist };
    }

    private AltaCommand CreateChecklist(PluginAltaCommandContext context)
    {
        string canvas = "checklist", project = "", session = "", space = "", key = "";
        var open = new AltaCommand("open", "Opens a checklist canvas in a tab of the window.") { new CommandUsage(), new HelpOption() };
        open.Add("canvas=", "checklist, project-checklist or session-checklist.", value => canvas = value ?? canvas);
        open.Add("project=", "The project of the canvas.", value => project = value ?? "");
        open.Add("session=", "The session of the canvas.", value => session = value ?? "");
        open.Add("space=", "The space to open the tab in; the shown one by default.", value => space = value ?? "");
        open.Add("key=", "Tells apart several checklists of the same context.", value => key = value ?? "");
        open.Add(async (_, _) =>
        {
            var result = await Services.Canvases.OpenAsync(canvas, new PluginCanvasOpenOptions
            {
                ProjectId = project.Length > 0 ? project : null, SessionId = session.Length > 0 ? session : null,
                SpaceId = space.Length > 0 ? space : null, Key = key.Length > 0 ? key : null,
            }, CancellationToken.None);
            context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.checklist.open", status = result.Status.ToString(), instance = result.InstanceId, space = result.SpaceId, shown = result.Shown }));
            return result.Requested ? 0 : 1;
        });
        var root = new AltaCommand("checklist", "Checklist commands.") { new CommandUsage(), new HelpOption() };
        root.Add(open);
        root.Add(CreateTick(context));
        return root;
    }

    // `alta checklist tick`: the same change as the command of the palette, from a session or from a script. Every open tab shows it.
    private AltaCommand CreateTick(PluginAltaCommandContext context)
    {
        var tick = new AltaCommand("tick", "Ticks the next open item of the application checklist.") { new CommandUsage(), new HelpOption() };
        tick.Add(async (_, _) =>
        {
            var ticked = false;
            await ChangeAsync("app", list =>
            {
                var index = list.FindIndex(static item => !item.Done);
                if (index >= 0) { list[index] = list[index] with { Done = true }; ticked = true; }
            }, CancellationToken.None);
            context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.checklist.tick", ticked }));
            return 0;
        });
        return tick;
    }

    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        // From a command: ticks the next open item of the checklist of the application, and every open tab shows it.
        yield return Command.Shell("checklist-tick", "Ticks the next open item of the application checklist.", async (_, cancellationToken) =>
        {
            var ticked = false;
            await ChangeAsync("app", list =>
            {
                var index = list.FindIndex(static item => !item.Done);
                if (index >= 0) { list[index] = list[index] with { Done = true }; ticked = true; }
            }, cancellationToken);
            return PluginCommandResult.Message(ticked ? "Ticked." : "Nothing left to tick.");
        }) with { Label = "Checklist: tick the next item" };
        // From a command: asks the window for the tabs, in the context of the pane the command runs in.
        yield return Command.Shell("checklist-open", "Opens the checklist canvases.", async (context, cancellationToken) =>
        {
            await Services.Canvases.OpenAsync("checklist", cancellationToken: cancellationToken);
            if (context.ProjectId is not null) await Services.Canvases.OpenAsync("project-checklist", new PluginCanvasOpenOptions { ProjectId = context.ProjectId }, cancellationToken);
            if (context.SessionId is not null) await Services.Canvases.OpenAsync("session-checklist", new PluginCanvasOpenOptions { ProjectId = context.ProjectId, SessionId = context.SessionId }, cancellationToken);
            return PluginCommandResult.Handled;
        }) with { Label = "Checklist: open the canvases" };
    }

    private async ValueTask<PluginCanvasActionResult> OnActionAsync(PluginCanvasContext canvas, PluginCanvasAction action, CancellationToken cancellationToken)
    {
        var key = KeyOf(canvas);
        switch (action.Name)
        {
            case "toggle":
                await ChangeAsync(key, list =>
                {
                    var index = list.FindIndex(item => item.Id == action.Value);
                    if (index >= 0) list[index] = list[index] with { Done = !list[index].Done };
                }, cancellationToken);
                break;
            case "add" when !string.IsNullOrWhiteSpace(action.Values.GetValueOrDefault("text")):
                await ChangeAsync(key, list => list.Add(new Item(Guid.NewGuid().ToString("N")[..6], action.Values["text"].Trim(), false)), cancellationToken);
                break;
            case "clear":
                await ChangeAsync(key, list => list.RemoveAll(static item => item.Done), cancellationToken);
                break;
            case "close":
                return PluginCanvasActionResult.CloseCanvas();
        }

        // Every open tab of this list was told by ChangeAsync; the tab that asked shows the result too.
        return PluginCanvasActionResult.Update(Render(canvas));
    }

    private async ValueTask ChangeAsync(string key, Action<List<Item>> change, CancellationToken cancellationToken)
    {
        string snapshot;
        lock (_gate)
        {
            if (!_lists.TryGetValue(key, out var list)) _lists[key] = list = [];
            change(list);
            snapshot = JsonSerializer.Serialize(_lists);
        }

        await Services.State.WriteJsonAsync(PluginStateScope.User, "lists", JsonSerializer.Deserialize<Dictionary<string, List<Item>>>(snapshot)!, cancellationToken);
        // The buttons show how many items are left: the window reads their state again.
        Services.Ui.InvalidateButtons();
        // The state changed: every open instance of this list writes its fragment again, and says how many are done.
        foreach (var canvas in _open.Values.Where(canvas => KeyOf(canvas) == key))
        {
            await canvas.InvalidateAsync(cancellationToken);
            await canvas.SetStatusAsync(Summary(key), cancellationToken);
        }
    }

    // The list of an instance: the application's, a project's or a session's.
    private static string KeyOf(PluginCanvasContext canvas)
        => canvas.CanvasId switch { "project-checklist" => "project:" + canvas.ProjectId, "session-checklist" => "session:" + canvas.SessionId, _ => "app" };

    private List<Item> Items(string key)
    {
        lock (_gate) return _lists.TryGetValue(key, out var list) ? [.. list] : [];
    }

    // How many items of a list are left: a plain count of what is in memory, which is what a button state may do.
    private int Left(string key)
    {
        lock (_gate) return _lists.TryGetValue(key, out var list) ? list.Count(static item => !item.Done) : 0;
    }

    private string Summary(string key)
    {
        var items = Items(key);
        return items.Count == 0 ? string.Empty : $"{items.Count(static item => item.Done)} of {items.Count} done";
    }

    private string Render(PluginCanvasContext canvas)
    {
        var key = KeyOf(canvas);
        var items = Items(key);
        var html = new StringBuilder();
        html.Append($"<div class=\"alta-column\"><h2>{PluginHtml.Encode(canvas.CanvasId switch { "project-checklist" => "Project checklist", "session-checklist" => "Session checklist", _ => "Checklist" })}</h2>");
        html.Append($"<p class=\"alta-muted\">{PluginHtml.Encode(key)} · {PluginHtml.Encode(Summary(key))}</p>");
        if (items.Count == 0) html.Append("<p class=\"alta-muted\">Nothing to do yet.</p>");
        foreach (var item in items)
        {
            html.Append($"<div class=\"alta-row\"><button type=\"button\" data-alta-action=\"toggle\" data-alta-value=\"{PluginHtml.Encode(item.Id)}\">{(item.Done ? "☑" : "☐")}</button>");
            html.Append(item.Done ? $"<s class=\"alta-muted\">{PluginHtml.Encode(item.Text)}</s>" : $"<span>{PluginHtml.Encode(item.Text)}</span>");
            html.Append("</div>");
        }

        html.Append("<div class=\"alta-row\"><input class=\"alta-grow\" name=\"text\" placeholder=\"New item\" data-alta-action=\"add\">");
        html.Append(PluginHtml.ActionButton("add", "Add", primary: true)).Append(PluginHtml.ActionButton("clear", "Clear done"));
        html.Append(PluginHtml.CommandButton("checklist-tick", "Tick next")).Append(PluginHtml.ActionButton("close", "Close"));
        html.Append("</div></div>");
        return html.ToString();
    }

    private string Markdown(string key)
    {
        var items = Items(key);
        return $"# Checklist ({key})\n\n" + (items.Count == 0 ? "No items." : string.Join('\n', items.Select(static item => $"- [{(item.Done ? 'x' : ' ')}] {item.Text} (`{item.Id}`)")));
    }

    private sealed record Item(string Id, string Text, bool Done);
}
