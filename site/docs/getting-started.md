---
title: Getting Started
---

# Getting Started

## Install

CodeAlta is distributed as a .NET global tool. The package id is `CodeAlta`; the installed command is `alta`.

> [!IMPORTANT]
> CodeAlta is currently distributed as preview `0.x` releases before the final `1.0`. Expect behavior, configuration shape, screenshots, and extension APIs to evolve between preview versions; review release notes before upgrading a workflow you depend on.

Install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) first, then install/update CodeAlta:

```sh
dotnet tool install -g CodeAlta
```

Alternatively, use `dnx` to install, update, and run in a single command:

```sh
dnx --yes CodeAlta
```

Then launch the terminal UI:

```sh
alta
```

CodeAlta stores user state under `~/.alta/`, including configuration, logs, cached provider state, session journals, saved prompts, plugins, and skills.

## Terminal font requirement

> [!IMPORTANT]
> CodeAlta uses [Nerd Fonts](https://www.nerdfonts.com/) icons throughout the terminal UI. Install a current Nerd Font-patched font and select it in your terminal profile before using CodeAlta. Without a font that includes the required Nerd Font glyphs, icons may appear as empty boxes, question marks, or misaligned symbols.

Use the latest Nerd Fonts release when possible. Nerd Fonts v3.0.0 changed many icon code points, so older v2-era patched fonts can still render CodeAlta incorrectly even when the font name includes `NF` or `Nerd Font`.

Recommended setup:

1. Download a recent font from the official [Nerd Fonts downloads](https://www.nerdfonts.com/font-downloads) page.
2. Remove older copies of the same Nerd Font before installing the new one, especially if you have had the font installed for years. Duplicate old and new font files can cause the terminal or OS font cache to keep using the outdated glyph set.
3. Choose the updated Nerd Font family in your terminal profile, such as `CaskaydiaCove Nerd Font`. If a similarly named family such as `CaskaydiaCove NF` still shows broken tree-view icons, check for stale older copies and reinstall the current font.
4. Restart the terminal window and `alta` after changing or reinstalling fonts.

See [Troubleshooting: glyphs or tree icons look wrong]({{site.basepath}}/docs/troubleshooting/#glyphs-or-tree-icons-look-wrong) if icons still do not display correctly.

## First launch

On first launch, CodeAlta creates a starter `~/.alta/config.toml` with common provider entries disabled. If no provider is enabled yet, the app opens the Model Providers dialog automatically.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-model-providers.png" alt="CodeAlta Model Providers dialog opened during first launch" loading="lazy">
  <figcaption class="small text-secondary mt-2">First launch routes you directly to provider setup so you can enable a provider, choose a model, and validate credentials before sending prompts.</figcaption>
</figure>

Use the dialog to:

1. Select a provider entry on the left.
2. Choose or confirm the model and reasoning effort.
3. Add credentials through an API-key environment variable, direct API key, or provider login flow.
4. Click **Test Provider** to validate credentials and model discovery.
5. Confirm the provider shows as enabled. Successful tests and subscription login flows enable it automatically.
6. Click **Save** to write `~/.alta/config.toml` and refresh the runtime.

You can reopen this dialog any time with `Ctrl+G Ctrl+R` or from the provider summary in the footer.

## Configure one provider quickly

The most common first setup is a subscription-backed provider: **Copilot** for GitHub Copilot subscriptions, or **Codex** for ChatGPT/Codex subscriptions. In the Model Providers dialog, keep or choose a model, start the browser/device login flow, then click **Save** after login completes. Successful browser/device login enables that provider automatically; a successful **Test Provider** does the same for any provider type.

Codex and Copilot credentials are stored in CodeAlta-owned state through their login flows. They are intentionally separate from OpenAI platform API keys.

If you use an API-key provider instead, set the provider's environment variable and enable that provider in the dialog or TOML. For OpenAI platform access:

> [!TIP]
> Environment variables keep API keys out of `~/.alta/config.toml` and project files. Set the variable in the same shell or profile that launches `alta`, then restart `alta` so the running process can see the new value.

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

The in-app dialog edits the same file and preserves advanced TOML values.

Other API-key providers use the same pattern with their own environment variables, such as `CODEALTA_ALIBABA_API_KEY`, `CODEALTA_AZURE_OPENAI_API_KEY`, `CODEALTA_ANTHROPIC_API_KEY`, `CODEALTA_DEEPSEEK_API_KEY`, or `CODEALTA_ZAI_API_KEY`. For Azure OpenAI, choose `type = "azure-openai"`, set `api_url` to the resource endpoint, and use the deployment name as the model.

## Send a first prompt

1. Open a project with `Ctrl+O`, `/open`, or the `+` button in the Projects sidebar row.
2. Select the provider/model/reasoning combination in the footer if needed.
3. Type a prompt in the prompt editor.
4. Press `Enter` to send. Use `Shift+Enter` for a new line.
5. Watch the timeline for reasoning/status messages, assistant messages, tool calls, tool results, statistics, and modified-file summaries.

Useful first prompts:

```text
Summarize this repository and identify the main build/test commands.
```

```text
Inspect the failing test output below and propose the smallest safe fix.
```

## Add files to a prompt

Type `@` in the prompt editor to open the project file/folder picker. Accepted entries are inserted as Markdown links such as `[Program.cs](src/CodeAlta/Program.cs)` and are sent as structured attachments. Raw `@path`, quoted paths, and optional `:line` or `:start-end` suffixes are also recognized at send time.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-file-selection.gif" alt="CodeAlta animated file selection dialog for attaching project files to a prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">Type <code>@</code>, search for files or folders, and accept entries to add them to the prompt as structured attachments.</figcaption>
</figure>

## Add images to a prompt

When the selected model supports image input, copy an image to the clipboard and press `Ctrl+V` in the prompt editor. CodeAlta opens a preview/title dialog so you can confirm the image before it is attached, then stores the image beside the session journal.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-image-prompt-copy-paste.png" alt="CodeAlta image paste dialog showing an image copied into a prompt" loading="lazy">
  <figcaption class="small text-secondary mt-2">Paste an image into the prompt, preview it, give it a title, and send it as model context when the provider/model accepts image input.</figcaption>
</figure>

> [!IMPORTANT]
> Image preview requires a terminal with inline image protocol support. Use a terminal that supports Sixel, such as Windows Terminal, or the Kitty/iTerm2 image protocols. Without that support, pasted-image previews may not render correctly even when the selected model can accept images.

## Essential shortcuts

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-command-bar-with-shortcuts.png" alt="CodeAlta command bar showing commonly used keyboard shortcuts" loading="lazy">
  <figcaption class="small text-secondary mt-2">The command bar keeps high-frequency shortcuts visible while you work, so common actions are discoverable without interrupting the prompt flow.</figcaption>
</figure>

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-help.png" alt="CodeAlta help dialog listing common shortcuts and commands" loading="lazy">
  <figcaption class="small text-secondary mt-2">Open help with <code>F1</code>, <code>/help</code>, or <code>?</code> whenever you need a reminder of workspace, provider, thread, and dialog shortcuts.</figcaption>
</figure>

| Action | Shortcut or command |
| --- | --- |
| Help / command discovery | `F1`, `/help`, or `?` |
| Open project | `Ctrl+O` or `/open` |
| Open file editor | `Ctrl+E` or `/edit` |
| Open full prompt editor | `F6` |
| Focus provider/model selector | `/model` |
| Open model providers | `Ctrl+G Ctrl+R` |
| Browse models | `Ctrl+G Ctrl+O` or `/models` |
| About / update status | `Ctrl+G Ctrl+A` or `/about` |
| Open workspace settings | `Ctrl+G Ctrl+W` or `/settings` |
| Open plugins | `Ctrl+G Ctrl+N` or `/plugins` |
| Open logs | `Ctrl+G Ctrl+L` or `/logs` |
| Context usage popup | `Ctrl+G Ctrl+U` |
| Thread report | `Ctrl+G Ctrl+T` |
| Steer a running thread | `Ctrl+Enter` |
| Delegate to another session | `F7` |
| Compact idle thread | `F11` |
| Clear prompt queue | `F10` |
| Previous/next user or assistant message | `F3` / `F4` |
| Switch tabs | `Alt+Left` / `Alt+Right` |

If these shortcuts do not work in Windows Terminal, see [Troubleshooting: Windows Terminal shortcuts do not reach CodeAlta]({{site.basepath}}/docs/troubleshooting/#windows-terminal-shortcuts-do-not-reach-codealta).
