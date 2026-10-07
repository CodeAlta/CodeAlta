---
title: User Guide
---

# User Guide

CodeAlta is a workspace for agentic coding. It brings together local project navigation, model-provider setup, prompt attachments, durable session history, agent prompts, delegated work, MCP-connected tools, skills, and trusted plugins.

It comes as two apps that run the same agents on the same `~/.alta` profile:

- **CodeAlta Desktop**, the `alta` command, is the app we recommend: session tabs you can split and arrange, a code editor, terminals and git changes for your projects, automations, and one Settings window.
- **CodeAlta TUI**, the `altatui` command: a keyboard-first terminal UI.

This guide applies to both. Screenshots have a **Desktop / TUI** switch, and pages point out where the two apps differ.

{{ alta_shot "alta-desktop-home.webp" "alta-home.png" "CodeAlta workspace showing the projects sidebar, a session timeline, the prompt editor, and provider status" "The main workspace keeps projects, sessions, timeline entries, prompt drafting, provider state, agent prompt selection, and context usage in one place." }}

## Start here

1. [Getting Started](getting-started.md): install the tools, launch CodeAlta, configure the first provider, choose an agent prompt, and send a first prompt.
2. [Desktop and TUI](desktop-and-tui.md): a table that compares the two apps, what they share, and what the desktop app adds.
3. [Model Providers](model-providers.md): understand provider configuration, credentials, models, reasoning settings, and provider testing.
4. [Agent Prompts](prompts.md): use Default and Plan modes, create global/project workflow prompts, and understand prompt override rules.
5. [Workspace and Dialogs](workspace.md): learn the main screen, timeline, prompt editor, file picker, logs, settings, and management screens.

## Workflow topics

- [Sessions and Delegation](sessions.md): global vs project sessions, multiple-agent delegation, prompt queues, steering, compaction, notes, and reminders.
- [Worktrees](worktrees.md): sessions that work in their own git worktree, so that several can change the same project at the same time.
- [Automations](automations.md): prompts that CodeAlta Desktop runs on a schedule, on a new issue or pull request, or on demand.
- [Work items](work-items.md): the follow-up tasks agents propose and the plans you approved, and where each one is carried out.
- [Issues and pull requests](issues.md): the issues and the pull requests of a project, from GitHub, GitLab, Azure DevOps, Bitbucket or Jira, and a session started on one of them.
- [UI tools and MCP server](ui-tools.md): an agent sees and drives the window of CodeAlta Desktop, from a session or from another application.
- [Advanced Agent Workflows](advanced-agent-workflows.md): how custom prompts can leverage CodeAlta live-tool capabilities for asks, notes, reminders, sessions, MCP, skills, model comparisons, and self-inspection.
- [CodeAlta Principles](principles.md): the efficient, transparent, keyboard-first, session-oriented, provider-agnostic, native .NET, error-aware, and extensible design principles.

## Extensibility and integrations

- [Plugins](plugins/readme.md): built-in plugins, trusted source plugins, plugin management, safe mode, and developer authoring guidance.
- [MCP plugin](plugins/mcp.md): configure MCP servers, manage policy, discover tools, and activate MCP tools for future agent turns.
- [Troubleshooting](troubleshooting.md): logs, broken configuration, plugin startup failures, login flows, shortcut issues, and single-instance behavior.

## What is intentionally not here

This website is end-user documentation. Internal architecture notes, implementation specs, and development plans live in the repository `doc/` folder instead of the public site.
