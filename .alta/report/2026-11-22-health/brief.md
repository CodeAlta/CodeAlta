# Brief: health and architecture review of CodeAlta

## What is reviewed

The whole repository `C:\code\CodeAlta` at commit `2a3fd8388` on branch `feature50`, the branch of CodeAlta 5.0. The working folder has no change to tracked files; it has untracked work items under `.alta/tasks/`, which are not code.

CodeAlta runs AI coding agents on local projects. It is written in .NET 10 (C#) with a React and TypeScript frontend for the desktop window.

| Part | Where | Lines (C#, TS, TSX, CSS) |
| --- | --- | --- |
| Desktop frontend (React) | `src/CodeAlta/frontend` | 245,000 |
| Desktop host (C#, RPC services, native window) | `src/CodeAlta` | 91,000 |
| Terminal UI, kept working, no new features | `src/CodeAlta.Tui` | 65,000 |
| Headless libraries: catalog, orchestration, agent runtime, `alta` live tool, providers, plugins, fleet, server, node, automations, credentials, sandbox, code mode, search, model servers | the other `src/CodeAlta.*` projects | about 560,000 |
| Tests (MSTest; frontend tests beside their modules) | `src/*.Tests`, `src/CodeAlta/frontend/src/**/*.test.ts(x)` | about 350,000 of the .NET total |
| User guide (Lunet site) | `site/docs` | |
| Architecture and repository rules | `doc/`, `AGENTS.md` | |

The history starts on 2026-02-24: 4,631 commits, 3,368 of them in the last three months, by several contributors and many agent sessions.

## What the owner says hurts

From the 5.0 roadmap (GitHub issue #487), in the owner's words: "CodeAlta is over 4,600+ commits now. It has grown very quickly in the past 2 months with more than 3,000+ commits during this period, with several contributors. Still the existing architecture and code practices have proven that CodeAlta is very stable with no regressions and a consistent user experience. We need to make a deep dive into the codebase, simplify it, make sure that the architecture is solid and the codebase is healthy (e.g. remove workarounds, cleanup tech debt...etc.)"

So the review looks for what makes the next change expensive: code to delete or merge, workarounds whose cause can be fixed, decisions known in several places, boundaries that leak, migrations never finished, tests that guard the wrong thing or fail at random, and docs that grew by addition.

## The project's own rules

These are decisions, not findings. A finding reopens one only with new evidence, and says so.

- `AGENTS.md`: build and test commands, the TUI policy (kept working, no new features), contribution rules (no static mutable data, XML docs on public APIs, tests for new behavior, commit prefixes).
- `doc/architecture.md`: the principles (CodeAlta owns sessions and providers are adapters; journals are the truth and the database is the projection; one writer per session; a headless core with thin frontends; extensions use the public contracts; local and private; the prompt cache is respected; work is bounded) and the projects with their roles. The area documents in `doc/` explain each area.
- `doc/development-guide.md`: the repository rules (layers, persistence through `CodeAlta.Core`, async and threading, desktop frontend, tests and their time limits, documentation).
- `ArchitectureGuardrailTests` checks the main boundaries.
- Open work items are in `.alta/tasks/` and plans in `.alta/plans/`: a finding that one of them already covers says so.

## Constraints

- Public contracts others build on stay compatible, or a finding says how they change: the plugin API (`CodeAlta.Plugins.Abstractions`), the `alta` commands agents call, the configuration files under `~/.alta` and `.alta`, the session journal format, the fleet protocol.
- The TUI is kept working: a change in shared code keeps it building and its tests passing.
- Libraries maintained in other repositories (NeoAstra, XenoAtom, SharpYaml, Tomlyn) are out of reach of a change here; a finding may name a cost they cause.
- Every change must be able to land in small steps on `feature50`, each leaving the build and all tests passing.

## Out of scope

- Security review, performance tuning, formatting and naming style.
- A rewrite in another technology or a change of framework (React, NeoAstra, MSTest).
- Generated code: `src/CodeAlta.Acp/generated`, `src/CodeAlta.CodexAppServer/generated`, `src/CodeAlta.Browser/Cdp` (their generators are in scope).
- New features.

## What the review delivers

Findings with evidence, each with what it removes and what it adds and a first step that lands alone. The review changes nothing in the repository outside `.alta/report/2026-11-22-health/`.
