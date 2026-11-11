---
name: codealta-monorepo
description: Use this skill to set a repository up as a monorepo in CodeAlta, or to change how it is set up - find the folders that are projects of their own (services, apps, libraries, git submodules), propose them with the labels that organize them, write the project map `.alta/projects.toml` with `alta project manifest`, and keep it right when the repository changes.
---

# Set a monorepo up

A repository declares the folders of it that are projects of their own in its project map, `.alta/projects.toml`, a file committed with it. Whoever adds the repository to CodeAlta gets every sub-project, grouped under the repository with the labels of the map, each with its own sessions, plans, tasks, skills and agents, on top of the setup of the repository. A sub-project is any folder: a service, an app, a library, a git submodule, a folder inside a submodule.

You cannot write under `.alta/` with your file tools: change the map with `alta project manifest`. Each command answers with JSON records; `alta project manifest <command> --help` gives its options.

## 1. Read the repository

- `alta project manifest show` - the map as CodeAlta reads it: its labels, its projects with the state of their folders, and its problems. Without a map, it says `exists: false`.
- `alta project manifest scan` - the folders that hold a project (a `package.json`, a `.csproj`, `Cargo.toml`, `go.mod`, `pyproject.toml`, ...) three levels down, the git submodules, and the files that tell how the repository is organized. Narrow it with `--glob "services/*"` (repeat it), choose what marks a project with `--marker`, go deeper with `--depth`.
- Read the workspace files the scan names (`alta.project.manifest.workspace`): `pnpm-workspace.yaml`, the `workspaces` of `package.json`, `Cargo.toml` (`[workspace] members`), `go.work`, `nx.json` and the `project.json` files, `turbo.json`, `WORKSPACE`/`MODULE.bazel`, the `.sln` files, `settings.gradle`, `.gitmodules`.
- Read `CODEOWNERS` when there is one: the teams that own folders are good labels.
- Read the `AGENTS.md` of the root and of the folders, and the readme: they tell what each part is for.

## 2. Propose, then ask

A package is not always a project. A project is a folder someone works in on its own: a service, an app, a tool, a library with its own life. Small shared packages, generated code, fixtures, examples and documentation are usually not.

Propose to the user, in one short message:

- the sub-projects: key, folder, name, and why;
- the labels that organize them (by team, by layer, by product), with at most two levels unless the repository asks for more;
- the folders you leave out, and why;
- the ones to mark `optional` (listed only for those who add them): large or rarely used parts;
- the ones whose sessions should write only their folder (`write = folder`): parts where a change must never touch the rest.

Wait for the user's answer before you write the map, unless they asked you to go ahead.

## 3. Write the map

```text
alta project manifest label backend --name Backend --icon server --color #2d72d2
alta project manifest label payments --name Payments --parent backend
alta project manifest add services/billing --name Billing --label payments --description "Invoices and payment collection."
alta project manifest add apps/web --name Web --label frontend
alta project manifest add tools/reports --optional
alta project manifest set web --write folder
alta project manifest show
```

- A key is lower-case letters, digits, `-` and `_`. It is the identity of the sub-project: its folder can move (`set <key> --path <new folder>`) and it keeps its sessions. Choose keys that will last.
- A label that `add` or `set` names and the map does not declare is created, named from its key: name it with `label`.
- Every change is checked: a change that would add a problem to the map is refused with the reason.
- End with `alta project manifest show`: no problem, every folder `ok`.

## 4. Finish

- The map is a file of the repository: tell the user to review it in Changes and commit it, so that everyone who adds the repository gets the same projects. The user also sees and edits it in CodeAlta Desktop, with **Sub-projects…** in the menu of the repository.
- Offer an `AGENTS.md` for the sub-projects that have none: what the folder is, how to build and test it, its conventions. The `AGENTS.md` of the root still applies to them.
- What a sub-project inherits from the repository: the `AGENTS.md` of every folder above it, the skills, prompts and MCP servers of the repository (its own win on a name), its `.alta/config.toml` under its own. Its plans and tasks are its own. Say so when the user wonders where to put something.

## Keep it right

- A folder that moved: `alta project manifest set <key> --path <new folder>`.
- A project that is gone: `alta project manifest remove <key>`; its folder and its sessions stay.
- New folders: `alta project manifest scan` lists them with `declared` empty.
- `alta project list --parent <repository>` lists the sub-projects as projects; `alta project show <sub-project>` names its repository.
