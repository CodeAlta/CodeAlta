# CodeAlta [![ci](https://github.com/CodeAlta/CodeAlta/actions/workflows/ci.yml/badge.svg)](https://github.com/CodeAlta/CodeAlta/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.Tui.svg)](https://www.nuget.org/packages/CodeAlta.Tui/)

CodeAlta is a terminal workspace for agentic coding. It brings model-provider setup, project navigation, prompt attachments, durable sessions, delegated work, and trusted local plugins behind the `altatui` command.

> CodeAlta is pre-release software. Configuration, screenshots, and extension APIs may change before `1.0`.

> On this development branch, the terminal package is renamed to `CodeAlta.Tui` / `altatui`. Installation commands below describe that package once released. Existing releases used `CodeAlta` / `alta`; the replacement desktop head is still in development. Shared `~/.alta` state and the in-session `alta` tool keep their identities.

The development desktop includes an opt-in [workspace snapshot browser](src/CodeAlta/README.md#browse-a-task-owned-catalog-copy), bounded persisted-event history, and separately consented [existing-session text submission](src/CodeAlta/README.md#explicit-owned-text-submission) with a selected-session live status/text window and two recently reported plain tool activities for trusted task-owned roots. The live window is partial, not a complete transcript; tool reports and submission receipts do not establish permission approval or live-run completion. Full agent workflows and native qualification remain incomplete. This is not shared-profile startup. Continue using `altatui` for normal agent workflows.

Shared-runtime [provider-event forwarding ownership](doc/runtime-provider-event-forwarding.md) now retains admitted callback work through queue/parent bookkeeping and shutdown joins. It remains separate from the partial Desktop display window and does not guarantee event replay or shutdown of noncooperative providers.

Experimental owned Desktop mode also offers explicitly opted-in plain-command review with **Allow once / Deny / Cancel**. Permissions remain denied by default; pending requests are refreshed manually, and approval can execute commands with the host's privileges. **Observe retained decision** checks the original response locally across selection changes, without resending it; renderer reload loses that record. See the [owned-mode safety and usage notes](src/CodeAlta/README.md#explicit-owned-text-submission); this is not full permission or ask parity.

Owned Desktop submissions also support restricted caller-session `alta ask --stdin` questions. **Refresh asks**, answer the original ask through a new text submission, or cancel an unclaimed ask without stopping its run. **Observe original action** reads retained backend evidence without resending; uncertainty blocks competing actions. There are no attached-file reviews, general LiveTool commands, provider-input activation or restart recovery in this workflow.

Owned Desktop mode also provides **Refresh notes** for the selected session's durable notes, without starting a provider. The read-only view shows complete literal text up to 16,384 UTF-16 units; oversized notes are refused rather than truncated. Refresh is manual, and a read failure is not reported as empty notes. Editing and rich Markdown presentation remain separate work.

Live-display reconnect waits for the previous observation's successful cleanup; cleanup failure blocks reopening in that view. Runtime refreshes retain only one frontend waiter and the latest explicit pending refresh. Valid late host/runtime changes disable shared mutation controls, but observation refresh/reload does not prove backend termination or recover missing history and effects.

Live file-change notifications now invalidate the shared file-search cache without requiring an open terminal frontend. History replay does not repeat this invalidation; it remains best effort, not confirmation that a file write succeeded.

Plugin agent-event observation now admits at most 64 outstanding callbacks per activation, without queued waiters. Events rejected at capacity or during closing are not delivered to that plugin. Shutdown retains admitted callbacks and their dependencies until the required drain succeeds; a timeout does not make release safe. This is a lifetime prerequisite, not complete Desktop plugin parity or history recovery.

Separately add `--enable-owned-user-input` to the complete owned-mode command to review supported **nonsecret provider input**. Use **Refresh input** to list pending forms, then submit literal answers or cancel only that attempt. This does not approve commands or files. Never enter credentials: answers may enter provider tool results and history. Original actions survive panel remounts; after observing and acknowledging a terminal result, refresh explicitly for a new page. Lost outcomes and host restart cannot be recovered from list absence.

For providers supporting run-bound review, cancellation of that run invalidates its pending reviews, including requests without a run ID. Already accepted decisions cannot be revoked. In experimental owned mode, **Signal cancellation for observed run** targets only the explicitly refreshed runtime, attachment and run. Success means cancellation signalled, not run completion; stale or unsupported targets fail without fallback. Uncertain requests keep their original target and key for manual reconciliation or exact retry.

Owned mode also supports text steering of an explicitly refreshed runtime/run target. Uncertain requests retain their exact key and target for manual receipt reconciliation or deliberate retry; they are never automatically retried or redirected to a later run. This remains experimental, not complete session-command parity.

The same experimental mode can compact an explicitly observed attachment only if the supported provider admits it while idle, without waiting or replacing the target. Compaction uses context current at admission; inspect its receipt for success or busy/unsupported/failure outcomes.

The same experimental mode offers **Queue text — this host only** after manual runtime refresh, including while the observed attachment is busy. Reservation, host-only insertion and execution/cleanup are separate; queued text is not durable. Cancellation targets the original queued operation, not a later run. Uncertain requests retain their exact text/key/target for manual reconciliation or deliberate retry while the document remains open; reload does not reconstruct lost local intent. Existing terminal queue behavior is unchanged.

Experimental Send/Abort also retains exact local intent and live-waiter exclusion across selection changes. Manual receipt refresh cannot release an in-flight request; Abort-only recovery preserves unrelated composer text. Late epoch changes disable mutations, and reload does not reconstruct lost text or retry keys. Abort control settlement is not rollback or run termination.

In the owned Desktop composer, the Reminders icon or Ctrl+G, Ctrl+D (from the workspace or prompt) opens reminders for the verified selected session; it does not schedule anything or poll in the background. Create accepts whole seconds (1–86400) or invariant `HH:mm:ss` / `d.HH:mm:ss` with whole seconds only (`1.00:00:00` is the 24-hour maximum); total attempts remain 1–20. A list count is only as of its explicit refresh. Host changes or uncertain reminder admissions require inspection, not automatic retries. Catalog-only and unverified sessions cannot use the composer shortcut.
On the Reminders page, Ctrl+Enter invokes the existing Create button only when focus is on the current Create message, delay, repeat or Create button and no modal/confirmation or reminder admission blocks it. Enter in the message remains a newline; Ctrl+Enter in the Save editor, list, deletion confirmation, recovery or another pane does not create. Ctrl+S remains the scoped Save-message shortcut. This is not full TUI keyboard parity.

In the Desktop regular composer (owned or catalog/archived draft-only), typing `?` into an **exactly empty** prompt opens keyboard help; typing `/` opens the existing implemented-actions palette. These are transient keyboard shortcuts, not slash-command execution. Pasted text, composition, selection replacement and nonempty drafts remain literal; the expanded editor and other search/input fields do not use these shortcuts. Draft-only scopes can navigate/help but cannot gain mutation permissions from the palette.

The Desktop timeline/composer divider can be dragged to reserve more room for the regular composer. Focus the divider and use **Arrow Up** to enlarge the composer, **Arrow Down** to shrink it, or **Home** (or **Auto size**) to restore compact automatic sizing. A bounded in-memory preference is kept separately per host/project/session during this window's lifetime; it does not alter drafts, Send/Steer authority or profile settings. A shorter window temporarily clamps the visible size without changing the preference. The composer and timeline stay mounted during resizing, and long controls/recovery details remain scrollable.

The adjacent **Session info** icon opens saved catalog metadata for the selected session in owned, catalog-only, and archived draft-only regular composers. It does not read live runtime/model/prompt/usage data or grant mutation rights. Ctrl+G, Ctrl+T and the implemented-actions palette retain their exact unique selected-session eligibility; ambiguous identity still displays unverified scope through the icon but disables Copy ID. Closing info restores focus only while its original composer control remains current and no newer focus or modal owns it.

<p align="center">
  <img src="site/img/alta-theme-default.png" alt="CodeAlta terminal workspace using the default dark theme" width="920">
</p>

## 🚀 Install

Install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then install the CodeAlta terminal global tool:

```sh
dotnet tool install -g CodeAlta.Tui
altatui
```

Update an existing installation with:

```sh
dotnet tool update -g CodeAlta.Tui
```

On first launch, CodeAlta creates `~/.alta/config.toml`. Existing config files are left untouched on later launches so you can remove, rename, or customize bundled entries. If no provider is enabled yet, the Model Providers dialog opens so you can configure Codex, Copilot, xAI Grok, OpenAI/Azure OpenAI/Alibaba APIs, Anthropic, Gemini/Vertex, or custom endpoints.

CodeAlta also expects a current [Nerd Fonts](https://www.nerdfonts.com/) patched font in your terminal profile. If icons or tree glyphs look wrong, update to the latest Nerd Fonts release, remove stale older font copies, and select the refreshed Nerd Font family, such as `CaskaydiaCove Nerd Font`.

## ✨ What it gives you

- **Keyboard-first terminal workspace**: tabs, prompt editor, project sidebar, command discovery, model selectors, context status, and inspectable timeline cards stay in one TUI.
- **Conflict-aware file editing**: file tabs and attached ask-file reviews preserve supported Unicode encodings and line endings, retaining unsaved edits when disk content has changed.
- **Structured asks**: retain queued questions and choices in per-session order without requiring an open tab. Response attempts reject duplicate/stale submit and cancel callbacks; only positive runtime admission evidence consumes the ask. Uncertain responses block resubmission and local cancellation, with no recovery action yet. Pending asks are in-memory, not restart-persistent.
- **Multilingual UI**: choose Auto, English, Spanish, French, German, Japanese, or Simplified Chinese from Workspace Settings.
- **Provider-neutral model setup**: configure hosted APIs, subscription-backed Codex/Copilot/xAI Grok, cloud providers, and compatible endpoints with the same provider workflow.
- **Context-aware prompts**: select reusable agent prompt profiles, edit global/project prompt and system-prompt replacements or append-only extensions, attach files and folders with `@`, search GitHub issues with `#` in GitHub repositories, paste images when the selected model supports them, and inspect what context was sent.
- **Durable agent sessions**: keep project-scoped history in CodeAlta-owned journals, reopen sessions independently of provider startup, queue prompts on busy sessions, steer running work where supported, and compact long agent-runtime conversations.
- **Session notes**: keep sticky Markdown in the session journal, use `alta notes` even when the caller's tab is closed, and restore the latest notes when reopening it.
- **Actionable operations**: model/provider tests, startup config recovery, usage details, logs, modified-file summaries, and tool input/output dialogs are built into the workspace.
- **Trusted local extension points**: source plugins, Agent Skills-compatible skill folders, and the in-session `alta` live tool let you automate local workflows while keeping provenance visible.

## ⌨️ Common shortcuts

| Action | Shortcut / command |
| --- | --- |
| Help and command discovery | `F1`, `/help`, or `?` |
| Open project | `Ctrl+O` or `/open` |
| Attach project files | type `@` in the prompt |
| Manage prompts | `Ctrl+G Ctrl+H` or `/prompt` |
| Switch to next agent prompt | `Ctrl+T` or `/next_prompt` |
| Open model providers | `Ctrl+G Ctrl+R` or `/model_providers` |
| Refresh model providers | `/model_providers_refresh` |
| Browse models | `Ctrl+G Ctrl+O` or `/models` |
| Open logs | `Ctrl+G Ctrl+L` or `/logs` |
| Toggle navigator | `Ctrl+G Ctrl+G` |
| Switch tabs | `Ctrl+Alt+Left` / `Ctrl+Alt+Right` |
| Focus sidebar / prompt | `Ctrl+G Ctrl+S` / `Ctrl+G Ctrl+P` |

## 📖 Documentation

`altatui --help` (or `-h`) and `altatui --version`, each used alone, print built-in information without initializing application state or plugins. Other command paths, including `--plugins-status` and plugin commands, acquire the shared `~/.alta/alta.lock` before mutable startup and retain it through cleanup. Plugin-specific or combined help invocations follow that guarded path. If inspecting the recorded owner fails, startup conservatively refuses to reclaim its lock.

The prompt manager preserves unsaved edits on external-file conflicts and requires confirmation before retrying; built-ins stay read-only and same-scope creation never overwrites an existing prompt.

- User guide and screenshots: <https://codealta.github.io/>
- Getting started: <https://codealta.github.io/docs/getting-started/>
- Model provider configuration: <https://codealta.github.io/docs/model-providers/>
- Prompts and instructions: <https://codealta.github.io/docs/prompts/>
- In-session `alta` live tool: [doc/live-tool.md](doc/live-tool.md)
- Skills: [doc/skills.md](doc/skills.md)
- Maintainer notes: [doc/readme.md](doc/readme.md)

## 🪪 License

CodeAlta is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).
