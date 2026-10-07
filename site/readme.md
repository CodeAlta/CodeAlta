---
title: Home
layout: simple
og_type: website
---

<div class="alta-home">

<section class="alta-hero">
  <pre class="codealta-ascii-logo" aria-label="CodeAlta"><span class="logo-code">   ██████                  ██           </span><span class="logo-alta">     ██       ██    ██</span>
<span class="logo-code">  ██░░░░██                ░██           </span><span class="logo-alta">    ████     ░██   ░██</span>
<span class="logo-code"> ██    ░░    ██████       ░██   █████   </span><span class="logo-alta">   ██░░██    ░██  ██████   ██████</span>
<span class="logo-code">░██         ██░░░░██   ██████  ██░░░██  </span><span class="logo-alta">  ██  ░░██   ░██ ░░░██░   ░░░░░░██</span>
<span class="logo-code">░██        ░██   ░██  ██░░░██ ░███████  </span><span class="logo-alta"> ██████████  ░██   ░██     ███████</span>
<span class="logo-code">░░██    ██ ░██   ░██ ░██  ░██ ░██░░░░   </span><span class="logo-alta">░██░░░░░░██  ░██   ░██    ██░░░░██</span>
<span class="logo-code"> ░░██████  ░░██████  ░░██████ ░░██████  </span><span class="logo-alta">░██     ░██  ███   ░░██  ░░████████</span>
<span class="logo-code">  ░░░░░░    ░░░░░░    ░░░░░░   ░░░░░░   </span><span class="logo-alta">░░      ░░  ░░░     ░░    ░░░░░░░░</span></pre>
  <h1 class="alta-hero-title">AI coding agents on your <span class="alta-gradient-text">desktop</span> and in your <span class="alta-gradient-text">terminal</span></h1>
  <p class="alta-hero-lead">
    CodeAlta is a workspace for agentic coding on your local projects: model providers, durable sessions, agent prompts, MCP tools, plugins, and delegated agents, in a desktop app or a terminal UI.
  </p>
  <div class="alta-hero-actions">
    <a href="{{site.basepath}}/docs/getting-started/" class="btn btn-primary btn-lg"><i class="bi bi-rocket-takeoff"></i> Get started</a>
    <a href="{{site.basepath}}/docs/desktop-and-tui/" class="btn btn-outline-secondary btn-lg"><i class="bi bi-window-split"></i> Desktop and TUI</a>
    <a href="https://github.com/CodeAlta/CodeAlta" class="btn btn-info btn-lg"><i class="bi bi-github"></i> GitHub</a>
  </div>
  <div class="alta-install">
    <div class="alta-install-card">
      <div class="alta-install-label"><i class="bi bi-window"></i> CodeAlta Desktop <small>recommended</small></div>
      <pre class="language-shell-session"><code>dotnet tool install -g CodeAlta
alta</code></pre>
    </div>
    <div class="alta-install-card">
      <div class="alta-install-label"><i class="bi bi-terminal"></i> CodeAlta TUI</div>
      <pre class="language-shell-session"><code>dotnet tool install -g CodeAlta.Tui
altatui</code></pre>
    </div>
  </div>
  <p class="alta-install-note">Requires <a href="https://dotnet.microsoft.com/en-us/download/dotnet/10.0">.NET 10</a>. Both apps use the same <code>~/.alta</code> profile, so you can install both.</p>
</section>

<section class="my-5 py-4">
  <div class="alta-section-head">
    <span class="alta-kicker">CodeAlta Desktop</span>
    <h2 class="display-6">The complete CodeAlta experience</h2>
    <p>A desktop app with session tabs you can drag and split, a code editor for your projects, and every setting in one window.</p>
  </div>
  <div class="alta-showcase">
    <div class="alta-window">
      <img src="{{site.basepath}}/img/alta-desktop-split-three.webp" alt="CodeAlta Desktop with three sessions arranged in split panes: a parent session and two delegated child sessions" width="2400" height="1500">
    </div>
  </div>
  <div class="alta-points">
    <div class="alta-point">
      <span class="alta-icon" style="--accent: #38bdf8; --accent-2: #6366f1;"><i class="bi bi-layout-split"></i></span>
      <div>
        <h3>Arrange your workspace</h3>
        <p>Drag a session tab to an edge to split the window side by side or stacked. Tabs from different projects stay open together.</p>
      </div>
    </div>
    <div class="alta-point">
      <span class="alta-icon" style="--accent: #34d399; --accent-2: #06b6d4;"><i class="bi bi-eye"></i></span>
      <div>
        <h3>Inspect everything</h3>
        <p>Tool calls, file diffs, images, context usage and turn statistics open in windows over the session.</p>
      </div>
    </div>
    <div class="alta-point">
      <span class="alta-icon" style="--accent: #f472b6; --accent-2: #a855f7;"><i class="bi bi-sliders"></i></span>
      <div>
        <h3>One Settings window</h3>
        <p>Providers, models, agent prompts, skills, plugins, MCP servers, logs and appearance are pages of the same window.</p>
      </div>
    </div>
  </div>
</section>

<section class="my-5 py-4">
  <div class="alta-section-head">
    <span class="alta-kicker">CodeAlta TUI</span>
    <h2 class="display-6">The same workspace in your terminal</h2>
    <p>A keyboard-first terminal UI with tabs, a full-width timeline, a prompt editor and dialogs for everything else.</p>
  </div>
  <div class="alta-showcase">
    <div class="alta-window">
      <div class="alta-window-bar" aria-hidden="true">
        <span></span><span></span><span></span><strong>altatui</strong>
      </div>
      <video controls autoplay loop muted playsinline preload="metadata" poster="{{site.basepath}}/img/alta-home.png" aria-label="CodeAlta TUI workflow video">
        <source src="{{site.basepath}}/img/alta-multi-agents.mp4" type="video/mp4">
        <a href="{{site.basepath}}/img/alta-multi-agents.mp4">Download the CodeAlta TUI workflow video.</a>
      </video>
    </div>
  </div>
</section>

<section class="container my-5 py-4">
  <div class="alta-section-head">
    <span class="alta-kicker">One harness</span>
    <h2 class="display-6">Two apps, the same agents</h2>
    <p>Desktop and TUI run the same agent runtime on the same profile. A session started in one can be continued in the other, and this documentation applies to both.</p>
  </div>
  <div class="alta-harness">
    <div class="alta-card">
      <h3><span class="alta-icon" style="--accent: #38bdf8; --accent-2: #6366f1;"><i class="bi bi-window"></i></span> Desktop</h3>
      <ul>
        <li>Session tabs you can drag, split and merge</li>
        <li>A code editor for each project, beside its sessions</li>
        <li>Terminals in tabs, which agents can use too</li>
        <li>Worktrees: each session on its own branch, in its own folder</li>
        <li>Automations: prompts that run on a schedule or on a new issue</li>
        <li>All settings in one window</li>
        <li>Light and dark themes with 13 color schemes</li>
        <li>Keeps running in the notification area</li>
        <li>Updates and restarts from the app</li>
      </ul>
    </div>
    <div class="alta-harness-core">
      <strong>Shared by both</strong>
      <ul>
        <li>Projects and sessions</li>
        <li>Providers and models</li>
        <li>Agent prompts and skills</li>
        <li>MCP servers and plugins</li>
        <li>Notes, reminders and asks</li>
        <li><code class="text-white">~/.alta</code> configuration</li>
      </ul>
    </div>
    <div class="alta-card">
      <h3><span class="alta-icon" style="--accent: #34d399; --accent-2: #06b6d4;"><i class="bi bi-terminal"></i></span> TUI</h3>
      <ul>
        <li>Runs in your terminal, keyboard-first</li>
        <li>Tabs, timeline, prompt and dialogs in one screen</li>
        <li>Terminal color themes</li>
        <li>Plugin dialogs drawn with terminal controls</li>
        <li>Permission prompts when auto-approve is off</li>
        <li>Screenshot of the UI with <code>Ctrl+F12</code></li>
      </ul>
    </div>
  </div>
  <p class="text-center mt-4 mb-0"><a href="{{site.basepath}}/docs/desktop-and-tui/">Compare Desktop and TUI <i class="bi bi-arrow-right"></i></a></p>
</section>

<section class="container my-5 py-4">
  <div class="alta-section-head">
    <span class="alta-kicker">A closer look</span>
    <h2 class="display-6">Built for real work on real repositories</h2>
    <p>Use the switch on a screenshot to see the same screen in the Desktop app or in the TUI.</p>
  </div>
  <div class="alta-tour">
    <div class="alta-tour-row">
      <div class="alta-tour-copy">
        <h3>Run several agents at once</h3>
        <p>A session can start child sessions for bounded tasks, wait for their reports, review the diffs and commit. Parent and children stay visible in the sidebar while they run.</p>
        <a href="{{site.basepath}}/docs/sessions/">Sessions and delegation <i class="bi bi-arrow-right"></i></a>
      </div>
      {{ alta_shot "alta-desktop-split-side.webp" "alta-home.png" "A parent session with its delegated child sessions" "" }}
    </div>
    <div class="alta-tour-row">
      <div class="alta-tour-copy">
        <h3>See what the agent did</h3>
        <p>Tool calls are grouped in the timeline with their status, size and duration. Open any of them to read the exact arguments and the output.</p>
        <a href="{{site.basepath}}/docs/workspace/#timeline-cards">The timeline <i class="bi bi-arrow-right"></i></a>
      </div>
      {{ alta_shot "alta-desktop-tool-details.webp" "alta-tool-input-output-dialog.png" "Tool call details with arguments and output" "" }}
    </div>
    <div class="alta-tour-row">
      <div class="alta-tour-copy">
        <h3>Review every change</h3>
        <p>Each turn ends with the list of modified files and their added and removed lines. Open a file to read its diff before you accept the work.</p>
        <a href="{{site.basepath}}/docs/workspace/#timeline-cards">Modified files <i class="bi bi-arrow-right"></i></a>
      </div>
      {{ alta_shot "alta-desktop-modified-files.webp" "alta-modified-files.png" "Diff of a file modified by the agent" "" }}
    </div>
    <div class="alta-tour-row">
      <div class="alta-tour-copy">
        <h3>Use the models you already have</h3>
        <p>Sign in with a ChatGPT or GitHub Copilot subscription, or add API keys for OpenAI, Anthropic, Google, Mistral, xAI, Azure OpenAI and OpenAI-compatible servers. Choose the provider, model and reasoning effort per session.</p>
        <a href="{{site.basepath}}/docs/model-providers/">Model providers <i class="bi bi-arrow-right"></i></a>
      </div>
      {{ alta_shot "alta-desktop-models.webp" "alta-models.png" "Model catalog listing the models of every provider" "" }}
    </div>
    <div class="alta-tour-row">
      <div class="alta-tour-copy">
        <h3>Edit files next to the session</h3>
        <p>On the desktop, each project has a code editor with its files, a search in files and the open files as tabs. Put it beside the session that is working on the code. In the TUI, <code>Ctrl+E</code> opens a file in an editor tab.</p>
        <a href="{{site.basepath}}/docs/workspace/#code-editor">Code editor <i class="bi bi-arrow-right"></i></a>
      </div>
      {{ alta_shot "alta-desktop-code-editor.webp" "alta-code-editor.png" "Code editor with the files of a project and a source file" "" }}
    </div>
    <div class="alta-tour-row">
      <div class="alta-tour-copy">
        <h3>Pick your colors</h3>
        <p>The desktop has light and dark themes with 13 color schemes. The TUI has its own set of terminal themes.</p>
        <a href="{{site.basepath}}/docs/workspace/#workspace-settings">Appearance settings <i class="bi bi-arrow-right"></i></a>
      </div>
      {{ alta_shot "alta-desktop-themes.webp" "alta-theme-multi.png" "CodeAlta in several color themes" "" }}
    </div>
  </div>
</section>

<section class="container my-5 py-4">
  <div class="alta-section-head">
    <span class="alta-kicker">Workflows</span>
    <h2 class="display-6">More than one prompt at a time</h2>
    <p>Combine agent prompts, MCP servers, skills, delegated sessions, notes, reminders and plugins. You ask for the outcome and the agents use what the host provides.</p>
  </div>
  <div class="alta-grid">
    <div class="alta-card">
      <h3><span class="alta-icon" style="--accent: #60a5fa; --accent-2: #c084fc;"><i class="bi bi-signpost-split"></i></span> Agent prompts</h3>
      <p>Switch between Default and Plan modes, or write your own prompts for review, triage or release work.</p>
      <a href="{{site.basepath}}/docs/prompts/" class="stretched-link" aria-label="Agent prompts"></a>
    </div>
    <div class="alta-card">
      <h3><span class="alta-icon" style="--accent: #22d3ee; --accent-2: #a78bfa;"><i class="bi bi-hdd-network"></i></span> MCP servers</h3>
      <p>Add stdio or HTTP Model Context Protocol servers, inspect their tools and activate them for a session.</p>
      <a href="{{site.basepath}}/docs/plugins/mcp/" class="stretched-link" aria-label="MCP servers"></a>
    </div>
    <div class="alta-card">
      <h3><span class="alta-icon" style="--accent: #fb923c; --accent-2: #f43f5e;"><i class="bi bi-stars"></i></span> Advanced workflows</h3>
      <p>Agents can ask for structured approval, keep notes, set reminders and coordinate other sessions.</p>
      <a href="{{site.basepath}}/docs/advanced-agent-workflows/" class="stretched-link" aria-label="Advanced agent workflows"></a>
    </div>
    <div class="alta-card">
      <h3><span class="alta-icon" style="--accent: #34d399; --accent-2: #facc15;"><i class="bi bi-diagram-3"></i></span> Sessions</h3>
      <p>Sessions are saved on disk. Queue prompts, steer a running turn, compact the context and come back later.</p>
      <a href="{{site.basepath}}/docs/sessions/" class="stretched-link" aria-label="Sessions and delegation"></a>
    </div>
    <div class="alta-card">
      <h3><span class="alta-icon" style="--accent: #818cf8; --accent-2: #2dd4bf;"><i class="bi bi-mortarboard"></i></span> Skills</h3>
      <p>Agent Skills-compatible <code>SKILL.md</code> packages add reusable instructions for a project or for all of them.</p>
      <a href="{{site.basepath}}/docs/workspace/#skills-management" class="stretched-link" aria-label="Skills"></a>
    </div>
    <div class="alta-card">
      <h3><span class="alta-icon" style="--accent: #a3e635; --accent-2: #06b6d4;"><i class="bi bi-puzzle"></i></span> Plugins</h3>
      <p>Extend the host with trusted local .NET plugins when prompts and configuration are not enough.</p>
      <a href="{{site.basepath}}/docs/plugins/" class="stretched-link" aria-label="Plugins"></a>
    </div>
  </div>
</section>

<section class="container my-5 py-4">
  <div class="alta-section-head">
    <span class="alta-kicker">Principles</span>
    <h2 class="display-6">What CodeAlta is built on</h2>
  </div>
  <div class="alta-principles">
    <a class="alta-chip" href="{{site.basepath}}/docs/principles/"><i class="bi bi-arrows-collapse"></i> Efficient</a>
    <a class="alta-chip" href="{{site.basepath}}/docs/principles/"><i class="bi bi-eye"></i> Transparent</a>
    <a class="alta-chip" href="{{site.basepath}}/docs/principles/"><i class="bi bi-keyboard"></i> Keyboard-first</a>
    <a class="alta-chip" href="{{site.basepath}}/docs/principles/"><i class="bi bi-diagram-3"></i> Session-oriented</a>
    <a class="alta-chip" href="{{site.basepath}}/docs/principles/"><i class="bi bi-cpu"></i> Provider-agnostic</a>
    <a class="alta-chip" href="{{site.basepath}}/docs/principles/"><i class="bi bi-braces-asterisk"></i> Native .NET</a>
    <a class="alta-chip" href="{{site.basepath}}/docs/principles/"><i class="bi bi-life-preserver"></i> Error-aware</a>
    <a class="alta-chip" href="{{site.basepath}}/docs/principles/"><i class="bi bi-puzzle"></i> Extensible</a>
  </div>
</section>

<section class="container my-5 pb-4">
  <div class="alta-cta">
    <h2>Start your first session</h2>
    <p>Install CodeAlta, enable one provider and send a prompt on one of your projects.</p>
    <a href="{{site.basepath}}/docs/getting-started/" class="btn btn-light btn-lg"><i class="bi bi-rocket-takeoff"></i> Get started</a>
  </div>
</section>

</div>
