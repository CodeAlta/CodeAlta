---
title: GitHub Copilot layout
---

# GitHub Copilot layout

A project written for GitHub Copilot works in CodeAlta as it is. CodeAlta reads the files Copilot keeps in `.github` and in `~/.copilot`, beside its own files in `.alta` and `~/.alta`. Nothing has to be copied or converted.

What comes from the Copilot layout is marked **Copilot**, with the GitHub mark, wherever CodeAlta lists it.

{.table}
| What | Where Copilot keeps it | In CodeAlta |
|---|---|---|
| Instructions of a project | `.github/copilot-instructions.md` | Part of what every session of the project is told, like `AGENTS.md`. |
| Instructions for some files | `.github/instructions/*.instructions.md` | The session is told which files each one is for, and reads it before it changes such a file. |
| Your instructions for every project | `~/.copilot/copilot-instructions.md` | Part of what every session is told. |
| Skills of a project | `.github/skills/<name>/SKILL.md` | Listed in **Settings > Skills**, offered to the agent like any skill. |
| Your skills | `~/.copilot/skills/<name>/SKILL.md` | The same, for every project. |

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-copilot-skills.webp" alt="The Skills page of Settings with a skill of the .github folder marked Copilot" loading="lazy">
  <figcaption class="small text-secondary mt-2">A skill of <code>.github/skills</code> in Settings > Skills, marked Copilot.</figcaption>
</figure>

## Instructions

`AGENTS.md`, `CLAUDE.md` and `.github/copilot-instructions.md` often say the same thing. When a folder has several of them, CodeAlta reads the largest one.

An instructions file for some files names them with `applyTo`:

```markdown
---
applyTo: "src/**/*.ts,src/**/*.tsx"
---
Use function components.
```

CodeAlta does not put the text of these files in every prompt. It tells the session which patterns each file is for, and the session reads the file before it changes a file that matches. A file marked `excludeAgent: "coding-agent"` is left out.

## Skills

A Copilot skill is read as Copilot writes it: the fields `argument-hint`, `user-invocable`, `disable-model-invocation` and `context` are accepted, and `allowed-tools` can be a list. A skill with `disable-model-invocation: true` is not offered to the model.

When a project has a skill of the same name in `.alta/skills` or `.agents/skills`, that one is used and the Copilot one is shown as overridden.

You can enable, disable and edit a Copilot skill in **Settings > Skills**. See [Workspace and Dialogs](workspace.md#skills-management).
