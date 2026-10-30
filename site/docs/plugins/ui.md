---
title: Plugin UI
---

# Plugin UI

What a plugin shows in the window: HTML fragments, scripts, status items, buttons, welcome page cards, prompt pickers, timeline cards and canvases. The workflow and the core API are on [Plugin development](developers.md).

## HTML fragments

A fragment is plain HTML that CodeAlta Desktop inserts in its window. CodeAlta sanitizes it and gives its elements the look of the app:

{.table}
| Write | Shown as |
|---|---|
| `<button>` | A button of the app; with a tone class, in that tone |
| `<input>`, `<textarea>`, `<select>` | A field of the app |
| `<table>` | A compact table |
| `class="alta-tag"`, `class="alta-callout"`, `class="alta-card"` | A tag, a callout, a card |
| `PluginHtml.Markdown(text)` | Markdown rendered as in the timeline |
| `PluginHtml.Code(code, "csharp")` | Source code with the colors of its language |
| `PluginHtml.Diagram(text)` | A [Mermaid](https://mermaid.js.org/) diagram |

Nothing in a fragment runs: scripts, styles, event handlers, images and forms are removed (a plugin that wants JavaScript gives it beside the fragment, see [Scripts](#scripts)). A link only opens an `http` or `https` page in the browser. A fragment asks the window to act with attributes, and the plugin answers in C#:

{.table}
| Attribute | Effect |
|---|---|
| `data-alta-command="name"` | A click runs the plugin's command `name`. Works wherever a fragment is shown. |
| `data-alta-action="name"` | In a dialog, a click, Enter in a field, or a changed select calls `OnAction` with that name. |
| `data-alta-value="…"` | The value passed with the action. |
| `name="…"` on a field | The field is returned in `Values`: text, `true`/`false` for a checkbox, the chosen radio value. |

`OnAction` returns what the dialog does next:

```csharp
OnAction = (action, cancellationToken) =>
{
    if (action.Name == "remove") notes.RemoveAt(int.Parse(action.Value!));
    return ValueTask.FromResult(PluginDialogActionResult.Update(BuildHtml()));   // or KeepOpen, or CloseDialog("button")
},
```

Classes you can use (others are removed):

{.table}
| Class | Effect |
|---|---|
| `alta-row`, `alta-column` | Lay out children in a row or a column |
| `alta-grow` | In a row, takes the space left: a field beside a button |
| `alta-field` | On a `label`: the label above its field, which takes the width |
| `alta-primary`, `alta-success`, `alta-warning`, `alta-danger`, `alta-muted` | Tone of a button, a tag, a callout or text |
| `alta-tag`, `alta-callout`, `alta-card` | A small rounded label, a highlighted block, a bordered block |
| `alta-stat` | One figure above its label, written by `PluginHtml.Stat(value, label)`; several in an `alta-row` make a line of figures |

Allowed elements are text and structure (`p`, `div`, `span`, headings, lists, `table`, `pre`, `code`, `details`, `a`, …) and fields (`button`, `input`, `select`, `textarea`, `label`, `fieldset`, `progress`, `meter`). Write text with `PluginHtml.Encode(text)`; `PluginHtml.CommandButton` and `PluginHtml.ActionButton` write the two kinds of buttons.

## Markdown, code and diagrams

A fragment cannot load a library: it uses the window's Markdown, code highlighting, diagrams and charts (`PluginHtml.Chart(optionJson, label)` draws a chart from a JSON description, in the window's colors):

```csharp
var html = $"""
    <div class="alta-column">
      {PluginHtml.Markdown("## Build report\n\n| Step | Result |\n|---|---|\n| Test | **2 failed** |")}
      {PluginHtml.Diagram("flowchart LR\n  build[Build] --> test[Test] -->|2 failed| fix[Fix]")}
      {PluginHtml.Code(failingTest, "csharp")}
    </div>
    """;
```

<figure class="alta-figure my-4" style="max-width: 44rem;">
  <img src="{{site.basepath}}/img/alta-desktop-plugin-report.webp" alt="A plugin dialog of CodeAlta Desktop with a Markdown table, a flowchart and a block of highlighted C# code" loading="lazy">
  <figcaption class="small text-secondary mt-2">A plugin dialog with Markdown, a diagram and code.</figcaption>
</figure>

Each helper writes an element of class `alta-markdown` holding Markdown, which you can also write yourself. Its links behave as in a message: a web link opens in the browser, a file link in the code editor. The code editor, the terminal, tabs, trees, icons and images of the app are not available to a fragment.

Content around the prompt and timeline cards also take Markdown directly, in both apps: `PluginRenderResult.Markdown`, `PluginDerivedSessionEvent.Markdown`.

## Scripts

CodeAlta Desktop runs JavaScript a plugin brings, to draw a board, a dashboard or a viewer with the window's own components. The script is a module given beside some HTML: in a canvas (`PluginCanvasView.Script`), a dialog (`PluginDialogRequest.Script`), content around the prompt (`PluginRenderResult.Script`) or a timeline card (`PluginDerivedSessionEvent.Script`). CodeAlta TUI ignores it.

```csharp
yield return new PluginCanvasContribution
{
    Id = "board", Title = "Board",
    Open = (canvas, ct) => ValueTask.FromResult(
        PluginCanvasView.Html("<p class=\"alta-muted\">Loading…</p>") with { Script = PluginScript.File("ui/board.js") }),
};
```

`PluginScript.File("ui/board.js")` is a file of the plugin folder; `PluginScript.Inline(code)` (or `PluginHtml.Script(code)`) keeps a one-file plugin in one file. The module either exports a React component as default:

```js
// ui/board.js: no build step
import { Button } from "@blueprintjs/core";
import { html, useAlta } from "codealta";

export default function Board() {
    const alta = useAlta();
    return html`<${Button} onClick=${() => alta.host.notify("Hello")}>Hello<//>`;
}
```

or `export async function mount(root, alta)`, which fills the element holding the HTML and may return a cleanup function.

**Libraries.** A script imports `react`, `react-dom`, `@blueprintjs/core`, `@blueprintjs/table`, `flexlayout-react`, `lucide-react` and `@xyflow/react` (React Flow, for graphs of nodes and links) and gets the very ones the window runs: menus and popovers open above the tabs, and the theme, color scheme and zoom apply. When CodeAlta moves one to a new major version, a script that used what changed needs an update; `alta.versions` gives the versions. Use `PopoverNext` for popovers. The HTML above and the `codealta` module stay stable across releases.

**The `codealta` module** gives `html` (JSX as a template), `Markdown`, `Code`, `Diagram`, `Icon`, `BrandIcon`, `FileLink`, `SessionLink`, the window's charts (`Chart`, `Sparkline`, `StatTile`, `CalendarHeatmap`, histograms, box plots), and the hooks `useAlta`, `useVisible` and `useTheme`.

**The `alta` object:**

{.table}
| Member | What it is |
|---|---|
| `alta.context` | The plugin, canvas, project, session, key, and the `input` the canvas was opened with |
| `alta.visible` | Whether the content is shown, with an event. Pause timers and reads while hidden |
| `alta.closed` | An `AbortSignal` that aborts when the content goes away (tab closed, plugin reloaded) |
| `alta.host` | Opens a file at a line, the changes of a project, a session, another canvas, this guide or a web page; shows a notice; runs a plugin command; sets the tab's title, status and mark |
| `alta.theme` | The window's colors as values, with an event |
| `alta.html(text)` | Cleans a string of HTML as fragments are cleaned. Use it instead of `innerHTML` |
| `alta.rpc` | Calls, streams and events of your C# handlers (canvases only) |
| `alta.versions` | The versions of the libraries and of this interface |

When the plugin is rebuilt or a module file changes, the tab mounts the new module. A hidden tab keeps what it drew. A failing script shows its error in its tab, with a button to copy it; the rest of the window goes on. HTML written as a string still has no script: `<script>` and `onclick` are removed.

## Calling the plugin

The script of a canvas reaches the plugin's C# with `alta.rpc`. Register handlers in the canvas's `Open`, and call them by name:

```csharp
Open = (canvas, ct) =>
{
    canvas.Rpc.Handle<GetBoard, Board>("board.get", (request, ct) => ValueTask.FromResult(_board.Read()));
    canvas.Rpc.Handle<MoveCard>("board.move", async (request, ct) => await _board.MoveAsync(request, ct));
    canvas.Rpc.Stream<WatchBoard, BoardChange>("board.watch", (request, ct) => _board.WatchAsync(ct));
    return ValueTask.FromResult(PluginCanvasView.Html("<p>Loading…</p>") with { Script = PluginScript.File("ui/board.js") });
}
// Later: await canvas.Rpc.PublishAsync("board.changed", new BoardChanged(2));
```

```js
const board = await alta.rpc.invoke("board.get");                                             // useRpc("board.get") in a component
for await (const change of await alta.rpc.stream("board.watch")) draw(change);               // useStream("board.watch")
const stop = await alta.rpc.subscribe("board.changed", change => refresh());
```

- Requests and results are C# records, as camelCase JSON.
- A stream is an `IAsyncEnumerable` read one item at a time: a slow script slows it, a hidden tab pauses it.
- An event is not kept: subscribe, then read the state with a call.
- Throw `PluginRpcException("not_found", "No such card.")` to fail on purpose: the script gets an `AltaError` with that `code`. Other exceptions arrive as `internal_error`, with the text in the plugin log.
- A handler's token is cancelled when the script cancels, after 30 seconds, when the tab closes, or on reload.
- A reload fails pending calls with `connection_closed`; the next call reconnects. A script that only subscribes watches `alta.rpc.connected` and subscribes again when it turns false. `useRpc` retries by itself.
- Limits: 1 MiB per frame, 8 calls, 8 streams and 8 subscriptions open, about 200 calls a second. Past them: `too_many_requests` or `payload_too_large`.

Dialogs, prompt content and timeline cards have no `alta.rpc`: they act through `alta.host` and plugin commands.

The `canvas-board` sample keeps its board in the plugin: the script reads it with a call and a stream, changes it with calls, and hears an event; `alta board add --title ...` changes it from a terminal.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-canvas-board.webp" alt="CodeAlta Desktop with a session at the left, where an agent opened and read the board with alta canvas, and at the right the Board tab of the canvas-board sample: cards in the columns To do, Doing and Done" loading="lazy">
  <figcaption class="small text-secondary mt-2">The board of the <code>canvas-board</code> sample beside the session whose agent opened and read it.</figcaption>
</figure>

The `canvas-flow` sample draws a release pipeline with React Flow (`@xyflow/react`): custom nodes in the colors of the window, links that move into a running step, a minimap and panels of Blueprint cards. Its steps and links live in the plugin: a drag, a new link or **Done** is a call, and `alta flow status --step website --status done` moves a step on from a terminal or an agent.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-canvas-flow.webp" alt="The Pipeline tab of the canvas-flow sample in CodeAlta Desktop: a release pipeline drawn with React Flow, from Commit through the .NET and frontend builds and tests and the website to the NuGet packages and the GitHub release, each step a card with its icon and status, the running .NET tests step selected in a panel with Start, Done and Fail, and a minimap" loading="lazy">
  <figcaption class="small text-secondary mt-2">The pipeline of the <code>canvas-flow</code> sample, drawn with React Flow in the colors of the window.</figcaption>
</figure>

## Status items

Status items and content around the prompt work in both apps:

```csharp
yield return new PluginStatusContribution
{
    Region = PluginUiRegion.SessionStatus,
    Name = "notes-count",
    GetStatus = _ => new PluginStatusItem { Label = "Notes", Text = "3", Command = "notes" },
};

yield return PluginTui.Visual(PluginUiRegion.SessionFooter,
    _ => new Markup("[dim]3 notes[/]"),                                                       // TUI
    _ => PluginRenderResult.FromHtml("<span class=\"alta-tag\">3 notes</span>", "3 notes"),   // desktop
    "notes-footer");
```

`SessionFooter` is above the prompt; `CommandBar` and `SessionStatus` are in the status line. A `PluginRenderResult` shows its richest form: `Html` on the desktop, then `Markdown`, then `Text`. Return null to show nothing. These callbacks run often: keep them fast, with no I/O.

## Buttons

In CodeAlta Desktop a plugin can add buttons with an icon and a label, which run a plugin command or open a plugin canvas. CodeAlta TUI draws none; the command stays in its palette.

```csharp
public override IEnumerable<PluginUiContribution> GetUiContributions()
{
    yield return PluginUi.Button(PluginButtonPlace.TitleBar, "notes", icon: "notebook-pen", label: "Notes") with
    {
        Command = "notes",
        GetState = _ => new PluginButtonState { Badge = Open.Count },
    };
}
```

{.table}
| Place | Where | Limit per plugin |
|---|---|---|
| `TitleBar` | Top right, before the space switch, the zoom and the theme | 2 |
| `Rail` | In the navigation rail, after Issues and before Settings | 1 |
| `ProjectMenu` | A line in a project's menu in the Explorer, for that project | 6 |
| `SessionMenu` | A line in a session's menu, for that session | 6 |

- Name exactly one of `Command` (a command of the plugin) or `Canvas` (a canvas of the plugin, opened for the button's project and session).
- `GetState` gives a badge (a number, `PluginButtonBadge.Dot`, or `PluginButtonBadge.Busy`), a tone, and whether the button is hidden or disabled. It runs often: read a field, no I/O. The window reads it when a project, session or space is selected, when a plugin command ends, and on `Services.Ui.InvalidateButtons()`.
- The icon is a [Lucide](https://lucide.dev/icons) icon name, a brand logo name, or an SVG file of the plugin folder such as `icons/notes.svg` (at most 32 KiB, drawn in the text color).
- An invalid button or one over its limit is left out, and `alta plugin status` says why.

<figure class="alta-figure my-4" style="max-width: 52rem;">
  <img src="{{site.basepath}}/img/alta-desktop-plugin-buttons.webp" alt="The title bar of CodeAlta Desktop with three plugin buttons before the space switch: Statistics, and the two buttons of the canvas-checklist sample with a badge that says 3, above the Checklist tab that the first one opened" loading="lazy">
  <figcaption class="small text-secondary mt-2">Buttons of plugins in the title bar: Statistics, and the two of the <code>canvas-checklist</code> sample with the number of items left.</figcaption>
</figure>

Users hide a button with a right click and turn it back on in **Settings > Plugins**. In a narrow window the buttons fold into one menu.

<figure class="alta-figure my-4" style="max-width: 52rem;">
  <img src="{{site.basepath}}/img/alta-desktop-plugin-buttons-settings.webp" alt="The Plugins page of Settings: under Statistics and under the Canvas checklist plugin, a line for each button with its place, Title bar or Project menu, and a switch" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings > Plugins lists the canvases and the buttons of each plugin, with a switch for each button.</figcaption>
</figure>

## Welcome page cards

A plugin can pin a card on the [welcome page](../workspace/readme.md#welcome-page) of CodeAlta Desktop: a title, an icon, a short fragment and up to three buttons.

```csharp
public override IEnumerable<PluginLandingCardContribution> GetLandingCards()
{
    yield return new PluginLandingCardContribution
    {
        Id = "notes", Title = "Notes", Icon = "notebook-pen",
        GetCard = async (context, cancellationToken) =>
        {
            var notes = await ReadAsync(cancellationToken);
            if (notes.Count == 0) return null;                                   // nothing to say: no card for now
            return PluginLandingCard.Of($"<p>{PluginHtml.Encode(notes[^1].Title)}</p>",
                PluginLandingCardAction.OpenCanvas("Open", "notes") with { Primary = true },
                PluginLandingCardAction.RunCommand("Add", "notes-add")) with { Status = $"{notes.Count} notes" };
        },
    };
}
```

{.table}
| Part | What it is |
|---|---|
| `Id`, `Title`, `Icon`, `Order` | The card. At most 2 cards per plugin |
| `GetCard` | What the card shows now, or `null` to leave it out. It may read a file or a database, within 5 seconds |
| `Html` | A fragment. `PluginHtml.Stat("128", "Sessions")` writes a figure above its label; put several in `<div class="alta-row">` |
| `Status`, `Tone` | A short text beside the title, and its color |
| `Actions` | Up to 3 buttons: `RunCommand` or `OpenCanvas` of the plugin |

`context.SpaceId` is the space of the page. A project plugin also gets `context.ProjectId`: its card shows only in spaces that have that project, and its commands run for it.

The page asks for the card when shown, when a plugin command ends, and on `Services.Ui.InvalidateLandingCards()`. A card that throws or is too slow shows as not loaded, without affecting the others. Honor the cancellation token: the plugin cannot finish stopping until its card callback returns. Cards of archived or removed projects are not asked for.

## Prompt pickers

```csharp
public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
{
    yield return PluginUi.PromptPicker("notes", '!', "Notes", async (context, cancellationToken) =>
        [.. (await FindAsync(context.Query, cancellationToken)).Select(note => new PluginPromptPickerItem { Label = note.Title, InsertText = note.Title + " " })],
        "[!] to insert a note");
}
```

Typing the character at the start of a word opens the picker in both apps; the chosen item replaces the token with `InsertText`. The trigger is a punctuation character other than `@`, `#` and `/`, which CodeAlta uses.

## Timeline cards

`GetSessionEventProjections()` returns cards computed from a session's events. They show in the timeline and are not written to the conversation.

```csharp
new PluginDerivedSessionEvent
{
    EventId = $"notes:{context.SessionId}",
    Markdown = "**Notes** · 3 added",                     // the bold start is the card title
    Html = "<span class=\"alta-tag\">3 added</span>",        // desktop: shown after the title
    DetailSections = [new PluginDerivedSessionEventDetailSection { Header = "Added", Markdown = "- one\n- two" }],
}
```

Keep the `EventId` stable for the same turn so the card updates instead of repeating. A detail section can also carry `Html`. A `data-alta-command` element runs its command for the card's session. For a native TUI card, use `PluginTerminalDerivedSessionEvent` with a `VisualFactory`.

## Canvases

In CodeAlta Desktop a plugin can provide **canvases**: tabs it fills and keeps up to date, such as a checklist, a board or a report. The plugin declares them in `GetCanvases()` and holds their state, so closing a tab loses nothing. The `canvas-checklist` sample is a complete one.

A canvas is found without extra code:

- The **Canvases** page (title bar button after Issues) has a card per canvas, with what is open in the space. **Open** opens it for the project or session in front, or one you pick. **New canvas** asks an agent to write one.
- The search (`Ctrl+P`) has "Open canvas: <title>" for each canvas.
- A project's menu lists project canvases; a session's menu lists session canvases.
- **Settings > Plugins** lists the canvases of each plugin.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-canvases.webp" alt="The Canvases page of CodeAlta Desktop: a card for Statistics, for the board and for the three checklists of the samples, each with its scope, Application, Project or Session, and an Open button" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Canvases page: one card for each canvas, with its scope and <strong>Open</strong>.</figcaption>
</figure>

Agents use `alta canvas`: `list` and `show` read the canvases and what each shows, `invoke` runs an action, and `open`, `focus` and `close` act on a tab. A tab belongs to a space: when an agent opens a canvas for another space, the tab goes there and the window stays where it is.

## Settings page

A plugin whose settings need more than a line of configuration gives them a Settings page: a canvas with `Placement = PluginCanvasPlacement.Settings`. Settings lists it in the **Extensions** group, after Plugins and MCP Servers, and the plugin's row in **Settings > Plugins** gets a gear button to it. It is drawn like a tab (fragment, script, `canvas.Rpc`, Blueprint), under the canvas title and description:

```csharp
yield return new PluginCanvasContribution
{
    Id = "settings",
    Title = "Board",
    Description = "The columns of the board and who sees it.",
    Icon = "kanban",
    Placement = PluginCanvasPlacement.Settings,
    Open = (canvas, ct) =>
    {
        canvas.Rpc.Handle<NoInput, BoardSettings>("board.settings.get", (_, _) => ValueTask.FromResult(_settings));
        canvas.Rpc.Handle<BoardSettings, BoardSettings>("board.settings.set", async (value, ct) => await SaveAsync(value, ct));
        return ValueTask.FromResult(PluginCanvasView.Html("<div></div>") with { Script = PluginScript.File("ui/settings.js") });
    },
};
```

- A settings page has the `Application` scope and one instance, whatever the space.
- A plugin has at most one: a second one, or one of another scope, is left out with a warning in the log.
- It is listed while the plugin runs, and not on the Canvases page, in the search or in menus.
- `Services.Canvases.OpenAsync("settings")`, `alta.host.openCanvas("settings")` and `alta canvas open` open Settings on it.

The [Mobile](mobile.md) plugin has one: **Settings > Extensions > Mobile**.
