---
title: User Guide
---

# User Guide

CodeAlta is a workspace for agentic coding: your projects, the sessions of your agents, the providers they run on, and the tools they use, in one place.

It comes as two apps that run the same agents on the same `~/.alta` profile:

- **CodeAlta Desktop**, the `alta` command, is the app we recommend: tabs you can split or move to windows of their own, a code editor, terminals, git changes, automations and one Settings window.
- **CodeAlta TUI**, the `altatui` command: a keyboard-first terminal UI.

**CodeAlta CLI**, the `altacli` command, runs the same agents without a window, for scripts, CI and editors: see [Command Line](command-line.md).

This guide applies to the two apps. Screenshots have a **Desktop / TUI** switch, and pages point out where the two apps differ.

{{ alta_shot "alta-desktop-home.webp" "alta-home.png" "CodeAlta workspace showing the projects sidebar, a session timeline, the prompt editor, and provider status" "The main workspace keeps projects, sessions, timeline entries, prompt drafting, provider state, agent prompt selection, and context usage in one place." }}

## Start here

1. [Getting Started](getting-started.md): install, launch, set up a first provider and send a first prompt.
2. [Desktop and TUI](desktop-and-tui.md): what the two apps share and what the desktop app adds.
3. [Model Providers](model-providers/readme.md): subscriptions, API keys, local models and ACP agents.
4. [Workspace](workspace/readme.md): the window, the prompt and the timeline, the code editor and terminals, Settings.
5. [Sessions](sessions/readme.md): queues, steering, compaction, fork and rewind, and the work agents delegate.

## Working with agents

- [Agent Prompts](prompts.md): Default and Plan modes, and prompts of your own.
- [Model Tiers](model-tiers.md): run a session on Complex, Standard or Quick, fall back to the next model when a provider runs out of quota, and resume when the limit resets.
- [Spaces](spaces.md): a setup for each kind of work, with its projects, providers, tools and identity.
- [Worktrees](worktrees.md): sessions that work in a git worktree of their own.
- [Reviews](reviews.md): comment on a plan, a file or changes, and have agents wait for your approval before they commit.
- [Work Items](work-items.md): the tasks agents propose, the plans you approved and the reviews in flight.
- [Bookmarks](bookmarks.md): the messages and sessions you marked.
- [Automations](automations.md): prompts run on a schedule, on a new issue or pull request, or on demand.

## Tools and integrations

- [Command Line](command-line.md): run a prompt from a script, a CI job or a benchmark with `altacli`, or serve CodeAlta to an editor over ACP.
- [Search](search.md): find earlier conversations, plans, skills and tools by what they are about.
- [Speech to Prompt](speech.md): dictate a prompt with a speech model that runs on your computer.
- [Issues and PRs](issues.md): issues and pull requests from GitHub, GitLab, Azure DevOps, Bitbucket or Jira.
- [Copilot Layout](copilot-layout.md): the instructions, skills, agents and MCP servers a project keeps for GitHub Copilot.
- [UI Tools and MCP](ui-tools.md): an agent sees and drives the window of CodeAlta Desktop.
- [Plugins](plugins/readme.md): the built-in plugins, MCP servers, plugins of your own.
- [Principles](principles.md): the ideas CodeAlta is built on.
- [Troubleshooting](troubleshooting.md): logs, broken configuration, sign-in and startup problems.
