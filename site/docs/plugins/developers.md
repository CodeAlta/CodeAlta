---
title: For developers
---

# Plugin development

A source plugin is one C# file that CodeAlta builds and loads when it starts. The same plugin runs in CodeAlta Desktop and in CodeAlta TUI.

> [!WARNING]
> Build and load only plugins you trust. Building runs the .NET SDK, NuGet and MSBuild; loading runs the plugin's code inside CodeAlta.

> [!IMPORTANT]
> The plugin API can change between CodeAlta releases.

CodeAlta ships a `codealta-plugin-runtime` skill with this guidance and with sample plugins. Ask an agent to "write a CodeAlta plugin that…" and it uses the skill.

## Create a plugin

Create one folder per plugin, with a `plugin.cs` file:

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

Restart CodeAlta. It builds the plugin, loads it, and `/hello` appears in the command palette.

A plugin is a public class that inherits `PluginBase` and has a public parameterless constructor. The `[Plugin]` attribute is optional. One file can declare several plugins.

### What CodeAlta does with the folder

- It runs `dotnet build plugin.cs` with the .NET 10 SDK. No project file is needed.
- It writes `Directory.Build.props`, `Directory.Build.targets`, `Directory.Packages.props` and `global.json` in the plugin root. Do not create or edit them.
- It references `CodeAlta.Plugins.Abstractions`, `CodeAlta.Plugins.Tui` and the `XenoAtom.Terminal.UI` packages for you, in both apps.
- It rebuilds a plugin whose source changed at the next start.

Add NuGet packages with `#:package Name@Version` at the top of `plugin.cs`, and more source files with `#:include`. An optional `README.md` beside `plugin.cs` describes the plugin in the plugin list.

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
| Prompt picker | `PluginUi.PromptPicker` | | |
| Timeline card | `PluginDerivedSessionEvent.Markdown` | `Html` | `PluginTerminalDerivedSessionEvent.VisualFactory` |

The desktop app ignores terminal controls and the TUI ignores HTML, so a result that carries both forms works in both.

## Contribution points

Override only the methods the plugin needs.

{.table}
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

A fragment is plain HTML that CodeAlta inserts in its window. Before that, CodeAlta sanitizes it and gives buttons, fields and tables the look of the app.

Nothing in a fragment runs: scripts, styles, event handlers, images and forms are removed, and links are shown but not followed. A fragment cannot call the app's code. It asks the window to act with attributes:

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
| `alta-primary`, `alta-success`, `alta-warning`, `alta-danger`, `alta-muted` | Tone of a button, a tag, a callout or text |
| `alta-tag` | A small rounded label |
| `alta-callout` | A highlighted block |

Other classes are removed. Allowed elements are text and structure (`p`, `div`, `span`, headings, lists, `table`, `pre`, `code`, `details`, `a`, …) and fields (`button`, `input`, `select`, `textarea`, `label`, `fieldset`, `progress`, `meter`).

Write text with `PluginHtml.Encode(text)`. `PluginHtml.CommandButton` and `PluginHtml.ActionButton` write the two kinds of buttons.

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

## Agent tools and `alta` commands

`GetAgentTools()` adds tools the model can call: wrap an `AgentToolDefinition` with `AgentTool.Create`. `OnToolCallAsync` and `OnToolResultAsync` can observe or change any tool call.

`GetAltaCommands()` adds commands under the in-session `alta` tool. A plugin can also run `alta` commands:

```csharp
var result = await Services.Alta.InvokeAsync(["session", "create", "--project", projectId], cancellationToken: cancellationToken);
```

The result has the JSONL output, the exit code and an error summary.

## Prompts and instructions

- `GetSystemPromptContributions()` adds text to the system or developer prompt: `Prompt.Developer("…")`.
- `GetPromptProcessors()` can change or cancel the user's prompt before it is sent.
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

## Lifecycle, background work and state

Override `InitializeAsync`, `OnActivatedAsync`, `OnDeactivatingAsync` and `DisposeAsync` only when the plugin owns something to set up or release.

- Start background work with `Tasks.Run(...)` so CodeAlta can cancel it when the plugin unloads. Do not use an untracked `Task.Run`.
- `Services.State` stores plugin data.
- `Logger` writes to the CodeAlta log.
- Do not keep static references to host objects, and do not put secrets in plugin source.

## When a plugin does not start

{.table}
| | Desktop | TUI |
|---|---|---|
| While it builds | The start-up screen names the plugin being built | The console shows the build before the interface |
| Build or load failure | A notice at start, and the error under the plugin in **Settings > Plugins** | The error in the console and in `/plugins` |
| Not supported in this app | Marked in **Settings > Plugins** | Listed in `/plugins` |
| Full log | **Settings > Application Logs** | `~/.alta/logs/codealta.log` |

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
| `hello-command` | A command |
| `desktop-and-terminal` | One plugin for both apps: portable dialogs, an HTML dialog with actions, a status item, content above the prompt, a prompt picker |
| `ui-status`, `ui-all-regions` | Status items and content in every region |
| `prompt-guidance` | Text added to the prompt |
| `instruction-path-normalizer` | A change to the final instructions |
| `background-task` | Tracked background work |
| `package-reference` | A NuGet package |
| `skill-root` | A skill shipped by a plugin |
| `multi-plugin-assembly` | Several plugins in one file |
