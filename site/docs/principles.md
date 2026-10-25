---
title: CodeAlta Principles
---

# CodeAlta Principles

A few product and engineering principles keep CodeAlta efficient, inspectable and practical for real development work as it grows. They guide CodeAlta Desktop and CodeAlta TUI alike.

**Efficient. Transparent. Keyboard-first. Session-oriented. Provider-agnostic. Native .NET. Error-aware. Extensible.**

## <span class="principle-doc-icon" style="--accent: #f472ff; --accent-2: #38bdf8;"><i class="bi bi-arrows-collapse"></i></span> Efficient interface

High signal, low ceremony, no chat-bubble padding. The timeline is a full-width working stream where prompts, answers, reasoning, tool calls, statistics and file changes share one surface.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-theme-multi.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-theme-multi.png" alt="CodeAlta theme selector showing multiple available themes" loading="lazy">
  </a>
  <figcaption>Compact layouts and selectable themes help the information-rich terminal surface fit the user and terminal environment.</figcaption>
</figure>

**What this means in practice:**

- Event types are told apart by icons, colors and headers.
- Tool calls are compact chips, not noisy logs.
- Summaries show status, tool name, output size, line counts and file-change totals.
- Details stay one click away, in expandable sections and dialogs.
- Color themes are easy to choose.
- Compactness removes wasted space, never state.

## <span class="principle-doc-icon" style="--accent: #60a5fa; --accent-2: #c084fc;"><i class="bi bi-eye"></i></span> Transparent execution

Agent work stays inspectable. Verbose details can be collapsed, but stay reachable when they matter for review, debugging or trust.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-tool-input-output-dialog.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-tool-input-output-dialog.png" alt="CodeAlta tool input and output dialog showing inspectable execution details" loading="lazy">
  </a>
  <figcaption>Tool details remain available for review, debugging, and trust without flooding the default timeline.</figcaption>
</figure>

**What this means in practice:**

- System prompts are visible, and their changes show as diffs.
- Compactions appear in the timeline, with their summaries.
- Model and provider changes are visible in the session history.
- Tool results and changed files can be inspected afterwards.
- Usage and context are a shortcut away.

## <span class="principle-doc-icon" style="--accent: #34d399; --accent-2: #facc15;"><i class="bi bi-keyboard"></i></span> Keyboard-first workflow

Normal work is done from the keyboard; the mouse is a convenience. Commands, slash commands, command discovery and shortcuts cover the development loop.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-command-bar-with-shortcuts.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-command-bar-with-shortcuts.png" alt="CodeAlta command bar showing commonly used keyboard shortcuts" loading="lazy">
  </a>
  <figcaption>Common shortcuts stay visible so discovery does not interrupt the prompt-first workflow.</figcaption>
</figure>

**What this means in practice:**

- Open providers, models, plugins, settings, logs, usage and session reports.
- Move between the sidebar, the prompt, the model selector and the tabs.
- Switch, open and reopen tabs.
- Go to the previous or next message, the first or the latest.
- Open files, and attach files and folders to prompts.
- Send, queue, steer, abort, compact or delegate.
- Close a popup and be back at the prompt.

## <span class="principle-doc-icon" style="--accent: #22d3ee; --accent-2: #a78bfa;"><i class="bi bi-diagram-3"></i></span> Session-oriented workspace

Agent work is a durable session, not disposable chat scrollback. A session keeps its history, provider state, queue and project together, with its parent and children when several agents work on one goal.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-theme-default.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-theme-default.png" alt="CodeAlta default theme showing durable workspace sessions and timeline state" loading="lazy">
  </a>
  <figcaption>Durable project sessions keep navigation, history, provider state, queues, and prompt context together across the workspace.</figcaption>
</figure>

**What this means in practice:**

- Sessions of no project plan and coordinate work across projects.
- Closing a tab does not stop running work.
- Child sessions are visible and report back to their parent.
- A parent compares, merges or routes the results of several agents and knows which child gave which answer.
- A busy session keeps your prompts in its queue.

## <span class="principle-doc-icon" style="--accent: #fb923c; --accent-2: #38bdf8;"><i class="bi bi-cpu"></i></span> Provider-agnostic runtime

Models run through providers, not a single-vendor integration. Credentials, endpoints, models, reasoning effort, capabilities and context limits work the same way for every provider.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-model-providers.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-model-providers.png" alt="CodeAlta Model Providers dialog for configuring providers and models" loading="lazy">
  </a>
  <figcaption>Provider setup keeps credentials, endpoints, model selection, capabilities, tests, and login flows in one place.</figcaption>
</figure>

**What this means in practice:**

- Hosted APIs, subscriptions, cloud providers and custom endpoints are set up the same way.
- Local models are reached through whichever supported API fits them.
- Reasoning, tool and image support, and context limits are shown where you choose a model.
- Providers are set up in the app or in TOML.
- A project can override the global provider defaults.

## <span class="principle-doc-icon" style="--accent: #818cf8; --accent-2: #2dd4bf;"><i class="bi bi-braces-asterisk"></i></span> Native .NET foundation

CodeAlta stays native to C#/.NET, with a runtime and dependencies that are easy to understand, audit and control.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-code-editor.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-code-editor.png" alt="CodeAlta native terminal editor with syntax-highlighted C# code" loading="lazy">
  </a>
  <figcaption>CodeAlta stays in the .NET ecosystem while still providing native terminal dialogs, tabs, and syntax-highlighted editing.</figcaption>
</figure>

**What this means in practice:**

- The app, its libraries, its plugin interfaces and its tests are .NET.
- The XenoAtom libraries come from the author of CodeAlta.
- Other major dependencies come from established vendors, platform owners, model providers and well-maintained .NET libraries.
- A narrower dependency graph reduces supply-chain exposure, without claiming to remove it.
- Working the same on every platform is a core constraint, not an afterthought.

## <span class="principle-doc-icon" style="--accent: #f43f5e; --accent-2: #fbbf24;"><i class="bi bi-life-preserver"></i></span> Actionable errors

A failure comes with a way to repair it, shown where it happened, with enough context to fix or investigate.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-config-recovery.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-config-recovery.png" alt="CodeAlta configuration recovery editor with TOML validation feedback" loading="lazy">
  </a>
  <figcaption>Configuration recovery opens directly in the terminal with syntax highlighting and validation feedback when startup settings need repair.</figcaption>
</figure>

**What this means in practice:**

- Without a usable provider, the first launch opens the provider setup.
- The provider editor flags missing or conflicting settings at once, and tests credentials and endpoints.
- An invalid `~/.alta/config.toml` opens a recovery editor that checks it as you type.
- Errors of agent runs appear in the timeline, not only in the logs.
- The logs are in the app.
- A plugin that breaks the startup can be turned off for a run.

## <span class="principle-doc-icon" style="--accent: #a3e635; --accent-2: #06b6d4;"><i class="bi bi-puzzle"></i></span> Extensible workflows

Workflows are composed before any code is written: agent prompts define modes, the `alta` tool lets agents drive CodeAlta, MCP servers and skills add tools and reusable context, and plugins add trusted .NET code when the app itself must change.

<figure class="principle-doc-shot">
  <a href="{{site.basepath}}/img/alta-system-prompt-and-user-prompt.png" target="_blank" rel="noopener">
    <img src="{{site.basepath}}/img/alta-system-prompt-and-user-prompt.png" alt="CodeAlta timeline showing selected agent prompt and system prompt details" loading="lazy">
  </a>
  <figcaption>Agent prompt and system prompt details stay visible in the timeline so workflow behavior can be inspected and reviewed.</figcaption>
</figure>

**What this means in practice:**

- Modes such as Default, Plan, review, triage or release are agent prompts you choose, and a project can override them.
- With `alta`, agents inspect projects and sessions, ask questions, keep notes, set reminders, switch prompts and coordinate child sessions.
- MCP servers bring other tools without a CodeAlta plugin for each.
- Skills are reusable context that runs no code.
- Plugins stay source-visible and manageable, for what needs the app's interface, runtime, prompts, timeline, tools or `alta` commands.
- The state, diagnostics and recovery of extensions stay inspectable.

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
