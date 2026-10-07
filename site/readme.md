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
  <h1 class="alta-hero-title">A <span class="alta-gradient-text">desktop workspace</span> for AI coding agents</h1>
  <p class="alta-hero-lead">
    CodeAlta Desktop runs coding agents on your local projects: sessions in tabs you can split, a code editor, terminals and git changes in one window, with the model providers, agent prompts, MCP tools and plugins you choose.
  </p>
  <div class="alta-hero-actions">
    <a href="{{site.basepath}}/docs/getting-started/" class="btn btn-primary btn-lg"><i class="bi bi-rocket-takeoff"></i> Get started</a>
    <a href="#desktop-or-tui" class="btn btn-outline-secondary btn-lg"><i class="bi bi-window-split"></i> Desktop or TUI?</a>
    <a href="https://github.com/CodeAlta/CodeAlta" class="btn btn-info btn-lg"><i class="bi bi-github"></i> GitHub</a>
  </div>
  <div class="alta-install">
    <div class="alta-install-card alta-install-primary">
      <div class="alta-install-label"><i class="bi bi-window"></i> CodeAlta Desktop <span class="alta-install-tag">Recommended</span></div>
      <pre class="language-shell-session"><code>dotnet tool install -g CodeAlta
alta</code></pre>
    </div>
    <p class="alta-install-note">For Windows, macOS and Linux. Requires <a href="https://dotnet.microsoft.com/en-us/download/dotnet/10.0">.NET 10</a>.</p>
    <div class="alta-install-card alta-install-secondary">
      <div class="alta-install-label"><i class="bi bi-terminal"></i> CodeAlta TUI <small>for the terminal</small></div>
      <pre class="language-shell-session"><code>dotnet tool install -g CodeAlta.Tui
altatui</code></pre>
    </div>
  </div>
</section>

<section class="my-5 py-4">
  <div class="alta-section-head">
    <span class="alta-kicker">CodeAlta Desktop</span>
    <h2 class="display-6">The complete CodeAlta experience</h2>
    <p>Sessions, code, terminals and changes side by side, and every setting in one window.</p>
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
      <span class="alta-icon" style="--accent: #a3e635; --accent-2: #06b6d4;"><i class="bi bi-code-slash"></i></span>
      <div>
        <h3>Code, terminals and changes</h3>
        <p>Each project has a code editor, terminals that agents can use too, and a Changes tab with the diff of every file.</p>
      </div>
    </div>
    <div class="alta-point">
      <span class="alta-icon" style="--accent: #34d399; --accent-2: #06b6d4;"><i class="bi bi-eye"></i></span>
      <div>
        <h3>Watch the agent work</h3>
        <p>Open a tool call to follow a command in a live terminal, or to read a file or a diff in the colors of its language.</p>
      </div>
    </div>
    <div class="alta-point">
      <span class="alta-icon" style="--accent: #fb923c; --accent-2: #f43f5e;"><i class="bi bi-lightning-charge"></i></span>
      <div>
        <h3>Automations and worktrees</h3>
        <p>Run a prompt every morning or on a new issue, and give each session its own branch in its own folder.</p>
      </div>
    </div>
    <div class="alta-point">
      <span class="alta-icon" style="--accent: #818cf8; --accent-2: #2dd4bf;"><i class="bi bi-cursor"></i></span>
      <div>
        <h3>Agents that see the window</h3>
        <p>An agent can take a screenshot of CodeAlta, click and type, and write a plugin that the app reloads while it runs.</p>
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

<section class="container my-5 py-4" id="desktop-or-tui">
  <div class="alta-section-head">
    <span class="alta-kicker">Desktop or TUI?</span>
    <h2 class="display-6">Start with CodeAlta Desktop</h2>
    <p>CodeAlta also has a terminal UI. Both apps run the same agents on the same <code>~/.alta</code> profile, so a session started in one continues in the other. The desktop app does much more.</p>
  </div>
  <div class="alta-compare-wrap">
    <table class="alta-compare">
      <thead>
        <tr><th scope="col"></th><th scope="col"><i class="bi bi-window" aria-hidden="true"></i> Desktop</th><th scope="col"><i class="bi bi-terminal" aria-hidden="true"></i> TUI</th></tr>
      </thead>
      <tbody>
        <tr><th scope="row">Providers, sessions, agent prompts, skills, MCP servers, plugins</th><td>{{ alta_yes }}</td><td>{{ alta_yes }}</td></tr>
        <tr><th scope="row">Delegated agents, notes, reminders, asks</th><td>{{ alta_yes }}</td><td>{{ alta_yes }}</td></tr>
        <tr><th scope="row">Sessions side by side, in panes you split</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
        <tr><th scope="row">Code editor with the files of the project and a search</th><td>{{ alta_yes }}</td><td>{{ alta_part }} <small>One file at a time</small></td></tr>
        <tr><th scope="row">Git changes: diffs, commits, branches</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
        <tr><th scope="row">Terminals, which agents can use too</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
        <tr><th scope="row">Automations: on a schedule, on a new issue or pull request</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
        <tr><th scope="row">Worktrees created and removed from the app</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
        <tr><th scope="row">Agents that see and drive the app, and an MCP server</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
        <tr><th scope="row">Plugins written and reloaded while the app runs</th><td>{{ alta_yes }}</td><td>{{ alta_no }}</td></tr>
        <tr><th scope="row">Light and dark themes, 13 color schemes</th><td>{{ alta_yes }}</td><td>{{ alta_part }} <small>Terminal themes</small></td></tr>
        <tr><th scope="row">Runs in a terminal</th><td>{{ alta_no }}</td><td>{{ alta_yes }}</td></tr>
      </tbody>
    </table>
  </div>
  <p class="text-center mt-4 mb-0"><a href="{{site.basepath}}/docs/desktop-and-tui/">The full comparison <i class="bi bi-arrow-right"></i></a></p>
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
        <h3>See what the agent does</h3>
        <p>Tool calls are grouped in the timeline with their status and what they wrote. Open one to follow a command in a terminal while it runs, or to read the file or the diff it worked on.</p>
        <a href="{{site.basepath}}/docs/workspace/#timeline-cards">The timeline <i class="bi bi-arrow-right"></i></a>
      </div>
      {{ alta_shot "alta-desktop-tool-details.webp" "alta-tool-input-output-dialog.png" "Details of a tool call" "" }}
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

<section class="my-5 py-4">
  <div class="alta-section-head">
    <span class="alta-kicker">CodeAlta TUI</span>
    <h2 class="display-6">The same agents in your terminal</h2>
    <p>A keyboard-first terminal UI with tabs, a full-width timeline, a prompt editor and dialogs for everything else.</p>
  </div>
  <div class="alta-showcase alta-showcase-small">
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
      <p>Extend the host with trusted local .NET plugins when prompts and configuration are not enough. In the desktop app, an agent writes one for you and reloads it while the app runs.</p>
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
    <p>Install CodeAlta Desktop, enable one provider and send a prompt on one of your projects.</p>
    <a href="{{site.basepath}}/docs/getting-started/" class="btn btn-light btn-lg"><i class="bi bi-rocket-takeoff"></i> Get started</a>
  </div>
</section>

</div>
