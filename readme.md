# CodeAlta [![ci](https://github.com/CodeAlta/CodeAlta/actions/workflows/ci.yml/badge.svg)](https://github.com/CodeAlta/CodeAlta/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.svg?label=CodeAlta)](https://www.nuget.org/packages/CodeAlta/) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.Tui.svg?label=CodeAlta.Tui)](https://www.nuget.org/packages/CodeAlta.Tui/) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.Cli.svg?label=CodeAlta.Cli)](https://www.nuget.org/packages/CodeAlta.Cli/)

CodeAlta is an open-source workspace for AI coding agents, local code, terminals and Git. It runs on Windows, macOS and Linux, with a desktop app, a terminal UI and a command line.

Its agent harness holds its own against the vendors' own agents: on hard tasks of [SWE-bench Pro](https://codealta.github.io/docs/benchmark/), it resolves as many tasks as Claude Code with Claude Opus, for 12% less, and one more than Codex with GPT.

**No telemetry:** CodeAlta does not send usage analytics to its developers.

<p align="center">
  <img src="site/img/alta-desktop-split-three.webp" alt="CodeAlta Desktop with a parent session and its two child sessions in three panes" width="920">
</p>

## 🚀 Install

Install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (on macOS, 10.0.13 or later), then install CodeAlta Desktop, the app we recommend:

```sh
dotnet tool install -g CodeAlta
alta
```

CodeAlta TUI shares your profile, providers and sessions. Install both and continue your work in either:

```sh
dotnet tool install -g CodeAlta.Tui
altatui
```

CodeAlta CLI runs the same agents without a window, for scripts, CI and editors (ACP):

```sh
dotnet tool install -g CodeAlta.Cli
altacli run "Fix the failing test"
```

Launch the app from a project folder. On first launch it opens the provider setup: sign in with a Claude, ChatGPT or GitHub Copilot subscription, or add an API key.

See [Getting Started](https://codealta.github.io/docs/getting-started/) for requirements and the first steps.

## ✨ Features

CodeAlta Desktop brings together:

- **Sessions & delegation**: run agents in split panes; resume, queue, steer and delegate work; archive finished sessions and restore them when you need them.
  In Desktop, `Enter` sends when idle and enqueues while busy; `Cmd+Enter` on macOS or `Ctrl+Enter` on Windows/Linux sends now (steers a busy turn).
- **Several windows**: [move or drag tabs](https://codealta.github.io/docs/workspace/#windows) into windows of their own, name and color them, and find them on their monitor at the next start (the terminal UI has one screen).
- **Fork & rewind**: branch a session from any turn and keep the original, or roll it back to an earlier prompt.
- **Attach files**: [add files, images, folders and issues to a prompt](https://codealta.github.io/docs/getting-started/#attach-files) from a menu, a drop or a paste; PDFs go to the models that read them, and other documents (Word, Excel, PowerPoint…) are converted to Markdown for any model.
- **[Math & research](https://codealta.github.io/docs/workspace/prompt/#equations)**: LaTeX formulas, chemistry and footnotes in messages and documents, a live preview of the formula you type and an equation editor in the prompt (`Alt+=`), and LaTeX and BibTeX files highlighted.
- **Speech to prompt**: [dictate a prompt](https://codealta.github.io/docs/speech/) with a speech model that runs on your computer, downloaded the first time you use the microphone.
- **Search your work**: `Ctrl+P` and [`alta search`](https://codealta.github.io/docs/search/) find the prompts and answers of your sessions, your plans, the guide, the skills and the tools from a local index; turn on search by meaning to find them by what they are about, with a model that runs on your computer.
- **Code & Git**: search and edit files, run terminals and inspect diffs.
- **Worktrees**: give sessions separate checkouts and branches; manage and clean them up.
- **Prompt suggestions**: after a turn, the model suggests the next prompt in the empty composer, with any provider; `Tab` or `→` takes it.
- **Session titles**: a title you give stays; other sessions are titled from the first prompt, the last message or by the model, as you choose.
- **Work items**: review agent-proposed tasks and plans; start, defer or dismiss them; follow the reviews in flight.
- **[Reviews](https://codealta.github.io/docs/reviews/)**: comment on the lines of a plan, a file or an agent's changes, send them with a verdict, and have agents wait for your approval before they commit.
- **Bookmarks**: mark a message or a whole session, jump between the bookmarks of a timeline, and find them all in one tab.
- **Issues & pull requests**: browse issues and PRs on GitHub, GitLab, Azure DevOps or Bitbucket, plus Jira issues; start sessions and open PRs.
- **Automations**: start sessions on a schedule, issue/PR events or on demand.
- **On your phone**: [follow, answer, steer and start sessions from a phone, and answer the questions and review requests of agents](https://codealta.github.io/docs/plugins/mobile/), on your own tailnet through Tailscale.
- **Canvases**: use interactive boards, checklists, graphs and custom tools beside your conversations.
- **Plugins**: ask agents to build trusted local .NET canvases, dialogs, tools, commands and buttons for one project or the whole workspace, and [hooks](https://codealta.github.io/docs/plugins/developers/) that check, block or change the tool calls and prompts of sessions, or continue a session when its run ends.
- **Local statistics**: track activity, models, tokens, costs, tool use, skills and MCP tools by project, session or sub-agent, all on your computer.
- **Context usage**: see context pressure as a ring beside the prompt and when the prompt cache expires; inspect the last operation and session-wide token usage and costs.
- **Spaces**: give each kind of work its own setup: its projects and chats, providers and accounts (a second Claude account too), MCP servers, skills, agent prompts and plugins, and the Git, GitHub, Jira and command line (Azure, AWS, kubectl…) identity its agents use. Switch spaces while agents keep working. Inside a space, nested labels group the projects in the Explorer.
- **Welcome page**: jump back to recent projects and sessions, with useful shortcuts.
- **Built-in documentation**: read the guide beside your work, or ask an agent to help.
- **Model choice**: use Claude Code, Codex, GitHub Copilot or Mistral Vibe subscriptions, APIs, or a llama.cpp server of your own, with several accounts of the same provider; choose provider, model and reasoning effort per session.
- **Codex CLI**: run OpenAI's Codex agent as a provider, signed in from CodeAlta with Codex's own ChatGPT sign-in (ChatGPT Business and Enterprise included), with the instructions, tools, permissions and sandbox of CodeAlta.
- **ACP agents**: add a coding agent of the [ACP registry](https://agentclientprotocol.com/get-started/registry) (GitHub Copilot, Gemini CLI, Cursor…) in one click as a provider, with the tools of CodeAlta, its models named and priced and the reasoning levels of each, and continue its sessions with any other provider.
- **Adaptive (experimental)**: CodeAlta [chooses the model of each turn](https://codealta.github.io/docs/model-tiers/#adaptive-experimental): a cheap model for the turns, one recheck of each change of code against the request, and a stronger model only for a turn that ends in trouble.
- **Model tiers & fallback**: pick Complex, Standard or Quick instead of a model, and optionally [go on with the next model of the tier](https://codealta.github.io/docs/model-tiers/) when a provider runs out of quota or goes down, or resume by itself once the limit resets.
- **Claude Code with VS Code**: [continue in CodeAlta](https://codealta.github.io/docs/sessions/claude-code/) the Claude Code sessions you started in VS Code or a terminal, and open CodeAlta's in VS Code in one click.
- **Permissions & visibility**: set each session to ask first, accept edits or run freely, and turn off tools a session should not have, such as the shell; inspect tool inputs, outputs, file changes and live commands.
- **[Sandbox](https://codealta.github.io/docs/sessions/sandbox/)**: run the commands and file tools of a session in a container of the operating system, built on Microsoft's [MXC](https://github.com/microsoft/mxc), for every session or only for a provider, a model, a space or a project: it writes in its project, reaches the hosts you allow, and never reads your credentials.
- **[Code Mode & workflows](https://codealta.github.io/docs/sessions/code-mode/)**: agents write short JavaScript programs that call their tools, MCP servers and `alta` commands in loops and in parallel, with the session's permissions, and run whole workflows: steps that give each part of the work to a sub-agent, drawn live as a graph in the timeline, that can go on in the background, sleep, ask you, resume and return Markdown with pictures. Keep a run that went well as a script of the library, run it again from the **Workflows** tab or an automation, or start from the samples.
- **Agent prompts**: plan, build, review or define your own roles.
- **Skills & MCP**: bring in skills, tools and services, and the skills MCP servers publish, loaded once you approve them; the views of [MCP Apps](https://codealta.github.io/docs/plugins/mcp/#views-of-mcp-apps) show in the conversation; the skills used most come first in the prompt, and hundreds of them stay cheap.
- **Command line**: run a prompt to its end with [`altacli`](https://codealta.github.io/docs/command-line/) from a script, a CI job or a benchmark, with exit codes and a JSON Lines trace in the ACP format, or serve CodeAlta to an editor as an ACP agent; its sessions show in the app as they run.
- **Copilot compatibility**: reuse `.github` instructions, skills and agents unchanged.
- **UI automation**: let agents see and drive CodeAlta through built-in tools or MCP.
- **Storage**: manage [local disk usage and cleanup](https://codealta.github.io/docs/workspace/settings/#storage).

<p align="center">
  <img src="site/img/alta-home.png" alt="CodeAlta TUI with the projects sidebar, a session timeline, and the prompt editor" width="920">
</p>

## 📖 Documentation

Visit the [website](https://codealta.github.io/) for the feature tour and [user guide](https://codealta.github.io/docs/getting-started/), including [model providers](https://codealta.github.io/docs/model-providers/) and [how Desktop and TUI compare](https://codealta.github.io/docs/desktop-and-tui/).

For architecture and contributor guidance, see the [developer documentation](doc/readme.md).

## 🪪 License

CodeAlta is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).
