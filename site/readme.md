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
  <p class="alta-hero-lead">Run AI coding agents on your local projects, and follow them from your phone. Keep conversations, code, terminals and git changes together - with the models and tools you choose.</p>
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
      <a href="#feature-workflows"><span>02</span> Workflows</a>
      <a href="#feature-sandbox"><span>03</span> Sandbox</a>
      <a href="#feature-mobile"><span>04</span> On your phone</a>
      <a href="#feature-windows"><span>05</span> Windows</a>
      <a href="#feature-fork"><span>06</span> Fork &amp; rewind</a>
      <a href="#feature-code"><span>07</span> Code &amp; git</a>
      <a href="#feature-worktrees"><span>08</span> Worktrees</a>
      <a href="#feature-work-items"><span>09</span> Work items</a>
      <a href="#feature-issues"><span>10</span> Issues</a>
      <a href="#feature-automations"><span>11</span> Automations</a>
      <a href="#feature-canvases"><span>12</span> Canvases</a>
      <a href="#feature-plugins"><span>13</span> Plugins</a>
      <a href="#feature-statistics"><span>14</span> Statistics</a>
      <a href="#feature-spaces"><span>15</span> Spaces</a>
      <a href="#feature-welcome"><span>16</span> Welcome</a>
      <a href="#feature-documentation"><span>17</span> Documentation</a>
      <a href="#feature-models"><span>18</span> Model Providers</a>
    </nav>
    <div class="alta-story-layout">
      <div class="alta-story-chapters">
        <section class="alta-story-step" id="feature-sessions" aria-labelledby="feature-title-sessions" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>01</span> Sessions &amp; delegation</p>
            <h3 id="feature-title-sessions">More minds.<br>One clear view.</h3>
            <p>CodeAlta’s agent harness <a href="{{site.basepath}}/docs/benchmark/">holds its own against Claude Code and Codex</a> on hard tasks of real projects, and coordinates multiple agents and their tools for efficient, reliable parallel work. Delegate to child sessions, follow progress side by side, and keep the details in view. Archive what is done and bring it back when you need it.</p>
            <a href="{{site.basepath}}/docs/sessions/">Meet your agents <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-split-three.webp" alt="A parent Markdig session and two delegated sessions arranged in three CodeAlta panes" width="2400" height="1500" fetchpriority="high"></div>
        </section>
        <section class="alta-story-step" id="feature-workflows" aria-labelledby="feature-title-workflows" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>02</span> Workflows &amp; Code Mode</p>
            <h3 id="feature-title-workflows">One prompt.<br>A whole workflow.</h3>
            <p>Agents write short programs that call their tools in loops and in parallel, and run whole workflows: a step plans, sub-agents explore at once, another writes, and you watch the graph fill in live. A workflow can go on in the background, sleep, ask you and resume, then return Markdown with pictures. Keep one that went well as a script, and run it again from the Workflows tab or an automation.</p>
            <a href="{{site.basepath}}/docs/sessions/code-mode/">Run your first workflow <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><video controls loop muted playsinline preload="metadata" poster="{{site.basepath}}/img/alta-workflows-codemode.webp" width="2402" height="1510" aria-label="A CodeAlta session asked for a visual tour of its user guide runs a workflow: one session plans, three explore in parallel, one writes, and the tour comes back with its screenshots"><source src="{{site.basepath}}/img/alta-workflows-codemode.mp4" type="video/mp4"><a href="{{site.basepath}}/img/alta-workflows-codemode.mp4">Download the video of a workflow.</a></video></div>
        </section>
        <section class="alta-story-step" id="feature-sandbox" aria-labelledby="feature-title-sandbox" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>03</span> Sandbox</p>
            <h3 id="feature-title-sandbox">Let agents run.<br>Keep your secrets.</h3>
            <p>Run a session in a sandbox of the operating system, built on Microsoft's <a href="https://github.com/microsoft/mxc">MXC</a>: its commands and file tools reach the project and your developer tools, never your credentials, your SSH keys or the rest of your home folder, and only the hosts its profile allows. Turn it on for every session, a provider, a project or one session. Claude Code and the ACP agents run in it too, and when a command needs more, the agent asks you first.</p>
            <a href="{{site.basepath}}/docs/sessions/sandbox/">Sandbox your sessions <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-sandbox-settings.webp" alt="Settings > Sandbox: the sandbox of Windows passes its check, with the choice for every session and the workspace and strict profiles" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-mobile" aria-labelledby="feature-title-mobile" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>04</span> On your phone</p>
            <h3 id="feature-title-mobile">Step away.<br>Stay in the loop.</h3>
            <p>Scan a QR code and your sessions are on your phone: follow each one live, read its timeline, then answer, steer or stop it, change its model and its effort, or start a new session. The phone reaches your computer over your own Tailscale network, and nothing else of your computer is published.</p>
            <a href="{{site.basepath}}/docs/plugins/mobile/">Connect your phone <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-mobile.webp" alt="Three phones showing the sessions of CodeAlta live, the timeline of a session with its prompt, and the choice of a model and its reasoning effort" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-windows" aria-labelledby="feature-title-windows" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>05</span> Windows</p>
            <h3 id="feature-title-windows">Every monitor.<br>One workspace.</h3>
            <p>Drag a tab out of the main window and it opens in a window of its own: a session on one monitor, its code or a review on another. Drag it back, or between windows, and it joins the pane under the pointer. Name and color a window, and it opens again where it was at the next start.</p>
            <a href="{{site.basepath}}/docs/workspace/#windows">Spread out your work <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-windows.webp" alt="Two CodeAlta Desktop windows side by side on Windows: the main window with a new session and Statistics, and Window 2 with the session of a release" width="3456" height="2168" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-fork" aria-labelledby="feature-title-fork" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>06</span> Fork &amp; rewind</p>
            <h3 id="feature-title-fork">Go back.<br>Branch out.</h3>
            <p>Continue from any turn of a session in a new one, or take the session itself back and try again. Nothing is lost by default: a rewind keeps the turns it removes in a session of their own.</p>
            <a href="{{site.basepath}}/docs/sessions/#fork-and-rewind">Fork or rewind a session <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-fork-rewind.webp" alt="The Fork or rewind window listing the turns of a session, with what a fork at the selected turn does" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-code" aria-labelledby="feature-title-code" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>07</span> Code &amp; git</p>
            <h3 id="feature-title-code">Stay close<br>to the code.</h3>
            <p>Search a project, edit files, run a terminal and inspect git diffs without leaving the workspace. Open any tool call to see what the agent did.</p>
            <a href="{{site.basepath}}/docs/workspace/code/#code-editor">Explore the editor <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-editor-search.webp" alt="The SharpYaml code editor with a search across files and matching source lines" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-worktrees" aria-labelledby="feature-title-worktrees" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>08</span> Parallel worktrees</p>
            <h3 id="feature-title-worktrees">Work together.<br>Change apart.</h3>
            <p>Give each session its own checkout and branch. See which worktrees are in use, open their code or diffs, and clean up when the work is done.</p>
            <a href="{{site.basepath}}/docs/worktrees/">Manage worktrees <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-worktree-manager.webp" alt="The Worktrees window listing project checkouts, their branches, last-used sessions and actions" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-work-items" aria-labelledby="feature-title-work-items" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>09</span> Work items &amp; reviews</p>
            <h3 id="feature-title-work-items">Nothing falls<br>through the cracks.</h3>
            <p>Agents propose follow-up tasks for what they find along the way, and Plan mode writes plans for you to review. Nothing starts until you choose: run an item in a new session or its own worktree, keep it for later, or dismiss it. <a href="{{site.basepath}}/docs/reviews/">Comment on a plan or on the changes</a> as in a pull request, and have agents wait for your approval before they commit.</p>
            <a href="{{site.basepath}}/docs/work-items/">Review your work items <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-work-items.webp" alt="The Work items tab with the follow-up tasks of a project, one of them open with its Runs with model and its Start buttons" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-issues" aria-labelledby="feature-title-issues" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>10</span> Issues &amp; pull requests</p>
            <h3 id="feature-title-issues">From issue<br>to pull request.</h3>
            <p>Browse the issues and pull requests of your GitHub, GitLab, Azure DevOps or Bitbucket projects, and your Jira issues, beside your work, with views of your team and sprint on Azure DevOps. Start a session on one in a click, then let it open the pull request when the work is done.</p>
            <a href="{{site.basepath}}/docs/issues/">Work from your issues <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-issues.webp" alt="The Issues tab with the open issues of a GitHub repository and one issue open with its Start buttons" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-automations" aria-labelledby="feature-title-automations" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>11</span> Automations</p>
            <h3 id="feature-title-automations">Work that starts<br>by itself.</h3>
            <p>Schedule a prompt or a workflow script, or run it when an issue or a pull request is opened. Each run starts a new session you read like any other work - from a morning test report to the first look at a new issue.</p>
            <a href="{{site.basepath}}/docs/automations/">Automate your routines <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-automations.webp" alt="The Automations tab with the next 24 hours, six automations of a project, templates and the details of the selected one" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-canvases" aria-labelledby="feature-title-canvases" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>12</span> Interactive canvases</p>
            <h3 id="feature-title-canvases">Beyond<br>the conversation.</h3>
            <p>Boards, checklists, graphs and custom tools become interactive tabs. Ask an agent to build a plugin, then use its canvas right beside the conversation.</p>
            <a href="{{site.basepath}}/docs/plugins/ui/#canvases">Discover canvases <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-canvas-board.webp" alt="An agent session beside an interactive Board canvas with To do, Doing and Done columns" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-plugins" aria-labelledby="feature-title-plugins" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>13</span> Plugins</p>
            <h3 id="feature-title-plugins">Imagine it.<br>Ask your agent.</h3>
            <p>Ask your agent to create a plugin - a canvas, a dialog, a tool, a command or a button. Add it to one project or your whole workspace. Turn your ideas into the tools you want to use.</p>
            <a href="{{site.basepath}}/docs/plugins/">Explore plugins <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-plugins.webp" alt="The Plugins window with built-in integrations and the configurable Statistics buttons" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-statistics" aria-labelledby="feature-title-statistics" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>14</span> Local statistics</p>
            <h3 id="feature-title-statistics">A clearer picture<br>of your work.</h3>
            <p>Explore activity, models, tokens and tool use across your projects - or focus on one session and its sub-agents. Statistics stay on your computer.</p>
            <a href="{{site.basepath}}/docs/plugins/statistics/">Explore Statistics <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-statistics-overview.webp" alt="The Statistics dashboard showing session activity, token usage and time by project" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-spaces" aria-labelledby="feature-title-spaces" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>15</span> Spaces</p>
            <h3 id="feature-title-spaces">A setup for<br>each kind of work.</h3>
            <p>Keep work, personal and open-source projects in their own spaces, each with its own chats, providers and accounts, MCP servers, skills and agent prompts, and the Git, GitHub, Jira and command line identity its agents use. Inside a space, labels group its projects in the Explorer: one title per label, nested when you need it. Switch spaces while your agents keep working in the background.</p>
            <a href="{{site.basepath}}/docs/spaces/">Organize your spaces <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-labels.webp" alt="The Open source space in CodeAlta Desktop: its projects grouped under the labels Apps, Parsers, Config formats, Native and Terminal in the Explorer, beside a Markdig session" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-welcome" aria-labelledby="feature-title-welcome" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>16</span> Welcome home</p>
            <h3 id="feature-title-welcome">Pick up where<br>you left off.</h3>
            <p>Start from recent sessions, projects and useful shortcuts. Press <kbd>Ctrl+P</kbd> to <a href="{{site.basepath}}/docs/search/">find any earlier conversation</a>, plan or tool by its words, or by its meaning with a model that runs on your computer. The built-in documentation is always nearby, with an agent ready to answer your questions.</p>
            <a href="{{site.basepath}}/docs/workspace/#welcome-page">Meet your workspace <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-landing.webp" alt="The Welcome page with recent projects and sessions, Statistics and quick access to the documentation" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-documentation" aria-labelledby="feature-title-documentation" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>17</span> Built-in documentation</p>
            <h3 id="feature-title-documentation">The guide is<br>already here.</h3>
            <p>Read the documentation right inside CodeAlta, beside your work. Your agent can consult the same guide to help you find a feature, understand a setting or learn a new workflow.</p>
            <a href="{{site.basepath}}/docs/">Explore the guide <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
          </div>
          <div class="alta-window alta-story-shot"><img src="{{site.basepath}}/img/alta-desktop-documentation.webp" alt="The built-in Documentation tab with the CodeAlta user guide and its navigation" width="2400" height="1500" loading="lazy"></div>
        </section>
        <section class="alta-story-step" id="feature-models" aria-labelledby="feature-title-models" tabindex="-1">
          <div class="alta-story-copy">
            <p class="alta-kicker"><span>18</span> Your choice of AI</p>
            <h3 id="feature-title-models">Your AI.<br>Your choice.</h3>
            <p>Use your Claude Code, Codex, GitHub Copilot or Mistral Vibe subscriptions, even several accounts, run OpenAI's Codex agent itself with <a href="{{site.basepath}}/docs/model-providers/setup/#codex-cli">Codex CLI</a> (ChatGPT Business and Enterprise included), or connect an API-based provider or a <a href="{{site.basepath}}/docs/model-providers/setup/#llamacpp">llama.cpp</a> server of your own, and continue in CodeAlta the Claude Code sessions you started in VS Code or a terminal. Add any agent of the <a href="{{site.basepath}}/docs/model-providers/acp-agents/">ACP registry</a>, such as Cursor, Gemini CLI or Qwen Code, in one click: it works with the tools and permissions of CodeAlta. Choose a model for each session, or a tier (Complex, Standard, Quick), and let CodeAlta go on with the next model of the tier when a provider runs out of quota. Or try <a href="{{site.basepath}}/docs/model-tiers/#adaptive-experimental">Adaptive</a>: CodeAlta chooses the model of each turn, close to the best model for a fraction of its cost.</p>
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
      <div class="alta-highlight-copy"><span class="alta-kicker">Transparent by design</span><h3>See the work, not just the answer.</h3><p>Choose how much each session may do on its own: ask first, accept edits or run freely, and turn off the tools it should not use. Read tool inputs and outputs, review modified files and follow live commands.</p><a href="{{site.basepath}}/docs/workspace/prompt/#timeline">Inside the timeline <i class="bi bi-arrow-right" aria-hidden="true"></i></a></div>
      <img src="{{site.basepath}}/img/alta-desktop-modified-files.webp" alt="A source diff showing the exact lines an agent changed" width="2400" height="1500" loading="lazy">
    </article>
    <article class="alta-highlight">
      <div class="alta-highlight-copy"><span class="alta-kicker">Extensible by design</span><h3>Your tools belong here, too.</h3><p>Bring in MCP tools, skills of your own or published by your MCP servers, the views of MCP Apps right in the conversation, and the coding agents of the ACP registry as providers. Add trusted .NET plugins with their own canvases, commands and buttons - or ask an agent to build one.</p><a href="{{site.basepath}}/docs/plugins/">Make it your own <i class="bi bi-arrow-right" aria-hidden="true"></i></a></div>
      <img src="{{site.basepath}}/img/alta-desktop-canvases.webp" alt="The Canvases page with interactive Board and Checklist plugin tools" width="2400" height="1500" loading="lazy">
    </article>
  </div>
  <div class="alta-workflow-links">
    <a href="{{site.basepath}}/docs/prompts/"><i class="bi bi-signpost-split" aria-hidden="true"></i><strong>Agent prompts</strong><span>Plan, build, review - or define your own role.</span><i class="bi bi-arrow-up-right" aria-hidden="true"></i></a>
    <a href="{{site.basepath}}/docs/plugins/mcp/"><i class="bi bi-plug" aria-hidden="true"></i><strong>MCP servers</strong><span>Connect the tools and services you already use.</span><i class="bi bi-arrow-up-right" aria-hidden="true"></i></a>
    <a href="{{site.basepath}}/docs/copilot-layout/"><i class="bi bi-github" aria-hidden="true"></i><strong>GitHub Copilot layout</strong><span>Your .github instructions, skills and agents work as they are.</span><i class="bi bi-arrow-up-right" aria-hidden="true"></i></a>
    <a href="{{site.basepath}}/docs/ui-tools/"><i class="bi bi-cursor" aria-hidden="true"></i><strong>UI tools</strong><span>Let an agent see and drive the CodeAlta window.</span><i class="bi bi-arrow-up-right" aria-hidden="true"></i></a>
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
    <p>No window at all? <code>altacli</code> runs the same harness from a script, a CI job or a container: a prompt to its end, with text or JSONL output and exit codes, or CodeAlta as an ACP agent for your editor. Its sessions show in CodeAlta Desktop and TUI.</p>
    <div class="alta-install-card">
      <div class="alta-install-label"><i class="bi bi-gear-wide-connected" aria-hidden="true"></i> Install altacli</div>
      <pre class="language-shell-session"><code>dotnet tool install -g CodeAlta.Cli
altacli run "Fix the failing test"</code></pre>
    </div>
    <a href="{{site.basepath}}/docs/command-line/">Run CodeAlta from the command line <i class="bi bi-arrow-right" aria-hidden="true"></i></a>
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
