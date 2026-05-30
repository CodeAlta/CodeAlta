---
title: User Guide
---

# User Guide

CodeAlta is a terminal workspace for agentic coding. It brings together local project navigation, model-provider setup, prompt attachments, durable session history, delegated work, and trusted plugins behind the `alta` command.

Start with the first two pages, then use the topic pages as a reference while you work.

<figure class="my-4">
  <img class="img-fluid rounded-4 shadow" src="{{site.basepath}}/img/alta-home.png" alt="CodeAlta home workspace showing the projects sidebar, active session timeline, prompt editor, and provider footer" loading="lazy">
  <figcaption class="small text-secondary mt-2">The main <code>alta</code> workspace keeps projects, sessions, timeline entries, prompt drafting, provider state, and context usage in one terminal surface.</figcaption>
</figure>

## Start here

1. [Getting Started](getting-started.md): install the tool, launch it, configure the first provider, and send a first prompt.
2. [Model Providers](model-providers.md): understand the default `config.toml`, common provider types, credentials, models, reasoning settings, and provider testing.
3. [Workspace and Dialogs](workspace.md): learn the main terminal screen, timeline, prompt editor, file picker, logs, settings, and management dialogs.

## Daily workflow topics

- [Sessions and Delegation](sessions.md): global vs project sessions, multiple-agent delegation, prompt queues, steering, compaction, and the in-session `alta` live tool.
- [CodeAlta Principles](principles.md): the efficient, transparent, keyboard-first, session-oriented, provider-agnostic, native .NET, error-aware, and pluggable design manifesto.
- [Plugins](plugins/readme.md): built-in plugins, trusted source plugins, plugin management, safe mode, and developer authoring guidance.
- [Troubleshooting](troubleshooting.md): logs, broken configuration, plugin startup failures, login flows, and single-instance behavior.

## What is intentionally not here

This website is end-user documentation. Internal architecture notes, implementation specs, and development plans live in the repository `doc/` folder instead of the public site.
