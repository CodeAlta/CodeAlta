---
name: codealta-plugin-runtime
description: Use this skill to create, change, build, reload, test or troubleshoot a CodeAlta plugin - C# code that adds commands, dialogs, status items, agent tools, alta commands, prompt text or timeline cards to CodeAlta.
---

# CodeAlta plugins

A source plugin is one C# file, `plugin.cs`, in its own folder. CodeAlta builds it and loads it: there is no project file and nothing to install. The same plugin runs in CodeAlta Desktop (`alta`) and in CodeAlta TUI (`altatui`).

A plugin is trusted code: it runs inside CodeAlta with the rights of the user. Do not copy a plugin into a plugin folder only to read it.

## Work on a plugin

Use the `alta` tool. Each command answers with one JSON record.

1. **Create it.** `alta plugin create <id>` writes `plugin.cs` with one command, builds it and starts it. The record has `file`, the path to edit.
2. **Write it.** Replace the content of `file` with the plugin. Keep the key of `[Plugin("<id>")]`.
3. **Load it.** `alta plugin reload <id>` builds the file and replaces the running plugin. Read `change`:
   - `reloaded` or `started`: the new version runs.
   - `buildFailed`: `build.diagnostics` has each compiler error with `file`, `line`, `column`, `code` and `message`. The version that ran keeps running. Fix the file and reload again.
   - `startFailed`: `diagnostics` says why: an exception while the plugin starts, a key another plugin has, no public plugin class.
4. **Check it.** `alta plugin status <id>` lists what the plugin contributes and the errors its callbacks raised since the reload. A build that passes does not say that the plugin works: in CodeAlta Desktop, run what the plugin shows once in the window before you say it is done. See [Look at the window](#look-at-the-window).
5. **Tell the user** what the plugin adds and how to use it: the `/command` names, the shortcut, where the status item is.

| To | Run |
|---|---|
| List the plugins and their state | `alta plugin list` |
| See the folder, the state, the last build, the contributions and the errors of one | `alta plugin status <id>` |
| Create a plugin for all projects | `alta plugin create <id> --description "<one sentence>"` |
| Create a plugin for the project CodeAlta was started in | `alta plugin create <id> --project` |
| Look a type or a member of the plugin API up | `alta plugin api <name>` |
| Check that the file compiles without touching the running plugin | `alta plugin build <id>` |
| Build and load the new source | `alta plugin reload <id>` |
| Apply everything that changed on disk: new, changed and removed plugins | `alta plugin refresh` |
| Show the plugin to the user in the code editor (Desktop) | `alta plugin open <id>` |
| Remove a plugin | Delete its folder, then `alta plugin refresh` |

Where the plugins are:

| Scope | Folder |
|---|---|
| All projects | `~/.alta/plugins/<id>/plugin.cs` |
| One project | `<project>/.alta/plugins/<id>/plugin.cs`, loaded when CodeAlta is started in that project |

Create a plugin for all projects unless the user asks for a plugin of the project. Every command works for both: when a plugin of each scope has the same id, add `--global` or `--project` after the id.

What a reload changes, and when:

- Commands, shortcuts, status items, content around the prompt and pickers: at once, in the window.
- Agent tools: at once in the turn that ran `alta plugin reload` (`agentTools.available` is `now`), so you can call your tool right after the reload. Another session has them from its next prompt.
- Text added to the prompt: from the next prompt of each session.
- Timeline cards: after the next turn of a session.
- The fields of the plugin class start again: keep data with `Services.State`.

The commands that create, build and load a plugin are those of CodeAlta Desktop. CodeAlta TUI loads plugins when it starts: there, write `plugin.cs` in a plugin folder and restart it; `alta plugin list`, `status` and `api` work in both.

## Look at the window

In CodeAlta Desktop, `alta ui activate` gives your session tools that see and drive the window. They have the names of Chrome DevTools MCP.

| To | Do |
|---|---|
| Read what the window shows | `take_snapshot`: text, each element with a `uid` |
| See how it looks | `take_screenshot` |
| Run a command of the plugin | `press_key` `Control+P`, `type_text` the command name, `press_key` `Enter` |
| Use a dialog | `take_snapshot`, then `fill` a field or `click` a button by `uid` |
| Read the HTML a fragment became | `evaluate_script` with `() => [...document.querySelectorAll(".plugin-html")].map(e => e.outerHTML)` |

A status item and the content of a plugin are at the bottom of a session, around the prompt: open a session to see them. A notification is a short text at the top right. A dialog of a plugin is in the snapshot as `dialog "<title>"`; in the page, its content is the element `.plugin-html`. Close what you opened (`press_key` `Escape`) before you answer the user.

When a command shows "The plugin command failed", `alta plugin status <id>` has the exception and the line of `plugin.cs`.

## The shape of a plugin

```csharp
using CodeAlta.Plugins.Abstractions;

[Plugin("notes", DisplayName = "Notes", Description = "Keeps short notes.")]
public sealed class NotesPlugin : PluginBase
{
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        yield return Command.Shell("notes", "Shows the notes.", async (context, cancellationToken) =>
        {
            await context.Ui.NotifyAsync("No note yet.", cancellationToken);
            return PluginCommandResult.Handled;
        });
    }
}
```

`samples/todo/plugin.cs` is a complete plugin with most of what follows: commands, a dialog, a status item, a picker, tools, an `alta` command, prompt text and saved data. Read it before you write a plugin that has more than a command.

- The class is public, inherits `PluginBase` and has a public constructor without parameters. One file can declare several plugin classes.
- The key of `[Plugin("...")]` is unique among the plugins: use the id of the folder.
- CodeAlta calls each `Get...()` method once, when the plugin starts. Return a fixed set of contributions; compute what changes inside their callbacks (`GetStatus`, `CreateContent`, a command handler).
- `System`, `System.IO`, `System.Linq`, `System.Collections.Generic`, `System.Threading.Tasks` and `System.Net.Http` are imported. `CodeAlta.Plugins.Abstractions`, `CodeAlta.Agent`, `CodeAlta.Plugins.Tui` and `XenoAtom.Terminal.UI` are referenced.
- Add a NuGet package with `#:package Name@Version` at the top of the file.
- CodeAlta writes `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props`, `global.json` and `.gitignore` in the plugin folder that holds the plugins. Do not create or edit them.
- A `README.md` beside `plugin.cs` describes the plugin where it is not running.

## Look the API up

The API is in three namespaces: `CodeAlta.Plugins.Abstractions` (plugins), `CodeAlta.Plugins.Tui` (terminal controls) and `CodeAlta.Agent` (tools and events). This page shows the usual cases. For anything else, look the name up instead of guessing it:

| To | Run |
|---|---|
| See a type with its members and what each one is for | `alta plugin api PluginCommandContext` |
| List the types whose name has a word | `alta plugin api Dialog` |
| Find the type that has a member | `alta plugin api SelectedProjectPath` |
| List every type | `alta plugin api` |

A name that is not in the API is answered with the names that are close to it: look one of them up.

## What a plugin can add

Override only what the plugin needs.

| Method | Adds | Desktop | TUI |
|---|---|---|---|
| `GetCommands()` | Commands: the palette, `/name` in the prompt, shortcuts | yes | yes |
| `GetUiContributions()` | Status items and content around the prompt; buttons in the window (`PluginUi.Button`) | yes | yes (no buttons) |
| `GetCanvases()` | Tabs that the plugin provides, filled by HTML or by a script | yes | no |
| `GetPromptPickers()` | A picker opened by a character typed in the prompt | yes | yes |
| `GetSessionEventProjections()` | Cards in the timeline of a session | yes | yes |
| `GetAgentTools()` | Tools the model can call | yes | yes |
| `GetAltaCommands()` | Commands of the `alta` tool of the sessions | yes | yes |
| `GetCanvases()` | Tabs the plugin provides, which users and agents open (see "Canvases") | yes | no |
| `GetSystemPromptContributions()` | Text added to the system or developer prompt | yes | yes |
| `GetInstructionProcessors()` | Changes to the final instructions of a session | yes | yes |
| `GetResources()` | Skills shipped with the plugin: `Resources.SkillRoot("skills")` | yes | yes |
| `OnBeforeAgentRunAsync` | Tools and messages for one run; can cancel the run | yes | yes |
| `OnAgentEventAsync` | Sees the events of the sessions | yes | yes |
| `OnToolCallAsync`, `OnToolResultAsync` | Sees and changes the calls of the tools that plugins add | yes | yes |
| `GetPromptProcessors()`, `OnPromptSubmittingAsync` | Changes a prompt before it is sent | no | yes |
| `GetCompactionContributions()` | Hooks of a manual compaction | no | yes |
| `GetCommandLineContributions()` | Commands of the `altatui` command line | no | yes |

`[Plugin("id", Frontends = PluginFrontends.Desktop)]` (or `Terminal`) starts the plugin in one application only. `Context.Host.Frontend` says which application runs.

## Commands

```csharp
yield return Command.Shell("note-add", "Adds a note.", AddAsync) with
{
    Label = "Notes: add",
    KeyBinding = new PluginKeyBinding(new PluginKeyGesture(PluginKey.F9)),
};
```

- `Command.Shell` is always available. `Command.Session` needs a selected session. `Command.Prompt` belongs to the prompt.
- The name is what the user types after `/`: letters, digits, `.`, `_`, `-`.
- A command takes no argument: ask with `context.Ui`.
- Return `PluginCommandResult.Handled`, `Cancelled`, or `Message("...")` to show a notification. `PluginCommandResult.Handled with { PromptText = "..." }` sends a prompt to the session; add `EnqueuePrompt = true` to queue it.
- `context.ProjectPath` and `context.SessionId` are the project and the session the command was started from. `context.Prompts.DraftText` and `SetDraftTextAsync` read and replace the prompt being written. `context.Sessions.SendPromptAsync`, `EnqueuePromptAsync` and `TrySteerAsync` talk to the session.
- A key binding is one stroke, or `Ctrl+G` then a second stroke: `new PluginKeyBinding(new PluginKeyGesture('g', PluginKeyModifiers.Ctrl), new PluginKeyGesture('n'))`. A key CodeAlta uses keeps its meaning. On the desktop a single letter needs `Ctrl` or `Alt`.

## Dialogs

These work in both applications:

```csharp
await context.Ui.NotifyAsync("Saved.", cancellationToken);
if (!await context.Ui.ConfirmAsync("Notes", "Delete all the notes?", cancellationToken)) return PluginCommandResult.Cancelled;
var title = await context.Ui.InputAsync("Title", "Untitled", cancellationToken);                  // null when cancelled
var text = await context.Ui.EditTextAsync("Note", "First line", cancellationToken);               // several lines
var kind = await context.Ui.SelectAsync("Kind", [new PluginSelectItem<string> { Label = "Bug", Value = "bug" }], cancellationToken);
```

A dialog with its own content has one form per application:

```csharp
var close = new PluginDialogButton { Name = "close", Label = "Close", IsDefault = true, IsCancel = true };
var request = Context.Host.Frontend == PluginFrontends.Desktop
    ? PluginUi.HtmlDialog("Notes", BuildHtml(), close) with { OnAction = OnActionAsync }
    : PluginTui.CustomDialog("Notes", new TextBlock("3 notes")) with { Buttons = [close] };
var response = await context.Ui.ShowDialogForResultAsync(request, cancellationToken);
```

`response.ButtonName` is the button that closed the dialog and `response.Values` holds the named fields of an HTML dialog.

## HTML on the desktop

The window of CodeAlta Desktop is made with React and Blueprint. **A fragment of HTML runs nothing**: where a plugin shows its own content it returns an HTML fragment, and the window gives its elements the look of the Blueprint components. A plugin that wants its own JavaScript (a board, a dashboard) gives a script next to the fragment: see "Canvases and script" below.

| Write | Shown as |
|---|---|
| `<button>` | Blueprint button; with `class="alta-primary"`, `alta-success`, `alta-warning` or `alta-danger`, in that intent |
| `<input>`, `<textarea>` | Blueprint text field |
| `<select>`, `<input type="checkbox">`, `<input type="radio">` | Field in the colors of the window |
| `<table>` | Blueprint table, compact |
| `class="alta-tag"` | Blueprint tag; with a tone class, in that intent |
| `class="alta-callout"` | Blueprint callout; with a tone class, in that intent |
| `class="alta-row"`, `class="alta-column"` | Children side by side, or one under the other, with a gap |
| `class="alta-grow"` | In a row, takes the space that is left: a field beside a button |
| `<label class="alta-field">Title <input name="title"></label>` | A label above its field, which takes the width |
| `class="alta-card"` | Blueprint card: a bordered block |
| `class="alta-muted"`, or a tone class on text | Muted or colored text |
| `PluginHtml.Markdown(text)` | The text rendered as Markdown by the window, as in the timeline: headings, lists, tables, links |
| `PluginHtml.Code(code, "csharp")` | Source code with the colors of its language (`csharp`, `json`, `diff`, `bash`, `typescript`, ...) |
| `PluginHtml.Diagram(text)` | A Mermaid diagram: `flowchart`, `sequenceDiagram`, `pie`, `gantt`, ... |

**What a fragment uses of the window.** A fragment cannot load or call a library, and it does not need to for these four: a chart too (`PluginHtml.Chart(optionJson, label)`, a JSON option of data only), and the Markdown renderer, the code highlighter and Mermaid are those of the window, reached through `PluginHtml.Markdown`, `Code` and `Diagram`. Each helper writes `<div class="alta-markdown">` with Markdown as its text, encoded; you can write that element yourself, and the indentation its lines share is not part of the Markdown. See `samples/report-dialog`.

```csharp
private static string BuildHtml(string report, string failingTest) => $"""
    <div class="alta-column">
      {PluginHtml.Markdown(report)}
      {PluginHtml.Diagram("flowchart LR\n  build[Build] --> test[Test] -->|2 failed| fix[Fix]")}
      {PluginHtml.Code(failingTest, "csharp")}
    </div>
    """;
```

The other parts of the window are not for a fragment: the code editor, the terminal, trees, tabs, popovers, icons and images. To show a picture, draw a Mermaid diagram or a table.

Content around the prompt and timeline cards also take Markdown without HTML (`PluginRenderResult.Markdown`, `PluginDerivedSessionEvent.Markdown`, the `Markdown` of a detail section): on the desktop it is the same renderer, with the same code and diagrams. CodeAlta TUI shows that Markdown with its own renderer, and a diagram as its text.

The fragment is sanitized first: `script`, `style`, `img`, `svg`, `iframe`, `form`, event attributes (`onclick`), the `style` attribute and classes that do not start with `alta-` are removed. An `id` and a `for` start with `alta-`. A link is shown and not followed. Kept: text and structure (`p`, `div`, `span`, `h1`-`h6`, `ul`, `ol`, `li`, `table`, `pre`, `code`, `details`, `summary`, `a`, `b`, `i`, `small`, `hr`, ...) and fields (`button`, `input`, `select`, `option`, `textarea`, `label`, `fieldset`, `progress`, `meter`).

The window acts for the fragment through attributes:

| Attribute | Effect |
|---|---|
| `data-alta-command="name"` | A click runs the command `name` of the plugin. Works wherever a fragment is shown. |
| `data-alta-action="name"` | In a dialog: a click, Enter in a field or a change of a select calls `OnAction` with that name. |
| `data-alta-value="..."` | The value passed with the action. |
| `name="..."` on a field | The field is in `Values`: its text, `true` or `false` for a checkbox, the value of the chosen radio. A name starts with a letter. |

To change what a dialog shows, answer the action with new HTML:

```csharp
private ValueTask<PluginDialogActionResult> OnActionAsync(PluginDialogAction action, CancellationToken cancellationToken)
{
    if (action.Name == "remove" && int.TryParse(action.Value, out var index)) _notes.RemoveAt(index);
    if (action.Name == "add" && action.Values.TryGetValue("text", out var text) && text.Length > 0) _notes.Add(text);
    return ValueTask.FromResult(PluginDialogActionResult.Update(BuildHtml()));    // or KeepOpen, or CloseDialog("close")
}

private string BuildHtml() => $"""
    <div class="alta-column">
      <table><tbody>{string.Concat(_notes.Select((note, index) =>
          $"""<tr><td>{PluginHtml.Encode(note)}</td><td align="right"><button data-alta-action="remove" data-alta-value="{index}">Remove</button></td></tr>"""))}</tbody></table>
      <div class="alta-row"><input class="alta-grow" name="text" placeholder="New note" data-alta-action="add"> {PluginHtml.ActionButton("add", "Add", primary: true)}</div>
    </div>
    """;
```

Write every text of the user with `PluginHtml.Encode(text)`. `PluginHtml.CommandButton(command, label)` and `PluginHtml.ActionButton(action, label)` write the two kinds of buttons. CodeAlta TUI ignores HTML: give it Markdown, text, or a terminal control.

## Canvases and script

A canvas is a tab that a plugin provides (`GetCanvases()`): the plugin declares it, holds its state, and the tab is a view of it (`PluginCanvasContribution`: `Id`, `Title`, `Scope`, `Open`; `Services.Canvases.OpenAsync("id")` or `alta` commands ask the window for the tab). Without script a tab shows an HTML fragment, as above. With script it is drawn by a JavaScript module of the plugin:

```csharp
// a file of the package folder, or the module as text: PluginScript.Inline(code) / PluginHtml.Script(code)
yield return new PluginCanvasContribution
{
    Id = "board", Title = "Board", Open = (canvas, _) => ValueTask.FromResult(
        PluginCanvasView.Html("<p class=\"alta-muted\">Loading…</p>") with { Script = PluginScript.File("ui/board.js") }),
};
```

A script also goes with a dialog (`PluginDialogRequest.Script`), content around the prompt (`PluginRenderResult.Script`) and a card of the timeline (`PluginDerivedSessionEvent.Script`). It is given **next to** the HTML: a `<script>` or an `onclick` in an HTML string is removed. The module has one of two forms:

```js
import { Button, Tab, Tabs } from "@blueprintjs/core";
import { Chart, Markdown, html, useAlta, useVisible } from "codealta";

export default function Board() {                    // a React component, drawn by the window in its own tree
    const alta = useAlta();
    return html`<${Button} onClick=${() => alta.host.notify("Hello")}>Hello<//>`;
}
// or: export async function mount(root, alta) { ...fill root...; return () => { /* cleanup */ }; }
```

- **Libraries.** The application lends **its own** `react`, `react-dom`, `@blueprintjs/core`, `@blueprintjs/table`, `flexlayout-react` and `lucide-react`, the very instances it runs (React 19, Blueprint 6, FlexLayout 0.11), so import them by their names and ship nothing. There is no build step: write plain JavaScript, with `html` of `codealta` instead of JSX (or `React.createElement`). Use `PopoverNext`, not `Popover`. A script depends on these versions (`alta.versions`); the HTML vocabulary and `codealta` are what stays stable.
- **`codealta`** gives `html`, `useAlta`, `useVisible`, `useTheme`, `Markdown`, `Code`, `Diagram`, `Icon`, `BrandIcon`, `FileLink`, `SessionLink` and the charts (`Chart`, `Sparkline`, `StatTile`, `CalendarHeatmap`, `WeekdayHourHeatmap`, `histogram`…).
- **`alta`** (the argument of `mount`, and `useAlta()`): `alta.context` (project, session, key, the `input` the canvas was opened with), `alta.visible` (pause timers and reads while it is false), `alta.closed` (an `AbortSignal`: end what the script started), `alta.host` (`openFile`, `openDiff`, `openSession`, `openCanvas`, `openLink`, `notify`, `runCommand`, `setTitle`, `setStatus`, `setBadge`), `alta.theme` (the colors as values), `alta.html(text)` (a string cleaned as fragments are: the only way to put a string in `innerHTML`), `alta.versions`. `alta.rpc` (calls to the plugin's own handlers) is not available yet.
- Effects run twice in development (strict mode): write them so they can run again. A reload of the plugin, or an edit of a module, is a new address; the tab mounts the new module and the old one is released by `alta.closed`.
- An error in a module is shown in its tab, with a button that copies it; read it, fix the file, and `alta plugin reload` again.

`samples/canvas-board` is a complete React canvas (Blueprint tabs and menu, a chart, Markdown, a file link, `alta.host`) whose module is a file of the package; `samples/canvas-checklist` is a canvas without script. Look at the result in the window as described in "Look at the window".

## Status items and content around the prompt

```csharp
public override IEnumerable<PluginUiContribution> GetUiContributions()
{
    yield return new PluginStatusContribution
    {
        Region = PluginUiRegion.SessionStatus,
        Name = "notes-count",
        GetStatus = _ => new PluginStatusItem { Label = "Notes", Text = _notes.Count.ToString(), Tone = PluginStatusTone.Info, Command = "notes" },
    };

    yield return PluginUi.Content(PluginUiRegion.SessionFooter,
        _ => PluginRenderResult.FromHtml($"""<span class="alta-tag">{_notes.Count} notes</span>""", $"{_notes.Count} notes"),
        "notes-footer");
}
```

- A status item is in the status line under the prompt. `Command` names a command of the plugin that a click runs (Desktop). Tones: `Info`, `Success`, `Warning`, `Error`, `Muted`.
- Content: `SessionFooter` is above the prompt, `CommandBar` and `SessionStatus` are in the status line. A `PluginRenderResult` is shown in its richest form: `Html` (Desktop), then `Markdown`, then `Text`. `FromHtml(html, text)` gives both. Return null to show nothing.
- For a terminal control instead of text: `PluginTui.Visual(region, _ => new Markup("[dim]3 notes[/]"), _ => PluginRenderResult.FromHtml(...), "name")`.
- These callbacks run every few seconds and after each command of the plugin: read a field, never a file or the network.
- A callback gets the project it is shown for in `context.ProjectPath` (null without a project). What takes time to compute (a git branch) is computed by background work and kept in a field. Background work has no selected project: a project plugin has its project in `Context.ScopeProjectPath`.

## Buttons in the window

CodeAlta Desktop draws buttons that a plugin returns from `GetUiContributions()`. A button has an icon and a label, and it runs a command of the plugin or opens a canvas of the plugin; CodeAlta TUI draws none (the command stays in its palette).

```csharp
yield return PluginUi.Button(PluginButtonPlace.TitleBar, "notes", icon: "notebook-pen", label: "Notes") with
{
    Command = "notes",                                                      // a command of this plugin; or Canvas = "board" to open a canvas
    GetState = _ => new PluginButtonState { Badge = _open.Count },          // a number, PluginButtonBadge.Dot or PluginButtonBadge.Busy; Tone, Hidden, Disabled, Tooltip
};
```

- Places: `TitleBar` (top right, before the space switch; 2 for a plugin), `Rail` (after Issues; 1), `ProjectMenu` and `SessionMenu` (a line in the menu of a row, asked about that row; 6). A button over its limit, with an invalid identifier, or naming neither or both of `Command` and `Canvas` is left out with a warning in `alta plugin status`.
- The command runs for the context of the button: the project and session of the row (`context.ProjectId`, `context.SessionId`) and `context.Workspace.SelectedSpaceId`. A `Canvas` opens with that context and runs no handler; a canvas about a project or a session disables the button where there is none.
- `GetState` takes a `PluginButtonContext` (place, space, project, session). It is synchronous and runs each time the window reads the buttons, so read a field. The window reads them when the selection changes, when a command of the plugin ends, and when you call `Services.Ui.InvalidateButtons()` after a change. Do not poll.
- `Icon`: a Lucide icon name (`chart-column`), a brand logo name, or an SVG file of the package (`icons/notes.svg`, 32 KiB at most, drawn in the color of the text). A missing icon shows a neutral one. A canvas takes the same icons.
- A person can hide a button with a right click and turn it on again in Settings > Plugins, so a plugin does not rely on a button being there. The buttons fold into a menu in a narrow window.

## Prompt pickers

```csharp
public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
{
    yield return PluginUi.PromptPicker("notes", '!', "Notes", (context, cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<PluginPromptPickerItem>>(
            [.. _notes.Where(note => note.Contains(context.Query, StringComparison.OrdinalIgnoreCase))
                .Select(note => new PluginPromptPickerItem { Label = note, InsertText = note + " " })]),
        "[!] to insert a note");
}
```

Typing the character at the start of a word opens the picker; the chosen item replaces the word with `InsertText`. The character is a punctuation other than `@`, `#` and `/`.

## Timeline cards

`GetSessionEventProjections()` returns cards computed from the events of a session. A card is shown in the timeline and is never sent to the model. See `samples/timeline-card`.

```csharp
new PluginDerivedSessionEvent
{
    EventId = $"notes:{context.SessionId}:{runId}",          // the same id updates the card
    Markdown = "**Notes** · 3 added",                        // the bold start is the title of the card
    Html = """<span class="alta-tag">3 added</span>""",       // Desktop: shown after the title
    DetailSections = [new PluginDerivedSessionEventDetailSection { Header = "Added", Markdown = "- one\n- two" }],
}
```

What `context.Events` holds:

- The events of one turn have the same `RunId`: group by it.
- A tool call is an `AgentActivityEvent` with `Kind == AgentActivityKind.ToolCall`, the tool in `Name`, and a `Phase` that goes from `Requested` and `Started` to `Completed` or `Failed`. Its `Details` is JSON with `toolName`, `arguments`, `readFiles` and `modifiedFiles` (full paths) and, once it ended, `result`.
- A text is an `AgentContentCompletedEvent`: `Kind` is `User`, `Assistant` or `Reasoning`, and `Content` is the text.
- Providers do not all record the same events: some add `AgentActivityKind.Turn`, `CommandExecution` or `FileChange` activities. Do not wait for an event that a session may not have.
- To see the events a session really has, read its journal, one JSON event per line: `~/.alta/sessions/<year>/<month>/<day>/<session-id>.jsonl` (`~/.alta/dev/sessions/` when CodeAlta runs with `--dev`). `alta session current` gives the id of your session.

A card appears after the next turn of a session, or when the session is opened again.

## Canvases

A canvas is a tab of CodeAlta Desktop that the plugin provides: a checklist, a board, a report. The plugin declares it with `GetCanvases()` and holds its state; the tab is a view of that state, opened by the user or by an agent, and it comes back at the next start. Nothing is shown until someone opens it. See `samples/canvas-checklist`, a complete one: a checklist of the application, of a project and of a session.

```csharp
public override IEnumerable<PluginCanvasContribution> GetCanvases()
{
    yield return new PluginCanvasContribution
    {
        Id = "release", Title = "Release checklist", Description = "The steps of a release and who owns them.",
        Icon = "list-checks", Scope = PluginCanvasScope.Project,   // Application, Project or Session: what one tab is about
        Open = (canvas, ct) => ValueTask.FromResult(PluginCanvasView.Rendered((c, _) => ValueTask.FromResult(Render(c)), OnActionAsync)),
        Describe = (canvas, ct) => ValueTask.FromResult<string?>(Markdown(canvas)),   // what it shows now, for agents
        Actions = [ new PluginCanvasActionContribution { Name = "tick", Description = "Ticks a step.", InputSchema = "{...JSON Schema...}", Handler = TickAsync } ],
    };
}
```

- The id is 1 to 64 letters, digits, `-`, `_` or `.`. The description is written for agents: it is what they read to decide whether to use the canvas.
- The user finds the canvas without anything more from the plugin: the Canvases page, the search (`Open canvas: <title>`), the menu of a project row (scope Project) or of a session row (scope Session), and Settings > Plugins list what the plugin declares.
- An agent uses the `alta canvas` commands. Read them from `alta canvas --help` when you need the options:
  - `alta canvas list` lists the canvases that plugins declare (`ref` is `plugin-key/canvas-id`), `alta canvas list --open` the tabs that are open.
  - `alta canvas show <id> [--project <project>]` prints the actions with their schemas and, when the canvas has a `Describe` handler, what it shows now. Prefer it to a picture.
  - `alta canvas invoke <id> <action> --stdin` runs an action with JSON input and prints its JSON result. It works whether the tab is open or not, since the state is the plugin's.
  - `alta canvas open <id> --project <project>` shows the tab to the user; `focus` and `close` act on a tab that is open. The window shows one space at a time: a request for another space adds the tab there and the answer says `shown: false`. Tell the user, and run `alta space switch` only when they ask to see it.
- To create one: `alta plugin create`, write `GetCanvases()` in `plugin.cs`, `alta plugin build`, `alta plugin reload`, then `alta canvas open <id>` (and `alta canvas invoke` for an action) to try it in the same turn.

## Agent tools

A tool has a name, a description, a JSON schema of its arguments and a handler. See `samples/agent-tool`.

```csharp
using System.Text.Json;
using CodeAlta.Agent;

public override IEnumerable<PluginAgentToolContribution> GetAgentTools()
{
    var schema = JsonDocument.Parse("""
        { "type": "object", "properties": { "text": { "type": "string", "description": "The note." } }, "required": ["text"], "additionalProperties": false }
        """).RootElement.Clone();
    yield return AgentTool.Create(new AgentToolDefinition(
        new AgentToolSpec("note_add", "Adds a note to the notes of the user.", schema),
        (invocation, cancellationToken) =>
        {
            _notes.Add(invocation.Arguments.GetProperty("text").GetString()!);
            return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text($"{_notes.Count} notes.")]));
        }));
}
```

- The name uses letters, digits, `_` and `-`. The description is all the model knows of the tool: say what it does and when to use it.
- A failed call returns `new AgentToolResult(false, [new AgentToolResultItem.Text(message)], message)`: the model reads the message.
- The project of the session that calls: `Services.Workspace.SelectedProjectPath ?? Environment.CurrentDirectory`.
- To try the tool: `alta plugin reload <id>`, then call it in the same turn.

## Commands of the alta tool

A session runs them as `alta <root> <command>`. See `samples/alta-command`.

```csharp
using XenoAtom.CommandLine;
using Command = CodeAlta.Plugins.Abstractions.Command;      // XenoAtom.CommandLine has a Command too: this keeps Command.Shell
using AltaCommand = XenoAtom.CommandLine.Command;           // and this names the command-line type

yield return new PluginAltaCommandContribution { Path = "notes list", Description = "Lists the notes.", CreateCommandNode = context =>
{
    var list = new AltaCommand("list", "Lists the notes as JSON.") { new CommandUsage(), new HelpOption() };
    list.Add((_, _) => { context.Stdout.WriteLine(JsonSerializer.Serialize(new { type = "alta.notes.list", notes = _notes })); return ValueTask.FromResult(0); });
    var root = new AltaCommand("notes", "Notes commands.") { new CommandUsage(), new HelpOption() };
    root.Add(list);
    return root;
} };
```

The first word of `Path` is the root command. It cannot be a root of CodeAlta: `session`, `project`, `skill`, `plugin`, `tool`, `model`, `provider`, `ask`, `version`. Write one JSON object per line to `context.Stdout`; return 0 for success.

## Text added to the prompt

```csharp
public override IEnumerable<PluginSystemPromptContribution> GetSystemPromptContributions()
{
    yield return Prompt.Developer("Answer in French.", "Language");                                         // fixed
    yield return Prompt.Dynamic(PluginPromptChannel.Developer, (context, _) => new ValueTask<string?>($"The user has {_notes.Count} notes."));
}
```

A dynamic text is asked for each prompt of each session; return null to add nothing. Keep it stable: a text that changes replaces the prompt of the session.

## Data, background work and logs

- **Data.** `Services.State` keeps JSON between the runs and the reloads. See `samples/saved-data`.
  ```csharp
  public override async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
      => _notes = await Services.State.ReadJsonAsync<List<string>>(PluginStateScope.User, "notes", cancellationToken) ?? [];
  // after a change:
  await Services.State.WriteJsonAsync(PluginStateScope.User, "notes", _notes, cancellationToken);
  ```
  `PluginStateScope.User` is one file for the user, in `~/.alta/plugin-data/`. `PluginStateScope.Project` is one file per project, in `<project>/.alta/plugin-data/`; it needs a project, so use it in a command or a tool, not when the plugin starts. Store public types with public properties. Do not put secrets in the source of a plugin.
- **Tables.** For data that is queried or grows, `Services.Database` gives the plugin tables in the SQLite database of CodeAlta. Name every table with `Services.Database.TablePrefix`, create them in `MigrateAsync(version, (connection, from, to, token) => ...)`, which runs once per version, and use `ReadAsync` and `WriteAsync` (one short transaction, in turn with the other writers). Check `HasDatabase` first when the plugin may run in a host without one.
- **Background work.** Start it with `Tasks.Run("name", async token => { ... })`, in `OnActivatedAsync`. CodeAlta cancels the token when the plugin is reloaded. Do not start an untracked `Task.Run`.
- **Other commands of CodeAlta.** `await Services.Alta.InvokeAsync(["session", "list"])` runs an `alta` command; the result has `ExitCode` and `TranscriptJsonl`, its JSON lines.
- **Logs.** With `using XenoAtom.Logging;`, `Logger.Info("...")` writes to the log of the application: Settings > Application Logs on the desktop, `~/.alta/logs/codealta.log` for the terminal.

## When something fails

| What you see | What to do |
|---|---|
| `change: buildFailed` | Fix the errors of `build.diagnostics`, reload. |
| `'Command' is an ambiguous reference` | The file imports `XenoAtom.CommandLine`: add `using Command = CodeAlta.Plugins.Abstractions.Command;`. |
| `'Logger' does not contain a definition for 'Info'` | Add `using XenoAtom.Logging;`. |
| `change: startFailed` with "its key ... is the key of ..." | Give the plugin another key in `[Plugin("...")]`. |
| `change: startFailed` with "declares no plugin" | Make the class public, with a public constructor without parameters. |
| "The plugin command failed" in the window | `alta plugin status <id>`: `diagnostics` has the exception and the line. |
| `state: unsupported` | The plugin names the other application in `Frontends`. |
| `state: disabled` | It is turned off: `[plugins.<id>] enabled = true` in `~/.alta/config.toml`, or Settings > Plugins, then `alta plugin refresh`. |
| `plugin.otherProject` | A project plugin runs for the project CodeAlta was started in. Create a global plugin instead. |
| The plugin does not show what you expect | `alta plugin status <id>`: is the contribution listed? Is `sourceChanged` true? Reload. |

`CODEALTA_DISABLE_PLUGINS=1` starts CodeAlta without any plugin.

## Samples

Each folder under `samples/` is a complete plugin that the tests of CodeAlta build and load. Read the one that is closest to what you write.

| Sample | Shows |
|---|---|
| `todo` | A complete plugin: commands, an HTML dialog with actions, a status item, a picker, tools, an `alta` command, prompt text, saved data |
| `hello-command` | A command |
| `desktop-and-terminal` | One plugin for both applications: commands with a shortcut, an HTML dialog with actions, a status item, content above the prompt, a prompt picker |
| `saved-data` | Data kept between runs with `Services.State` |
| `canvas-checklist` | A tab that the plugin provides: a checklist of the application, of a project and of a session, ticked from the page, a command or an agent; buttons in the title bar and in the menu of a project |
| `canvas-board` | A tab drawn by a script of the package folder: a React component with Blueprint tabs and menus, a chart, Markdown, links and `alta.host` |
| `report-dialog` | A dialog with Markdown, a Mermaid diagram and highlighted code |
| `agent-tool` | A tool the model calls |
| `alta-command` | A command of the `alta` tool |
| `timeline-card` | A card in the timeline, computed from the events of a session |
| `prompt-guidance` | Text added to the prompt |
| `ui-status`, `ui-all-regions` | Status items and content in every region |
| `background-task` | Tracked background work |
| `package-reference` | A NuGet package |
| `instruction-path-normalizer` | A change to the final instructions |
| `skill-root` | A skill shipped by a plugin |
| `multi-plugin-assembly` | Several plugins in one file |
