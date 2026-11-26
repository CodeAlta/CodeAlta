---
name: codealta-plugin-runtime
description: Create, build, reload, test or troubleshoot a CodeAlta plugin that adds commands, UI, agent tools, alta commands, settings or session hooks.
---

# CodeAlta plugins

A source plugin is a `plugin.cs` in its own folder. CodeAlta builds and loads it without a project file. Build and load only plugins you trust: building runs the SDK, NuGet and MSBuild, and the plugin runs inside CodeAlta with the user's rights.

## Work on a plugin

Use the `alta` tool in CodeAlta Desktop:

1. **Create.** `alta plugin create <id>` writes, builds and starts a starter plugin in `~/.alta/plugins/<id>/`. Use `--project` for `<project>/.alta/plugins/<id>/`, where the project is the one CodeAlta started in. The result's `file` is the file to edit. Use `--no-start` to write the starter without building or loading it.
2. **Edit and build.** Keep the `[Plugin("<id>")]` key. Look up the types in the [contribution index](#contribution-index) and read the closest sample. `alta plugin build <id>` checks compilation without replacing the running plugin.
3. **Reload.** `alta plugin reload <id>` builds and loads the new source. When `change` is `buildFailed`, the old plugin keeps running: fix the compiler errors in `build.diagnostics`. For `startFailed`, read `diagnostics`. Use `--global` or `--project` on build, reload and status if both scopes have the same id.
4. **Verify.** `alta plugin status <id>` lists contributions and callback errors. Exercise what changed, not just the build. In Desktop, follow [Look at the window](#look-at-the-window); an agent tool can be called in the turn that reloaded it, while other sessions get it at their next prompt. Prompt contributions take effect on the next prompt.
5. **Report.** Tell the user how to use what you added: the `/command`, the shortcut or the place it appears.

CodeAlta TUI loads source plugins at startup: put `plugin.cs` in the appropriate plugin folder and restart. `alta plugin list`, `status` and `api` work there too. For less common operations, use `alta plugin --help`.

## Look at the window

In a CodeAlta Desktop session, `alta ui activate` gives the session the tools for its own window. Use `take_snapshot` to find controls by `uid`, `click` or `fill` to exercise them, and `take_screenshot` for visual checks. To run a plugin command, open the palette with `Control+P`, type the command name and press `Enter`. Open a session to see content around the prompt. For a canvas, `alta canvas open <id>` opens the tab; `alta canvas show <id>` describes it and lists its actions. Close dialogs you opened. If a command fails, read `alta plugin status <id>` for the exception and source line.

## Shape of a plugin

- Use a public class with a public parameterless constructor, derived from `PluginBase` in `CodeAlta.Plugins.Abstractions`. Give it a unique `[Plugin("<id>")]` key matching its folder. Override only the contributions it needs.
- The host reads the contribution list at startup. Return a fixed set and compute changing values in callbacks. Keep UI callbacks cheap: read state already in memory, not files or the network. Reload creates a new instance; keep data that must survive through `Services.State`.
- Use `Frontends` on `[Plugin(...)]` when a plugin is for one host. `PluginFrontends.All` covers Desktop and TUI, not the headless CLI; include `PluginFrontends.Cli` explicitly for a plugin that supports it.
- Add a NuGet package with `#:package Name@Version`. Do not edit the build files CodeAlta generates in the root that holds the plugins. Keep secrets out of source and logs.

## API and traps

`alta plugin api <type>` gives the signatures and XML docs of the running version, including the plugin abstractions, terminal controls and the `CodeAlta.Agent` types they use. A partial type name or a member name also works. It does **not** describe JavaScript or third-party APIs. For workflow and C# examples, read [Plugin development](https://codealta.io/docs/plugins/developers/); for UI behavior, read [Plugin UI](https://codealta.io/docs/plugins/ui/). Both ship in the user guide as `plugins/developers.md` and `plugins/ui.md`; find them offline with `alta search "Plugin development" --kind doc`.

- **Tool hooks.** If a pre-call check must not be skipped, override `ToolHooksFailClosed` to return `true`. Otherwise a throw or a timeout is logged and the call goes on. Honor the cancellation token and the call's capabilities (`CanBlock`, `CanModifyArguments`); use `ShellCommand` and `ModifiedFiles` rather than parsing the tool arguments yourself. Hooks do not bypass CodeAlta's permissions or sandbox. Declare `StartsPrograms` for an agent tool that can run arbitrary commands.
- **HTML.** A fragment is sanitized, inert HTML: `<script>`, event attributes and inline styles are removed. Encode user text with `PluginHtml.Encode`. Use `data-alta-command` or, in a dialog, `data-alta-action` to reach C#. Supply Markdown or a terminal control for TUI; it does not render HTML.
- **Canvases and scripts.** A canvas is a Desktop tab that views state owned by the plugin. It can show a fragment or a JavaScript module supplied separately with `PluginScript`; only a canvas script can call C# through `alta.rpc`. For any plugin JavaScript, read the [script API](references/script-api.md) and [library index](references/script-libraries.md) first. Import the libraries supplied by the window; do not install or bundle another copy. Use `alta.html` to sanitize HTML in a module.
- **Settings.** Declare ordinary user preferences with `GetSettings()` and read them through `Services.Settings`, not by opening the config file. Do not grant agents write access to settings that protect data or access. For a custom Settings page, use a canvas with `PluginCanvasPlacement.Settings`.
- **Logging.** Add `using XenoAtom.Logging;` for extension methods such as `Logger.Info(...)`.

## Contribution index

Pass the API type to `alta plugin api`, then read `plugin.cs` in the linked sample folder. Sample paths are relative to this skill. For points without a shipped sample, use the linked developer guide where available.

| Add | Override or entry | API type | Sample or guide |
| --- | --- | --- | --- |
| Command or shortcut | `GetCommands()` | `PluginCommandContribution` | [hello-command](samples/hello-command/) |
| Dialog from a command | `context.Ui.ShowDialogForResultAsync` | `PluginDialogRequest` | [report-dialog](samples/report-dialog/), [desktop-and-terminal](samples/desktop-and-terminal/) |
| Status or content around the prompt | `GetUiContributions()` | `PluginUiContribution` | [ui-status](samples/ui-status/), [ui-all-regions](samples/ui-all-regions/) |
| Window or menu button | `GetUiContributions()` | `PluginButtonContribution` | [canvas-checklist](samples/canvas-checklist/) |
| Canvas | `GetCanvases()` | `PluginCanvasContribution` | [canvas-checklist](samples/canvas-checklist/), [canvas-board](samples/canvas-board/), [canvas-flow](samples/canvas-flow/), [canvas-plot](samples/canvas-plot/) |
| Welcome page card | `GetLandingCards()` | `PluginLandingCardContribution` | [landing-card](samples/landing-card/) |
| Setting | `GetSettings()` | `PluginSettingContribution` | [settings](samples/settings/) |
| Prompt picker | `GetPromptPickers()` | `PluginPromptPickerContribution` | [todo](samples/todo/) |
| Prompt editor attachment | `GetPromptEditorContributions()` | `PluginPromptEditorContribution` | — |
| Timeline card | `GetSessionEventProjections()` | `PluginSessionEventProjectionContribution` | [timeline-card](samples/timeline-card/) |
| Agent tool | `GetAgentTools()` | `PluginAgentToolContribution` | [agent-tool](samples/agent-tool/) |
| In-session `alta` command | `GetAltaCommands()` | `PluginAltaCommandContribution` | [alta-command](samples/alta-command/) |
| System or developer prompt text | `GetSystemPromptContributions()` | `PluginSystemPromptContribution` | [prompt-guidance](samples/prompt-guidance/) |
| Final instruction processor | `GetInstructionProcessors()` | `PluginInstructionProcessorContribution` | [instruction-path-normalizer](samples/instruction-path-normalizer/) |
| Shipped skill | `GetResources()` | `PluginResourceContribution` | [skill-root](samples/skill-root/) |
| Automation trigger | `GetAutomationTriggers()` | `PluginAutomationTriggerContribution` | [automation-trigger](samples/automation-trigger/) |
| Code Mode library script | `GetScripts()` | `PluginScriptContribution` | [automation-trigger](samples/automation-trigger/) |
| Credential provider | `GetCredentialProviders()` | `PluginCredentialProviderContribution` | [Credential providers](https://codealta.io/docs/plugins/developers/#credential-providers) |
| Model provider | `GetModelProviders()` | `PluginModelProviderContribution` | — |
| Confidential-session provider boundary | `GetProviderBoundary` | `PluginProviderBoundaryContext` | — |
| Tool hook | `OnToolCallAsync`, `OnToolResultAsync` | `PluginToolCallContext`, `PluginToolResultContext` | [Tool hooks: complete PowerShell check sample](https://codealta.io/docs/plugins/developers/#tool-hooks) |
| Run start or end hook | `OnBeforeAgentRunAsync`, `OnAgentRunEndedAsync` | `PluginBeforeAgentRunContext`, `PluginAgentRunEndedContext` | [Continue after a run](https://codealta.io/docs/plugins/developers/#continue-after-a-run) |
| Event observer | `OnAgentEventAsync` | `PluginAgentEventContext` | [Contribution points and hooks](https://codealta.io/docs/plugins/developers/#contribution-points-and-hooks) |
| User prompt processor | `GetPromptProcessors()`, `OnPromptSubmittingAsync` | `PluginPromptProcessorContribution`, `PluginPromptSubmittingContext` | [Prompts and resources](https://codealta.io/docs/plugins/developers/#prompts-and-resources) |
| Manual compaction hooks | `GetCompactionContributions()` | `PluginCompactionContribution` | [Compaction hooks](https://codealta.io/docs/plugins/developers/#compaction-hooks) |
| Startup contribution | `GetStartupContributions()` | `PluginStartupContribution` | — |
| TUI command-line command | `GetCommandLineContributions()` | `PluginBase` | — |
| Persistent data | `Services.State` | `IPluginStateStore` | [saved-data](samples/saved-data/) |
| SQLite tables | `Services.Database` | `IPluginDatabase` | [Lifecycle and data](https://codealta.io/docs/plugins/developers/#lifecycle-and-data) |
| Run `alta` commands in-process | `Services.Alta` | `IPluginAltaService` | [Agent tools and alta](https://codealta.io/docs/plugins/developers/#agent-tools-and-alta) |
| Assigned spaces and other host services | `Services.Spaces`, `Services` | `IPluginServices` | [Lifecycle and data](https://codealta.io/docs/plugins/developers/#lifecycle-and-data) |
| Plugin-lifetime work | `Tasks.Run` | `IPluginTaskService` | [background-task](samples/background-task/) |

For source layout, see [package-reference](samples/package-reference/) and [multi-plugin-assembly](samples/multi-plugin-assembly/).
