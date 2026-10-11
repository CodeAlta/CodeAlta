# CodeAlta [![ci](https://github.com/CodeAlta/CodeAlta/actions/workflows/ci.yml/badge.svg)](https://github.com/CodeAlta/CodeAlta/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.svg?label=CodeAlta)](https://www.nuget.org/packages/CodeAlta/) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.Tui.svg?label=CodeAlta.Tui)](https://www.nuget.org/packages/CodeAlta.Tui/)

CodeAlta brings AI coding agents, your code and Git into one desktop workspace. A terminal UI is available too.

<p align="center">
  <img src="site/img/alta-desktop-split-three.webp" alt="CodeAlta Desktop with a parent session and its two child sessions in three panes" width="920">
</p>

## 🚀 Install

Install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then install CodeAlta Desktop, the app we recommend:

```sh
dotnet tool install -g CodeAlta
alta
```

CodeAlta TUI runs the same agents in a terminal. You can install both:

```sh
dotnet tool install -g CodeAlta.Tui
altatui
```

Launch the app from a project folder. On first launch it opens the provider setup: sign in with a ChatGPT or GitHub Copilot subscription, or add an API key.

See [Getting Started](https://codealta.github.io/docs/getting-started/) for requirements and the first steps.

## ✨ Features

CodeAlta Desktop brings together:

- **Your choice of models**: use provider subscriptions, API keys or Claude Code.
- **Sessions and sub-agents**: resume work, queue prompts, steer agents and delegate tasks.
- **Code and Git**: an editor, terminals, diffs and separate worktrees for parallel work.
- **A flexible workspace**: arrange tabs and split panes, and group projects into spaces.
- **Automations**: run tasks on a schedule, on project events or on demand.
- **Statistics and storage**: track usage and costs, and manage [disk space and cleanup](https://codealta.github.io/docs/workspace/#storage).
- **UI automation**: let agents inspect and use the desktop through built-in tools or MCP.
- **Extensions**: customize prompts, skills, MCP servers and trusted local .NET plugins.

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
