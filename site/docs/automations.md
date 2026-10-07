---
title: Automations
---

# Automations

An automation is a prompt that CodeAlta Desktop sends by itself: at a time you choose, when an issue or a pull request is opened, or when you click **Run now**. Each run starts a new session, so you read the result like any other work.

Automations are in CodeAlta Desktop only.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-automations.webp" alt="CodeAlta Desktop Automations tab with the next 24 hours, six automations of a project and the details of the selected one" loading="lazy">
  <figcaption class="small text-secondary mt-2">The next 24 hours, the automations, and the runs of the selected one.</figcaption>
</figure>

## Create an automation

1. Click the bolt at the top of the window, or press `Ctrl+G` `Ctrl+M`, to open the **Automations** tab.
2. Click **New automation**, or start from a template such as **Issue triage** or **Pull request review**.
3. Choose where it runs: in a project, or as a chat in no project.
4. Add what starts it and write the prompt.
5. Click **Save**, or **Save and run** to try it right away.

<figure class="alta-figure my-4" style="max-width: 36rem;">
  <img src="{{site.basepath}}/img/alta-desktop-automation-editor.webp" alt="The window that edits an automation: name, project, triggers, prompt, model and reasoning" loading="lazy">
  <figcaption class="small text-secondary mt-2">An automation that runs every morning and for each new issue.</figcaption>
</figure>

The session starts with nothing but your prompt: say what to look at and what to report.

## What starts an automation

{.table}
| Trigger | When it runs |
| --- | --- |
| None | When you click **Run now**. |
| Hourly | Every hour, or every few hours, at the minute you choose. |
| Daily | Every day, at one or more times. |
| Weekly | On the days and at the times you choose. |
| Cron | When a cron expression matches, for example `0 9 * * 1-5`. |
| Issue | When an issue is opened in the repository of the project. |
| Pull request | When a pull request is opened, or when it receives commits. |
| Jira issue | When an issue of the Jira project is created, or when it is updated. |

An automation can have several triggers. Times are read on the clock of your computer.

### Issues and pull requests

- They work with GitHub, GitLab and Azure DevOps, with the [sign-in of the issue picker](plugins/git.md#sign-in-for-issue-lookup).
- CodeAlta looks at the repository every five minutes. The automation shows the repository it watches.
- An automation runs for the issues and pull requests of the members of the repository. Choose **by anyone** to run for every author.
- The **Jira issue** trigger needs the [Jira plugin](plugins/jira.md) turned on for the project.

## Follow the runs

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-automation-session.webp" alt="A session started by an automation, with a bolt in the sidebar and a line at the top that names the automation" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session started by an automation.</figcaption>
</figure>

- A session started by an automation has a bolt in the sidebar. The line at its top names the automation and opens it.
- In the **Automations** tab, select an automation to see its runs. Click a run to open its session.
- The switch of an automation turns its triggers off. The **Running** switch at the top turns off the triggers of all of them. **Run now** always works.

## Good to know

- Automations run while CodeAlta Desktop is open, also when it [stays in the notification area](desktop-and-tui.md#runs-in-the-background).
- What was due while CodeAlta was closed is skipped. Turn on **Catch up on what was missed while CodeAlta was closed** in an automation to run it at the next start.
- An automation is stored in **My configuration** (`~/.alta/config.toml`) or in **The project** (`.alta/config.toml` of its folder), which you can commit to share it with your team.
- An automation that came with a project, written by someone else, waits for you: read its prompt and click **Allow**. It asks again when its prompt or its triggers change.
- An agent can list, create and run automations. Ask in your own words, for example:

```text
Create an automation that reviews the open pull requests every morning at 9.
```
