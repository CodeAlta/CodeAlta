using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;
using XenoAtom.CommandLine;
using XenoAtom.Terminal.UI.Controls;
// XenoAtom.CommandLine has a Command too: these two lines keep Command.Shell and name the other one.
using Command = CodeAlta.Plugins.Abstractions.Command;
using AltaCommand = XenoAtom.CommandLine.Command;

// A complete plugin: a to-do list that the user and the model share. It has commands, a dialog, a status
// item, a prompt picker, tools for the model, a command of the alta tool, text for the prompt and saved data.
[Plugin("todo", DisplayName = "To-do", Description = "A to-do list for the user and the model.")]
public sealed class TodoPlugin : PluginBase
{
    private List<TodoItem> _items = [];

    private int Open => _items.Count(static item => !item.Done);

    // Data: read once when the plugin starts, written after each change.
    public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        => _items = await Services.State.ReadJsonAsync<List<TodoItem>>(PluginStateScope.User, "items", cancellationToken) ?? [];

    private ValueTask SaveAsync(CancellationToken cancellationToken) => Services.State.WriteJsonAsync(PluginStateScope.User, "items", _items, cancellationToken);

    private async ValueTask AddAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The item has no text.");
        _items.Add(new TodoItem(text.Trim(), false));
        await SaveAsync(cancellationToken);
    }

    // Commands: the palette, /todo and /todo-add in the prompt, and a shortcut.
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("todo", "Shows the to-do list.", ShowAsync) with
        {
            Label = "To-do: show",
            KeyBinding = new PluginKeyBinding(new PluginKeyGesture(PluginKey.F8)),
        };

        yield return Command.Shell("todo-add", "Adds an item to the to-do list.", async (context, cancellationToken) =>
        {
            var text = await context.Ui.InputAsync("New to-do item", cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(text)) return PluginCommandResult.Cancelled;
            await AddAsync(text, cancellationToken);
            return PluginCommandResult.Message($"{Open} open items.");
        }) with { Label = "To-do: add" };
    }

    // A dialog with its own content: an HTML fragment on the desktop, a control in the terminal.
    private async ValueTask<PluginCommandResult> ShowAsync(PluginCommandContext context, CancellationToken cancellationToken)
    {
        var close = new PluginDialogButton { Name = "close", Label = "Close", IsDefault = true, IsCancel = true };
        var request = Context.Host.Frontend == PluginFrontends.Desktop
            ? PluginUi.HtmlDialog("To-do", ListHtml(), close) with { OnAction = OnActionAsync }
            : PluginTui.CustomDialog("To-do", new TextBlock(ListText())) with { Buttons = [close] };
        await context.Ui.ShowDialogAsync(request, cancellationToken);
        return PluginCommandResult.Handled;
    }

    // Nothing in the fragment runs: an element with data-alta-action calls this, with the named fields in Values.
    private async ValueTask<PluginDialogActionResult> OnActionAsync(PluginDialogAction action, CancellationToken cancellationToken)
    {
        var index = int.TryParse(action.Value, out var parsed) && parsed >= 0 && parsed < _items.Count ? parsed : -1;
        if (action.Name == "toggle" && index >= 0) _items[index] = _items[index] with { Done = !_items[index].Done };
        if (action.Name == "remove" && index >= 0) _items.RemoveAt(index);
        if (action.Name == "add" && action.Values.TryGetValue("text", out var text) && !string.IsNullOrWhiteSpace(text)) _items.Add(new TodoItem(text.Trim(), false));
        await SaveAsync(cancellationToken);
        return PluginDialogActionResult.Update(ListHtml());
    }

    // Plain elements: the window gives buttons, fields, tables and tags the look of its own components.
    private string ListHtml()
    {
        var rows = string.Concat(_items.Select(static (item, index) =>
            $"""<tr><td><label><input type="checkbox" data-alta-action="toggle" data-alta-value="{index}"{(item.Done ? " checked" : "")}> {(item.Done ? "<s class=\"alta-muted\">" : "<span>")}{PluginHtml.Encode(item.Text)}{(item.Done ? "</s>" : "</span>")}</label></td>"""
            + $"""<td align="right"><button data-alta-action="remove" data-alta-value="{index}">Remove</button></td></tr>"""));
        return $"""
            <div class="alta-column">
              <div class="alta-row"><span class="alta-tag alta-primary">{Open} open</span> <span class="alta-tag">{_items.Count - Open} done</span></div>
              {(_items.Count == 0 ? "<p class=\"alta-muted\">Nothing to do.</p>" : $"<table><tbody>{rows}</tbody></table>")}
              <div class="alta-row"><input class="alta-grow" name="text" placeholder="New item" data-alta-action="add"> {PluginHtml.ActionButton("add", "Add", primary: true)}</div>
            </div>
            """;
    }

    private string ListText()
        => _items.Count == 0 ? "The to-do list is empty." : string.Join('\n', _items.Select(static (item, index) => $"{index + 1}. [{(item.Done ? "x" : " ")}] {item.Text}"));

    // A status item under the prompt; on the desktop a click runs the command it names.
    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return new PluginStatusContribution
        {
            Region = PluginUiRegion.SessionStatus,
            Name = "todo",
            GetStatus = _ => _items.Count == 0 ? null : new PluginStatusItem
            {
                Label = "To-do",
                Text = $"{Open} open",
                Tone = Open == 0 ? PluginStatusTone.Success : PluginStatusTone.Info,
                Command = "todo",
            },
        };
    }

    // Typing + in the prompt opens a picker of the open items.
    public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
    {
        yield return PluginUi.PromptPicker("todo", '+', "To-do", (context, _) =>
            ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>(
            [
                .. _items.Where(item => !item.Done && item.Text.Contains(context.Query, StringComparison.OrdinalIgnoreCase))
                    .Select(static item => new PluginPromptPickerItem { Label = item.Text, InsertText = item.Text + " " }),
            ]),
            "[+] to insert a to-do item");
    }

    // Tools the model calls. The description of a tool is all the model knows of it.
    public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
    {
        yield return Tool("todo_list", "Lists the items of the to-do list of the user, with their numbers.",
            """{ "type": "object", "properties": {}, "additionalProperties": false }""",
            (_, _) => Task.FromResult(ListText()));

        yield return Tool("todo_add", "Adds an item to the to-do list of the user.",
            """{ "type": "object", "properties": { "text": { "type": "string", "description": "The item." } }, "required": ["text"], "additionalProperties": false }""",
            async (arguments, cancellationToken) =>
            {
                await AddAsync(arguments.GetProperty("text").GetString() ?? "", cancellationToken);
                return $"Added. {Open} open items.";
            });

        yield return Tool("todo_done", "Marks an item of the to-do list of the user as done.",
            """{ "type": "object", "properties": { "number": { "type": "integer", "description": "The number of the item in todo_list." } }, "required": ["number"], "additionalProperties": false }""",
            async (arguments, cancellationToken) =>
            {
                var index = arguments.GetProperty("number").GetInt32() - 1;
                if (index < 0 || index >= _items.Count) throw new InvalidOperationException($"The list has no item {index + 1}.");
                _items[index] = _items[index] with { Done = true };
                await SaveAsync(cancellationToken);
                return $"Done: {_items[index].Text}";
            });
    }

    // A failed result carries a message for the model, which can then call the tool again.
    private static PluginAgentToolContribution Tool(string name, string description, string schema, Func<JsonElement, CancellationToken, Task<string>> run)
        => AgentTool.Create(new AgentToolDefinition(
            new AgentToolSpec(name, description, JsonDocument.Parse(schema).RootElement.Clone()),
            async (invocation, cancellationToken) =>
            {
                try
                {
                    return new AgentToolResult(true, [new AgentToolResultItem.Text(await run(invocation.Arguments, cancellationToken))]);
                }
                catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
                {
                    return new AgentToolResult(false, [new AgentToolResultItem.Text(exception.Message)], exception.Message);
                }
            }));

    // A command of the alta tool: `alta todo list` prints one JSON record.
    public override IEnumerable<PluginAltaCommandContribution> GetAltaCommands()
    {
        yield return new PluginAltaCommandContribution
        {
            Path = "todo list",
            Description = "Lists the to-do items.",
            CreateCommandNode = context =>
            {
                var list = new AltaCommand("list", "Lists the to-do items as one JSON record.") { new CommandUsage(), new HelpOption() };
                list.Add((_, _) =>
                {
                    context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.todo.list", items = _items }));
                    return ValueTask.FromResult(0);
                });
                var root = new AltaCommand("todo", "To-do list commands.") { new CommandUsage(), new HelpOption() };
                root.Add(list);
                return root;
            },
        };
    }

    // Text added to the developer prompt of every session. A text that stays the same keeps the prompt of a session as it is.
    public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
    {
        yield return Prompt.Developer("The user keeps a to-do list. The tools todo_list, todo_add and todo_done read and change it.", "To-do");
    }
}

// What is stored: a public type with public properties.
public sealed record TodoItem(string Text, bool Done);
