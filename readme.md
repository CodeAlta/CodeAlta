# CodeAlta [![ci](https://github.com/CodeAlta/CodeAlta/actions/workflows/ci.yml/badge.svg)](https://github.com/CodeAlta/CodeAlta/actions/workflows/ci.yml) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.svg?label=CodeAlta)](https://www.nuget.org/packages/CodeAlta/) [![NuGet](https://img.shields.io/nuget/v/CodeAlta.Tui.svg?label=CodeAlta.Tui)](https://www.nuget.org/packages/CodeAlta.Tui/)

CodeAlta is a workspace for agentic coding on your local projects, in a desktop app or a terminal UI.

<p align="center">
  <img src="site/img/alta-desktop-split-three.webp" alt="CodeAlta Desktop with a parent session and its two child sessions in three panes" width="920">
</p>

## 🚀 Install

Install [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), then install one of the two apps, or both:

```sh
# CodeAlta Desktop
dotnet tool install -g CodeAlta
alta
```

```sh
# CodeAlta TUI
dotnet tool install -g CodeAlta.Tui
altatui
```

Launch the app from a project folder. On first launch it opens the provider setup: sign in with a ChatGPT or GitHub Copilot subscription, or add an API key.

See [Getting Started](https://codealta.github.io/docs/getting-started/) for requirements and the first steps.

## ✨ Features

- **Two apps, the same agents**: CodeAlta Desktop has session tabs you can drag and split, a code editor and terminals for your projects, and every setting in one window. CodeAlta TUI is a keyboard-first terminal UI. Both share the `~/.alta` profile, so a session started in one can be continued in the other.
- **Sessions on your projects**: sessions are saved on disk. Queue prompts, steer a running turn, and let a session delegate tasks to child sessions.
- **Worktrees**: a session can work in its own git worktree, on its own branch, so that several sessions change the same project at the same time.
- **Automations**: in CodeAlta Desktop, a prompt can run on a schedule, when an issue or a pull request is opened, or on demand.
- **UI tools and MCP server**: in CodeAlta Desktop, an agent sees and drives the window, and other applications do the same through its MCP server.
- **The models you already have**: subscriptions and API keys for OpenAI, Anthropic, Google, Mistral, xAI, Azure OpenAI, and OpenAI-compatible servers.
- **Everything the agent did**: tool calls, file diffs, and context usage are in the timeline and open with their details.
- **Extensions**: agent prompts, MCP servers, skills, and trusted local .NET plugins.

<p align="center">
  <img src="site/img/alta-home.png" alt="CodeAlta TUI with the projects sidebar, a session timeline, and the prompt editor" width="920">
</p>

## 📖 Documentation

The user guide is at <https://codealta.github.io>:

- [Getting started](https://codealta.github.io/docs/getting-started/)
- [Desktop and TUI](https://codealta.github.io/docs/desktop-and-tui/)
- [Model providers](https://codealta.github.io/docs/model-providers/)
- [Sessions and delegation](https://codealta.github.io/docs/sessions/)
- [Prompts](https://codealta.github.io/docs/prompts/)
- [Plugins and MCP servers](https://codealta.github.io/docs/plugins/)

Maintainer notes are in [doc/readme.md](doc/readme.md).

## 🪪 License

CodeAlta is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause).

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).
