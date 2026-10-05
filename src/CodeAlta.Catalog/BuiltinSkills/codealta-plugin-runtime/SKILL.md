---
name: codealta-plugin-runtime
description: Use this skill when authoring, testing, enabling, disabling, or troubleshooting CodeAlta source plugins.
---

# CodeAlta plugins

A source plugin is one C# file that CodeAlta builds and loads when it starts. The same plugin runs in CodeAlta Desktop (`alta`) and in CodeAlta TUI (`altatui`).

Plugins are trusted code. Building one runs the .NET SDK, NuGet and MSBuild; loading one runs its code inside CodeAlta. Do not copy a plugin into a plugin root only to read it.

## Create a plugin

1. Pick a package id: letters, digits, `.`, `_`, `-`, starting with a letter or digit.
2. Create the file in one of the two roots:

   | Scope | File |
   |---|---|
   | All projects | `~/.alta/plugins/<package-id>/plugin.cs` |
   | One project | `<project>/.alta/plugins/<package-id>/plugin.cs` |

3. Write a public class that inherits `PluginBase` and has a public parameterless constructor.
4. Restart CodeAlta. It builds the plugin and loads it.
5. Check the result: see [When a plugin does not start](#when-a-plugin-does-not-start).

```csharp
using CodeAlta.Plugins.Abstractions;

[Plugin("hello", DisplayName = "Hello", Description = "Adds a /hello command.")]
public sealed class HelloPlugin : PluginBase
{
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("hello", "Says hello.", static async (context, cancellationToken) =>
        {
            await context.Ui.NotifyAsync("Hello from a plugin.", cancellationToken);
            return PluginCommandResult.Handled;
        });
    }
}
```

Rules for the package folder:

- CodeAlta writes `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` and `global.json` in the plugin root. Do not create or edit them.
- No project file is needed. CodeAlta runs `dotnet build plugin.cs` with the .NET 10 SDK.
- Add NuGet packages with `#:package Name@Version` at the top of `plugin.cs`, and more source files with `#:include`.
- `CodeAlta.Plugins.Abstractions`, `CodeAlta.Plugins.Tui` and the `XenoAtom.Terminal.UI` packages are already referenced, in both applications.
- An optional `README.md` beside `plugin.cs` describes the plugin in the plugin list.
- One file can declare several plugin classes.

## One plugin, two applications

By default a plugin starts in both applications. Limit it with the attribute:

```csharp
[Plugin("notes", Frontends = PluginFrontends.Desktop)]   // or Terminal; the default is All
```

An application does not start a plugin that does not support it, and says so in its plugin list.

Most of the API is portable: write it once and each application shows it its own way. Where the look matters, give two forms. `Context.Host.Frontend` tells which application is running.

| What | Portable | Desktop form | Terminal form |
|---|---|---|---|
| Notification, confirm, input, selection, text editor | `context.Ui.NotifyAsync`, `ConfirmAsync`, `InputAsync`, `SelectAsync`, `EditTextAsync` | — | — |
| Dialog with your own content | — | `PluginUi.HtmlDialog(title, html, buttons)` | `PluginTui.CustomDialog(title, visual)` |
| Content around the prompt | `PluginUi.Content(region, _ => new PluginRenderResult { Markdown = … })` | `PluginRenderResult.FromHtml(html, text)` | `PluginTui.Visual(region, visualFactory, portableContent)` |
| Status item | `PluginStatusContribution` | `Command` makes it clickable | — |
| Prompt picker | `PluginUi.PromptPicker(name, trigger, title, search)` | — | — |
| Timeline card | `PluginDerivedSessionEvent.Markdown` | `Html` | `PluginTerminalDerivedSessionEvent.VisualFactory` |

The desktop application ignores terminal controls. The terminal application ignores HTML. So a result that has both forms works in both.

## Contribution points

Override only what the plugin needs.

| Method | Adds |
|---|---|
| `GetCommands()` | Commands for the palette, the `/` menu and shortcuts |
| `GetUiContributions()` | Status items and content around the prompt |
| `GetPromptPickers()` | A picker opened by a character typed in the prompt |
| `GetSessionEventProjections()` | Cards in the session timeline |
| `GetAgentTools()` | Tools the model can call |
| `GetAltaCommands()` | Commands under the in-session `alta` tool |
| `GetSystemPromptContributions()` | Text added to the system or developer prompt |
| `GetPromptProcessors()`, `GetInstructionProcessors()` | Changes to the user prompt or to the final instructions |
| `GetCompactionContributions()` | Hooks for session compaction |
| `GetResources()` | Skill, prompt and template folders of the package |
| `GetStartupContributions()`, `GetCommandLineContributions()` | Early startup hooks and command-line commands |
| `OnBeforeAgentRunAsync`, `OnToolCallAsync`, `OnToolResultAsync`, `OnAgentEventAsync` | Observation and changes while a session runs |

The factories `Command`, `PluginUi`, `PluginTui`, `Prompt`, `AgentTool`, `Resources` and `Startup` build the common contributions.

## Commands

```csharp
yield return Command.Shell("note-add", "Adds a note.", AddNoteAsync) with
{
    Label = "Notes: add",
    KeyBinding = new PluginKeyBinding(new PluginKeyGesture(PluginKey.F9)),
};
```

- `Command.Shell` is always available. `Command.Session` needs a selected session. `Command.Prompt` belongs to the prompt editor.
- The name is what the user types after `/`: letters, digits, `.`, `_`, `-`.
- A command takes no arguments. Ask with `context.Ui`.
- Return `PluginCommandResult.Handled`, `Cancelled`, or `Message("…")` to show a message. Set `PromptText` to send a prompt, with `EnqueuePrompt = true` to queue it.
- `context.Sessions` and `context.Prompts` give the session and the prompt draft the command was started from: `SelectedSessionId`, `DraftText`, `SetDraftTextAsync`, `SendPromptAsync`, `EnqueuePromptAsync`, `TrySteerAsync`, `RequestCompactionAsync`.
- A key binding is one stroke, or `Ctrl+G` followed by a second stroke. A key CodeAlta already uses keeps its meaning. On the desktop a single letter needs `Ctrl` or `Alt`.

## Dialogs

The portable dialogs need no extra code:

```csharp
if (!await context.Ui.ConfirmAsync("Notes", "Delete all notes?", cancellationToken)) return PluginCommandResult.Cancelled;
var title = await context.Ui.InputAsync("Title", "Untitled", cancellationToken);            // null when cancelled
var kind = await context.Ui.SelectAsync("Kind", items, cancellationToken);                  // items: PluginSelectItem<T>
var text = await context.Ui.EditTextAsync("Note", "First line", cancellationToken);
```

For a dialog with its own content, build the request for the running application:

```csharp
var request = Context.Host.Frontend == PluginFrontends.Desktop
    ? PluginUi.HtmlDialog("Notes", html, closeButton) with { OnAction = HandleActionAsync }
    : PluginTui.CustomDialog("Notes", visual) with { Buttons = [closeButton] };
var response = await context.Ui.ShowDialogForResultAsync(request, cancellationToken);
```

`response.ButtonName` is the button that closed the dialog and `response.Values` holds the named fields of an HTML dialog. In the terminal, read your own controls. A request that has only HTML shows its `Message` in the terminal, and nothing when it has none.

## HTML fragments on the desktop

A fragment is plain HTML. CodeAlta sanitizes it, gives buttons, fields and tables the look of the application, and inserts it in the window. Nothing in a fragment runs: no script, no style, no event handler, no image, no form. Links are shown and not followed.

The window acts on these attributes:

| Attribute | Effect |
|---|---|
| `data-alta-command="name"` | A click runs the command `name` of the plugin. Works everywhere a fragment is shown. |
| `data-alta-action="name"` | In a dialog, a click (or Enter in a field, or a change of a select) calls `OnAction` with the action name. |
| `data-alta-value="…"` | The value passed with the action. |
| `name="…"` on `input`, `select`, `textarea` | The field is returned in `Values`: text, `true`/`false` for a checkbox, the chosen radio value. |

`OnAction` returns what the dialog does next:

```csharp
OnAction = (action, cancellationToken) =>
{
    if (action.Name == "remove") notes.RemoveAt(int.Parse(action.Value!));
    return ValueTask.FromResult(PluginDialogActionResult.Update(BuildHtml()));   // or KeepOpen, or CloseDialog("button")
},
```

Classes you can use: `alta-row` and `alta-column` for layout, `alta-primary`, `alta-success`, `alta-warning`, `alta-danger` and `alta-muted` for tone, `alta-tag` for a small label, `alta-callout` for a highlighted block. Other classes and `id` values that do not start with `alta-` are removed.

Allowed elements: text and structure (`p`, `div`, `span`, headings, lists, `table`, `pre`, `code`, `details`, `a`, `b`, `i`, …) and fields (`button`, `input`, `select`, `textarea`, `label`, `fieldset`, `progress`, `meter`).

Write text with `PluginHtml.Encode(text)`. `PluginHtml.CommandButton(command, label)` and `PluginHtml.ActionButton(action, label)` write the two kinds of buttons.

## Status items and content around the prompt

```csharp
yield return new PluginStatusContribution
{
    Region = PluginUiRegion.SessionStatus,
    Name = "notes-count",
    GetStatus = _ => new PluginStatusItem { Label = "Notes", Text = "3", Tone = PluginStatusTone.Info, Command = "notes" },
};

yield return PluginTui.Visual(PluginUiRegion.SessionFooter,
    _ => new Markup("[dim]3 notes[/]"),                                        // terminal
    _ => PluginRenderResult.FromHtml("<span class=\"alta-tag\">3 notes</span>", "3 notes"),   // desktop, then plain text
    "notes-footer");
```

Regions: `SessionFooter` is above the prompt, `CommandBar` and `SessionStatus` are in the status line. A `PluginRenderResult` is shown in its richest form: `Html` (desktop), then `Markdown`, then `Text`. Return null to show nothing. These callbacks run often: keep them fast and do no I/O in them.

## Prompt pickers

```csharp
public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
{
    yield return PluginUi.PromptPicker("notes", '!', "Notes", async (context, cancellationToken) =>
        [.. (await FindAsync(context.Query, cancellationToken)).Select(note => new PluginPromptPickerItem { Label = note.Title, Description = note.Date, InsertText = note.Title + " " })],
        "[!] to insert a note");
}
```

Typing the character at the start of a word opens the picker. The chosen item replaces the token with `InsertText`. The trigger is a punctuation character other than `@`, `#` and `/`. The last argument is a hint shown in the empty prompt of the terminal application.

## Timeline cards

`GetSessionEventProjections()` returns cards computed from the events of a session. They are shown in the timeline and not written to the conversation.

```csharp
new PluginDerivedSessionEvent
{
    EventId = $"notes:{context.SessionId}",
    Markdown = "**Notes** · 3 added",                       // the bold start is the card title
    Html = "<span class=\"alta-tag\">3 added</span>",          // desktop: shown after the title
    DetailSections = [new PluginDerivedSessionEventDetailSection { Header = "Added", Markdown = "- one\n- two", Html = "<ul><li>one</li><li>two</li></ul>" }],
}
```

Keep the `EventId` stable for the same turn so the card is updated, not duplicated. For native terminal cards use `PluginTerminalDerivedSessionEvent` with a `VisualFactory`.

## Background work and state

- Start background work with `Tasks.Run(...)` so CodeAlta can cancel it when the plugin unloads. Do not use an untracked `Task.Run`.
- `Services.State` stores plugin data. Do not put secrets in plugin source.
- `Services.Alta.InvokeAsync([...])` runs an `alta` command and returns its JSONL output.
- `Logger` writes to the CodeAlta log.

## When a plugin does not start

| | Desktop | Terminal |
|---|---|---|
| While it builds | The start-up screen shows the plugin being built | The console shows the build before the interface |
| Build or load failure | A notice at start, and the error under the plugin in Settings > Plugins | The error in the console and in `/plugins` |
| Not supported in this application | Marked in Settings > Plugins | Listed in `/plugins` |
| Full log | Application Logs | `~/.alta/logs/codealta.log` |

Disable one plugin in Settings > Plugins, in `/plugins`, or in the configuration:

```toml
[plugins.hello]
enabled = false
```

Start without any plugin with `CODEALTA_DISABLE_PLUGINS=1`. The terminal application also accepts `--no-plugins` and `--plugin-safe-mode`.

A changed `plugin.cs` is rebuilt at the next start.

## Samples

Each folder under `samples/` is a complete plugin that CodeAlta's tests build and load. Copy one to a plugin root to try it.

| Sample | Shows |
|---|---|
| `hello-command` | A command |
| `desktop-and-terminal` | One plugin for both applications: portable dialogs, an HTML dialog with actions, a status item, content above the prompt, a prompt picker |
| `ui-status`, `ui-all-regions` | Status items and content in every region |
| `prompt-guidance` | Text added to the prompt |
| `instruction-path-normalizer` | A change to the final instructions |
| `background-task` | Tracked background work |
| `package-reference` | A NuGet package |
| `skill-root` | A skill shipped by a plugin |
| `multi-plugin-assembly` | Several plugins in one file |
