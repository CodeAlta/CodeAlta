---
title: Set Up a Provider
---

# Set Up a Provider

Each section below sets up one provider. The general steps, the test and the default provider are on the [overview](readme.md#open-the-providers); every setting of the file is in [Configuration file](configuration.md).

Subscriptions need no API key: **Claude Code** for a Claude plan, **Codex** for a ChatGPT plan, **Copilot** for GitHub Copilot, **xAI Grok** for SuperGrok, **Mistral Vibe** for Mistral Pro. Their credentials stay in CodeAlta's own state, apart from API keys.

For an API-key provider, put the key in an environment variable and name it with `api_key_env`:

```sh
# macOS/Linux
export CODEALTA_OPENAI_API_KEY="..."

# PowerShell
$env:CODEALTA_OPENAI_API_KEY = "..."
```

> [!TIP]
> Set the variable where CodeAlta starts, then restart CodeAlta. Started from the Start Menu, the Dock or the applications menu, CodeAlta Desktop sees the variables of your user session, not those of a terminal.

## Claude Code

The `claude-code` provider runs your sessions through the [Claude Code](https://code.claude.com/docs/en/overview) CLI, with the account it is signed in to. This is how a Claude subscription is used from CodeAlta: Anthropic does not let other applications sign in to a Claude account.

1. In CodeAlta Desktop, open **Settings > Providers** and select **Claude Code**. The **Claude Code CLI** box says which CLI the provider runs. **Download** gets the version CodeAlta is tested with (about 130 MB, once for all your Claude providers). The same download is proposed above the prompt when you choose a Claude Code provider without a CLI, and under the error of a turn that could not start.
2. Sign in: copy the command the box gives, run it in a terminal, then type `/login`. An API key or a cloud provider configured for the CLI works too.
3. Enable the provider and **Test** it. The test asks the CLI for its models without calling one. A provider whose account is not signed in is tagged **Signed out**, and its form says how to sign in.
4. Start a session. The models are the ones the CLI offers for your account; the first is the one Claude Code recommends.

CodeAlta never sees your Claude credentials. Usage counts against the account the CLI is signed in to, with the limits of its plan.

### Which CLI runs

Each version of CodeAlta is tested with one version of Claude Code. CodeAlta runs, in this order:

1. the program `command` names, when the provider has one;
2. the tested version, once downloaded: the official release from GitHub, checked before use, kept in `~/.alta/cache/claude-code/<version>/<platform>`;
3. the Claude Code you installed, on `PATH` or where its installers put it (`~/.local/bin`, Homebrew, npm). When it is another version, **Test** says so.

On Windows, CodeAlta runs the native `claude.exe`, not the `claude.cmd` of an npm installation: without the native installer, download the tested version.

### Several accounts

Each Claude account is a provider of its own, with its own folder for Claude Code: its login, settings, memory and conversations.

1. Select a Claude Code provider and choose **Duplicate**: CodeAlta adds `claude-code-2` with the same settings.
2. In **Account folder**, give it another folder, such as `~/.claude-work`, and save.
3. Sign in with the command the box gives: it starts Claude Code with that folder.

A new folder starts empty: copy the settings, `CLAUDE.md` or MCP servers you want from your usual folder (`~/.claude`). The conversations of the provider are in its folder too: **Copy resume command** and **Run in terminal** set `CLAUDE_CONFIG_DIR` to it, and **Open in VS Code** is refused, because VS Code opens conversations from the folder of its own account (see [Open a session in VS Code or a terminal](../sessions/claude-code.md#open-in-vs-code)). Changing the folder of a provider that has sessions asks first: their conversations stay in the old folder, where Claude Code no longer finds them.

### The ANTHROPIC_API_KEY variable

When `ANTHROPIC_API_KEY` is set, Claude Code started by another application uses it without asking, and bills the API account of the key instead of your plan. CodeAlta follows the answer you gave Claude Code for that key: with **No**, it starts Claude Code without the variable, so your login is used.

When Claude Code has no answer for the key, a session does not start, and its error says what to choose: **ANTHROPIC_API_KEY** in the provider editor (use it, or ignore it and use your Claude login), or `anthropic_api_key` in the file. The variable is left alone when Claude Code uses Bedrock, Vertex AI or Foundry.

### Claude Code sessions

A session of this provider is a Claude Code session shown in CodeAlta:

- Claude Code keeps its own system prompt, tools (`Read`, `Edit`, `Bash`...), settings, permission rules, hooks, skills and `CLAUDE.md` files. CodeAlta adds its own instructions (agent prompt, skills, `AGENTS.md`) to the prompt, and gives what changed with your next prompt when they change.
- CodeAlta's tools reach it as MCP tools named `mcp__codealta__<name>`: the `alta` tool, plugin tools and the tools of the MCP servers you activated.
- What it delegates is a [sub-agent](../sessions/delegation.md#sub-agents) of CodeAlta, which you can open, follow and stop. Claude Code's own subagents and workflows are not used.
- Its tool calls, output and diffs appear in the timeline; the session is saved, resumed, queued and steered like any other. Claude Code keeps the conversation in its transcript under `projects` of the account folder; when that transcript is gone, the session goes on in a new conversation that starts with the history CodeAlta recorded.
- The same conversation can be continued in VS Code and in a terminal: see [Claude Code sessions in VS Code and in a terminal](../sessions/claude-code.md) and [Claude Code sessions from VS Code and terminals](../sessions/claude-code.md).
- A permission that your Claude Code settings do not decide follows the [permission mode](../workspace/settings.md#tool-permissions) of the session. A question of Claude opens CodeAlta's question form.
- Claude Code's plan mode is not used: use the **Plan** agent prompt of CodeAlta. With `permission_mode = "plan"` in the provider, a session stays read-only and gives its plan as its answer.
- **Permissions** in the provider editor sets the mode: `default`, `acceptEdits`, `plan`, `auto`, `dontAsk` or `bypassPermissions`. Left blank, your Claude Code settings choose. In CodeAlta Desktop it is also the default mode of the provider's sessions: with `bypassPermissions` what the CLI asks is approved, otherwise you are asked.
- A session can have a mode of its own, with **Permissions** in the **Agent** chip below the prompt (see [Agent prompt, model and permissions](../workspace/prompt.md#agent-and-model)). It switches the running CLI at once, also during a turn. `plan` stays a provider setting. In **Ask first**, Claude Code still runs by itself what it holds to be safe, such as a command that only reads.
- [Remote Control](../workspace/prompt.md#remote-control) follows and drives a session from claude.ai and the Claude app, with the CLI signed in to your claude.ai account.
- Claude Code compacts its context itself; **Compact** asks it to (`/compact`). A prompt that starts with `/` is a Claude Code slash command.
- A command Claude Code leaves running in the background can start a turn when it ends; CodeAlta shows it as a new run, whose first message says what started it. While such a command, a watch or a background agent runs, the call reads **Running in the background**, the session has a dot in the sidebar and on its tab, and the line above the prompt counts them: select it to see or stop them. Closing CodeAlta ends them.
- A picture a tool returns is also saved by Claude Code in `~/.claude/projects/<folder>/<session>/tool-results`.

### Claude Code settings

```toml
[providers.claude-code]
type = "claude-code"
model = "sonnet"                 # optional
reasoning_effort = "high"        # optional: low, medium, high, xhigh, max
config_dir = "~/.claude-work"    # optional: the folder of the account (default: ~/.claude)
command = "~/.local/bin/claude"  # optional: a Claude Code of your own
permission_mode = "default"      # optional: default, acceptEdits, plan, auto, dontAsk, bypassPermissions
anthropic_api_key = "ignore"     # optional: use or ignore ANTHROPIC_API_KEY (default: your answer in Claude Code)
args = ["--add-dir", "/specs"]   # optional: more arguments for the CLI

[providers.claude-code.env]      # optional: variables the CLI starts with (Environment in the editor)
CLAUDE_CODE_OAUTH_TOKEN = "..."  # a token of the account, from `claude setup-token`
```

`config_dir` is a full path or starts with `~`; Claude Code gets it as `CLAUDE_CONFIG_DIR`. The variables of `env` (**Environment** in the provider editor, one `NAME=value` per line) are given to Claude Code over those CodeAlta inherits, also in a [sandbox](../sessions/sandbox.md#providers), where a token is what signs it in on macOS. A `command` that is a name is looked for on `PATH`, then in the folders of the Claude Code installers. The provider takes no `api_key`, `api_key_env` or `api_url`.

## Codex (ChatGPT)

The `codex` provider uses your ChatGPT plan. It is separate from OpenAI platform API keys.

```toml
[providers.codex]
enabled = true
display_name = "Codex"
type = "codex"
model = "gpt-5.5"
reasoning_effort = "high"
```

Sign in with **Sign in with the browser** (CodeAlta Desktop) or **Continue with ChatGPT** (CodeAlta TUI), and authorize CodeAlta in the system browser. No developer account or key is needed. The browser returns to `127.0.0.1` on the computer that runs CodeAlta, so it must reach that computer.

- **Another account or workspace**: add another `type = "codex"` provider with its own `display_name`, even for the same email address. The **Account** row of the provider (CodeAlta Desktop) or **Account Info** (CodeAlta TUI) shows the account it signed in to.
- **Plan access declined**: the sign-in is kept but requests are refused; choose **Sign in with the browser** (CodeAlta Desktop) or **Continue with ChatGPT** (CodeAlta TUI) again to grant access.
- **`invalid_grant` at the first sign-in**: sign in again; the retry reuses the same registration.
- **Credentials of an older CodeAlta**: they are not imported; sign in again. Remove an old `auth_source` or set `auth_source = "codealta_oauth"`.
- **Sign out** revokes the session and clears the tokens. When the revocation cannot be confirmed, disconnect CodeAlta in ChatGPT Settings.

Credentials are written under CodeAlta's state folder, protected with Windows DPAPI or owner-only permissions on Unix: never copy them into source control or a report. Plan usage is at [ChatGPT Settings → Usage](https://chatgpt.com/settings/usage); to see it in CodeAlta, see [Subscription usage](readme.md#subscription-usage). For a second account whose Codex CLI uses another folder, set `codex_home` to that folder.

### Web search

The model can search the web through your plan; OpenAI runs the search during the answer. Each search shows as a `web_search` tool call, and the answer cites its sources.

```toml
[providers.codex]
type = "codex"
web_search = "live" # "cached" (the default), "live" or "disabled"
```

- `cached`: the index OpenAI keeps, which can be a little behind the web.
- `live`: the web as it is now.
- `disabled`: no search.

What a search costs of your plan, and whether your workspace allows it, depend on your account. A compaction summary never searches.

### Fast routing

Fast routing is off by default. To turn it on:

```toml
[providers.codex]
type = "codex"
service_tier = "priority" # "fast" is an alias
```

- CodeAlta asks for the priority tier only when the selected model offers it; otherwise it uses standard routing and shows a warning.
- It applies to every session of the provider, its sub-agents and compactions, and may use more of your plan.
- `service_tier = "default"`, or no setting, returns to standard routing.

### Codex reasoning

CodeAlta offers the reasoning efforts Codex lists for each model; GPT-5.6 Sol, Terra and Luna go up to `max`. Codex's `ultra` level is not offered. A model can ask for several tool calls in one answer; CodeAlta runs them one after the other.

## Codex CLI

The `codex-cli` provider runs OpenAI's Codex agent itself, with your ChatGPT plan. Codex signs in with its own ChatGPT sign-in, which ChatGPT Business and Enterprise workspaces accept. Choose it when the [Codex (ChatGPT)](#codex-chatgpt) provider is refused with `3p_delegated_access_policy_denied`, or when you want Codex's own agent and tools.

| | Codex (ChatGPT) | Codex CLI |
| --- | --- | --- |
| Runs the model loop | CodeAlta | Codex |
| Tools | CodeAlta's | Codex's shell, `apply_patch`, web search and image generation, with the tools of CodeAlta (`alta`, plugins, MCP) |
| Sign-in | CodeAlta's ChatGPT sign-in | Codex's ChatGPT sign-in, run by CodeAlta |
| Business and Enterprise workspaces | refused by most | accepted |

To add it in CodeAlta Desktop: **Settings > Providers > Add provider > Codex CLI…**, then **Add**. CodeAlta downloads the version of Codex it is tested with (Codex 0.165.0, about 100 MB) and adds the provider. Then **Sign in** in its form:

- **Sign in with ChatGPT** opens the ChatGPT sign-in in the browser.
- **Sign in with a device code** shows a code to enter on the page it opens, for a computer whose browser cannot return to CodeAlta.

Then **Test selected provider** to see its models. Codex keeps its sign-in in its own folder; CodeAlta never reads it.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-codex-cli.webp" alt="Settings > Providers with the Codex CLI provider selected: its form with Code Mode, its permissions, the version of Codex that CodeAlta downloaded, what it supports and its Sign in button" loading="lazy">
  <figcaption class="small text-secondary mt-2">The Codex CLI provider: the version of Codex that CodeAlta runs, and <strong>Sign in</strong>.</figcaption>
</figure>

```toml
[providers.codex-cli]
type = "codex-cli"
display_name = "Codex CLI"
# codex_home = "~/.codex-work"   # its folder: sign-in and threads (default: ~/.alta/plugin-data/codex-cli/<key>)
# command = "/opt/homebrew/bin/codex"   # a codex or codex-app-server you installed, instead of the one CodeAlta downloads
```

- **Its own folder.** Each provider has a Codex folder of its own (`CODEX_HOME`), separate from the `~/.codex` of your Codex CLI: CodeAlta writes the settings of that folder, so your own settings, MCP servers and skills do not apply. Several accounts are several providers. `~/.codex` cannot be used.
- **CodeAlta's way.** Codex gets CodeAlta's instructions and tools. Its own sub-agents, skills, plugins, hooks and memories are off: sub-agents are CodeAlta sessions you see, skills are CodeAlta's. The AGENTS.md of the project reaches it with CodeAlta's instructions.
- **Images.** With a model that reads images, Codex can generate one when you ask for it. The image shows in the session after its call, and CodeAlta keeps a copy with the session. Codex also saves it in its folder, under `generated_images`.
- **Permissions.** `default` asks you before the commands and edits Codex asks for, `acceptEdits` allows the edits, `bypassPermissions` allows everything. Codex's own sandbox is off: a [sandboxed session](../sessions/sandbox.md#providers) runs Codex in CodeAlta's sandbox.
- **Sessions.** A session keeps its Codex thread: it goes on after a restart, and a fork or a rewind continues from the same point of the thread. Codex compacts its own context. A message sent while Codex works steers its turn, and ends a wait of Codex, which shows as a call.
- **The version.** A provider without `command` runs the version CodeAlta is tested with, and moves to the next one with CodeAlta. A program you name runs as it is; its status says when it is another version.
- **Workspace policies.** A Business or Enterprise workspace can forbid what CodeAlta runs Codex with (full access, with its own sandbox off). The session then says that the configuration could not be loaded.

## Copilot

```toml
[providers.copilot]
enabled = true
display_name = "Copilot"
type = "copilot"
model = "claude-sonnet-4.6"
reasoning_effort = "high"
auth_source = "github_device_flow"
```

Sign in with **Sign in with the browser** or **Sign in with a device code**. Where nobody can sign in, set `auth_source` to read a GitHub token or a Copilot token from an environment variable (see [Codex, Copilot and xAI](configuration.md#codex-copilot-and-xai)). Each model is called through the endpoint Copilot lists for it: Messages for Claude, Responses for GPT and Grok, chat completions otherwise.

## xAI Grok

The `xai` provider uses the xAI API at `https://api.x.ai/v1`. Its sign-in unlocks SuperGrok and Grok Heavy on accounts that subscribed.

```toml
[providers.xai]
enabled = true
display_name = "xAI Grok"
type = "xai"
model = "grok-4.3"
reasoning_effort = "high"
auth_source = "xai_browser_oauth"
```

- `xai_browser_oauth`: sign in in the browser.
- `xai_device_flow`: sign in with a device code, for a computer without a browser (SSH, server).

CodeAlta renews the tokens by itself. The models listed are the text models of xAI (`grok-4.3`, `grok-4`, `grok-4-fast` when the list cannot be read).

## OpenAI

```toml
[providers.openai]
enabled = true
display_name = "OpenAI"
type = "openai-responses"
model = "gpt-5.5"
reasoning_effort = "high"
api_key_env = "CODEALTA_OPENAI_API_KEY"
api_url = "https://api.openai.com/v1"
```

Use `openai-chat` for an endpoint that speaks the Chat Completions API. A reasoning model of the OpenAI API shows its reasoning summary in the timeline.

## Anthropic

```toml
[providers.anthropic]
enabled = true
display_name = "Anthropic"
type = "anthropic"
model = "claude-sonnet-4-6"
api_key_env = "CODEALTA_ANTHROPIC_API_KEY"
```

An Anthropic-compatible provider uses the same type with its own `api_url`, and `single_model_id` when it lists no models.

- **Another key**: you can change the key for one of another account and go on with the sessions in progress. The model continues without the earlier reasoning that the new account cannot verify.
- **Output limit**: CodeAlta sends the output limit of the model. For a custom model that publishes none, set `output_token_limit` in its [model overrides](configuration.md#model-metadata-overrides).
- **Refusals**: when a Claude model declines a request, the run fails with the category and explanation Anthropic gives; what the model wrote before is not kept. Rephrase, or continue with another model.
- **Refusal fallback**: on the Claude API, Claude Fable 5.1, Opus 5.5, Opus 5 and Sonnet 5.5 have a declined request run again by Anthropic on another model, shown by a model line in the timeline. The run fails only when that model declines too. `refusal_fallback = false` turns it off for the provider.
- **PDF files**: a PDF added to a prompt (paperclip, `@`, or a Markdown link) reaches a Claude model that reads PDFs, of Anthropic or of Claude Code, as the document itself, up to 20 MB. Other models get its text (see [Attach files](../getting-started.md#attach-files)).
- **Overload**: when Anthropic is overloaded in the middle of an answer, before a tool call, CodeAlta asks again after two seconds. Google and Mistral get the same retry.
- **Retired models** are not listed; a session on one asks you to choose another model. A deprecated model runs until it retires: **Models** marks it **Deprecated**, with its retirement date when known.

## Gemini and Vertex AI

`google-genai` uses the Gemini API with an API key. Leave `model` unset to choose among the models it lists:

```toml
[providers.gemini]
enabled = true
display_name = "Gemini"
type = "google-genai"
api_key_env = "CODEALTA_GEMINI_API_KEY"
model = "gemini-2.5-pro"
```

`vertex-ai` uses a Google Cloud project and location instead of a key; both are required:

```toml
[providers.vertex]
enabled = true
display_name = "Vertex"
type = "vertex-ai"
project = "my-gcp-project"
location = "europe-west4"
models_dev_provider_id = "google"
model = "gemini-2.5-pro"
reasoning_effort = "high"
```

## Azure OpenAI

Azure names models by deployment: set `model`, or `single_model_id`, to your deployment name. Azure lists no models, so the deployment of `model` is the one shown.

```toml
[providers.azure-openai]
enabled = true
display_name = "Azure OpenAI"
type = "azure-openai"
model = "my-gpt-4o-mini-deployment"
api_key_env = "CODEALTA_AZURE_OPENAI_API_KEY"
api_url = "https://your-resource.openai.azure.com"
network_timeout_seconds = 180
```

Raise `network_timeout_seconds` (100 seconds by default) when long answers fail on a network timeout.

## Mistral

```toml
[providers.mistral]
enabled = true
display_name = "Mistral"
type = "mistral"
api_key_env = "CODEALTA_MISTRAL_API_KEY"
model = "mistral-small-latest"
models_dev_provider_id = "mistral"
# api = "responses"   # the Responses API of Mistral (preview) instead of Chat Completions
```

- **The API** is Chat Completions. Set `api = "responses"` for the [Responses API](https://docs.mistral.ai/studio/conversations/responses) of Mistral, which is in preview. It keeps nothing between requests: CodeAlta sends the whole conversation each time, as it does for Chat Completions.
- **Reasoning**: each model offers the efforts Mistral takes for it. Most take **None** and **High**; Mistral Large 4 takes **Low**, **Medium** and **High**, and reasons when no effort is chosen; GLM 5.3 takes **Low**, **High** and **Max**.

## Mistral Vibe

The `mistral-vibe` provider signs in to Mistral Vibe, with a Mistral Pro subscription or higher, instead of an API key:

```toml
[providers.mistral-vibe]
enabled = true
display_name = "Mistral Vibe"
type = "mistral-vibe"
model = "mistral-medium-latest"
```

In CodeAlta Desktop, open its form in **Settings > Providers** and choose **Sign in with the browser**. The page of Mistral opens: sign in, choose the workspace, and come back. The sign-in creates an API key in that workspace, as the Vibe CLI does; CodeAlta keeps it in its own state, and **Sign out** removes it from there. The key itself stays in the workspace until you revoke it in the console of Mistral.

The models are the ones Mistral Vibe offers, GLM 5.3 among them. The CodeAlta TUI uses a provider signed in from CodeAlta Desktop, but cannot sign in itself. `api = "responses"` works here too.

## Alibaba Cloud

Alibaba Cloud Model Studio has an [OpenAI-compatible Chat API](https://modelstudio.console.alibabacloud.com/ap-southeast-1?tab=api#/api/?type=model&url=3016807):

```toml
[providers.alibaba]
enabled = true
display_name = "Alibaba"
type = "openai-chat"
model = "qwen3.7-max"
api_key_env = "CODEALTA_ALIBABA_API_KEY"
api_url = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1"
```

CodeAlta already sends what this API expects: no developer role, no `store`, `max_tokens` for the output limit, no `reasoning_effort`, the usage of a streamed answer, and Alibaba's thinking with `enable_thinking` and `preserve_thinking`. Other Alibaba parameters (`thinking_budget`, `top_k`, `enable_search`, `search_options`) are yours to add in `request.extra_body`; see [Request customizations](configuration.md#request-customizations).

## llama.cpp

A llama.cpp server (`llama-server`) has a type of its own, `llama-cpp`:

```toml
[providers.llama]
type = "llama-cpp"
api_url = "http://localhost:9931/v1"  # optional: the address of llama-server 0.8.0 by default
```

- **No key** is needed. Give one with `api_key` or `api_key_env` only for a server started with `--api-key`, or behind a proxy that asks for it.
- **The model** is the one the server loaded. The context it was started with (`--ctx-size`) sizes the compaction, and a model started with its vision projector (`--mmproj`) takes pasted images. **Refresh** reads them again after you restart the server with another model.
- **The API** is Chat Completions. Set `api = "responses"` for the Responses API of llama-server 0.8.0 or later; with an older server, the answer of a reasoning model shows twice there.
- **Reasoning** follows the options of the server, such as `--reasoning-effort`: the server lists no efforts for its model, so CodeAlta offers none.
- A slow model on a small machine may need a longer `network_timeout_seconds`.

The server runs the model: what one model does wrong (a tool call left in its thinking, a template that ignores the effort) is fixed in llama.cpp or with its options, not in CodeAlta.

## Other endpoints

Any endpoint that speaks a supported API works as a provider of your own: **Custom provider…** in **Add provider**, or a table in the file with its `type`, `api_url` and key.

- **DeepSeek, MiniMax, Kimi and Z.ai** are built in, with what their APIs expect already set; see the [built-in providers](readme.md#built-in-providers).
- **OpenRouter** and similar services use `openai-responses` or `openai-chat`; see [Request customizations](configuration.md#request-customizations) for their headers.
- **OpenCode Go and Zen** need a header with the session id: see [Session values in headers](configuration.md#session-values-in-headers).
- **Local servers** such as Ollama use `openai-chat` with their local `api_url`; llama.cpp has [its own type](#llamacpp).
- An endpoint that serves one model, or lists none, takes `single_model_id` (see [Single-model endpoints](configuration.md#single-model-endpoints)); one that behaves differently from the API it imitates takes a [compatibility profile](configuration.md#compatibility-profile).
