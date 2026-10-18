---
title: Usage statistics of skills and MCP tools, and skills at scale
status: in-progress
created: 2026-10-18
summary: The Statistics plugin counts every use of a skill (activated by an agent, attached by the user, read as SKILL.md) and every call of an MCP tool (direct or through `alta mcp tool call`), and shows them. The prompt lists first the skills used most in the last days, from a ranking frozen per day and per session so provider prompt caches survive. The skill catalog stops parsing every SKILL.md at every turn, so hundreds of skills stay cheap.
---

# Usage statistics of skills and MCP tools, and skills at scale

[#175](https://github.com/CodeAlta/CodeAlta/issues/175) asks for usage statistics of skills, with MCP included, and for a selection of the skills in the prompt from recent use (a moving average over about 7 days). With [#118](https://github.com/CodeAlta/CodeAlta/issues/118) (local search and tool search), it is what makes many installed skills usable. The statistics belong to the existing Statistics plugin; using skills better at scale is the second half of the work.

**A rule that holds everywhere:** the provider prompt cache keeps working. The system prompt and the tool list of a session never change because usage moved.

## What exists, and what is missing

| Use | In the journal | Counted by name today |
| --- | --- | --- |
| `alta skill activate <name>` (in-process providers) | activity `kind: Skill`, `name: alta`, `args: ["skill","activate","<name>"]` | No: `ParseWords` keeps two words, `SkillName` comes only from `skillName` |
| The same from Claude Code | `kind: ToolCall`, `name: alta`, same `args` | No (counted as the `alta` command `skill activate`) |
| Legacy `codealta_skills_activate` | `arguments.skillName` | Yes |
| Claude Code's own `Skill` tool | `name: Skill`, `arguments.skill` | No |
| A skill attached by the user | user content item `{"$type":"skill","name","path"}` | No: only a count (`Skills`) |
| An agent reading `SKILL.md` | `details.readFiles` | No: only a count of files read |
| An MCP tool called directly | `mcp__<server>__<tool>` (the alias) | As a tool row; the frontend splits server and tool |
| An MCP tool called through `alta mcp tool call --server s --tool t` | `alta` with `args` | No: the command `mcp tool` only |

- The `skill` list of `DetailsAsync` is shown nowhere in the window.
- The prompt has a hook that is never filled: `preferredSkills` and `preferred="…"` in `AgentInstructionTemplateProvider`.
- The skill list and the MCP block are built at **every send** and compared with the live attachment; any difference replaces it.
- `SkillCatalog.ListAsync` walks every root and parses every `SKILL.md` at each call: once per send for the prompt, per `alta skill show|activate`, `1 + projects` times per check of the search index.

## What the user gets

1. **Statistics › Tools** gets a **Skills** block: per skill, its uses, split into activated by agents, attached by you and read as `SKILL.md`, the sessions that used it and its trend. The **MCP servers** block counts every call of an MCP tool, the ones made through `alta` included. `alta statistics skills` gives the same table.
2. **The prompt** marks the skills used most in the last days `preferred="true"` and lists them first, with their whole description, whatever the number of skills: in the short forms (above 12 and above 60 skills) they stay listed in full while the others get one line or a count.
3. **Many skills stay cheap:** a parsed `SKILL.md` is kept until its file changes.

## How the ranking stays cache-friendly

- **One ranking per local day**, computed from the facts of the days **before** today: a restart in the same day gives the same ranking, and today's uses do not move it.
- **Frozen per session**: the first ranking a session gets in a process is the one it keeps for the life of the process. A session open across midnight keeps its prompt.
- **Deterministic**: sorted by score, then by name; a fixed number of skills (8 at most) and a fixed threshold.
- **Score**: uses weighted by `0.5^(age in days / 7)` over the last 28 days (an exponential moving average with a half-life of 7 days). Uses in sessions of the same project count fully, uses in the other sessions of the same space a quarter. A skill needs a score of at least 1 to be preferred.
- The ranking is a delegate of the host (`CodeAltaHostOptions.PreferredSkills`), composed by the desktop from the Statistics plugin. Without it (TUI, Statistics turned off) nothing is preferred, as today.

## Steps

**Phase 1: the facts**

- [x] 1.1 **Skill names from every path, MCP tools through `alta`.** Facts version 6.
  - `skill`: activations by an agent (`alta skill activate`, `skills activate`, `skills_activate`, under any tool name ending in `alta`, Claude Code's `Skill`, the legacy tool), counted when the call ends without failing.
  - `skill-attached`: the skills of a user prompt, by name.
  - `skill-read`: a `SKILL.md` among the files a call read, named by its folder.
  - `mcp-tool`: `server/tool` of every MCP call, direct (`mcp__s__t`) or through `alta mcp tool call`.
  - Tests: scanner, reducer, split invariance on the new records. Docs: `doc/statistics-facts.md`, `doc/statistics.md` (also schema 4, not 3).

**Phase 2: the questions and the window**

- [x] 2.1 **`SkillsAsync` and `McpToolsAsync`** (rows with uses by origin, sessions, last use, trend), `alta statistics skills|mcp`, the RPC, the golden results.
- [x] 2.2 **Tools page**: the Skills block and the MCP block from `mcp-tool`. Frontend tests, fixture data.
- [x] 2.3 **`SkillUsageScoresAsync`**: the decayed scores per project, space and skill for a day.

**Phase 3: the prompt**

- [ ] 3.1 **`PreferredSkills`** in `CodeAltaHostOptions` and `AgentInstructionTemplateProvider`: preferred first and in full in every form, the line that explains them. Tests: byte-identical prompt, the three forms, preferred skills that are not installed are ignored.
- [ ] 3.2 **Desktop composition**: the daily snapshot and the freeze per session, from the Statistics plugin. Tests: the day boundary, the freeze, Statistics absent.

**Phase 4: scale**

- [ ] 4.1 **Parsed skills cached in `SkillCatalog`** by path, length and write time. A test with 500 skills: the second listing reads no `SKILL.md`; the time recorded here.
- [ ] 4.2 **Search ranks used skills a little higher** among hits that match (a bounded factor, never a hit by itself), from the same snapshot. The evaluation is unchanged without usage.

**Phase 5: docs and verification**

- [ ] 5.1 `doc/skills.md`, `doc/statistics*.md`, `site/` (Statistics and skills pages), `readme.md` if needed.
- [ ] 5.2 `dotnet test` (Debug: the running CodeAlta is `bin/Release`), `npm test`, `lunet build`; the developer instance: the Tools page in both themes and at 900×650, a session prompt with preferred skills.

## Progress and deviations

- **1.1.** An activation is counted when its call ends without failing (it was counted at its start, with any name given); the name seen at the start is kept in the state of the open call. A `SKILL.md` read counts once per call and skill, an attachment once per prompt and skill. The MCP tool of a direct call keeps the parts of its `mcp__s__t` name as written (sanitized by the MCP plugin), the one of `alta mcp tool call` the key and the name as given; the window joins them by the sanitized form. The store schema was 4, not 3, in `doc/statistics.md`: fixed.
- **2.x.** The RPC names are `statistics.skills` and `statistics.mcp-tools`, the commands `alta statistics skills` and `alta statistics mcp`. The Tools page reads the MCP block from `McpToolsAsync` (calls through `alta` included) and adds a Skills table after it. `SkillUsageScoresAsync(end, window, halfLife)` takes the end from its caller (the start of today) and returns a sorted list.

## Out of scope

- Usage-driven MCP tools kept loaded under deferral, and the order of the MCP block by usage: they need a usage service for plugins and change tool lists; proposed as a follow-up once the skill ranking has proven itself.
- The TUI: no new screen; it keeps working without a ranking.
