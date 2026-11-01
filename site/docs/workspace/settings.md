---
title: Settings
---

# Settings

Settings holds your preferences and the pages that manage providers, models, prompts, skills, plugins, MCP servers and permissions.

## Settings window

Open it with `Ctrl+,`, the gear of the title bar, `Ctrl+G Ctrl+W` or `/settings`. CodeAlta Desktop has one window with a page per area: **Appearance**, **General**, **Windows**, **Speech**, **Sessions**, **Permissions**, **Code Mode**, **Providers**, **Models**, **Agent prompts**, **Skills**, **Plugins**, **MCP Servers**, **Configuration file**, **Application Logs** and **About**, plus a page for each [space](../spaces.md). It opens on Appearance, and closes with `Esc`, its close button or a click outside it.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-settings.webp" alt="CodeAlta Desktop Settings window showing the Appearance page" loading="lazy">
  <figcaption class="small text-secondary mt-2">Settings groups visual preferences in Appearance, language and startup behavior in General, and confirmations in Permissions.</figcaption>
</figure>

What you create in Settings (a prompt, a skill, a plugin, an MCP server, a color scheme, a kind of pull request) has a red **Remove** button there, which asks first.

### Search settings

Settings opens with the focus in **Search settings**, at the top of its pages. Type what you look for, in your language or in English: the settings of every page that match show at once, grouped by page and by the part of the page they are on, and you change them right there. Pages without a match are dimmed, and the others show how many they have.

<figure class="alta-figure my-4" style="max-width: 48rem;">
  <img src="{{site.basepath}}/img/alta-desktop-settings-search.webp" alt="Settings over a SharpYaml session with tab typed in Search settings: results from Appearance, Windows and Sessions with their controls, the other pages dimmed and the matching ones counted" loading="lazy">
  <figcaption class="small text-secondary mt-2">A search in Settings: the settings that match, from every page, changed where they are found.</figcaption>
</figure>

- **Open page** shows the whole page of a group. A list (providers, MCP servers, prompts) or a setting changed on its page has **Open**.
- `Enter` opens the first result on its page, `↓` moves into the results, and `Esc` clears the search; a second `Esc` closes Settings.
- A setting you open from the search, from `Ctrl+P` or from an agent glows for a moment on its page, so you see where it is.

`Ctrl+P` finds settings too: see [Search](../search.md#find-a-conversation).

### Ask an agent

Agents know the settings of CodeAlta: ask one where a setting is, what it does, or to change it. An agent shows you a setting by opening Settings on it, and changes one only when you ask.

- When an agent changes a setting, a toast names it, with **Show** and **Undo**.
- Agents never change what protects you: the permission modes, the confirmations, the tools turned off, **Review changes before commit**, the sandbox, Code Mode, the CodeAlta MCP server, Remote Control, storage cleanup and search by meaning. They show you those, and you change them.
- A list of items, such as the MCP servers, the prompts or the windows, is changed with its own commands, as before.

<figure class="alta-figure my-4" style="max-width: 48rem;">
  <img src="{{site.basepath}}/img/alta-desktop-settings-agent-change.webp" alt="A toast saying the agent of a session changed Suggest the next prompt to Off, with Show and Undo, over Settings open on Sessions where Suggest the next prompt glows" loading="lazy">
  <figcaption class="small text-secondary mt-2">A setting an agent changed: the toast with <strong>Show</strong> and <strong>Undo</strong>, and Settings open on the setting, which glows.</figcaption>
</figure>

The zoom of the window is in the title bar, before the theme switch: click it, or use `Ctrl+-`, `Ctrl+=` and `Ctrl+0`. It is kept for the next start.

### Files of the settings

Most settings are files. Each page shows its paths, with **Edit in the code editor** (opens it in the [code editor](code.md#code-editor)), **Copy path** and **Reveal in File Explorer** (**Reveal in Finder** on macOS) beside each.

| Page | Files and folders |
| --- | --- |
| **Configuration file** | `~/.alta/config.toml`, and `.alta/config.toml` of the selected project |
| **Providers**, **Sessions**, **Code Mode**, **Worktrees**, **Work items** | `~/.alta/config.toml` |
| **MCP Servers** | `~/.alta/mcp.json`, `.alta/mcp.json` of the project, and the files of other tools CodeAlta reads |
| **Agent prompts** | `~/.alta/prompts`, `.alta/prompts` of the project, and the selected prompt |
| **Skills** | `~/.alta/skills`, `.alta/skills` of the project, and the selected skill |
| **Plugins** | `~/.alta/plugins`, `.alta/plugins` of the project, and each source plugin |
| **Appearance** | The folder of your color schemes |

Opening `~/.alta/skills` or `~/.alta/plugins` lets you work on all of them at once. A file that does not exist yet says **Not created yet** beside its path, and has no button to open it: nothing of it applies until you create it. `config.toml` and `mcp.json` of `~/.alta` open alone in their tab.

## Appearance

- **Theme and colors**: **Dark**, **Light** or **Auto** (follows the system), **Darker dark theme**, and one of 13 color schemes (each with dark, darker and light variants) or [your own](#your-own-color-scheme).
- **Fonts**: the code font and the prompt font. See [Fonts](#fonts).
- **Explorer**: project sorting, the number of recent sessions and sub-agents, and whether the project rail is collapsed.
- **Conversation and changes**: the [conversation width](prompt.md#conversation-width), [Focused timeline](prompt.md#focused-timeline), **Images under folded lines**, [Entry appearance](prompt.md#entry-appearance), and whether [changes](code.md#all-files-in-one-view) show one file at a time or all at once.

<div class="row g-3 my-4">
  <div class="col-md-6">
    <figure class="alta-figure mb-0">
      <img src="{{site.basepath}}/img/alta-desktop-theme-dark.webp" alt="CodeAlta Desktop in the dark theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Dark</figcaption>
    </figure>
  </div>
  <div class="col-md-6">
    <figure class="alta-figure mb-0">
      <img src="{{site.basepath}}/img/alta-desktop-theme-light.webp" alt="CodeAlta Desktop in the light theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Light</figcaption>
    </figure>
  </div>
</div>

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-themes.webp" alt="CodeAlta Desktop in six color schemes, dark and light" loading="lazy">
  <figcaption class="small text-secondary mt-2">Blueprint, Cherry, Kiwi, Plum and Orange in dark mode, and Blueberry in light mode.</figcaption>
</figure>

### Fonts

CodeAlta Desktop brings its own code font, **CaskaydiaCove Nerd Font** (Cascadia Code with the Nerd Fonts icons), used by code blocks, the editor, diffs, tool output, terminals and the prompt.

- **Code font** takes installed font names separated by commas, such as `JetBrains Mono, Sarasa Mono SC`; a missing character comes from the next font. On Windows, **Installed fonts** lists the system fonts. **Reset** goes back to the built-in font.
- **Prompt font** writes the prompt in the code font (the default) or the **Interface font**.

Chinese or Japanese text uses the sans-serif font of the system. If a prompt that mixes it with code looks too wide, choose the **Interface font** for the prompt, or name a monospace font made for your language first.

### Your own color scheme

**Customize**, beside the color scheme, makes a scheme of your own from the selected one. Name it, pick the theme to edit (**Light**, **Dark** or **Darker**), and change any of:

| Color | What it changes |
| --- | --- |
| **Background** | The window background; panels follow its tint |
| **Text** | The text |
| **Muted text** | Secondary text, icons and borders |
| **Accent** | Buttons, links and selections |
| **Success**, **Warning**, **Danger** | What succeeded, needs attention or failed |

Click a color for the system color picker, or type `#rrggbb`; nothing is kept until **Save**. Colors you leave alone follow the base scheme. **Edit** opens a scheme of yours again to change, duplicate or remove it.

Your schemes are files in `~/.alta/color-schemes/`, which you can copy, share or write by hand:

```json
{
  "name": "Deep Sea",
  "base": "plum",
  "dark": {
    "background": "#0b1d2a",
    "accent": "#33ccff"
  }
}
```

`base` is `blueprint`, `cherry`, `tomato`, `orange`, `pineapple`, `apple`, `kiwi`, `kale`, `blueberry`, `plum`, `elderberry`, `blackberry` or `raspberry`. `light`, `dark` and `darker` hold `background`, `text`, `muted`, `accent`, `success`, `warning` and `danger`. A file that cannot be used is listed on the Appearance page with the reason.

## General and Sessions

**General** has:

- **Language**: **Auto**, English, Spanish, French, German, Japanese or Simplified Chinese.
- **Welcome page**: whether the [welcome page](readme.md#welcome-page) opens at startup and is animated.
- **Startup and closing**: turn [Remote Control](prompt.md#remote-control) back on at startup, what closing the main window does (see [Close or keep running](readme.md#close-or-keep-running)), and **Keep computer awake when CodeAlta is running**.
- **External editor**: see [External editor](code.md#external-editor).

**Sessions** has **Session titles** (see [Session titles](../sessions/readme.md#session-titles)), **Suggest the next prompt** (see [Prompt suggestions](../sessions/readme.md#prompt-suggestions)), **Review changes before commit** (see [Review before commit](../reviews.md#review-before-commit)), **Add CodeAlta as co-author** (see [CodeAlta as co-author](../sessions/readme.md#codealta-as-co-author)) and [Single tab for sessions](readme.md#single-tab-for-sessions).

**Windows** is described in [Windows](readme.md#windows), and **Speech** in [Speech](../speech.md).

## Tool permissions

Agents ask before running commands or changing files, depending on the [permission mode](prompt.md#permission-modes) of the session. **Settings > Permissions** has:

- **Default mode**, for sessions without one: **Bypass permissions** (the default) or **Ask first**.
- **Sessions created by agents**: **Bypass permissions** (the default, so a sub-agent never stops its parent's work with a question), or **Same as the session that creates them**.
- **Tools**: the [tools turned off](#tools-turned-off) for every session.
- **Confirmations**: ask before deleting a session, archiving a project or stopping a plugin whose agents are at work. Turn one on again here after **Do not ask again**.

In CodeAlta TUI, **Auto approve commands**, in the Workspace Settings, is on by default; turn it off to answer each request with **Allow Once**, **Allow for Session** or **Deny**.

A mode decides what you are asked; an agent that is not sandboxed runs with your user privileges. **Settings > Sandbox** limits what it can read, write and reach whatever you approve: see [sandbox](../sessions/sandbox.md).

Code Mode, on by default, lets agents write scripts that call their tools; each call of a script asks as a call of the agent does. **Settings > Code Mode** sets it off, on or only for every session, and the **Code Mode** of a provider in **Settings > Providers** for that provider or one of its models. See [Code Mode](../sessions/code-mode.md).

### Answer a request

A request appears on top of the prompt of its session, with the command and its folder, or the folder a change writes under.

- `Enter` allows once; `2` or `Esc` denies. Or write what the agent should do instead and press `Enter`: the request is denied and your text is sent. A denial does not stop the agent: use Stop.
- A command longer than 4,096 characters is shown cut, with a red warning: deny it unless you know what it does.
- A session that waits is marked in the Explorer and on its tab, and a message with **Show** goes to it. A sub-session's request also shows on the sessions above it, where **Allow once** and **Deny** answer it.

A session in a mode that asks cannot start a [background job](../sessions/delegation.md#background-jobs), type in a terminal, give an automation a command, create or build a plugin, or use the [UI tools](../ui-tools.md).

## Tools turned off

A session can do without some built-in tools, such as `shell_command` when you give the agent specific tools through MCP servers or plugins instead. The tools turned off in three places add up:

{.table}
| Where | For | File |
|---|---|---|
| Settings > Permissions, **Tools** | every session | `[tools] disabled = ["shell_command"]` in `~/.alta/config.toml` |
| The page of a space, **Tools** | the sessions of that space | `tools: { disabled: [...] }` in the file of the space |
| The editor of an agent prompt, **Tools turned off** | the sessions of that agent | `disabled_tools: [...]` in the front matter of the prompt |

Click **Turn off a tool** to choose a built-in tool, or type the name of another. A change applies from the next turn.

Without `shell_command`, a session runs no program of its choice:

- no jobs, terminals, plugin builds, new MCP servers or automations that run commands, no UI tools, and plugin tools that run programs (`gh`, `glab`, `az`) refuse;
- its worktree is checked out without git hooks, and its file tools write only in its project, worktree and scratchpad, never in `.git`, `.claude`, `.codex`, `.vscode`, `.mcp.json` or `.alta` (except `plans` and `tasks`);
- a Claude Code session runs the CLI in its restricted mode, without shell, subagents, skills, settings files or other MCP servers.

A session never hands a tool it does without to another session, and does not change agent prompts, spaces, projects or automations: you do that from the window. The MCP servers you turned on stay available.

## Storage

**Settings > Advanced > Storage** has two cards.

**Automatic cleanup**:

- **Clear scratchpad on startup**, on by default.
- **Delete inactive sessions**, **After 3 months** by default: a number and a unit (days, weeks, months, years), or **Never**. A session with a [bookmark](../bookmarks.md) is kept.

Cleanup runs at the **next startup**, never at once. Session cleanup runs on local Windows storage only; elsewhere the card says why it did not run. A session is inactive since its file was last written (viewing it does not count). Open sessions, drafts and active work are kept, and a session goes only with the sessions it started or that continue it, once all are inactive; its [Statistics](../plugins/statistics.md) are saved first. Other files of the `sessions` folder older than the duration go too. The card shows the progress and result of the last cleanup.

**File sizes** shows what takes room in your profile (sessions, scratchpads, saved Statistics, database, configuration, extensions, caches and logs), for information only. **Refresh** measures again.

## Providers and models

{{ alta_shot "alta-desktop-model-providers.webp" "alta-model-providers.png" "Model provider configuration with validation controls" "Provider setup, endpoint details, credential options, and tests live in one place." }}

**Settings > Providers** enables providers, takes their credentials, tests them and signs in to Codex or Copilot. Open it with `Ctrl+G Ctrl+R`, `/model_providers`, or **N active providers** in the **⋯** menu of the prompt (**N provider errors** above it). See [Model Providers](../model-providers/readme.md).

{{ alta_shot "alta-desktop-models.webp" "alta-models.png" "Model browser showing provider models and capability metadata" "The model browser lists the models of every provider with their context size and capabilities." }}

The model browser (**Settings > Models**, `Ctrl+G Ctrl+O` or `/models`) lists the models of every provider with their context size, their capabilities (reasoning, tool calls, images) and their model refs, such as `codex:gpt-5.5@high`.

Above it, **Model tiers** sets the models of the Complex, Standard and Quick tiers, the fallback to the next model of a tier when a provider fails, and the resume of a session when the limit resets. See [Model Tiers and Fallback](../model-tiers.md).

## Agent prompts

Open the prompt manager with `Ctrl+G Ctrl+H` or `/prompt` (**Settings > Agent prompts**). It lists the built-in prompts, such as Default and Plan, and your global and project prompts, shows which are overridden, and edits the selected one: `name`, `description`, `system` and the Markdown body. Built-in prompts are read-only: **Customize a copy**, or a prompt with the same id, overrides one. System prompt files follow the same rules. The `</>` button opens the file of a prompt in the [code editor](code.md#code-editor). See [Agent Prompts](../prompts.md).

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-prompts.webp" alt="CodeAlta Desktop Agent prompts page listing built-in prompts with the selected prompt on the right" loading="lazy">
  <figcaption class="small text-secondary mt-2">The <strong>Agent prompts</strong> page of the desktop Settings. <strong>Customize a copy</strong> creates your own version of a built-in prompt.</figcaption>
</figure>

## Skills management

{{ alta_shot "alta-desktop-skills.webp" "alta-skills.png" "Skills management showing discovered skills" "Skills management shows discovered skill packages, their descriptions, and whether they are enabled." }}

Open it with `Ctrl+G Ctrl+K` or `/skills`. CodeAlta finds Agent Skills `SKILL.md` packages in your folders and the project's. Inspect them, create one with **New skill**, and enable or disable each for every project (`~/.alta/config.toml`) or the selected one (`.alta/config.toml`). A disabled skill is not offered to models. To use a skill yourself, see [Use a skill](readme.md#use-a-skill).

CodeAlta comes with built-in skills: `codealta-plugin-runtime` to write [plugins](../plugins/developers.md), `document-conversion` to read PDF and Office documents, and `ultra-profiler` to profile the CPU of a .NET or native application with [Ultra](https://github.com/xoofx/ultra), run with `dotnet dnx` of the .NET SDK 10 or later.

A folder of skills can hold symbolic links, for example to a library of skills shared with other tools: CodeAlta finds the skills where the links lead, offers them to agents and sees a change made there; a broken link is skipped. Skills management lists them, shows their details with a **Linked** tag and switches them on or off, but does not edit or remove a skill through a link: change it where the link leads.

- **Edit**, or the `</>` button, opens the folder of a skill in the [code editor](code.md#code-editor); built-in and plugin skills have **View files** (read-only).
- **Remove** moves one of your skills, or one of the project, to the trash after you confirm.
- An **Invalid** skill (unreadable YAML, a duplicate name) shows its diagnostics and can be removed.
- **Enable all** and **Disable all** act on the skills that match the filter.
- With several spaces, a chip shows the [spaces](../spaces.md#what-a-space-has) a skill of yours is offered in.
- The [skills of MCP servers](../plugins/mcp.md#skills-of-mcp-servers) come after yours, read-only, with a switch each.

In `SKILL.md`, `disable-model-invocation: true` makes a skill manual: it is used only with `/` and its name. `argument-hint`, `user-invocable` and `context` are accepted and ignored; any other unknown field makes the skill **Invalid**, except in a [GitHub Copilot](../copilot-layout.md#skills) folder.

The prompt of a session lists the skills of CodeAlta first, then those used most in the project lately; agents find the others with [`alta search`](../search.md).

In CodeAlta TUI, the `G` and `P` checkboxes set the global and project state, and bulk actions enable, disable or invert the skills shown.

## Plugins and MCP servers

{{ alta_shot "alta-desktop-plugins.webp" "alta-plugins.png" "Plugin management" "Plugin management lists built-in and source plugins and lets you enable or disable them." }}

Open **Plugins** with `Ctrl+G Ctrl+N` or `/plugins`. It lists global and project plugins with a switch for each. A source plugin has buttons to build and reload it, edit it in the code editor and remove it; **New plugin** creates one. See [Plugins](../plugins/readme.md).

{{ alta_shot "alta-desktop-mcp.webp" "alta-plugin-mcp.png" "MCP server configuration" "MCP servers are defined globally or per project, with a local command or an HTTP endpoint." }}

Open **MCP Servers** with `/mcp`, or `Ctrl+G Ctrl+Y` in CodeAlta Desktop. See [MCP](../plugins/mcp.md).

## Logs and recovery

{{ alta_shot "alta-desktop-logs.webp" "alta-logs.png" "Logs viewer with diagnostic output and search controls" "The logs viewer keeps startup, provider, credential, and plugin diagnostics available inside CodeAlta." }}

Open the logs with `Ctrl+G Ctrl+L` or `/logs` (**Settings > Application Logs**): startup, provider, credential and plugin messages, with search and level filters.

In CodeAlta Desktop, each message belongs to a feature: **System**, **Providers**, **Sessions**, **Tools**, **Issue trackers**, **Git**, **MCP**, **Plugins**, **Search**, **Speech** or **Mobile**. The feature list beside the level filter shows the messages of one feature, with the number of messages of each.

**Log levels** sets what is written to the log: a default level, and a level for any feature that needs another one. Choose **Debug** for the feature you investigate, such as **Issue trackers**, and its details are written while the others stay quiet. The change applies at once and is saved in `config.toml`:

```toml
[logging]
level = "info"        # trace, debug, info, warn or error

[logging.features]
issues = "debug"      # system, providers, sessions, tools, issues, git, mcp, plugins, search, speech, mobile
```

If `~/.alta/config.toml` cannot be loaded at startup, CodeAlta opens a recovery editor with TOML highlighting and live errors: fix the file and save to continue. **Settings > Configuration file** edits it at any time.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-config-recovery.png" alt="CodeAlta TUI configuration recovery editor with TOML validation feedback" loading="lazy">
  <figcaption class="small text-secondary mt-2">The recovery editor opens before normal startup continues, highlights TOML issues, and lets you save once the configuration is valid.</figcaption>
</figure>

## TUI settings

In CodeAlta TUI, the Workspace Settings dialog sets the navigator, the theme, the language (applied fully after a restart) and **Auto approve commands**. Providers have their own editor.

<div class="row g-3 my-4">
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-default.png" alt="CodeAlta TUI default dark theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Default</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-blue.png" alt="CodeAlta TUI blue theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Blue</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-green.png" alt="CodeAlta TUI green theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Green</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-cherry.png" alt="CodeAlta TUI cherry theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Cherry</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-light.png" alt="CodeAlta TUI light theme" loading="lazy">
      <figcaption class="small text-secondary mt-2">Light</figcaption>
    </figure>
  </div>
  <div class="col-md-6 col-xl-4">
    <figure>
      <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-theme-multi.png" alt="CodeAlta TUI theme selector showing multiple available themes" loading="lazy">
      <figcaption class="small text-secondary mt-2">Theme selector</figcaption>
    </figure>
  </div>
</div>

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-workspace-settings-with-language.png" alt="CodeAlta Workspace Settings dialog showing theme, recent sessions, language, and auto-approve options" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Workspace Settings dialog includes the language selector and a restart notice for fully applying language changes.</figcaption>
</figure>
