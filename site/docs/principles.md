---
title: CodeAlta Principles
---

# CodeAlta Principles

CodeAlta is designed around a small set of product and engineering principles. They are meant to keep the terminal workspace efficient, inspectable, and practical for real development work as the feature set grows.

**Efficient. Transparent. Keyboard-first. Session-oriented. Provider-agnostic. Native .NET. Error-aware. Pluggable.**

## <span class="principle-doc-icon" style="--accent: #f472ff; --accent-2: #38bdf8;"><i class="bi bi-arrows-collapse"></i></span> Efficient interface

CodeAlta should use terminal space efficiently: high signal, low ceremony, no chat-bubble padding. The main timeline should be a full-width working stream where user prompts, assistant messages, reasoning and status entries, tool calls, statistics, and file-change summaries share the same surface.

XenoAtom.Terminal.UI makes this practical inside a terminal: compact layouts, dynamic controls, dialogs, tabs, popups, syntax-highlighted editors, selectable color themes, and interactive timeline entries.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-theme-multi.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-theme-multi.png" alt="CodeAlta theme selector showing multiple available themes" loading="lazy">
  </a>
  <figcaption>Compact layouts and selectable themes help the information-rich terminal surface fit the user and terminal environment.</figcaption>
</figure>

**What this means in practice:**

- Important event types should be visually distinct through icons, colors, and headers.
- Tool calls should be grouped as compact chips instead of expanding into noisy logs by default.
- Summaries should show status, tool name, output size, line counts, and file-change totals.
- Details should stay available through expandable sections and dialogs.
- Color themes should be easy to select so the information-rich surface fits the user and terminal environment.
- Compactness should reduce wasted space, not hide state.

## <span class="principle-doc-icon" style="--accent: #60a5fa; --accent-2: #c084fc;"><i class="bi bi-eye"></i></span> Transparent execution

CodeAlta should keep agent execution inspectable. Verbose details can be collapsed by default, but they should remain reachable when they affect review, debugging, or trust.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-tool-input-output-dialog.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-tool-input-output-dialog.png" alt="CodeAlta tool input and output dialog showing inspectable execution details" loading="lazy">
  </a>
  <figcaption>Tool details remain available for review, debugging, and trust without flooding the default timeline.</figcaption>
</figure>

**What this means in practice:**

- System prompts should be visible and expandable.
- System-prompt changes should be able to show diffs.
- Compaction events should be represented explicitly and their summaries should be inspectable.
- Model and provider changes should be visible in session history.
- Tool results and modified files should be inspectable after the fact.
- Usage and context information should be available without leaving the app.

## <span class="principle-doc-icon" style="--accent: #34d399; --accent-2: #facc15;"><i class="bi bi-keyboard"></i></span> Keyboard-first workflow

CodeAlta should support normal work from the keyboard, with mouse interactions as convenience rather than requirement. Commands, slash aliases, command discovery, and shortcuts should cover the common development loop.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-command-bar-with-shortcuts.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-command-bar-with-shortcuts.png" alt="CodeAlta command bar showing commonly used keyboard shortcuts" loading="lazy">
  </a>
  <figcaption>Common shortcuts stay visible so discovery does not interrupt the prompt-first workflow.</figcaption>
</figure>

**What this means in practice:**

- Open management dialogs such as providers, models, plugins, settings, logs, usage, session reports, and theme or workspace preferences.
- Move focus between the sidebar, prompt, model selector, editor tabs, and session tabs.
- Switch tabs and open or reopen project and session surfaces.
- Navigate the timeline by previous or next user or assistant message, first message, and latest message.
- Open files, attach project files and folders to prompts, and inspect editor tabs.
- Send, queue, steer, abort, compact, or delegate session work.
- Close popups and return to the prompt without rebuilding the working context.

## <span class="principle-doc-icon" style="--accent: #22d3ee; --accent-2: #a78bfa;"><i class="bi bi-diagram-3"></i></span> Session-oriented workspace

CodeAlta should model agent work as durable sessions rather than disposable chat scrollback. Sessions keep history, provider state, queue state, journals, and project scope together, including parent and child relationships when multiple agents cooperate on the same goal.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-theme-default.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-theme-default.png" alt="CodeAlta default theme showing durable workspace sessions and timeline state" loading="lazy">
  </a>
  <figcaption>Durable project sessions keep navigation, history, provider state, queues, and prompt context together across the workspace.</figcaption>
</figure>

**What this means in practice:**

- Global sessions should support planning, cross-project coordination, and multi-agent delegation.
- Project sessions should keep project context, prompt history, provider state, queues, and session journals together.
- Closing a tab should not have to stop running work.
- Delegated child sessions should be visible and able to report back to the parent session.
- Parent sessions should make it possible to compare, merge, or route results from multiple agents without losing which child session produced which answer.
- Busy sessions should preserve prompts through queues and steering fallback behavior.

## <span class="principle-doc-icon" style="--accent: #fb923c; --accent-2: #38bdf8;"><i class="bi bi-cpu"></i></span> Provider-agnostic runtime

CodeAlta should model LLM execution as providers, not as a single-vendor integration. Credentials, endpoints, model discovery, selected model, reasoning effort, capabilities, and context metadata should fit the same workspace model where possible.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-model-providers.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-model-providers.png" alt="CodeAlta Model Providers dialog for configuring providers and models" loading="lazy">
  </a>
  <figcaption>Provider setup keeps credentials, endpoints, model selection, capabilities, tests, and login flows in one place.</figcaption>
</figure>

**What this means in practice:**

- Hosted APIs, subscription-backed providers, cloud providers, and compatible or custom endpoints should fit the same UI model.
- Local models should be reachable through whichever supported provider adapter or API shape fits them; no single protocol should be the default assumption.
- Model, reasoning, tool and image capability, and context metadata should be visible in selectors and dialogs.
- Provider setup should work through dialogs or TOML.
- Project-local provider configuration should be able to override global defaults.

## <span class="principle-doc-icon" style="--accent: #818cf8; --accent-2: #2dd4bf;"><i class="bi bi-braces-asterisk"></i></span> Native .NET foundation

CodeAlta should stay native to C#/.NET and keep the runtime and dependency surface easy to understand, audit, and control.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-code-editor.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-code-editor.png" alt="CodeAlta native terminal editor with syntax-highlighted C# code" loading="lazy">
  </a>
  <figcaption>CodeAlta stays in the .NET ecosystem while still providing native terminal dialogs, tabs, and syntax-highlighted editing.</figcaption>
</figure>

**What this means in practice:**

- The main app, orchestration libraries, plugin abstractions, and tests should live in the .NET ecosystem.
- XenoAtom libraries are first-party dependencies owned by the same author as CodeAlta.
- Major external dependencies should come from established vendors, platform owners, model providers, and other well-maintained .NET libraries.
- A narrower dependency graph should reduce supply-chain exposure compared with large transitive dependency stacks, without pretending to eliminate supply-chain risk.
- Cross-platform terminal behavior should be a core product constraint, not an afterthought.

## <span class="principle-doc-icon" style="--accent: #f43f5e; --accent-2: #fbbf24;"><i class="bi bi-life-preserver"></i></span> Actionable errors

CodeAlta should turn setup and runtime failures into visible repair paths. Errors should appear close to the workflow that produced them, with enough context to fix or investigate.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-config-recovery.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-config-recovery.png" alt="CodeAlta configuration recovery editor with TOML validation feedback" loading="lazy">
  </a>
  <figcaption>Configuration recovery opens directly in the terminal with syntax highlighting and validation feedback when startup settings need repair.</figcaption>
</figure>

**What this means in practice:**

- First launch should open the Model Providers dialog when no usable provider configuration exists.
- Provider configuration should give immediate feedback about missing or conflicting settings.
- Provider tests should validate credentials and endpoints before applying changes.
- Invalid `~/.alta/config.toml` should open a TOML recovery editor with live parse feedback.
- Errors from agent runs should appear in the timeline, not only in log files.
- Logs should be available in-app for provider, credential, plugin, and startup troubleshooting.
- Plugin safe modes and bypasses should exist for startup recovery when extension code breaks.

## <span class="principle-doc-icon" style="--accent: #a3e635; --accent-2: #06b6d4;"><i class="bi bi-puzzle"></i></span> Plugin support

CodeAlta should support trusted local plugins that remain visible as source and manageable from the UI. Extension should not make the core workflow opaque.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-plugins.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-plugins.png" alt="CodeAlta plugin management dialog with plugin diagnostics and contributions" loading="lazy">
  </a>
  <figcaption>Plugin state, diagnostics, contributions, and source actions stay inspectable from the management UI.</figcaption>
</figure>

**What this means in practice:**

- Plugins should be able to live under `~/.alta/plugins/` or project `.alta/plugins/` folders.
- Plugins should be able to add commands, prompt processors, UI regions, tools, resource roots, timeline projections, and `alta` live-tool commands.
- Plugin diagnostics, state, and contributions should be inspectable.
- Safe-mode and no-plugin startup paths should provide recovery when extension code breaks.

<style>
.principle-doc-icon {
  display: inline-grid;
  place-items: center;
  width: 2.35rem;
  height: 2.35rem;
  margin-right: .6rem;
  border-radius: .85rem;
  color: white;
  background: linear-gradient(135deg, var(--accent), var(--accent-2));
  box-shadow: 0 .65rem 1.45rem color-mix(in srgb, var(--accent) 26%, transparent);
  font-size: 1.05rem;
  vertical-align: .13em;
}
.principle-doc-shot {
  position: relative;
  margin: 1.45rem 0 2rem;
  border: 1px solid rgba(255, 255, 255, .13);
  border-radius: 1.2rem;
  background: linear-gradient(180deg, rgba(11, 18, 32, .96), rgba(3, 10, 19, .96));
  box-shadow: inset 0 0 0 1px rgba(255,255,255,.035), 0 1rem 2.4rem rgba(0,0,0,.24);
  overflow: hidden;
}
.principle-doc-shot a,
.principle-doc-shot img {
  display: block;
}
.principle-doc-shot img {
  width: 100%;
  height: auto;
}
.principle-doc-shot figcaption {
  margin: 0;
  padding: .75rem 1rem;
  color: rgba(234, 242, 255, .70);
  background: linear-gradient(90deg, rgba(255,255,255,.075), rgba(255,255,255,.025));
  font-size: .92rem;
}
</style>
