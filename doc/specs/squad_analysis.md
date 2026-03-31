# Squad Analysis for CodeAlta

## Purpose

This document analyzes the local `C:\code\squad` codebase from two angles:

- the **user/product surface** exposed by its documentation
- the **implementation/runtime design** exposed by its SDK and CLI

The goal is not to copy Squad mechanically. The goal is to extract the strongest ideas, identify the weak assumptions, and turn that into concrete guidance for CodeAlta's own design.

This report is intentionally detailed because Squad overlaps with several CodeAlta directions:

- durable agent definitions
- file-backed workspace/team knowledge
- multi-agent orchestration
- MCP/tool integration
- portability across machines and projects
- long-lived working memory
- Git-native workflows

---

## Executive Summary

Squad's strongest idea is not parallelism by itself. Its strongest idea is that the AI system is a **durable team that lives in the repository as editable files**. That produces a simple and powerful user mental model:

- the team exists independently from the current chat
- agents have names, roles, memory, and routing rules
- decisions persist
- the team can be versioned, copied, exported, reviewed, and shared

This is stronger than a typical "chat with subagents" experience because it turns team structure and memory into visible project assets.

From an implementation perspective, Squad is more lightweight than its docs imply. The docs present a fairly rich orchestration product; the codebase often implements a simpler, pragmatic subset:

- the coordinator is relatively thin
- fan-out is straightforward `Promise.allSettled`
- routing is rule-based, not very deep
- portability and consult mode are stronger than most of the runtime logic
- governance hooks are more concrete than the plugin story

This mismatch is useful. It shows that Squad already has a strong product framing, but also exposes where CodeAlta should be stricter:

- keep a single canonical config/state model
- keep event and orchestration abstractions typed and coherent
- distinguish implemented behavior from aspirational behavior
- build portability and file-backed durable state intentionally, not incidentally

For CodeAlta, the largest lessons are:

1. **Make durable knowledge user-visible**, not only internal infrastructure.
2. **Lean into file-backed portable definitions**, but keep machine-local operational state separate.
3. **Model teams, roles, and routing explicitly** as first-class product concepts.
4. **Treat consult/extract workflows as important**, not niche.
5. **Prefer deterministic governance and typed runtime contracts** over prompt-only conventions.
6. **Avoid Squad's config/runtime duplication and naming drift**.

---

## What Squad Is Trying To Be

At the product level, Squad is trying to be a **repo-native AI development team** rather than a generic assistant.

The docs consistently push the following positioning:

- the team lives in the repo
- the team persists
- the team accumulates memory
- the team can work in parallel
- the team can participate in GitHub-native workflows
- the team can be exported, imported, consulted, and extended

Key user-facing docs reinforcing that positioning:

- `C:\code\squad\docs\src\content\docs\guide.md`
- `C:\code\squad\docs\src\content\docs\get-started\first-session.md`
- `C:\code\squad\docs\src\content\docs\concepts\your-team.md`
- `C:\code\squad\docs\src\content\docs\concepts\memory-and-knowledge.md`
- `C:\code\squad\docs\src\content\docs\concepts\parallel-work.md`
- `C:\code\squad\docs\src\content\docs\concepts\github-workflow.md`
- `C:\code\squad\docs\src\content\docs\concepts\portability.md`

That framing is strong because it gives the user a clear answer to "why not just use Copilot directly?"

The answer is effectively:

- Copilot gives you a session
- Squad gives you a team with persistent structure and memory

That is a meaningful product distinction.

---

## User-Facing Analysis

## 1. The mental model is excellent

The strongest docs explain Squad in terms of a team, not in terms of tool plumbing.

Examples:

- Init Mode proposes a team composition.
- Roles are visible and named.
- Agents retain history.
- Shared decisions are explicit.
- Routing is explicit.
- Ceremonies are explicit.

This is much more concrete than a generic "planner / worker / reviewer" architecture because users can reason about it socially:

- who should do this?
- who reviews this?
- who owns decisions?
- who remembers what?

### Implication for CodeAlta

CodeAlta should continue moving toward explicit **agent/team/workspace definitions**, but should go further and give users a stronger visible operating model:

- what agents exist
- what each agent is for
- how work is routed
- what memory each agent maintains
- what shared decisions apply

Right now CodeAlta has much stronger runtime infrastructure than Squad in some areas, but its user-facing mental model is not yet as crisp.

---

## 2. Persistent memory is presented as a feature, not an implementation detail

Squad's docs on memory are among its best.

Important concepts:

- personal memory per agent (`history.md`)
- shared memory (`decisions.md`)
- reusable skills (`SKILL.md`)
- directive capture from natural language
- progressive memory growth over time

This is good product thinking because users care that the system "learns the project" and "stops forgetting decisions." They do not care whether that is implemented with markdown, sqlite, or background summarization.

### What is especially good

- The memory layers are understandable.
- The memory is inspectable.
- The memory is editable.
- The memory is durable across sessions.
- The memory is portable.

### What is weak

- The docs sometimes blur durable knowledge and operational logs.
- The current markdown-first approach risks becoming noisy and hard to maintain at scale.
- Merge behavior and structured querying are under-specified.

### Implication for CodeAlta

CodeAlta should absolutely keep the direction of:

- portable plain-text durable metadata in `~/.codealta/`
- clear separation between durable catalog data and machine-local state
- explicit per-agent/per-workspace/per-project knowledge

But CodeAlta should improve on Squad by making the durable layer **structured-first** and the rendered markdown **human-first**, not the other way around.

That is a key distinction:

- Squad often starts from the markdown artifact and builds behavior around it.
- CodeAlta should define a stronger canonical metadata contract and then render or project it into markdown-friendly documents.

This reduces drift, improves machine processing, and still preserves inspectability.

---

## 3. Parallel work is productized well, even if implementation is simpler

The docs around parallel work are compelling:

- eager fan-out
- specialized parallelism
- background execution
- dependency-aware sync when needed
- model selection per task
- reviewer protocol
- division of work across agents

This gives the user a sense that the system is operationally capable, not just chatty.

### Reality in code

The actual runtime is more modest:

- route
- build spawn configs
- spawn in parallel
- collect results

This is still fine. The point is that the product framing is ahead of the implementation.

### Implication for CodeAlta

CodeAlta already has stronger building blocks for a richer execution model:

- typed event model
- AgentHub
- explicit SDKs for Copilot and Codex
- tasks, jobs, workspaces, artifacts
- more intentional UI work

What CodeAlta should borrow from Squad is not the exact orchestration logic, but the productization:

- explain parallel work simply
- show routed specialists clearly
- show when the system is coordinating vs directly answering
- expose review/verification as normal team behavior

CodeAlta should then implement this with stronger runtime contracts than Squad currently has.

---

## 4. GitHub-native workflow thinking is strong

Squad has a strong GitHub workflow story:

- issues
- PRs
- labels as workflow state
- project boards
- PRD decomposition
- notifications and backlog monitoring

Even where the implementation is incomplete or lighter than the docs, the direction is good: the AI team is meant to integrate with the software delivery lifecycle, not just code editing.

### Implication for CodeAlta

CodeAlta should keep its current broader scope, but it should productize a few opinionated workflows:

- spec/issue -> plan -> multi-agent execution -> review -> verification
- branch/worktree-oriented implementation
- workspace-level coordination across multiple repos
- artifact production attached to projects and workspaces

Squad is repo-centric. CodeAlta can surpass it by making these workflows work across **workspace graphs** and not only inside a single repository.

That is a major opportunity.

---

## 5. Portability is one of Squad's best ideas

The docs on portability, consult mode, export/import, and upstream inheritance are among Squad's most differentiated ideas.

### Especially strong ideas

- Export/import the team.
- Make the team movable across repos.
- Copy a personal squad into another project temporarily.
- Extract generic learnings back later.
- Use plugins/upstreams to share reusable knowledge.

The important point is not the exact file format. The important point is that Squad treats the AI team as a **portable organizational asset**.

### Implication for CodeAlta

This aligns directly with CodeAlta's new file-backed catalog direction.

CodeAlta should preserve and strengthen:

- portable durable catalog under `~/.codealta/`
- machine-local state under `~/.codealta/machine/`
- file-defined agents, skills, workspaces, and projects
- export/import or sync workflows
- consult/copy/extract patterns
- team-level reuse across machines and repos

This should become one of CodeAlta's strongest differentiators.

---

## 6. Response modes are a useful UX abstraction

Squad exposes:

- direct
- lightweight
- standard
- full

This is useful because it explains why some tasks are fast and others involve more orchestration.

It gives the user a practical model for cost, depth, and latency.

### Implication for CodeAlta

CodeAlta should adopt a similar concept, but likely with more typed internal semantics.

For example, CodeAlta can distinguish:

- direct answer
- immediate single-agent work
- coordinated multi-agent work
- review/verification workflow

The UI can then show this explicitly.

This is especially useful because CodeAlta now already models event streams and can render different activity types in the terminal UI.

---

## 7. Squad's docs are strong, but they oversell some capabilities

This is the largest caution on the product side.

There is visible drift in the docs:

- `.ai-team/` vs `.squad/`
- some features described as if mature, though implemented more partially
- multiple overlapping stories for plugins, streams, and worktrees

This does not invalidate the ideas. But it does weaken confidence.

### Implication for CodeAlta

CodeAlta should be disciplined here:

- one canonical naming scheme
- one canonical state model
- clear separation between implemented and planned features
- avoid introducing multiple similar concepts unless they materially differ

This matters especially because CodeAlta is already growing a fairly broad surface area.

---

## Implementation Analysis

## 1. The adapter boundary is one of Squad's best architectural decisions

Squad has a clear adapter seam around Copilot:

- `C:\code\squad\packages\squad-sdk\src\adapter\types.ts`
- `C:\code\squad\packages\squad-sdk\src\adapter\client.ts`

This is good engineering because it stabilizes the runtime against upstream SDK churn and keeps the rest of the system from depending directly on every external nuance.

### Implication for CodeAlta

CodeAlta should preserve and continue sharpening its own backend seams:

- `CodeAlta.Agent`
- `CodeAlta.Agent.Copilot`
- `CodeAlta.Agent.Codex`

This is already the right direction, and CodeAlta should be more disciplined here than Squad:

- keep upstream-specific mapping isolated
- keep event normalization centralized
- keep backend-specific MCP and approval semantics localized
- avoid polluting the shared abstractions with leaky backend details unless truly necessary

This is one area where CodeAlta can already be stronger than Squad.

---

## 2. The coordinator is intentionally thin

The newer `SquadCoordinator` in:

- `C:\code\squad\packages\squad-sdk\src\coordinator\coordinator.ts`

does a simple pipeline:

1. direct response fast path
2. route match
3. choose strategy
4. spawn agents
5. collect results

This is good because it keeps the coordinator understandable.

The related fan-out logic in:

- `C:\code\squad\packages\squad-sdk\src\coordinator\fan-out.ts`

is similarly simple:

- compile charter
- resolve model
- create session
- send initial prompt
- aggregate results

### Strength

This keeps the orchestration easy to reason about and test.

### Weakness

The docs imply a richer operational engine than the code actually enforces. The implementation is mostly lightweight coordination plus convention.

### Implication for CodeAlta

CodeAlta should aim for:

- a thin coordination layer
- explicit orchestration primitives
- typed execution states
- richer observability than Squad

CodeAlta should **not** bury routing, state transitions, and task lifecycle inside prompt conventions.

Given CodeAlta's existing direction, a good target is:

- a thin orchestrator
- typed plans/tasks/activities/interactions
- pluggable routing policy
- backend-specific execution adapters

---

## 3. Governance hooks are stronger than the plugin story

One of the best implementation ideas in Squad is the hook pipeline:

- `C:\code\squad\packages\squad-sdk\src\hooks\index.ts`

This gives deterministic enforcement for:

- allowed write paths
- blocked shell commands
- `ask_user` rate limiting
- reviewer lockout
- PII scrubbing

This is stronger than prompt-only policy because it is:

- testable
- inspectable
- deterministic
- composable

### Implication for CodeAlta

CodeAlta should take this seriously.

It already has strong eventing and approval concepts. It should add or preserve a similarly deterministic policy layer for:

- tool usage constraints
- write boundaries
- approval policy
- user-input quotas or policies
- data scrubbing
- background execution restrictions
- MCP tool allow/deny lists

This should be a real runtime subsystem, not merely a prompt appendix.

---

## 4. Durable file-backed state is the core of the implementation

Squad's deepest architectural idea is still its file-backed team state:

- team roster
- routing
- charters
- histories
- decisions
- logs
- skills
- casting metadata

The initialization path in:

- `C:\code\squad\packages\squad-sdk\src\config\init.ts`

shows how much of the system is designed around file generation and subsequent file mutation.

### Strength

This makes the system:

- legible
- portable
- versionable
- editable by users

### Weakness

The runtime data model is not always sharply separated from the human-readable document model.

That makes it easy to start, but harder to keep coherent over time.

### Implication for CodeAlta

CodeAlta should adopt the good part of this pattern:

- store durable entities as files
- make them human-readable
- make them versionable

But CodeAlta should improve it with:

- stronger frontmatter/schema contracts
- clearer ownership between canonical metadata and generated/human narrative content
- better distinction between durable catalog vs machine-local operational data

This aligns with the recent CodeAlta storage spec direction and should remain the baseline.

---

## 5. Consult mode is one of Squad's best concrete features

The consult-mode implementation is materially interesting:

- `C:\code\squad\packages\squad-sdk\src\sharing\consult.ts`
- `C:\code\squad\packages\squad-sdk\src\resolution.ts`

The core idea:

- keep a personal/team root separate from project-local state
- copy or link the team into an external project
- keep it hidden locally
- allow extraction of reusable learnings later
- gate extraction by policy/license if needed

This is much stronger than a generic "import/export" story.

### Why it matters

It respects that users often want:

- their own AI team identity
- temporary project engagement
- a way to keep reusable knowledge
- a way to avoid polluting the target repo

### Implication for CodeAlta

CodeAlta should adopt a similar first-class concept.

This fits naturally with:

- `~/.codealta/` as portable catalog root
- workspace/project-specific overlays
- machine-local state under `~/.codealta/machine/`

CodeAlta can likely do better than Squad here because it is already rethinking its durable storage model. Consult/extract should be designed into that model now, not retrofitted later.

---

## 6. Resolution and dual-root ideas are valuable

The dual-root resolution logic in:

- `C:\code\squad\packages\squad-sdk\src\resolution.ts`

is important because it separates:

- project-local state
- team identity root

This is directly relevant to CodeAlta's emerging storage/catalog design.

### Why this matters

Without this separation, several things get harder:

- personal vs shared team assets
- consult mode
- portable identity
- machine-local vs syncable state
- multi-project reuse

### Implication for CodeAlta

CodeAlta should formalize this early:

- portable catalog root
- project/workspace references
- overlays or attachments
- machine-local operational state

This also matches the user's recent direction on `~/.codealta/`.

---

## 7. Session pooling and lifecycle handling are pragmatic

The session pool:

- `C:\code\squad\packages\squad-sdk\src\client\session-pool.ts`

is a practical piece of engineering:

- max concurrency
- idle timeout
- health tracking

This is not a glamorous subsystem, but it is the kind of thing that makes a multi-agent runtime behave sanely.

### Implication for CodeAlta

CodeAlta should ensure that session lifecycle is explicit and observable across backends:

- active
- idle
- steering
- waiting for approval
- background
- completed
- failed

CodeAlta's event model work already points in this direction. It should continue.

---

## 8. Squad has overlapping config models and overlapping coordination paths

This is probably its main implementation weakness.

Examples:

- `config/schema.ts`
- `runtime/config.ts`
- multiple coordinator entry points
- shell-specific routing logic vs SDK-level orchestration logic
- event naming inconsistencies

### Why this matters

As product surface grows, duplication becomes a design tax:

- harder to know what is canonical
- easier for docs and behavior to diverge
- harder to evolve safely
- harder to add new runtimes consistently

### Implication for CodeAlta

CodeAlta should actively avoid this.

That means:

- one canonical agent/session/event abstraction
- one canonical storage model
- one canonical routing model
- one canonical MCP/session configuration model

Adapters and UI layers can transform or present this, but the core model should stay singular.

This is especially important because CodeAlta now supports both Copilot and Codex and is already mapping subtly different behaviors.

---

## 9. Plugins are less mature than they appear

Squad talks about plugins and marketplaces, but the implementation is comparatively shallow:

- marketplace registration
- package manifests
- security checks
- discovery plumbing

This is not useless, but it is less central than the docs sometimes imply.

By contrast, the more meaningful extension mechanisms today appear to be:

- file-based agent/skill definitions
- hooks/policies
- MCP configuration
- portable/imported knowledge

### Implication for CodeAlta

CodeAlta should be careful not to over-invest in a marketplace concept too early.

Near-term extension value is more likely to come from:

- file-defined agents
- file-defined skills
- MCP server definitions
- reusable workspace/project templates
- portable catalogs

That is enough surface area already.

---

## 10. MCP is present, but not deeply orchestrated

Squad supports MCP configuration, but it feels more like a pass-through capability than a deeply modeled subsystem.

That is acceptable, but it leaves room for improvement.

### Implication for CodeAlta

CodeAlta already has an opportunity to be stronger here:

- shared high-level MCP config in `CodeAlta.Agent`
- backend-specific mapping for Copilot and Codex
- stronger validation
- explicit tool namespaces
- better lifecycle/diagnostics
- UI visibility into tool calls and approvals

Given recent work in CodeAlta, this is one area where it can quickly exceed Squad in rigor.

---

## Routing In Practice: What The Code Actually Does

This is the most important implementation clarification.

Squad does **not** have one routing system. It has at least two materially different routing paths:

1. an **SDK rule-based router**
2. a **CLI LLM-coordinator router**

That split matters because the docs mostly describe a single coherent orchestration story, while the code uses different mechanisms depending on entry point.

### A. Shell-level routing is mostly LLM-driven

In the interactive shell, the routing path is:

1. parse the user input into slash command / direct `@agent` / coordinator
2. if coordinator is needed, create a dedicated Copilot session for the coordinator
3. build a large coordinator system prompt from `team.md` and `routing.md`
4. ask the coordinator model to emit a constrained text format:
   - `DIRECT:`
   - `ROUTE:`
   - `MULTI:`
5. parse that text response
6. spawn separate agent sessions accordingly

Relevant files:

- `C:\code\squad\packages\squad-cli\src\cli\shell\router.ts`
- `C:\code\squad\packages\squad-cli\src\cli\shell\coordinator.ts`
- `C:\code\squad\packages\squad-cli\src\cli\shell\index.ts`
- `C:\code\squad\packages\squad-cli\src\cli\shell\spawn.ts`

This is important because the practical router is not a typed decision engine. It is an **LLM asked to behave like a router**, with a small text protocol on top.

That has clear advantages:

- fast to evolve
- easy to explain
- flexible with ambiguous user input
- good fit for conversational UX

But it also has real limitations:

- output parsing is brittle
- route quality depends on prompt quality
- routing behavior is harder to validate deterministically
- shell behavior and SDK behavior can drift

### B. SDK-level routing is regex/rule-based

Separately, the SDK has a typed routing compiler in:

- `C:\code\squad\packages\squad-sdk\src\config\routing.ts`
- `C:\code\squad\packages\squad-sdk\src\coordinator\coordinator.ts`

That path:

- parses `routing.md`
- generates regex patterns from `workType` and examples
- calculates a simple specificity priority
- matches the first rule whose generated regex succeeds
- returns agents or a fallback route

This is straightforward and serviceable, but it is not deep routing.

It is effectively:

- keyword expansion
- regex testing
- priority sorting
- fallback to `@coordinator`

There is no sophisticated planning or graph-based task decomposition in this router.

### Key conclusion

When Squad says "routing", the code currently means two different things:

- **shell product routing** = LLM-as-router
- **SDK runtime routing** = regex rule matching

That is one of the most important architectural tensions in the project.

### Implication for CodeAlta

CodeAlta should avoid reproducing this split unintentionally.

A stronger design would be:

- one canonical routing decision model
- optional LLM assistance for ambiguous cases
- deterministic typed fallback
- explicit route-decision events and traceability

CodeAlta can still expose a human-friendly `routing.md`-style document, but the runtime should not depend on ad hoc text parsing as the primary coordinator contract.

---

## Copilot Integration In Practice

Another important correction: Squad's implementation is more "multi-session over Copilot" than "deep native Copilot agent orchestration."

### 1. The Copilot adapter is thin and mostly pass-through

The adapter in:

- `C:\code\squad\packages\squad-sdk\src\adapter\client.ts`
- `C:\code\squad\packages\squad-sdk\src\adapter\types.ts`

does several useful things:

- wraps `CopilotClient`
- normalizes event names
- tracks connection lifecycle
- exposes Squad-stable config and session interfaces
- requires a permission handler and gives a clearer error if missing

But session creation is still largely a direct pass-through to the Copilot SDK:

- Squad config is cast and handed into `this.client.createSession(...)`
- event names are normalized from Copilot dotted event names to shorter Squad names

This is a good seam, but it is not a heavy orchestration layer by itself.

### 2. Agent spawning is separate-session orchestration, not native subagent orchestration

The practical spawn path in:

- `C:\code\squad\packages\squad-cli\src\cli\shell\spawn.ts`
- `C:\code\squad\packages\squad-sdk\src\coordinator\fan-out.ts`

creates separate sessions for each agent and sends prompts into them.

This is conceptually simple:

- load charter
- build system prompt
- create session
- send task
- stream response

I do **not** see Squad heavily relying on a native Copilot subagent runtime in the core shell path. The orchestration is mostly implemented in Squad itself by managing multiple Copilot sessions.

That means the practical architecture is closer to:

- "session-per-agent orchestration on top of Copilot"

than to:

- "one native agent host delegating through first-class Copilot subagent primitives"

### 3. `@copilot` is more of a roster/workflow concept than a deep runtime specialization

The `@copilot` support in:

- `C:\code\squad\packages\squad-cli\src\cli\commands\copilot.ts`
- `C:\code\squad\packages\squad-cli\src\cli\core\team-md.ts`

is mostly:

- roster editing
- capability guidance in `team.md`
- issue assignment / auto-assign policy

That is useful, but it is not the same thing as a rich runtime-specific Copilot execution strategy.

So when evaluating Squad's Copilot integration, the main value is:

- practical session management
- practical shell UX
- practical file-backed team scaffolding

not a particularly advanced runtime exploitation of Copilot-native orchestration features.

### 4. Permissions are simplified aggressively

The shell uses an "approve all" handler by default for local trust.

That reduces friction, but it also means that some policy/governance behavior is more optimistic than locked down by default.

This is relevant for CodeAlta because CodeAlta already has a more explicit approval and interaction model emerging in its UI.

### Implication for CodeAlta

Integrating similar concepts into CodeAlta is feasible because Squad is not doing anything magical here. The main pieces are:

- stable backend adapter
- coordinator route decision
- session-per-agent orchestration
- charter/system-prompt injection
- event streaming and aggregation

CodeAlta already has equivalents or stronger primitives for most of those.

The hard part is not "can CodeAlta do this?" The hard part is:

- choosing one coherent routing model
- keeping it typed
- making UI and runtime agree on it

---

## How Agents Are Actually Connected

This is worth stating very plainly:

**Squad does not appear to use an internal MCP server to route work between agents.**

Its agent-to-agent orchestration is implemented primarily by **Squad code creating and managing multiple Copilot sessions itself**.

### 1. The practical handoff path is coordinator -> parsed route -> new session

In the shell path:

1. the coordinator receives the user message
2. the coordinator produces a routing instruction in text form
3. Squad parses that routing instruction
4. Squad calls `dispatchToAgent(...)`
5. `dispatchToAgent(...)` creates or reuses a Copilot session for that named agent
6. the task text is sent into that session

Relevant files:

- `C:\code\squad\packages\squad-cli\src\cli\shell\index.ts`
- `C:\code\squad\packages\squad-cli\src\cli\shell\coordinator.ts`
- `C:\code\squad\packages\squad-cli\src\cli\shell\spawn.ts`

So the connection model is not:

- agent A calls an internal MCP tool that causes agent B to run

It is:

- Squad itself, outside the model, decides to start or message another session

That is a major architectural distinction.

### 2. There is a "squad_route" tool, but it is not the main runtime routing path

The SDK has a custom tool registry in:

- `C:\code\squad\packages\squad-sdk\src\tools\index.ts`

It defines tools such as:

- `squad_route`
- `squad_decide`
- `squad_memory`
- `squad_status`
- `squad_skill`

At first glance that suggests an internal tool-based agent-routing model.

But in practice, `squad_route` is currently much weaker than the shell path:

- it validates the target agent name
- it creates a route request payload
- it returns a success message
- and it explicitly says session creation will be implemented later

So this tool is more of a planned orchestration surface than the central routing mechanism used by the real shell experience.

This is another place where the codebase has parallel stories:

- **actual shell routing** = coordinator session + parsed output + explicit spawn
- **tool-based routing surface** = partially defined SDK tool contract, not yet the dominant path

### 3. MCP is treated as external integration, not internal orchestration

The MCP docs and scaffolding strongly suggest that Squad uses MCP for external services:

- GitHub
- Trello
- notifications
- deployments
- other external systems

Relevant references:

- `C:\code\squad\docs\src\content\docs\features\mcp.md`
- `C:\code\squad\packages\squad-sdk\src\config\init.ts`

The generated sample config writes `.copilot/mcp-config.json`, which configures external MCP servers for Copilot to discover.

That means MCP in Squad is primarily:

- a way to make external tools available to the model

not:

- the transport or bus Squad uses internally to connect its own agents

### 4. Even the remote-control bridge does not suggest internal MCP-based routing

The ACP bridge in:

- `C:\code\squad\packages\squad-cli\src\cli\commands\copilot-bridge.ts`

starts Copilot in ACP mode and creates a session with:

- `cwd`
- `mcpServers: []`

That further reinforces the point: the internal control path is JSON-RPC / session management, not MCP-based inter-agent messaging.

### Key conclusion

Squad's internal orchestration is primarily:

- coordinator logic in Squad
- session creation in Squad
- route parsing in Squad
- event aggregation in Squad

MCP is mainly:

- external capability injection for Copilot

not:

- the internal backbone for routing work between Squad agents

---

## What This Means For CodeAlta

This is good news for CodeAlta.

If the question is "do we need an internal MCP server just to support Squad-like routing?", the answer is:

- **no, not to match Squad's current practical design**

CodeAlta can implement comparable or better routing using:

- its own orchestration layer
- its own agent session abstractions
- explicit task/route objects
- backend-specific session creation

without introducing MCP as the internal agent-to-agent transport.

### When MCP is still useful

MCP remains valuable in CodeAlta for:

- external tools and services
- exposing CodeAlta capabilities to the model in a backend-portable way
- structured, model-callable operations where backend tools need to be discoverable

But MCP should not be confused with the orchestration core.

### Recommended CodeAlta stance

CodeAlta should likely keep three layers distinct:

1. **orchestration layer**
   - decides where work goes
   - creates/steers sessions
   - tracks tasks and lifecycle

2. **tool layer**
   - exposes model-callable actions
   - can be native tools or MCP tools

3. **integration layer**
   - external systems via MCP
   - project/workspace services

That separation is cleaner than letting "tool calls" become the only way agents coordinate.

### Difficulty of bringing Squad-like routing into CodeAlta

On this specific question, the difficulty is lower than it might first appear.

The core Squad pattern is:

- coordinator picks target(s)
- runtime spawns or reuses sessions
- tasks are sent explicitly

CodeAlta already has the foundations for that pattern.

The main design question is not transport. It is whether CodeAlta wants:

- direct orchestrator-controlled routing
- model-suggested routing with typed confirmation
- or model-executed tool-based routing

Based on Squad's code, the most robust path for CodeAlta is probably:

- orchestrator-controlled routing first
- MCP/tools as complementary capability surfaces

not the other way around.

---

## How Difficult Would Similar Routing Be In CodeAlta?

Overall difficulty: **moderate**, not high.

### Easy to medium

These parts are easy or already partially present in CodeAlta:

- file-defined agents and skills
- backend adapters for Copilot and Codex
- event streaming
- approval and user-input interactions
- session creation and message sending
- terminal UI rendering for non-assistant events

CodeAlta already has much of the runtime substrate that Squad had to build from scratch in TypeScript.

### Medium

These parts require design discipline more than raw implementation effort:

- a canonical routing definition format
- a unified "send vs steer vs enqueue" story
- agent/workspace/project scoping
- policy hooks around tools and interactions
- durable memory projection into human-readable files

### Hard

These are the parts where CodeAlta should be careful:

- keeping routing coherent across Copilot and Codex
- avoiding a split between UI-level routing, orchestration-level routing, and backend-specific routing
- building consult/extract overlays cleanly on top of the new file catalog
- keeping docs, storage, runtime, and UI terminology aligned

### Recommended CodeAlta approach

CodeAlta should not copy Squad's exact shell routing strategy.

Instead, it should combine:

1. **typed routing policy**
2. **LLM-assisted route suggestion when ambiguity is high**
3. **explicit orchestration/task objects**
4. **clear UI rendering of chosen route and why**

That would preserve the flexibility of Squad's conversational router while avoiding its current split-brain architecture.

---

## Code-Level Lessons That Matter Most

## 1. Squad's strongest practical code is not the coordinator, but the seams

The most reusable implementation lessons are:

- adapter boundary around Copilot
- consult-mode dual-root resolution
- deterministic hook pipeline
- file-backed knowledge separation

Those are more substantial than the routing logic itself.

### Why this matters

The routing logic can be rewritten later. The seams and state model are much harder to change once the system scales.

For CodeAlta, this means:

- do not obsess over imitating Squad's router
- do obsess over getting the catalog/runtime/adapter boundaries right

---

## 2. The shell path is more important than the SDK path in practice

If a user asks "how does Squad route in practice?", the answer is mostly in:

- `packages/squad-cli/src/cli/shell/index.ts`
- `packages/squad-cli/src/cli/shell/coordinator.ts`
- `packages/squad-cli/src/cli/shell/spawn.ts`

not in the SDK coordinator.

That indicates a product reality:

- the shell experience is the main product
- the SDK is supportive but not the sole behavioral source

### Implication for CodeAlta

CodeAlta should make an explicit decision:

- either the orchestration core is canonical and the UI is a presentation layer
- or the UI owns special routing behavior

The first option is much safer.

CodeAlta should keep orchestration canonical and let the UI remain thin.

---

## 3. Session-per-agent orchestration is viable

Squad demonstrates that a practical multi-agent system can be built with:

- one session per agent
- one coordinator session
- lightweight registry/pool tracking
- streamed event aggregation

That lowers the barrier for CodeAlta.

CodeAlta does not need exotic backend-native subagent features to achieve useful team orchestration. Those features can help later, but they are not required for the core product.

This is especially relevant for Codex, where native/experimental agent features may exist but do not need to be the foundation.

---

## Direct Comparison: What CodeAlta Should Adopt, Adapt, Or Avoid

## Adopt

### 1. Durable visible team/workspace memory

Adopt the principle that memory and configuration should be visible, editable, and portable.

### 2. Consult/extract workflow

Adopt the notion that the user can bring an existing team into a new context temporarily and later extract reusable learning.

### 3. Strong team-oriented mental model

Adopt the product framing that the user is working with a stable team or system of specialists, not just with a single chat session.

### 4. Response modes as a UX concept

Adopt visible distinctions between direct answers, immediate work, coordinated work, and heavier review flows.

### 5. Deterministic governance hooks

Adopt hard enforcement for tool policies and workflow restrictions, not only prompt instructions.

### 6. Git/worktree-aware workflows

Adopt the idea that workspace branching, worktrees, and task isolation should be normal and documented.

---

## Adapt

### 1. File-backed state

Adapt the idea, but make CodeAlta's durable state:

- more structured
- more schema-driven
- more clearly separated from ephemeral machine state

### 2. Agent definition format

Adapt the user-friendly, file-defined approach, but unify it with CodeAlta's own catalog design and backend abstraction needs.

### 3. Routing manifests

Adapt explicit routing, but use stronger typed contracts and better integration with workspaces/projects/tasks.

### 4. Skills and learned behavior

Adapt the skill/memory layering, but ground it in a clearer storage/catalog model and a cleaner lifecycle.

### 5. Portability and upstream inheritance

Adapt the reuse story, but ensure compatibility and provenance are explicit.

---

## Avoid

### 1. Duplicate config models

Avoid having multiple similar-but-not-identical config representations.

### 2. Duplicate orchestration paths

Avoid having shell logic, SDK logic, and runtime logic all acting as separate informal coordinators.

### 3. Overspecifying unimplemented features

Avoid presenting planned capabilities as if they are already complete.

### 4. Letting markdown become the only canonical data model

Avoid making human-authored documents the sole canonical representation for structured state.

### 5. Naming drift

Avoid multiple competing directory and concept names for the same subsystem.

---

## Specific Opportunities For CodeAlta

## 1. Elevate workspace/project/agent catalogs into the core product story

CodeAlta already has the beginnings of a stronger storage model than Squad.

It should explicitly productize:

- workspaces
- projects
- agents
- skills
- artifacts
- activity streams

This should become the visible "team brain" of CodeAlta.

Squad shows that users will understand and value this if presented clearly.

---

## 2. Make multi-repo workspaces the differentiator

Squad is mostly repo-native.

CodeAlta can differentiate by making the durable operating model work across:

- multiple repositories
- multiple checkouts
- multiple worktrees
- project sets within a workspace

That is a stronger real-world orchestration story than a single-repo team metaphor.

---

## 3. Make the event model a product feature

CodeAlta is already investing in a typed event model and richer terminal UI rendering.

This can exceed Squad if CodeAlta presents:

- reasoning
- tool calls
- approvals
- user-input requests
- plans
- session updates
- raw diagnostics when needed

as first-class observable behavior.

Squad's docs are strong, but its runtime observability is less systematically modeled than CodeAlta's current direction.

---

## 4. Make runtime policy explicit and backend-agnostic

Because CodeAlta targets both Copilot and Codex, it needs a stronger common policy layer than Squad.

That should include:

- approval policy
- auto-answer behavior
- write restrictions
- command restrictions
- MCP tool policy
- user-input handling policy
- sandbox policy where relevant

This should be explicit in the shared agent layer and mapped carefully per backend.

---

## 5. Design portability before scaling the catalog

Squad shows that portability is important enough to users that it should not be bolted on later.

CodeAlta should therefore plan early for:

- export/import
- consult mode
- personal vs shared catalog roots
- syncable vs machine-local state
- provenance on imported agents/skills
- conflict handling for decisions and memories

This should be part of the storage design from the start.

---

## 6. Give users a clearer narrative documentation set

One concrete gap exposed by comparing CodeAlta to Squad is documentation style.

Squad's best docs are:

- narrative
- workflow-oriented
- example-driven
- user-goal oriented

CodeAlta's docs are currently more infrastructure-oriented.

CodeAlta should add or improve:

- first session
- why CodeAlta
- core workflows
- how teams/workspaces/agents fit together
- how memory and portability work

This matters because the user's mental model is part of the product.

---

## Proposed Design Adjustments For CodeAlta

## 1. Treat the durable catalog as the visible system identity

The durable files under `~/.codealta/` should not just be storage. They should become the visible operating model:

- agents as defined roles
- workspaces as scopes of work
- projects as attached codebases
- skills as reusable capabilities
- activity as inspectable logs

This is the closest high-value adaptation of Squad's strongest idea.

---

## 2. Introduce first-class consult/overlay mode

CodeAlta should formalize a mode where:

- an existing personal/system team can operate within another workspace/project
- extracted learnings can be reviewed and merged back
- provenance is kept
- machine-local vs portable state remains distinct

This should not be left as an implementation afterthought.

---

## 3. Keep one canonical runtime abstraction

CodeAlta should resist the drift visible in Squad.

Required discipline:

- one event taxonomy
- one agent session abstraction
- one storage model
- one MCP configuration model
- one routing model

Everything else should adapt to those, not reinvent them.

---

## 4. Treat routing as a typed decision system, not only a markdown convention

Squad's `routing.md` is understandable and useful.

CodeAlta should keep routing inspectable, but likely back it with stronger typed metadata:

- task categories
- workspace/project affinity
- agent specialization
- verification/review rules
- escalation rules

The human-readable document can still exist, but it should not be the only source of truth.

---

## 5. Model reusable knowledge more deliberately

Squad has:

- charters
- histories
- decisions
- skills

CodeAlta should define similarly clear categories, but be stricter about lifecycle:

- what is portable
- what is workspace/project-local
- what is machine-local
- what is ephemeral
- what can be learned automatically
- what must be user-approved

This will reduce future confusion.

---

## 6. Do not overbuild plugins before the catalog and runtime are solid

Squad's marketplace story is interesting, but less mature than its core file/team model.

CodeAlta should prioritize:

- durable catalog
- consult/extract portability
- routing and task orchestration
- MCP integration
- policy/governance

before chasing a large plugin story.

---

## Suggested Priorities For CodeAlta

## Near-term

1. Finalize the file-backed catalog/storage model.
2. Make agent/workspace/project concepts explicit in UX and docs.
3. Preserve a single canonical agent/event/session abstraction.
4. Strengthen runtime policy hooks around tools, approvals, and user input.
5. Improve user-facing workflow docs.

## Medium-term

1. Add consult/extract workflows.
2. Add explicit routing definitions tied to agents/workspaces/projects.
3. Add portability/export/import around the catalog.
4. Add richer lifecycle workflows for issue/spec -> execution -> review.

## Longer-term

1. Add reusable packaged agent/skill bundles.
2. Add stronger multi-machine/team sharing.
3. Add higher-level workflow automation around ceremonies, reviews, and backlog operations.

---

## Final Assessment

Squad is valuable primarily because it has a strong answer to a product question that many agent systems answer poorly:

**What persists after the chat ends?**

Its answer is:

- the team
- the roles
- the memory
- the decisions
- the skills
- the routing rules

That is the part CodeAlta should learn from most aggressively.

Where CodeAlta should improve on Squad is equally clear:

- stronger typed runtime contracts
- cleaner abstraction boundaries
- cleaner storage model
- stricter separation between durable and ephemeral state
- more coherent cross-backend behavior
- better alignment between docs and implementation

If CodeAlta adopts Squad's strongest product ideas but implements them with more discipline, stronger typing, and a better storage/runtime architecture, it can produce a substantially stronger system than Squad rather than a parallel imitation.
