---
title: Getting Started
---

# Getting Started

## Install

Install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) first (on macOS, 10.0.13 or later), then install CodeAlta Desktop and launch it from a project folder:

```sh
dotnet tool install -g CodeAlta
alta
```

CodeAlta Desktop is the app we recommend. CodeAlta also has a terminal UI, which runs the same agents on the same `~/.alta` profile:

```sh
dotnet tool install -g CodeAlta.Tui
altatui
```

| App | NuGet package | Command |
| --- | --- | --- |
| **CodeAlta Desktop** | [`CodeAlta`](https://www.nuget.org/packages/CodeAlta/) | `alta` |
| **CodeAlta TUI** | [`CodeAlta.Tui`](https://www.nuget.org/packages/CodeAlta.Tui/) | `altatui` |

You can install both. [Desktop and TUI]({{site.basepath}}/docs/desktop-and-tui/) compares them.

For the TUI, `dnx` can install, update, and run in a single command:

```sh
dnx --yes CodeAlta.Tui
```

The folder you launch from is opened as a project. CodeAlta stores user state under `~/.alta/`, including configuration, logs, cached provider state, session journals, agent prompts under `~/.alta/prompts/agents`, plugins, and skills.

Only one CodeAlta runs on a profile at a time. Close the desktop app before starting the TUI, and the other way around.

### Add a project from the terminal

`alta .` adds the current folder as a project of the desktop app, the way `code .` opens a folder in VS Code, and `alta <folder>` adds the folder named. It works whether CodeAlta is running or not: the window comes to the front and shows the project. A folder that is already a project is shown.

```sh
alta .                                   # the current folder
alta ~/src/my-repo                       # another folder
alta . --space work                      # and put it in the space "work"
alta ~/src/repo-a ~/src/repo-b ~/src/repo-c
```

Several folders are added at once; the window shows the first one. `--space` takes the id, the start of the id or the name of a [space](spaces.md), and can be repeated. Every project is in the default space too. Plain `alta` without a folder only shows the window of a running CodeAlta.

### Desktop requirements

The desktop app uses the web view of the operating system:

- **Windows**: WebView2, which is part of Windows 11 and of up-to-date Windows 10.
- **macOS**: macOS 11 or later.
- **Linux**: WebKitGTK 6.0 with GTK 4, in an X11 or Wayland session. On Ubuntu 24.04 or later: `sudo apt-get install libgtk-4-1 libwebkitgtk-6.0-4`.

When the desktop app cannot start its window, it says why and what to do: in the terminal you started it from, in a message box on Windows, or in a notification. See [The desktop app does not start](troubleshooting.md#the-desktop-app-does-not-start).

On its first start, the desktop app adds CodeAlta to the Start Menu on Windows, to `~/Applications` on macOS, or to the applications menu on Linux. After that you can start it without a terminal. On macOS the app shows where it is and how to keep it in the Dock: **Reveal in Finder** opens the Applications folder of your home folder with CodeAlta selected, and you drag it to the Dock. The Dock entry stays the same across updates. In a terminal, `alta` gives the prompt back once the window is open; `alta --wait` keeps the terminal until the app closes.

On Windows and macOS, after an update run by hand with `dotnet tool update -g CodeAlta`, start `alta` once from a terminal to refresh the Start Menu shortcut or the Dock entry. **Update and restart** in the app does it for you.

On macOS, **System Settings > Privacy & Security** lists the app as **CodeAlta**, with its icon, and **Show in Finder** opens `~/Applications/CodeAlta.app`. A permission given there usually stays across updates. Started from a terminal, CodeAlta uses the permissions of that terminal. Older versions were listed as `alta`, once per version: remove those entries with **−**.

### Terminal font requirement

> [!IMPORTANT]
> The TUI uses [Nerd Fonts](https://www.nerdfonts.com/) icons. Install a current Nerd Font-patched font and select it in your terminal profile before using `altatui`. Without a font that includes the required Nerd Font glyphs, icons may appear as empty boxes, question marks, or misaligned symbols. The desktop app does not need it.

Use the latest Nerd Fonts release when possible. Nerd Fonts v3.0.0 changed many icon code points, so older v2-era patched fonts can still render CodeAlta incorrectly even when the font name includes `NF` or `Nerd Font`.

Recommended setup:

1. Download a recent font from the official [Nerd Fonts downloads](https://www.nerdfonts.com/font-downloads) page.
2. Remove older copies of the same Nerd Font before installing the new one, especially if you have had the font installed for years. Duplicate old and new font files can cause the terminal or OS font cache to keep using the outdated glyph set.
3. Choose the updated Nerd Font family in your terminal profile, such as `CaskaydiaCove Nerd Font`. If a similarly named family such as `CaskaydiaCove NF` still shows broken tree-view icons, check for stale older copies and reinstall the current font.
4. Restart the terminal window and `altatui` after changing or reinstalling fonts.

See [Troubleshooting: glyphs or tree icons look wrong]({{site.basepath}}/docs/troubleshooting/#glyphs-or-tree-icons-look-wrong) if icons still do not display correctly.

## First launch

On first launch, CodeAlta creates a starter `~/.alta/config.toml` with common provider entries disabled. If no provider is enabled yet, the app opens the provider setup automatically: **Settings > Providers** in the desktop app, with a short guide the first time, or the Model Providers dialog in the TUI.

{{ alta_shot "alta-desktop-model-providers.webp" "alta-model-providers.png" "Model provider setup" "First launch takes you to provider setup so you can enable a provider, choose a model, and validate credentials before sending prompts." }}

Use it to:

1. Select a provider entry on the left.
2. Choose or confirm the model and reasoning effort.
3. Add credentials through an API-key environment variable, direct API key, or provider sign-in.
4. Test the provider to validate credentials and model discovery.
5. Confirm the provider shows as enabled. Successful tests and subscription sign-ins enable it automatically.
6. Save to write `~/.alta/config.toml` and refresh the runtime.

You can reopen it any time with `Ctrl+G Ctrl+R` or, in CodeAlta Desktop, from **N active providers** in the **⋯** menu of the prompt.

## Configure one provider quickly

The most common first setup is a subscription-backed provider: **Claude Code** for Claude subscriptions, **Copilot** for GitHub Copilot subscriptions, or **Codex** for ChatGPT/Codex subscriptions. For Copilot and Codex, select the provider, keep or choose a model, start the browser or device-code sign-in, then save after sign-in completes. A successful sign-in enables that provider automatically; a successful provider test does the same for any provider type.

For **Claude Code**, CodeAlta runs the Claude Code CLI with the account it is signed in to. In CodeAlta Desktop, select the provider and choose **Download** in its **Claude Code CLI** box to get the version CodeAlta is tested with, then run the command the box gives in a terminal and type `/login`. Enable the provider and **Test** it. See [Claude Code](model-providers.md#claude-code).

A coding agent that speaks the Agent Client Protocol, such as GitHub Copilot or Gemini CLI, can be a provider too: in CodeAlta Desktop, choose **Add provider**, then **ACP registry…**, and **Add** the agent. See [Agents of the ACP registry](model-providers.md#agents-of-the-acp-registry).

Codex and Copilot credentials are stored in CodeAlta-owned state through their sign-in flows. They are intentionally separate from OpenAI platform API keys.

If you use an API-key provider instead, set the provider's environment variable and enable that provider in the app or in TOML. For OpenAI platform access:

> [!TIP]
> Environment variables keep API keys out of `~/.alta/config.toml` and project files. Set the variable in the same shell or profile that launches CodeAlta, then restart CodeAlta so the running process can see the new value.

```sh
# macOS/Linux
export CODEALTA_OPENAI_API_KEY="..."

# PowerShell
$env:CODEALTA_OPENAI_API_KEY = "..."
```

In `~/.alta/config.toml`:

```toml
[chat]
default_provider = "openai"

[providers.openai]
enabled = true
display_name = "OpenAI"
type = "openai-responses"
model = "gpt-5.5"
reasoning_effort = "high"
api_key_env = "CODEALTA_OPENAI_API_KEY"
api_url = "https://api.openai.com/v1"
```

The provider editor of both apps edits the same file and preserves advanced TOML values.

Other API-key providers use the same pattern with their own environment variables, such as `CODEALTA_ALIBABA_API_KEY`, `CODEALTA_AZURE_OPENAI_API_KEY`, `CODEALTA_ANTHROPIC_API_KEY`, `CODEALTA_DEEPSEEK_API_KEY`, or `CODEALTA_ZAI_API_KEY`. For Azure OpenAI, choose `type = "azure-openai"`, set `api_url` to the resource endpoint, and use the deployment name as the model.

## Send a first prompt

{{ alta_shot "alta-desktop-new-session.webp" "alta-home.png" "New session screen" "Selecting a project opens a new session for it. The prompt at the bottom starts the session in that project folder." }}

1. Open a project with `Ctrl+O` or `/open`, or add a folder with the `+` button of the Projects sidebar. In CodeAlta Desktop, `+` opens the folder dialog of your operating system.
2. If needed, choose the agent prompt, the model and the reasoning below the prompt: with the **Agent** chip in CodeAlta Desktop, with the selectors in CodeAlta TUI. The built-in **Default** agent prompt is a good starting point.
3. Type a prompt in the prompt editor.
4. Press `Enter` to send. Use `Shift+Enter` for a new line.
5. Watch the timeline for reasoning/status messages, assistant messages, tool calls, tool results, statistics, and modified-file summaries.

Agent prompts are selectable session profiles. Open the prompt manager with `Ctrl+G Ctrl+H` or `/prompt` to inspect Default, Plan mode, or your own global/project prompts. See [Agent Prompts]({{site.basepath}}/docs/prompts/) for the file layout, override rules, and custom workflow guidance.

Use **Plan** when you want CodeAlta to research and write a reviewable `.alta/plans/` file before implementation. Use **Default** when the task is already scoped enough to inspect, edit, verify, and report directly.

Useful first prompts:

```text
Summarize this repository and identify the main build/test commands.
```

```text
Inspect the failing test output below and propose the smallest safe fix.
```

## Add files to a prompt

Type `@` in the prompt editor to open the project file/folder picker. Accepted entries are inserted as Markdown links such as `[Program.cs](src/CodeAlta.Tui/Program.cs)` and are sent as structured attachments. Raw `@path`, quoted paths, and optional `:line` or `:start-end` suffixes are also recognized at send time.

In GitHub, GitLab and Azure DevOps repositories, type `#` to search recent issues (work items on Azure DevOps). The picker accepts numbers or words, matches words case-insensitively, and inserts Markdown issue links such as `[#18](https://github.com/org/repo/issues/18)`. See the [Git plugin](plugins/git.md).

{{ alta_shot "alta-desktop-file-selection.webp" "alta-file-selection.gif" "File picker opened from the prompt" "Type <code>@</code>, search for files or folders, and accept entries to add them to the prompt as structured attachments." }}

## Attach files and images to a prompt

In the desktop app, the paperclip button of the prompt bar, before the model, opens a menu:

- **Add files or photos** (`Ctrl+U`, `Cmd+U` on macOS, or `/attach_files`) opens the file dialog of your system: choose one file or several. You can also drop files from your file manager onto the prompt, or copy them and press `Ctrl+V` in the prompt editor.
- **Add folder…** (`Ctrl+Alt+F` or `/attach_folder`) opens the folder dialog of your system in the folder the session works in. A folder of the project reaches the agent as a folder named with `@` does; any other folder, by its full path.
- **Add issues…** (`Ctrl+Alt+I` or `/attach_issues`) opens the issue picker of the project: check issues with a click, their box or `Shift+Enter`, then press **Attach** (or `Enter`). Checked issues stay checked if you click outside the picker; **Cancel** (or `Esc`) drops them.

Each one appears above the prompt: an image as a thumbnail, a file with its icon and name, a folder or an issue with its name. Click a file or an image to preview, rename or remove it. When the prompt is sent, the links of the folders and the issues are added at the end of its text, one per line, as `@` and `#` put them in the text. In a narrow window, the menu is in the `⋯` menu of the prompt bar.

When the prompt is sent, CodeAlta stores a copy of each file beside the session journal, so the conversation does not change if you later edit, move or delete the original. The model gets each file in a form it reads:

| File | What the model gets |
| --- | --- |
| Image (PNG, JPEG, WebP, GIF, BMP) | The image, in a format it reads (PNG, JPEG or WebP), scaled down to 2000 pixels on its longest side and to 3.75 MB at most. The selected model must support image input. |
| PDF | The document itself for a model that reads PDFs (the Claude models of Anthropic and of Claude Code). Any other model gets its text, converted to Markdown when the prompt is sent to such a model; a PDF sent to a Claude model and later read by another one reaches that one by its path. |
| Word, Excel, PowerPoint, EPUB, RTF | Its text, converted to Markdown. |
| Text: code, Markdown, CSV, JSON, logs… | Its content, up to 100,000 characters; the agent reads the rest from the stored copy. |
| Any other file | Its path: the agent opens it with its tools. |

A file is attached up to 20 MB, and a prompt has at most 64 attachments and 64 MB of files. Files work with any model; only images need a model that reads images.

The conversion to Markdown uses [MarkItDotNet](https://github.com/elbruno/ElBruno.MarkItDotNet), run with `dotnet dnx` of the .NET SDK (version 10 or later): it is not part of CodeAlta, and the first conversion downloads it from nuget.org, which can take a few seconds. A document that cannot be converted (no SDK, no network, a protected or damaged file) reaches the model by its path, and the agent can try again with the built-in `document-conversion` skill, which it also uses for a document of the project that you name with `@`.

- **Desktop**: the prompt in the timeline lists the files it was sent with, below its text.
- **TUI**: pasting an image opens a preview/title dialog so you can confirm the image before it is attached. Other files are not attached in the TUI: name them with `@`.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-image-prompt-copy-paste.png" alt="CodeAlta TUI image paste dialog showing an image copied into a prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">In the TUI, paste an image into the prompt, preview it, give it a title, and send it as model context when the provider/model accepts image input.</figcaption>
</figure>

> [!IMPORTANT]
> In the TUI, image preview requires a terminal with inline image protocol support. Use a terminal that supports Sixel, such as Windows Terminal, or the Kitty/iTerm2 image protocols. Without that support, pasted-image previews may not render correctly even when the selected model can accept images.

## Let the agent look at an image

The agent can open an image file on its own. Give it a path, or let it find the file, and it reads the image with its `view_image` tool:

```text
Look at docs/img/login-page.png and tell me why the button is misaligned.

Take a screenshot of the app with the test script, then check that the dialog is centered.
```

PNG, JPEG, GIF, WebP and BMP files are supported. A large image is scaled down to 2000 pixels on its longest side before it is sent. Images returned by MCP tools, such as a browser screenshot, reach the model the same way. The selected model must support image input.

- **Desktop**: the timeline shows an **Image read** card with a preview of each image the agent looked at. Click the preview to open the image.
- **TUI**: the tool call shows a line with the name, type and size of the image.

In the desktop app, an image of the timeline, sent with a prompt or read by the agent, can be copied to the clipboard: right-click its thumbnail and choose **Copy image**, or open it and use the **Copy image** button of its window or `Ctrl+C`. The image is copied as PNG.

## Ask CodeAlta about CodeAlta

This guide ships with CodeAlta, and every agent can read it. Ask a session how to do something:

```text
How do I make the conversation narrower?
```

```text
Where do I see the pull requests of my project?
```

The agent answers from the guide and can walk you through the interface step by step.

## Essential shortcuts

{{ alta_shot "alta-desktop-help.webp" "alta-help.png" "Commands and shortcuts help" "Open help with <code>F1</code>, <code>/help</code>, or <code>?</code> in an empty prompt whenever you need a reminder of the commands and their shortcuts." }}

Both apps use the same shortcuts and slash commands unless noted.

| Action | Shortcut or command |
| --- | --- |
| Help / command discovery | `F1`, `/help`, or `?` |
| Search (desktop) or command palette (TUI) | `Ctrl+P`; `/` for commands, and for skills in the desktop app |
| Open project | `Ctrl+O` or `/open` |
| Open a file in the editor | `Ctrl+E` or `/edit` |
| Open the code editor of the project (desktop) | `Ctrl+E` `Ctrl+E` or `/editor` |
| Open full prompt editor | `F6` |
| Attach files, a folder, issues (desktop) | `Ctrl+U` (`Cmd+U` on macOS), `Ctrl+Alt+F`, `Ctrl+Alt+I` or `/attach_files`, `/attach_folder`, `/attach_issues` |
| Choose agent, model and reasoning | `/model` |
| Open model providers | `Ctrl+G Ctrl+R` or `/model_providers` |
| Manage prompts | `Ctrl+G Ctrl+H` or `/prompt` |
| Switch to next agent prompt | `Ctrl+T` or `/next_prompt` |
| Browse models | `Ctrl+G Ctrl+O` or `/models` |
| About / update status | `Ctrl+G Ctrl+A` or `/about` |
| Open settings | `Ctrl+G Ctrl+W` or `/settings` (also `Ctrl+,` in the desktop app) |
| Open skills | `Ctrl+G Ctrl+K` or `/skills` |
| Open plugins | `Ctrl+G Ctrl+N` or `/plugins` |
| Open MCP servers | `/mcp` (also `Ctrl+G Ctrl+Y` in the desktop app) |
| Open logs | `Ctrl+G Ctrl+L` or `/logs` |
| Toggle navigator | `Ctrl+G Ctrl+G` |
| Context usage | `Ctrl+G Ctrl+U` |
| Session info | `Ctrl+G Ctrl+T` |
| Reminders | `Ctrl+G Ctrl+D` or `/reminder` |
| Open the bookmarks (desktop) | `Ctrl+G Ctrl+F` or `/bookmarks` |
| Previous / next bookmarked message (desktop) | `Shift+F3` / `Shift+F4` |
| Send when idle / enqueue while busy | `Enter` |
| Send now (steer while busy) | `Cmd+Enter` on macOS (Desktop); `Ctrl+Enter` on Windows/Linux (Desktop) or in TUI |
| Abort the running turn | `F8` or `/abort` |
| [Dictate a prompt](speech.md) (desktop) | `F9` |
| Compact idle session | `Ctrl+F11` or `/compact` |
| [Fork a session, or rewind it](sessions.md#fork-and-rewind) to an earlier turn (desktop) | `/fork`, `/rewind` |
| Clear prompt queue | `F10` |
| Previous/next user or assistant message | `F3` / `F4` |
| Switch tabs | `Ctrl+Alt+Left` / `Ctrl+Alt+Right` (also `Ctrl+Shift+Tab` / `Ctrl+Tab` and `Ctrl+PageUp` / `Ctrl+PageDown` in the desktop app) |
| Go to a [space](spaces.md) (desktop) | `Ctrl+G Ctrl+V` or `/space` |
| Show the space at a place of the list (desktop) | `Ctrl+G 1` to `Ctrl+G 9` |
| Previous / next space (desktop) | `Ctrl+Alt+PageUp` / `Ctrl+Alt+PageDown` |
| Close tab / reopen closed tab (desktop) | `Ctrl+W` / `Ctrl+Shift+T` |
| Browse saved sessions (desktop) | `Ctrl+Alt+B` or `/sessions` |
| Show or hide session notes (desktop) | `Ctrl+Shift+N` or `/notes` |
| Open the [statistics](plugins/statistics.md) page (desktop) | `Ctrl+G C` or `/statistics` |
| Zoom in / out / reset (desktop) | `Ctrl+=` or `Ctrl+Plus` / `Ctrl+-` / `Ctrl+0`, or `/zoom_in` / `/zoom_out` / `/reset_zoom`, or the percentage in the title bar |
| Copy the UI as an image (TUI) | `Ctrl+F12` |

If these shortcuts do not work in Windows Terminal, see [Troubleshooting: Windows Terminal shortcuts do not reach CodeAlta]({{site.basepath}}/docs/troubleshooting/#windows-terminal-shortcuts-do-not-reach-codealta).
