---
title: Getting Started
---

# Getting Started

## Install

Install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) first, then install CodeAlta Desktop and launch it from a project folder:

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

### Desktop requirements

The desktop app uses the web view of the operating system:

- **Windows**: WebView2, which is part of Windows 11 and of up-to-date Windows 10.
- **macOS**: macOS 11 or later.
- **Linux**: WebKitGTK 6.0 with GTK 4.

On its first start, the desktop app adds CodeAlta to the Start Menu on Windows, to `~/Applications` on macOS, or to the applications menu on Linux. After that you can start it without a terminal. In a terminal, `alta` gives the prompt back once the window is open; `alta --wait` keeps the terminal until the app closes.

On Windows, after an update run by hand with `dotnet tool update -g CodeAlta`, start `alta` once from a terminal to refresh the Start Menu shortcut. **Update and restart** in the app does it for you.

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

You can reopen it any time with `Ctrl+G Ctrl+R` or from the provider summary below the prompt.

## Configure one provider quickly

The most common first setup is a subscription-backed provider: **Copilot** for GitHub Copilot subscriptions, or **Codex** for ChatGPT/Codex subscriptions. Select the provider, keep or choose a model, start the browser or device-code sign-in, then save after sign-in completes. A successful sign-in enables that provider automatically; a successful provider test does the same for any provider type.

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
2. Select the agent prompt and the provider/model/reasoning combination below the prompt if needed. The built-in **Default** agent prompt is a good starting point.
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

## Add images to a prompt

When the selected model supports image input, copy an image to the clipboard and press `Ctrl+V` in the prompt editor. The image is stored beside the session journal when the prompt is sent.

- **Desktop**: pasted PNG, JPEG, WebP, GIF and BMP images appear as thumbnails above the prompt. Click a thumbnail to preview, rename, or remove it.
- **TUI**: a preview/title dialog opens so you can confirm the image before it is attached.

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

PNG, JPEG, GIF, WebP and BMP files are supported. A large image is scaled down to 2048 pixels on its longest side before it is sent. Images returned by MCP tools, such as a browser screenshot, reach the model the same way. The selected model must support image input.

- **Desktop**: the timeline shows an **Image read** card with a preview of each image the agent looked at. Click the preview to open the image.
- **TUI**: the tool call shows a line with the name, type and size of the image.

## Essential shortcuts

{{ alta_shot "alta-desktop-help.webp" "alta-help.png" "Commands and shortcuts help" "Open help with <code>F1</code>, <code>/help</code>, or <code>?</code> in an empty prompt whenever you need a reminder of the commands and their shortcuts." }}

Both apps use the same shortcuts and slash commands unless noted.

| Action | Shortcut or command |
| --- | --- |
| Help / command discovery | `F1`, `/help`, or `?` |
| Command palette | `Ctrl+P` or `/` |
| Open project | `Ctrl+O` or `/open` |
| Open a file in the editor | `Ctrl+E` or `/edit` |
| Open the code editor of the project (desktop) | `Ctrl+E` `Ctrl+E` or `/editor` |
| Open full prompt editor | `F6` |
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
| Steer a running session | `Ctrl+Enter` |
| Abort the running turn | `F8` or `/abort` |
| Compact idle session | `Ctrl+F11` or `/compact` |
| Clear prompt queue | `F10` |
| Previous/next user or assistant message | `F3` / `F4` |
| Switch tabs | `Ctrl+Alt+Left` / `Ctrl+Alt+Right` |
| Close tab / reopen closed tab (desktop) | `Ctrl+W` / `Ctrl+Shift+T` |
| Browse saved sessions (desktop) | `Ctrl+Alt+B` or `/sessions` |
| Show or hide session notes (desktop) | `Ctrl+Shift+N` or `/notes` |
| Copy the UI as an image (TUI) | `Ctrl+F12` |

If these shortcuts do not work in Windows Terminal, see [Troubleshooting: Windows Terminal shortcuts do not reach CodeAlta]({{site.basepath}}/docs/troubleshooting/#windows-terminal-shortcuts-do-not-reach-codealta).
