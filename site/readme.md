---
title: Home
layout: simple
og_type: website
---

<section class="text-center py-5 codealta-hero">
  <div class="container">
    <pre class="codealta-ascii-logo mx-auto" aria-label="CodeAlta"><span class="logo-code">   ██████                  ██           </span><span class="logo-alta">     ██       ██    ██</span>
<span class="logo-code">  ██░░░░██                ░██           </span><span class="logo-alta">    ████     ░██   ░██</span>
<span class="logo-code"> ██    ░░    ██████       ░██   █████   </span><span class="logo-alta">   ██░░██    ░██  ██████   ██████</span>
<span class="logo-code">░██         ██░░░░██   ██████  ██░░░██  </span><span class="logo-alta">  ██  ░░██   ░██ ░░░██░   ░░░░░░██</span>
<span class="logo-code">░██        ░██   ░██  ██░░░██ ░███████  </span><span class="logo-alta"> ██████████  ░██   ░██     ███████</span>
<span class="logo-code">░░██    ██ ░██   ░██ ░██  ░██ ░██░░░░   </span><span class="logo-alta">░██░░░░░░██  ░██   ░██    ██░░░░██</span>
<span class="logo-code"> ░░██████  ░░██████  ░░██████ ░░██████  </span><span class="logo-alta">░██     ░██  ███   ░░██  ░░████████</span>
<span class="logo-code">  ░░░░░░    ░░░░░░    ░░░░░░   ░░░░░░   </span><span class="logo-alta">░░      ░░  ░░░     ░░    ░░░░░░░░</span></pre>
    <p class="lead mt-4 mb-4">
      A keyboard-first, terminal AI coding workspace for managing projects, model providers, durable sessions, agent prompts, MCP-backed tools, plugins, and delegated agents.
    </p>
    <div class="d-flex justify-content-center gap-3 mt-4 flex-wrap">
      <a href="{{site.basepath}}/docs/getting-started/" class="btn btn-primary btn-lg"><i class="bi bi-rocket-takeoff"></i> Get started</a>
      <a href="{{site.basepath}}/docs/model-providers/" class="btn btn-outline-secondary btn-lg"><i class="bi bi-cpu"></i> Configure providers</a>
      <a href="https://github.com/CodeAlta/CodeAlta" class="btn btn-info btn-lg"><i class="bi bi-github"></i> GitHub</a>
    </div>
    <div class="mt-4 text-start mx-auto" style="max-width: 48rem;">
      <pre class="language-shell-session"><code>dotnet tool install -g CodeAlta.Tui
altatui</code></pre>
      <p class="text-center text-secondary mt-2" style="font-size: 0.85rem;">The terminal NuGet package is <a href="https://www.nuget.org/packages/CodeAlta.Tui/" class="text-secondary">CodeAlta.Tui</a>; the installed command is <code>altatui</code>. Requires <a href="https://dotnet.microsoft.com/en-us/download/dotnet/10.0" class="text-secondary">.NET 10</a>. Alternatively, use <code>dnx --yes CodeAlta.Tui</code> to install, update, and run in a single command.</p>
      <p class="text-center text-secondary">Development branch: these commands describe the renamed terminal package once released. Earlier releases used <code>CodeAlta</code> / <code>alta</code>; the desktop head is still in development. The in-session <code>alta</code> tool and shared <code>~/.alta</code> state are unchanged.</p>
    </div>
  </div>
</section>

<section class="container my-5 codealta-workflow-preview">
  <div class="workflow-preview-panel">
    <div class="workflow-terminal-frame">
      <div class="workflow-terminal-titlebar" aria-hidden="true">
        <span></span><span></span><span></span><strong>altatui</strong>
      </div>
      <div class="workflow-demo-video">
        <video controls autoplay loop muted playsinline preload="metadata" poster="{{site.basepath}}/img/alta-home.png" aria-label="CodeAlta terminal workflow video">
          <source src="{{site.basepath}}/img/alta-multi-agents.mp4" type="video/mp4">
          <a href="{{site.basepath}}/img/alta-multi-agents.mp4">Download the CodeAlta terminal workflow video.</a>
        </video>
      </div>
    </div>
  </div>
</section>

<section class="container my-5 codealta-capabilities">
  <div class="text-center mx-auto" style="max-width: 58rem;">
    <p class="text-uppercase text-secondary fw-semibold mb-2">Recent workflow capabilities</p>
    <h2 class="display-6 mb-3">Compose higher-level coding workflows instead of only sending one prompt at a time.</h2>
    <p class="lead text-secondary mb-4">Use built-in modes, custom agent prompts, MCP servers, skills, delegated sessions, notes, reminders, and trusted plugins together. You ask for the outcome; CodeAlta-managed agents coordinate the available host capabilities.</p>
  </div>
  <div class="row g-4 mt-2">
    <div class="col-md-6 col-xl-4">
      <div class="card h-100 shadow-sm">
        <div class="card-body">
          <h3 class="h5"><i class="bi bi-signpost-split text-primary"></i> Plan mode and agent prompts</h3>
          <p>Create selectable workflow profiles such as planning-only, implementation, review, triage, or project-specific release assistance.</p>
          <a href="{{site.basepath}}/docs/prompts/" class="stretched-link">Learn about agent prompts</a>
        </div>
      </div>
    </div>
    <div class="col-md-6 col-xl-4">
      <div class="card h-100 shadow-sm">
        <div class="card-body">
          <h3 class="h5"><i class="bi bi-hdd-network text-info"></i> MCP server support</h3>
          <p>Configure stdio or HTTP/SSE Model Context Protocol servers, inspect tools, and activate server tools for future agent turns.</p>
          <a href="{{site.basepath}}/docs/plugins/mcp/" class="stretched-link">Configure MCP servers</a>
        </div>
      </div>
    </div>
    <div class="col-md-6 col-xl-4">
      <div class="card h-100 shadow-sm">
        <div class="card-body">
          <h3 class="h5"><i class="bi bi-stars text-warning"></i> Advanced agent workflows</h3>
          <p>Prompts can direct agents to inspect CodeAlta state, ask for structured approval, update notes, set reminders, and coordinate sessions.</p>
          <a href="{{site.basepath}}/docs/advanced-agent-workflows/" class="stretched-link">Build advanced workflows</a>
        </div>
      </div>
    </div>
    <div class="col-md-6 col-xl-4">
      <div class="card h-100 shadow-sm">
        <div class="card-body">
          <h3 class="h5"><i class="bi bi-diagram-3 text-success"></i> Sessions and delegation</h3>
          <p>Keep work durable, compare providers or reasoning levels, and let parent sessions synthesize bounded child-session results.</p>
          <a href="{{site.basepath}}/docs/sessions/" class="stretched-link">Coordinate sessions</a>
        </div>
      </div>
    </div>
    <div class="col-md-6 col-xl-4">
      <div class="card h-100 shadow-sm">
        <div class="card-body">
          <h3 class="h5"><i class="bi bi-journal-check text-secondary"></i> Notes, reminders, and approvals</h3>
          <p>Ask agents to keep visible Markdown checklists, return later to check delegated work, or request structured user decisions before proceeding.</p>
          <a href="{{site.basepath}}/docs/advanced-agent-workflows/#workflow-recipes" class="stretched-link">See workflow recipes</a>
        </div>
      </div>
    </div>
    <div class="col-md-6 col-xl-4">
      <div class="card h-100 shadow-sm">
        <div class="card-body">
          <h3 class="h5"><i class="bi bi-puzzle text-danger"></i> Skills and trusted plugins</h3>
          <p>Add reusable skill context or extend the host with trusted local .NET plugins when prompts and configuration are not enough.</p>
          <a href="{{site.basepath}}/docs/plugins/" class="stretched-link">Explore extensibility</a>
        </div>
      </div>
    </div>
  </div>
</section>

<section class="container my-5 codealta-principles">
  <div class="principles-intro mx-auto text-center">
    <p class="text-uppercase text-secondary fw-semibold mb-2">CodeAlta principles</p>
    <h2 class="display-6 mb-3">Efficient. Transparent. Keyboard-first. Session-oriented. Provider-agnostic. Native .NET. Error-aware. Extensible.</h2>
    <p class="lead mb-0">A compact set of design principles for a terminal workspace that stays practical while it grows.</p>
  </div>
  <div class="principle-flow mt-5">
    <article class="principle-feature" style="--accent: #f472ff; --accent-2: #38bdf8;">
      <div class="principle-copy">
        <span class="principle-kicker">Efficient interface</span>
        <h3><i class="bi bi-arrows-collapse"></i> Using terminal space efficiently</h3>
      </div>
      <div class="principle-shot principle-shot--image">
        <img src="{{site.basepath}}/img/alta-home.png" alt="CodeAlta main workspace with projects, session timeline, prompt editor, and provider footer" loading="lazy">
      </div>
    </article>
    <article class="principle-feature" style="--accent: #60a5fa; --accent-2: #c084fc;">
      <div class="principle-copy">
        <span class="principle-kicker">Transparent execution</span>
        <h3><i class="bi bi-eye"></i> Keeping execution inspectable</h3>
      </div>
      <div class="principle-shot principle-shot--image principle-shot--wide">
        <img src="{{site.basepath}}/img/alta-tool-input-output-dialog.png" alt="CodeAlta tool input and output dialog showing inspectable execution details" loading="lazy">
      </div>
    </article>
    <article class="principle-feature" style="--accent: #34d399; --accent-2: #facc15;">
      <div class="principle-copy">
        <span class="principle-kicker">Keyboard-first workflow</span>
        <h3><i class="bi bi-keyboard"></i> Working keyboard-first</h3>
      </div>
      <div class="principle-shot principle-shot--image principle-shot--wide">
        <img src="{{site.basepath}}/img/alta-command-palette.png" alt="CodeAlta command palette with keyboard-first commands" loading="lazy">
      </div>
    </article>
    <article class="principle-feature" style="--accent: #22d3ee; --accent-2: #a78bfa;">
      <div class="principle-copy">
        <span class="principle-kicker">Session-oriented workspace</span>
        <h3><i class="bi bi-diagram-3"></i> Coordinating multiple agents in durable sessions</h3>
      </div>
      <div class="principle-shot principle-shot--image">
        <img src="{{site.basepath}}/img/alta-help.png" alt="CodeAlta help dialog listing session, queue, steering, and delegation shortcuts" loading="lazy">
      </div>
    </article>
    <article class="principle-feature" style="--accent: #fb923c; --accent-2: #38bdf8;">
      <div class="principle-copy">
        <span class="principle-kicker">Provider-agnostic runtime</span>
        <h3><i class="bi bi-cpu"></i> Switching between multiple providers and models, local and remote</h3>
      </div>
      <div class="principle-shot principle-shot--image">
        <img src="{{site.basepath}}/img/alta-model-providers.png" alt="CodeAlta Model Providers dialog for configuring providers and models" loading="lazy">
      </div>
    </article>
    <article class="principle-feature" style="--accent: #818cf8; --accent-2: #2dd4bf;">
      <div class="principle-copy">
        <span class="principle-kicker">Native .NET foundation</span>
        <h3><i class="bi bi-braces-asterisk"></i> Staying native to C# and .NET</h3>
      </div>
      <div class="principle-shot principle-shot--image">
        <img src="{{site.basepath}}/img/alta-code-editor.png" alt="CodeAlta native terminal editor with syntax-highlighted C# code" loading="lazy">
      </div>
    </article>
    <article class="principle-feature" style="--accent: #f43f5e; --accent-2: #fbbf24;">
      <div class="principle-copy">
        <span class="principle-kicker">Actionable errors</span>
        <h3><i class="bi bi-life-preserver"></i> Turning failures into repair paths</h3>
      </div>
      <div class="principle-shot principle-shot--image">
        <img src="{{site.basepath}}/img/alta-config-recovery.png" alt="CodeAlta configuration recovery editor with TOML validation feedback" loading="lazy">
      </div>
    </article>
    <article class="principle-feature" style="--accent: #a3e635; --accent-2: #06b6d4;">
      <div class="principle-copy">
        <span class="principle-kicker">Extensible workflows</span>
        <h3><i class="bi bi-puzzle"></i> Composing prompts, tools, MCP, skills, and trusted plugins</h3>
      </div>
      <div class="principle-shot principle-shot--image">
        <img src="{{site.basepath}}/img/alta-system-prompt-and-user-prompt.png" alt="CodeAlta timeline showing selected agent prompt and system prompt details" loading="lazy">
      </div>
    </article>
  </div>
  <div class="principles-cta text-center mt-5">
    <a href="{{site.basepath}}/docs/principles/" class="btn btn-outline-primary btn-lg">Read the full principles</a>
  </div>
</section>
<style>
.codealta-ascii-logo {
  display: block;
  width: max-content;
  max-width: 100%;
  overflow-x: auto;
  padding: 1rem 1.25rem;
  margin-bottom: 0;
  border-radius: 1rem;
  background: radial-gradient(circle at 20% 10%, rgba(0, 209, 255, 0.14), transparent 28%), linear-gradient(135deg, rgba(11, 18, 32, 0.52), rgba(17, 27, 48, 0.32));
  border: 1px solid rgba(255, 255, 255, 0.10);
  box-shadow: 0 1.25rem 3rem rgba(0, 0, 0, 0.28);
  color: rgba(234, 242, 255, 0.92);
  font-family: "Cascadia Mono", "SFMono-Regular", Consolas, "Liberation Mono", Menlo, monospace;
  font-size: clamp(0.42rem, 1.23vw, 1rem);
  line-height: 1.05;
  text-align: left;
  white-space: pre;
}
.logo-code { color: rgba(234, 242, 255, 0.86); }
.logo-alta {
  color: transparent;
  background-image: linear-gradient(115deg, rgba(0, 209, 255, 0.82), #7ae8ff, #4f46e5, #a855f7, #ffffff, #00d1ff);
  background-size: 240% 240%;
  background-position: var(--alta-logo-shift, 0%) 50%;
  -webkit-background-clip: text;
  background-clip: text;
  filter: drop-shadow(0 0 0.4rem rgba(0, 209, 255, 0.26));
}
.codealta-workflow-preview {
  position: relative;
  isolation: isolate;
}
.workflow-preview-panel {
  position: relative;
  max-width: 72rem;
  margin-inline: auto;
}
.workflow-preview-panel::before,
.workflow-preview-panel::after {
  content: "";
  position: absolute;
  pointer-events: none;
  z-index: -1;
  border-radius: 999px;
}
.workflow-preview-panel::before {
  inset: -6rem -4rem -8rem -4rem;
  background:
    radial-gradient(circle at 18% 35%, rgba(0, 209, 255, .44), transparent 28%),
    radial-gradient(circle at 52% 18%, rgba(99, 102, 241, .34), transparent 30%),
    radial-gradient(circle at 82% 55%, rgba(244, 114, 255, .36), transparent 31%),
    radial-gradient(circle at 46% 92%, rgba(45, 212, 191, .22), transparent 34%);
  filter: blur(4.8rem);
  opacity: .72;
}
.workflow-preview-panel::after {
  inset: 12% 8% -18% 8%;
  background: linear-gradient(90deg, rgba(0, 209, 255, .12), rgba(168, 85, 247, .16), rgba(45, 212, 191, .10));
  filter: blur(3rem);
  opacity: .78;
}
.workflow-terminal-frame {
  position: relative;
  border: 1px solid rgba(255,255,255,.16);
  border-radius: 1.35rem;
  background: linear-gradient(180deg, rgba(12, 23, 40, .98), rgba(3, 10, 19, .98));
  box-shadow:
    inset 0 0 0 1px rgba(255,255,255,.035),
    0 1.7rem 4rem rgba(0,0,0,.34),
    0 0 4rem rgba(0, 209, 255, .10),
    0 0 5rem rgba(168, 85, 247, .08);
  overflow: hidden;
}
.workflow-terminal-titlebar {
  display: flex;
  align-items: center;
  gap: .5rem;
  padding: .72rem .95rem;
  color: rgba(234, 242, 255, .72);
  background: linear-gradient(90deg, rgba(255,255,255,.105), rgba(255,255,255,.04));
  border-bottom: 1px solid rgba(255,255,255,.09);
  font-family: "Cascadia Mono", Consolas, monospace;
  font-size: .86rem;
}
.workflow-terminal-titlebar span {
  display: inline-block;
  width: .7rem;
  height: .7rem;
  border-radius: 50%;
}
.workflow-terminal-titlebar span:nth-child(1) { background: #ff5f56; }
.workflow-terminal-titlebar span:nth-child(2) { background: #ffbd2e; }
.workflow-terminal-titlebar span:nth-child(3) { background: #27c93f; margin-right: .45rem; }
.workflow-terminal-titlebar strong {
  font-weight: 700;
  letter-spacing: .04em;
}
.workflow-demo-video {
  background: #07111f;
}
.workflow-demo-video video {
  display: block;
  width: 100%;
  height: auto;
}
.codealta-principles {
  position: relative;
}
.principles-intro {
  max-width: 68rem;
  position: relative;
}
.principles-intro::after {
  content: "";
  display: block;
  width: min(36rem, 82%);
  height: 1px;
  margin: 1.75rem auto 0;
  background: linear-gradient(90deg, transparent, rgba(0, 209, 255, .55), rgba(168, 85, 247, .55), transparent);
}
.principle-flow {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  align-items: stretch;
  gap: clamp(1.1rem, 2.1vw, 1.75rem);
}
.principle-feature {
  isolation: isolate;
  position: relative;
  display: grid;
  grid-template-columns: minmax(0, .82fr) minmax(12rem, 1.18fr);
  align-items: center;
  gap: clamp(.9rem, 1.8vw, 1.4rem);
  min-height: 14.5rem;
  padding: clamp(1rem, 2vw, 1.5rem);
  border: 1px solid rgba(255, 255, 255, .10);
  border-radius: 1.65rem;
  background:
    linear-gradient(135deg, rgba(255,255,255,.075), rgba(255,255,255,.025) 38%, rgba(255,255,255,.055)),
    rgba(7, 17, 31, .70);
  box-shadow: 0 1.35rem 3rem rgba(0, 0, 0, .20);
  overflow: hidden;
}
.principle-feature::before,
.principle-feature::after {
  content: "";
  position: absolute;
  z-index: -1;
  border-radius: 999px;
  filter: blur(8px);
  opacity: .20;
}
.principle-feature::before {
  width: 15rem;
  height: 15rem;
  inset: -7rem auto auto -5rem;
  background: radial-gradient(circle, var(--accent), transparent 68%);
}
.principle-feature::after {
  width: 12rem;
  height: 12rem;
  right: -5rem;
  bottom: -6rem;
  background: radial-gradient(circle, var(--accent-2), transparent 68%);
}
.principle-copy {
  position: relative;
  z-index: 1;
}
.principle-kicker {
  display: inline-flex;
  align-items: center;
  gap: .4rem;
  margin-bottom: .55rem;
  color: rgba(234, 242, 255, .66);
  font-size: .72rem;
  font-weight: 700;
  letter-spacing: .12em;
  text-transform: uppercase;
}
.principle-copy h3 {
  margin: 0;
  color: rgba(248, 252, 255, .96);
  font-size: clamp(1.08rem, 1.25vw, 1.42rem);
  line-height: 1.14;
}
.principle-copy h3 i {
  display: inline-grid;
  place-items: center;
  width: 2.05rem;
  height: 2.05rem;
  margin-right: .48rem;
  border-radius: .78rem;
  color: white;
  background: linear-gradient(135deg, var(--accent), var(--accent-2));
  box-shadow: 0 .6rem 1.35rem color-mix(in srgb, var(--accent) 25%, transparent);
  font-size: 1rem;
  vertical-align: .08em;
}
.principle-shot {
  border: 1px solid rgba(255, 255, 255, .13);
  border-radius: 1.1rem;
  background: linear-gradient(180deg, rgba(11, 18, 32, .96), rgba(3, 10, 19, .96));
  box-shadow: inset 0 0 0 1px rgba(255,255,255,.035), 0 .85rem 2rem rgba(0,0,0,.22);
  overflow: hidden;
}
.principle-shot--image {
  display: block;
}
.principle-shot--image img {
  display: block;
  width: 100%;
  height: clamp(9.4rem, 13vw, 12.5rem);
  object-fit: cover;
  object-position: center top;
}
.principle-shot--wide img { height: clamp(8rem, 11vw, 10rem); }
.principles-cta .btn {
  border-radius: 999px;
}
@media (max-width: 1199.98px) {
  .principle-flow {
    grid-template-columns: 1fr;
  }
}
@media (max-width: 991.98px) {
  .principle-feature {
    grid-template-columns: 1fr;
  }
}
@media (prefers-reduced-motion: reduce) {
  .logo-alta { background-position: 50% 50%; }
}
</style>

<script>
(function () {
  "use strict";
  var alta = document.querySelector(".logo-alta");
  if (!alta || window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
  var start;
  function tick(timestamp) {
    if (start === undefined) start = timestamp;
    var phase = ((timestamp - start) / 5200) % 1;
    document.documentElement.style.setProperty("--alta-logo-shift", (phase * 100).toFixed(2) + "%");
    window.requestAnimationFrame(tick);
  }
  window.requestAnimationFrame(tick);
})();
</script>
