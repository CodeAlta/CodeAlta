---
title: Home
layout: simple
og_type: website
---

<div class="alta-home">

<div class="alta-scroll-brand" data-alta-scroll-brand hidden>
  <a href="#" aria-label="CodeAlta - back to top">
    <img src="{{site.basepath}}/img/CodeAlta.svg" alt="" width="30" height="30">
    <span class="alta-wordmark" aria-hidden="true"><span class="logo-code">Code</span><span class="logo-alta">Alta</span></span>
  </a>
</div>

<section class="alta-hero" aria-labelledby="home-title">
  <p class="alta-kicker">Open source · Local projects · Your choice of AI</p>
  <pre class="codealta-ascii-logo" role="img" aria-label="CodeAlta"><span class="logo-code">   ██████                  ██           </span><span class="logo-alta">     ██       ██    ██</span>
<span class="logo-code">  ██░░░░██                ░██           </span><span class="logo-alta">    ████     ░██   ░██</span>
<span class="logo-code"> ██    ░░    ██████       ░██   █████   </span><span class="logo-alta">   ██░░██    ░██  ██████   ██████</span>
<span class="logo-code">░██         ██░░░░██   ██████  ██░░░██  </span><span class="logo-alta">  ██  ░░██   ░██ ░░░██░   ░░░░░░██</span>
<span class="logo-code">░██        ░██   ░██  ██░░░██ ░███████  </span><span class="logo-alta"> ██████████  ░██   ░██     ███████</span>
<span class="logo-code">░░██    ██ ░██   ░██ ░██  ░██ ░██░░░░   </span><span class="logo-alta">░██░░░░░░██  ░██   ░██    ██░░░░██</span>
<span class="logo-code"> ░░██████  ░░██████  ░░██████ ░░██████  </span><span class="logo-alta">░██     ░██  ███   ░░██  ░░████████</span>
<span class="logo-code">  ░░░░░░    ░░░░░░    ░░░░░░   ░░░░░░   </span><span class="logo-alta">░░      ░░  ░░░     ░░    ░░░░░░░░</span></pre>
  <div class="alta-install" id="install">
    <div class="alta-install-card alta-install-primary">
      <div class="alta-install-label"><i class="bi bi-window" aria-hidden="true"></i> Install CodeAlta Desktop</div>
      <pre class="language-shell-session"><code>dotnet tool install -g CodeAlta
alta</code></pre>
    </div>
    <p class="alta-install-note">Windows, macOS &amp; Linux · Requires <a href="https://dotnet.microsoft.com/en-us/download/dotnet/10.0">.NET 10</a> · <a href="#desktop-or-tui">Prefer a terminal?</a></p>
  </div>
  <h1 class="alta-hero-title" id="home-title">Your agents. <span class="alta-gradient-text">One workspace.</span></h1>
  <p class="alta-hero-lead">Run AI coding agents on your local projects. Keep conversations, code, terminals and git changes together - with the models and tools you choose.</p>
  <div class="alta-hero-actions">
    <a href="{{site.basepath}}/docs/getting-started/" class="btn btn-primary"><i class="bi bi-rocket-takeoff" aria-hidden="true"></i> Get started</a>
    <a href="#explore" class="btn btn-outline-secondary">Explore the workspace <i class="bi bi-arrow-down" aria-hidden="true"></i></a>
    <a href="https://github.com/CodeAlta/CodeAlta" class="alta-github-link"><i class="bi bi-github" aria-hidden="true"></i> View on GitHub</a>
  </div>
  <div class="alta-trust-row">
    <span><i class="bi bi-shield-check" aria-hidden="true"></i> <strong>No telemetry</strong></span>
    <span><i class="bi bi-folder2-open" aria-hidden="true"></i> Your files stay yours</span>
    <span><i class="bi bi-code-slash" aria-hidden="true"></i> BSD-2-Clause licensed</span>
  </div>
  <p class="alta-privacy-note">CodeAlta does not send usage analytics to its developers.</p>
</section>

<section class="alta-explore" id="explore" aria-labelledby="explore-title">
  <div class="alta-section-head">
    <span class="alta-kicker">See it in action</span>
    <h2 id="explore-title">A place for the whole workflow.</h2>
    <p>From the first prompt to the final diff. Scroll to step inside CodeAlta Desktop.</p>
  </div>
  <div class="alta-scroll-story" data-alta-story>
    <nav class="alta-story-index" aria-label="Workspace features">
      <a href="#feature-sessions"><span>01</span> Sessions</a>
      <a href="#feature-code"><span>02</span> Code &amp; git</a>
      <a href="#feature-canvases"><span>03</span> Canvases</a>
      <a href="#feature-plugins"><span>04</span> Plugins</a>
      <a href="#feature-statistics"><span>05</span> Statistics</a>
      <a href="#feature-worktrees"><span>06</span> Worktrees</a>
      <a href="#feature-spaces"><span>07</span> Spaces</a>
      <a href="#feature-welcome"><span>08</span> Welcome</a>
      <a href="#feature-documentation"><span>09</span> Documentation</a>
      <a href="#feature-models"><span>10</span> Model Providers</a>
    </nav>
    <div class="alta-story-layout">
      <div class="alta-story-chapters">
        <section class="alta-story-step" id="feature-sessions" aria-labelledby="feature-title-sessions" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>01</span> Sessions &amp; delegation</p>
            <h3 id="feature-title-sessions">More minds.<br>One clear view.</h3>
            <p>CodeAlta’s robust agent harness coordinates multiple agents and their tools for efficient, reliable parallel work. Delegate to child sessions, follow progress side by side, and keep the details in view.</p>
            <a href="{{site.basepath}}/docs/sessions/">Meet your agents <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-split-three.webp" alt="A parent Markdig session and two delegated sessions arranged in three CodeAlta panes" width="2400" height="1500" fetchpriority="high"></div>
        </section>
        <section class="alta-story-step" id="feature-code" aria-labelledby="feature-title-code" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>02</span> Code &amp; git</p>
            <h3 id="feature-title-code">Stay close<br>to the code.</h3>
            <p>Search a project, edit files, run a terminal and inspect git diffs without leaving the workspace. Open any tool call to see what the agent did.</p>
            <a href="{{site.basepath}}/docs/workspace/#code-editor">Explore the editor <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-editor-search.webp" alt="The SharpYaml code editor with a search across files and matching source lines" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-canvases" aria-labelledby="feature-title-canvases" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>03</span> Interactive canvases</p>
            <h3 id="feature-title-canvases">Beyond<br>the conversation.</h3>
            <p>Boards, checklists and custom tools become interactive tabs. Ask an agent to build a plugin, then use its canvas right beside the conversation.</p>
            <a href="{{site.basepath}}/docs/plugins/developers/#canvases">Discover canvases <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-canvas-board.webp" alt="An agent session beside an interactive Board canvas with To do, Doing and Done columns" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-plugins" aria-labelledby="feature-title-plugins" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>04</span> Plugins</p>
            <h3 id="feature-title-plugins">Imagine it.<br>Ask your agent.</h3>
            <p>Ask your agent to create a plugin - a canvas, a dialog, a tool, a command or a button. Add it to one project or your whole workspace. Turn your ideas into the tools you want to use.</p>
            <a href="{{site.basepath}}/docs/plugins/">Explore plugins <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-plugins.webp" alt="The Plugins window with built-in integrations and the configurable Statistics buttons" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-statistics" aria-labelledby="feature-title-statistics" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>05</span> Local statistics</p>
            <h3 id="feature-title-statistics">A clearer picture<br>of your work.</h3>
            <p>Explore activity, models, tokens and tool use across your projects - or focus on one session and its sub-agents. Statistics stay on your computer.</p>
            <a href="{{site.basepath}}/docs/plugins/statistics/">Explore Statistics <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-statistics-overview.webp" alt="The Statistics dashboard showing session activity, token usage and time by project" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-worktrees" aria-labelledby="feature-title-worktrees" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>06</span> Parallel worktrees</p>
            <h3 id="feature-title-worktrees">Work together.<br>Change apart.</h3>
            <p>Give each session its own checkout and branch. See which worktrees are in use, open their code or diffs, and clean up when the work is done.</p>
            <a href="{{site.basepath}}/docs/worktrees/">Manage worktrees <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-worktree-manager.webp" alt="The Worktrees window listing project checkouts, their branches, last-used sessions and actions" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-spaces" aria-labelledby="feature-title-spaces" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>07</span> Spaces</p>
            <h3 id="feature-title-spaces">A space for<br>each kind of work.</h3>
            <p>Keep work, personal and open-source projects in their own spaces. Switch between projects, sessions and tab layouts while your agents keep working in the background.</p>
            <a href="{{site.basepath}}/docs/spaces/">Organize your spaces <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-spaces.webp" alt="The Open source workspace and the Spaces switch, with Work, Personal and Open source groups and session activity indicators" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-welcome" aria-labelledby="feature-title-welcome" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>08</span> Welcome home</p>
            <h3 id="feature-title-welcome">Pick up where<br>you left off.</h3>
            <p>Start from recent sessions, projects and useful shortcuts. The built-in documentation is always nearby, with an agent ready to answer your questions.</p>
            <a href="{{site.basepath}}/docs/workspace/#welcome-page-desktop">Meet your workspace <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-landing.webp" alt="The Welcome page with recent projects and sessions, Statistics and quick access to the documentation" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-documentation" aria-labelledby="feature-title-documentation" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>09</span> Built-in documentation</p>
            <h3 id="feature-title-documentation">The guide is<br>already here.</h3>
            <p>Read the documentation right inside CodeAlta, beside your work. Your agent can consult the same guide to help you find a feature, understand a setting or learn a new workflow.</p>
            <a href="{{site.basepath}}/docs/">Explore the guide <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-documentation.webp" alt="The built-in Documentation tab with the CodeAlta user guide and its navigation" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-models" aria-labelledby="feature-title-models" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>10</span> Your choice of AI</p>
            <h3 id="feature-title-models">Your AI.<br>Your choice.</h3>
            <p>Use your Claude Code, Codex or GitHub Copilot subscription, or connect an API-based provider. Pick a provider, model and reasoning effort for each session, not the whole workspace.</p>
            <a href="{{site.basepath}}/docs/model-providers/">See model providers <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-model-providers.webp" alt="The Model Providers window listing Claude Code, Codex, GitHub Copilot and API-based providers" width="2400" height="1500" loading="lazy"></div>
        </section>
      </div>
    </div>
  </div>
</section>

<section class="alta-home-section" aria-labelledby="make-it-yours">
  <div class="alta-section-head">
    <span class="alta-kicker">Built around your work</span>
    <h2 id="make-it-yours">Not another chat box.</h2>
    <p>A workspace you can inspect, extend and make your own.</p>
  </div>
  <div class="alta-highlight-grid">
    <article class="alta-highlight">
      <div class="alta-highlight-copy"><span class="alta-kicker">Transparent by design</span><h3>See the work, not just the answer.</h3><p>Read tool inputs and outputs, review modified files and follow live commands. Your agent's work is part of the conversation.</p><a href="{{site.basepath}}/docs/workspace/#timeline-cards">Inside the timeline <i class="bi bi-arrow-right" aria-hidden="true"></i></a></div>
      <img src="{{site.basepath}}/img/alta-desktop-modified-files.webp" alt="A source diff showing the exact lines an agent changed" width="2400" height="1500" loading="lazy">
    </article>
    <article class="alta-highlight">
      <div class="alta-highlight-copy"><span class="alta-kicker">Extensible by design</span><h3>Your tools belong here, too.</h3><p>Bring in MCP tools and skills. Add trusted .NET plugins with their own canvases, commands and buttons - or ask an agent to build one.</p><a href="{{site.basepath}}/docs/plugins/">Make it your own <i class="bi bi-arrow-right" aria-hidden="true"></i></a></div>
      <img src="{{site.basepath}}/img/alta-desktop-canvases.webp" alt="The Canvases page with interactive Board and Checklist plugin tools" width="2400" height="1500" loading="lazy">
    </article>
  </div>
  <div class="alta-workflow-links">
    <a href="{{site.basepath}}/docs/prompts/"><i class="bi bi-signpost-split" aria-hidden="true"></i><strong>Agent prompts</strong><span>Plan, build, review - or define your own role.</span><i class="bi bi-arrow-up-right" aria-hidden="true"></i></a>
    <a href="{{site.basepath}}/docs/automations/"><i class="bi bi-lightning-charge" aria-hidden="true"></i><strong>Automations</strong><span>Start work on a schedule or a repository event.</span><i class="bi bi-arrow-up-right" aria-hidden="true"></i></a>
    <a href="{{site.basepath}}/docs/issues/"><i class="bi bi-git" aria-hidden="true"></i><strong>Issues &amp; pull requests</strong><span>Start a session from an issue or a pull request.</span><i class="bi bi-arrow-up-right" aria-hidden="true"></i></a>
    <a href="{{site.basepath}}/docs/plugins/mcp/"><i class="bi bi-plug" aria-hidden="true"></i><strong>MCP servers</strong><span>Connect the tools and services you already use.</span><i class="bi bi-arrow-up-right" aria-hidden="true"></i></a>
  </div>
</section>

<section class="alta-home-section alta-terminal-section" id="desktop-or-tui" aria-labelledby="terminal-title">
  <div class="alta-terminal-copy">
    <span class="alta-kicker">Desktop first. Terminal ready.</span>
    <h2 id="terminal-title">The same agents.<br>A different kind of window.</h2>
    <p>Prefer the terminal? CodeAlta TUI shares the same profile, providers and sessions. Start in one app and continue in the other.</p>
    <div class="alta-install-card">
      <div class="alta-install-label"><i class="bi bi-terminal" aria-hidden="true"></i> Install CodeAlta TUI</div>
      <pre class="language-shell-session"><code>dotnet tool install -g CodeAlta.Tui
altatui</code></pre>
    </div>
    <a href="{{site.basepath}}/docs/desktop-and-tui/">Compare Desktop and TUI <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
  </div>
  <div class="alta-window">
    <video controls loop muted playsinline preload="none" poster="{{site.basepath}}/img/alta-home.png" aria-label="CodeAlta TUI workflow demonstration">
      <source src="{{site.basepath}}/img/alta-multi-agents.mp4" type="video/mp4">
      <a href="{{site.basepath}}/img/alta-multi-agents.mp4">Download the CodeAlta TUI workflow video.</a>
    </video>
  </div>
</section>

<section class="alta-home-section alta-home-close" aria-labelledby="start-title">
  <i class="bi bi-terminal" aria-hidden="true"></i>
  <h2 id="start-title">Your next idea starts here.</h2>
  <p>Install CodeAlta. Connect a provider. Open a project.</p>
  <div class="alta-hero-actions"><a href="#install" class="btn btn-primary">Install CodeAlta <i class="bi bi-arrow-up" aria-hidden="true"></i></a><a href="{{site.basepath}}/docs/getting-started/" class="btn btn-outline-secondary">Read the quick start</a></div>
  <p class="alta-closing-note">Open source, BSD-2-Clause licensed, and built with .NET. <a href="https://github.com/CodeAlta/CodeAlta">Contributions welcome.</a></p>
</section>

</div>
