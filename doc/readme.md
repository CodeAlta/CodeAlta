# CodeAlta internal documentation

The architecture of CodeAlta for maintainers and agents: what each part owns, how the parts fit, and why. The user guide is in `site/docs`; the repository rules are in [AGENTS.md](../AGENTS.md) and the [development guide](development-guide.md).

| Document | What it covers |
| --- | --- |
| [Architecture](architecture.md) | Principles, composition, projects and their layers, a turn, where new code goes. Start here. |
| [Development guide](development-guide.md) | Rules that hold across the repository, tests, and how the docs are kept. |
| [Runtime](runtime.md) | Sessions, turns, the session actor, journals and the session database, tools, compaction, fork and rewind. |
| [Catalog and configuration](catalog-and-config.md) | `~/.alta` and project `.alta`, instance state, configuration, projects and spaces. |
| [Model providers](providers.md) | The provider model, API providers, agents that run their own loop (Claude Code, ACP), credentials. |
| [`alta` live tool](live-tool.md) | The commands agents call, their contract and their safety rules. |
| [Plugins](plugins.md) | Source plugins, contributions, the plugin side of the window, built-in plugins. |
| [MCP](mcp.md) | MCP servers: configuration, tools, apps and skills of servers. |
| [Browser](browser.md) | The drivers of real browsers, how a session's browser is chosen and owned, consent, the sandbox rules. |
| [Skills](skills.md) | Skill discovery, precedence and activation. |
| [CodeAlta Desktop](desktop.md) | The host, the RPC, the React page, windows, security boundaries, native workers. |
| [Statistics](statistics.md) | How the statistics read journals and keep their facts. |
| [Reviews](review.md) | Review threads on plans, files and changes. |
| [Site screenshots](site-screenshots.md) | How the pictures of the website are taken, for releases. |

These documents describe the current architecture only: no history, plans or decision logs. A detail that explains one piece of code is a comment in that code.
