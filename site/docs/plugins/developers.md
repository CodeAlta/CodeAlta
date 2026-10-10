---
title: For developers
---

# Plugin development

A source plugin is one C# file that CodeAlta builds and loads. CodeAlta Desktop builds it again while it runs, so a change shows at once. The same plugin runs in CodeAlta Desktop and in CodeAlta TUI.

> [!WARNING]
> Build and load only plugins you trust. Building runs the .NET SDK, NuGet and MSBuild; loading runs the plugin's code inside CodeAlta.

> [!IMPORTANT]
> The plugin API can change between CodeAlta releases.

## Ask an agent to write it

In CodeAlta Desktop, ask a session in your own words:

```text
Write a plugin that shows the current git branch beside the prompt, with a command that lists the last ten commits in a dialog.
```

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-plugin-agent.webp" alt="A session of CodeAlta Desktop that wrote a plugin: its request, the tool calls of the agent, the dialog of the new command with the last ten commits, and the git branch beside the prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session wrote this plugin: the dialog of its command, and the branch beside the prompt.</figcaption>
</figure>

The plugin is for all your projects: the agent creates it under `~/.alta/plugins`. Ask for a plugin *for this project* to keep it in the project instead, under `.alta/plugins`. Everything on this page works the same for both.

The agent reads the `codealta-plugin-runtime` skill, which ships with CodeAlta and has this guidance with sample plugins. Then it works with the `alta plugin` commands of its session:

{.table}
| Command | What it does |
|---|---|
| `alta plugin create <id>` | Writes a first `plugin.cs` under `~/.alta/plugins/<id>`, builds it and starts it. With `--project`, the plugin is created in the project. |
| `alta plugin reload <id>` | Builds the file again and replaces the plugin that runs. A build that fails returns the compiler errors, and the version that ran keeps running. |
| `alta plugin status <id>` | Shows the state of the plugin, its last build, what it adds and the errors it raised. |
| `alta plugin api <name>` | Shows a type of the plugin API with its members. |
| `alta plugin open <id>` | Shows the plugin to you in the code editor. |

The agent calls a tool its plugin adds in the same turn. With the [UI tools](../ui-tools.md), it also runs the command of the plugin in the window and checks what it shows.

CodeAlta TUI loads plugins when it starts: there the agent writes the file and you restart. A session whose [permission mode](../workspace.md#tool-permissions) asks before commands cannot create or build a plugin, since a plugin runs its code.

## Create a plugin

In CodeAlta Desktop, open **Settings > Plugins** and click **New plugin**. Enter an id: CodeAlta creates the folder with a first `plugin.cs`, starts the plugin and opens it in the code editor. The plugin is for all your projects; choose **Project** at the top of the page first to create it in the selected project.

You can also create the folder yourself. A plugin is one folder with a `plugin.cs` file:

{.table}
| Scope | File |
|---|---|
| All projects | `~/.alta/plugins/<package-id>/plugin.cs` |
| One project | `<project>/.alta/plugins/<package-id>/plugin.cs` |

The package id uses letters, digits, `.`, `_` and `-`, and starts with a letter or digit.

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

In CodeAlta Desktop, click **Build and reload** on the row of the plugin in **Settings > Plugins**: CodeAlta builds the plugin and loads it, and `/hello` appears among the commands of the search (`Ctrl+P`). In CodeAlta TUI, restart.

A plugin is a public class that inherits `PluginBase` and has a public parameterless constructor. The `[Plugin]` attribute is optional. One file can declare several plugins.

### What CodeAlta does with the folder

- It runs `dotnet build plugin.cs` with the .NET 10 SDK. No project file is needed.
- It writes `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` and `global.json` in the plugin root. Do not create or edit them.
- It writes a `.gitignore` beside them, so a project keeps only the sources of its plugins in git.
- It references `CodeAlta.Plugins.Abstractions`, `CodeAlta.Plugins.Tui` and the `XenoAtom.Terminal.UI` packages for you, in both apps.
- When it starts, it builds again a plugin whose source changed.

Add NuGet packages with `#:package Name@Version` at the top of `plugin.cs`, and more source files with `#:include`. An optional `README.md` beside `plugin.cs` describes the plugin in the plugin list.

A project plugin is loaded when CodeAlta is started in that project.

## Edit, build and reload

In CodeAlta Desktop, **Settings > Plugins** is where you work on a source plugin, whether it is for all your projects (**User**) or for one (**Project**).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-plugins.webp" alt="The Plugins page of Settings with a source plugin, its Reload and Edit buttons, and New plugin" loading="lazy">
  <figcaption class="small text-secondary mt-2">The last row is a source plugin, with a button to build and reload it and one to edit it.</figcaption>
</figure>

The row of a source plugin has the path of its folder, three buttons and a switch:

- **Build and reload** builds the plugin and replaces the one that runs. Its commands, shortcuts, status items and pickers change at once.
- **Edit in the code editor** opens the folder of the plugin in the code editor, in a **Plugin** tab.
- The red **Remove** button stops the plugin and moves its folder to the trash of your system, after you confirm. The plugins that ship with CodeAlta cannot be removed: turn them off with their switch.
- The switch starts or stops the plugin at once.

When a build fails, the page shows the compiler errors under the plugin, with their line, and the version that ran keeps running. **Source changed** marks a plugin whose file is not the one that runs.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-plugin-editor.webp" alt="The code editor of CodeAlta Desktop opened on the folder of a plugin, in a Plugin tab" loading="lazy">
  <figcaption class="small text-secondary mt-2">The folder of a plugin in the code editor.</figcaption>
</figure>

A reload starts the plugin again: the fields of its class are new. Keep data with `Services.State` (see [Lifecycle, background work and data](#lifecycle-background-work-and-data)).

## One plugin, two apps

A plugin starts in both apps unless it says otherwise:

```csharp
[Plugin("notes", Frontends = PluginFrontends.Desktop)]   // or Terminal; the default is All
```

An app does not start a plugin that does not support it, and says so in its plugin list.

Most of the API is portable: you write it once and each app shows it its own way. Where the look matters, you give two forms: an HTML fragment for the desktop app and `XenoAtom.Terminal.UI` controls for the TUI. `Context.Host.Frontend` tells which app is running.

{.table}
| What | Portable | Desktop form | TUI form |
|---|---|---|---|
| Notification, confirm, input, selection, text editor | `context.Ui.NotifyAsync`, `ConfirmAsync`, `InputAsync`, `SelectAsync`, `EditTextAsync` | | |
| Dialog with your own content | | `PluginUi.HtmlDialog` | `PluginTui.CustomDialog` |
| Content around the prompt | `PluginUi.Content` with Markdown or text | `PluginRenderResult.FromHtml` | `PluginTui.Visual` |
| Status item | `PluginStatusContribution` | `Command` makes it clickable | |
| Button | | `PluginUi.Button`, with a `Command` or a `Canvas` | |
| Prompt picker | `PluginUi.PromptPicker` | | |
| Timeline card | `PluginDerivedSessionEvent.Markdown` | `Html` | `PluginTerminalDerivedSessionEvent.VisualFactory` |

The desktop app ignores terminal controls and the TUI ignores HTML, so a result that carries both forms works in both.

## Contribution points

Override only the methods the plugin needs.

{.table}
| Method | Adds | Desktop | TUI |
|---|---|---|---|
| `GetCommands()` | Commands for the search or the palette, the `/` menu and shortcuts | yes | yes |
| `GetUiContributions()` | Status items and content around the prompt; buttons in the window | yes | yes (no buttons) |
| `GetPromptPickers()` | A picker opened by a character typed in the prompt | yes | yes |
| `GetSessionEventProjections()` | Cards in the session timeline | yes | yes |
| `GetAgentTools()` | Tools the model can call | yes | yes |
| `GetAltaCommands()` | Commands under the in-session `alta` tool | yes | yes |
| `GetSystemPromptContributions()` | Text added to the system or developer prompt | yes | yes |
| `GetInstructionProcessors()` | Changes to the final instructions of a session | yes | yes |
| `GetResources()` | Skills shipped with the plugin | yes | yes |
| `OnBeforeAgentRunAsync` | Tools and messages for one run; can cancel the run | yes | yes |
| `OnAgentEventAsync` | Sees the events of the sessions | yes | yes |
| `OnToolCallAsync`, `OnToolResultAsync` | Sees and changes the calls of the tools that plugins add | yes | yes |
| `GetPromptProcessors()` | Changes a prompt before it is sent | no | yes |
| `GetCompactionContributions()` | Hooks of a manual compaction | no | yes |
| `GetCommandLineContributions()` | Commands of the `altatui` command line | no | yes |

A contribution method is called once when the plugin starts. The factories `Command`, `PluginUi`, `PluginTui`, `Prompt`, `AgentTool`, `Resources` and `Startup` build the common contributions.

To look a type up, ask a session for `alta plugin api <name>`: it prints the type with its members, from the version of CodeAlta that runs.

## Commands

```csharp
yield return Command.Shell("note-add", "Adds a note.", AddNoteAsync) with
{
    Label = "Notes: add",
    KeyBinding = new PluginKeyBinding(new PluginKeyGesture(PluginKey.F9)),
};
```

- `Command.Shell` is always available, `Command.Session` needs a selected session, and `Command.Prompt` belongs to the prompt editor.
- A command takes no arguments. Ask the user with `context.Ui`.
- Return `PluginCommandResult.Handled`, `Cancelled`, or `Message("…")` to show a message. Set `PromptText` to send a prompt, with `EnqueuePrompt = true` to queue it.
- `context.Sessions` and `context.Prompts` give the session and the prompt draft the command was started from.
- A key binding is one stroke, or `Ctrl+G` followed by a second stroke. A key CodeAlta already uses keeps its meaning. On the desktop a single letter needs `Ctrl` or `Alt`.

## Dialogs

The portable dialogs work in both apps:

```csharp
if (!await context.Ui.ConfirmAsync("Notes", "Delete all notes?", cancellationToken)) return PluginCommandResult.Cancelled;
var title = await context.Ui.InputAsync("Title", "Untitled", cancellationToken);   // null when cancelled
var kind = await context.Ui.SelectAsync("Kind", items, cancellationToken);
var text = await context.Ui.EditTextAsync("Note", "First line", cancellationToken);
```

For a dialog with its own content, build the request for the running app:

```csharp
var request = Context.Host.Frontend == PluginFrontends.Desktop
    ? PluginUi.HtmlDialog("Notes", html, closeButton) with { OnAction = HandleActionAsync }
    : PluginTui.CustomDialog("Notes", visual) with { Buttons = [closeButton] };
var response = await context.Ui.ShowDialogForResultAsync(request, cancellationToken);
```

`response.ButtonName` is the button that closed the dialog. `response.Values` holds the named fields of an HTML dialog. In the TUI, read your own controls.

## HTML fragments in the desktop app

A fragment is plain HTML that CodeAlta inserts in its window. Before that, CodeAlta sanitizes it and gives its elements the look of the app's own components:

{.table}
| Write | Shown as |
|---|---|
| `<button>` | A button of the app; with a tone class, in that tone |
| `<input>`, `<textarea>`, `<select>` | A field of the app |
| `<table>` | A compact table |
| `class="alta-tag"`, `class="alta-callout"`, `class="alta-card"` | A tag, a callout, a card |
| `PluginHtml.Markdown(text)` | Markdown rendered as in the timeline: headings, lists, tables |
| `PluginHtml.Code(code, "csharp")` | Source code with the colors of its language |
| `PluginHtml.Diagram(text)` | A [Mermaid](https://mermaid.js.org/) diagram |

A plugin has no JavaScript in the window, and creates no component of the app: its code is C#. Nothing in a fragment runs: scripts, styles, event handlers, images and forms are removed, and a link only opens a web page (`http` or `https`) in the browser of the user. A fragment asks the window to act with attributes, and the plugin answers in C#:

{.table}
| Attribute | Effect |
|---|---|
| `data-alta-command="name"` | A click runs the command `name` of the plugin. Works wherever a fragment is shown. |
| `data-alta-action="name"` | In a dialog, a click, Enter in a field, or a changed select calls `OnAction` with that name. |
| `data-alta-value="…"` | The value passed with the action. |
| `name="…"` on a field | The field is returned in `Values`: text, `true` or `false` for a checkbox, the chosen radio value. |

`OnAction` runs in the plugin and returns what the dialog does next:

```csharp
OnAction = (action, cancellationToken) =>
{
    if (action.Name == "remove") notes.RemoveAt(int.Parse(action.Value!));
    return ValueTask.FromResult(PluginDialogActionResult.Update(BuildHtml()));   // or KeepOpen, or CloseDialog("button")
},
```

Classes you can use:

{.table}
| Class | Effect |
|---|---|
| `alta-row`, `alta-column` | Lay out children in a row or a column |
| `alta-grow` | In a row, takes the space that is left: a field beside a button |
| `alta-field` | On a `label`: the label above its field, which takes the width |
| `alta-primary`, `alta-success`, `alta-warning`, `alta-danger`, `alta-muted` | Tone of a button, a tag, a callout or text |
| `alta-tag` | A small rounded label |
| `alta-callout` | A highlighted block |
| `alta-card` | A bordered block |

Other classes are removed. Allowed elements are text and structure (`p`, `div`, `span`, headings, lists, `table`, `pre`, `code`, `details`, `a`, …) and fields (`button`, `input`, `select`, `textarea`, `label`, `fieldset`, `progress`, `meter`).

Write text with `PluginHtml.Encode(text)`. `PluginHtml.CommandButton` and `PluginHtml.ActionButton` write the two kinds of buttons.

### Markdown, code and diagrams

A fragment cannot load a library. For Markdown, highlighted code and diagrams it uses those of the window:

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

Each helper writes an element of class `alta-markdown` whose text is Markdown, which you can also write yourself. The links of that Markdown are followed as in a message: a web link opens in the browser, and a link to a file in the code editor. The code editor, the terminal, tabs, trees, icons and images of the app are not available to a fragment.

Content around the prompt and timeline cards also take Markdown directly, in both apps: `PluginRenderResult.Markdown`, `PluginDerivedSessionEvent.Markdown`.

## Status items and content around the prompt

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

`SessionFooter` is above the prompt. `CommandBar` and `SessionStatus` are in the status line. A `PluginRenderResult` is shown in its richest form: `Html` on the desktop, then `Markdown`, then `Text`. Return null to show nothing.

These callbacks run often. Keep them fast and do no I/O in them.

## Buttons in the window

CodeAlta Desktop lets a plugin put its own buttons in the window. A button has an icon and a label, and it runs a command of your plugin or opens a canvas of your plugin. CodeAlta TUI draws no button; the command stays in its palette.

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
| Place | Where | Limit for one plugin |
|---|---|---|
| `TitleBar` | At the top right, before the space switch, the zoom and the theme | 2 |
| `Rail` | In the navigation rail, after Issues and before Settings | 1 |
| `ProjectMenu` | A line in the menu of a project in the Explorer, asked about that project | 6 |
| `SessionMenu` | A line in the menu of a session, asked about that session | 6 |

Name exactly one of `Command` (a command of the same plugin) and `Canvas` (a canvas of the same plugin; no code runs, the window opens it for the project and session of the button). `GetState` gives a badge (a number, `PluginButtonBadge.Dot`, or `PluginButtonBadge.Busy` for a small ring), a tone, and whether the button is hidden or disabled. Like status items, it runs often: read a field, do no I/O. The window reads it when a project, session or space is selected, when a command of your plugin ends, and when you call `Services.Ui.InvalidateButtons()`.

The icon is the name of any [Lucide](https://lucide.dev/icons) icon, the name of a brand logo, or an SVG file of your plugin folder such as `icons/notes.svg` (at most 32 KiB, drawn in the color of the text). A button that is invalid or over its limit is left out, and `alta plugin status` says why.

People can hide any button with a right click, and turn each one back on in Settings > Plugins. In a narrow window the buttons fold into one menu.

## Prompt pickers

```csharp
public override IEnumerable<PluginPromptPickerContribution> GetPromptPickers()
{
    yield return PluginUi.PromptPicker("notes", '!', "Notes", async (context, cancellationToken) =>
        [.. (await FindAsync(context.Query, cancellationToken)).Select(note => new PluginPromptPickerItem { Label = note.Title, InsertText = note.Title + " " })],
        "[!] to insert a note");
}
```

Typing the character at the start of a word opens the picker in both apps. The chosen item replaces the token with `InsertText`. The trigger is a punctuation character other than `@`, `#` and `/`, which CodeAlta uses.

## Timeline cards

`GetSessionEventProjections()` returns cards computed from the events of a session. They are shown in the timeline and are not written to the conversation.

```csharp
new PluginDerivedSessionEvent
{
    EventId = $"notes:{context.SessionId}",
    Markdown = "**Notes** · 3 added",                     // the bold start is the card title
    Html = "<span class=\"alta-tag\">3 added</span>",        // desktop: shown after the title
    DetailSections = [new PluginDerivedSessionEventDetailSection { Header = "Added", Markdown = "- one\n- two" }],
}
```

Keep the `EventId` stable for the same turn so the card is updated, not duplicated. A detail section can also carry `Html`. For a native TUI card, use `PluginTerminalDerivedSessionEvent` with a `VisualFactory`.

## Canvases

In CodeAlta Desktop a plugin can provide **canvases**: tabs that it fills and keeps up to date, such as a checklist, a board or a report. The plugin declares them in `GetCanvases()` and holds their state, so closing a tab loses nothing. The `canvas-checklist` sample is a complete one.

You do not write anything to make a canvas easy to find:

- The **Canvases** page, opened with the button of the title bar after Issues, has a card for each canvas, with what is open in the space you look at. **Open** opens it for the project or the session in front, or for the one you pick on its card. **New canvas** starts a request for an agent to write one.
- The search (`Ctrl+P`) has a command "Open canvas: <title>" for each canvas.
- The menu of a project row lists the canvases about a project, and the menu of a session row those about a session.
- Settings > Plugins lists the canvases of each plugin.

Agents use `alta canvas`: `list` and `show` read what canvases exist and what each shows, `invoke` runs an action, and `open`, `focus` and `close` act on a tab. A tab belongs to a space. When an agent opens a canvas for a space that you are not looking at, the tab is added to that space and the window stays where it is.

## Agent tools and `alta` commands

`GetAgentTools()` adds tools the model can call: wrap an `AgentToolDefinition` with `AgentTool.Create`. `OnToolCallAsync` and `OnToolResultAsync` see and change the calls of the tools that plugins add.

A session has the tools of a plugin from its next prompt. The session that created or reloaded the plugin has them in the same turn.

`GetAltaCommands()` adds commands under the in-session `alta` tool. A plugin can also run `alta` commands:

```csharp
var result = await Services.Alta.InvokeAsync(["session", "create", "--project", projectId], cancellationToken: cancellationToken);
```

The result has the JSONL output, the exit code and an error summary.

## Prompts and instructions

- `GetSystemPromptContributions()` adds text to the system or developer prompt: `Prompt.Developer("…")`.
- `GetPromptProcessors()` can change or cancel the user's prompt before it is sent, in CodeAlta TUI.
- `GetInstructionProcessors()` can change the final instructions after CodeAlta has composed them. CodeAlta records which plugin changed them.

Keep added text short: it uses model context on every turn.

## Resources

```csharp
public override IEnumerable<PluginResourceContribution> GetResources()
{
    yield return Resources.SkillRoot("skills");
}
```

Paths are relative to the plugin folder. The resources of a project plugin apply to that project only.

## Lifecycle, background work and data

Override `InitializeAsync`, `OnActivatedAsync`, `OnDeactivatingAsync` and `DisposeAsync` only when the plugin owns something to set up or release.

- Start background work with `Tasks.Run(...)` so CodeAlta can cancel it when the plugin is reloaded or unloaded. Do not use an untracked `Task.Run`.
- `Services.State` keeps the data of the plugin as JSON, between runs and reloads:

  ```csharp
  var notes = await Services.State.ReadJsonAsync<List<string>>(PluginStateScope.User, "notes", cancellationToken) ?? [];
  await Services.State.WriteJsonAsync(PluginStateScope.User, "notes", notes, cancellationToken);
  ```

  `PluginStateScope.User` stores under `~/.alta/plugin-data/`, and `PluginStateScope.Project` under `<project>/.alta/plugin-data/`.
- `Services.Database` gives the plugin tables of its own in the SQLite database of CodeAlta, for data that is queried or that grows. Every table name starts with `Services.Database.TablePrefix`, which CodeAlta derives from the plugin, and `MigrateAsync` runs the changes to the tables once for each version:

  ```csharp
  var database = Services.Database;
  await database.MigrateAsync(1, async (connection, from, to, token) =>
  {
      await using var command = connection.CreateCommand();
      command.CommandText = $"CREATE TABLE {database.TablePrefix}notes (id INTEGER PRIMARY KEY, text TEXT)";
      await command.ExecuteNonQueryAsync(token);
  }, cancellationToken);
  ```

  `ReadAsync` reads without waiting for anyone, and `WriteAsync` writes in one transaction, in turn with the other writers: keep a write short. The data is kept when the plugin is turned off.
- `Logger` writes to the CodeAlta log.
- Do not keep static references to host objects, and do not put secrets in plugin source.

## When a plugin does not start

{.table}
| | Desktop | TUI |
|---|---|---|
| While it builds | The start-up screen names the plugin being built | The console shows the build before the interface |
| Build or load failure | A notice at start, and the compiler errors under the plugin in **Settings > Plugins** | The error in the console and in `/plugins` |
| Not supported in this app | Marked in **Settings > Plugins** | Listed in `/plugins` |
| Full log | **Settings > Application Logs** | `~/.alta/logs/codealta.log` |

In a session, `alta plugin status <id>` returns the same errors with their file, line and column.

Disable one plugin in the plugin list or in the configuration:

```toml
[plugins.hello]
enabled = false
```

To start without any plugin, set `CODEALTA_DISABLE_PLUGINS=1`. The TUI also accepts `--no-plugins` and `--plugin-safe-mode`.

## Samples

The `codealta-plugin-runtime` skill ships complete plugins that CodeAlta's tests build and load:

{.table}
| Sample | Shows |
|---|---|
| `todo` | A complete plugin: commands, an HTML dialog with actions, a status item, a picker, tools, an `alta` command, prompt text, saved data |
| `hello-command` | A command |
| `desktop-and-terminal` | One plugin for both apps: portable dialogs, an HTML dialog with actions, a status item, content above the prompt, a prompt picker |
| `saved-data` | Data kept between runs with `Services.State` |
| `canvas-checklist` | A tab that the plugin provides, found from the Canvases page, the search and `alta canvas`: a checklist of the application, of a project and of a session, ticked from the page, a command or an agent; buttons in the title bar and in the menu of a project |
| `report-dialog` | A dialog with Markdown, a diagram and highlighted code |
| `agent-tool` | A tool the model calls |
| `alta-command` | A command of the `alta` tool |
| `timeline-card` | A card in the timeline, computed from the events of a session |
| `ui-status`, `ui-all-regions` | Status items and content in every region |
| `prompt-guidance` | Text added to the prompt |
| `instruction-path-normalizer` | A change to the final instructions |
| `background-task` | Tracked background work |
| `package-reference` | A NuGet package |
| `skill-root` | A skill shipped by a plugin |
| `multi-plugin-assembly` | Several plugins in one file |
