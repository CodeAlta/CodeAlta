---
title: Automations
---

# Automations

An automation is a prompt that CodeAlta Desktop sends by itself: at a time you choose, when an issue or a pull request is opened, when a command of yours succeeds, or when you click **Run now**. Each run starts a new session, so you read the result like any other work. Automations are in CodeAlta Desktop only.

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-automations.webp" alt="CodeAlta Desktop Automations tab with the next 24 hours, six automations of a project and the details of the selected one" loading="lazy">
  <figcaption class="small text-secondary mt-2">The next 24 hours, the automations, and the runs of the selected one.</figcaption>
</figure>

## Create an automation

1. Click the bolt at the top of the window, or press `Ctrl+G` `Ctrl+M`, to open the **Automations** tab.
2. Click **New automation**, or start from a template such as **Issue triage** or **Pull request review**.
3. Choose where it runs: in a project, or as a chat in no project.
4. Add what starts it and write the prompt, or choose a [script](#run-a-script).
5. Choose what its sessions run with in **Runs with** (see below).
6. Click **Save**, or **Save and run** to try it right away.

<figure class="alta-figure my-4" style="max-width: 36rem;">
  <img src="{{site.basepath}}/img/alta-desktop-automation-editor.webp" alt="The window that edits an automation: name, project, triggers, prompt, model and reasoning" loading="lazy">
  <figcaption class="small text-secondary mt-2">An automation that runs every morning and for each new issue.</figcaption>
</figure>

The session starts with nothing but your prompt: say what to look at and what to report.

## Run a script

An automation can run a [script of the library](sessions/code-mode.md#scripts-of-the-library) in place of a prompt. The editor offers **Prompt** or **Script**: choose the script, and write its arguments as a JSON object. The editor lists its parameters under them. The template **Pull request review in parallel** runs the sample `review-pr` on each pull request.

- Its session calls the script through `exec`, even when [Code Mode](sessions/code-mode.md) is off, then answers with its result.
- A script that declares an `event` parameter gets what started the run in it: the issue, the pull request, or what the command printed. `review-pr` reads the number of the pull request there.
- An automation that runs a script waits for **Allow** until it is saved in the editor or created with `alta automation create`, and asks again whenever the script changes: a version saved by an agent, or one that came with a `git pull`.

```toml
script = "triage-issues"
script_args = '{"limit": 10}'
```

An agent creates one with `alta automation create --name "PR review" --trigger pull_request@opened --script review-pr`, and gives the arguments with `--script-args '{"lenses": ["security"]}'`. The script and its arguments are checked as a run checks them.

An automation an agent creates is disabled, and an agent enables none: what runs by itself is turned on by you, in the **Automations** tab. An agent can still run one now, disable or delete one.

## What its sessions run with

**Runs with** is the [Agent chip](workspace/prompt.md#agent-and-model) of the prompt: click it for the same form, with the agent prompt, the provider, the model, the reasoning effort, the [permission mode](workspace/prompt.md#permission-modes), [review before commit](reviews.md#review-before-commit) and the [sandbox](sessions/sandbox.md).

- **Use the defaults**, at the top of the form, is on for a new automation. The rows then show what a run takes, grayed: the [default provider](model-providers/readme.md), its model and effort, and the defaults of your settings. A provider marked **(first enabled)** means no default provider is set.
- Turn it off to choose. What the defaults gave becomes your choice, and you change it from there.
- A provider, model or permission mode that is no longer offered stops the run, which says why.
- **More options**, at the end of the form, has **Sub-sessions**: off, the sessions of the automation do all the work themselves and start no [sub-agents](sessions/delegation.md#sessions-without-sub-sessions).

```toml
model = "codex:gpt-6.1-sol@high"
permission_mode = "acceptEdits"
review_before_commit = true
sandbox = "off"
sub_sessions = false
```

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
| Jira issue | When an issue of the Jira project is created, or when it is updated. A project with [several Jira projects](plugins/jira.md#several-jira-projects) watches all of them. |
| Command | When a command that CodeAlta keeps running ends with success. |
| Jira version | When a version of a Jira project is released. |
| Jira query | When an issue starts to match a JQL query, or changes while it matches. |
| Slack | When the app is mentioned in a channel, or when a message is posted there. |
| incident.io | When incident.io declares or updates an incident ([a hook of a server](fleet.md#receive-events-of-other-tools)). |
| Webhook | When another tool pushes a signed event ([a hook of a server](fleet.md#receive-events-of-other-tools)). |

An automation can have several triggers. Times are read on the clock of your computer.

### Issues and pull requests

- They work with GitHub, GitLab and Azure DevOps, with the [sign-in of the issue picker](plugins/git.md#sign-in-for-issue-lookup).
- CodeAlta looks at the repository every five minutes. The automation shows the repository it watches.
- An automation runs for the issues and pull requests of the members of the repository. Choose **by anyone** to run for every author.
- The **Jira issue** trigger needs the [Jira plugin](plugins/jira.md) turned on for the project.

### Commands

A **Command** trigger is for a tool that waits for something and then ends: a script that waits for a file, or `gh run watch 123 --exit-status`, which waits for a workflow run of GitHub.

- CodeAlta starts the command and keeps it running. Each time it ends with exit code 0, the automation starts a session with your prompt followed by what the command printed. Another exit code starts no session. Either way, the command is started again.
- The command runs in the shell agents use: PowerShell on Windows, your shell on macOS and Linux. It runs in the folder of the project, or in your home folder for a chat. Type a folder next to the command to run it there.
- A command that ends in less than five seconds is started again after a pause, which grows up to five minutes. When it keeps failing, the automation shows its last line.
- When the command succeeds while the last session still works, the new session starts after it.
- Turning off the automation, or the **Running** switch at the top, ends the command.

```toml
triggers = [
  { type = "command", command = "gh run watch 123 --exit-status", cwd = "tools" },
]
```

### Jira, Slack and incident.io

These triggers read another tool with an account of yours: no project is needed.

- Keep their tokens in [Settings > Secrets](secrets.md) and type the name of the secret in the trigger: the configuration holds `${secret:NAME}`, never the token. A token written in the file is refused.
- **Set up** under the trigger says what to create in the tool. **Test the connection** checks the tokens; **Show a sample event** reads the latest event and shows the prompt the session would get; **Try it on this event** runs the saved automation once on it.
- Turn on **Write the answer back where the event came from** to post the last answer of the session as a comment on the Jira issue, a reply in the Slack thread or an item on the incident's timeline. A try keeps the answer on its run instead.
- Jira is looked at every five minutes, with the API token of an account (`id.atlassian.com`). Slack is reached over Socket Mode: the app needs an app-level token and a bot token, and nothing connects to your computer. A Slack trigger on every message leaves alone what people write in a thread it answered; turn on **Also start on people's replies in the threads it answered** to follow them too. incident.io and webhooks push their events to a [hook of a server](fleet.md#receive-events-of-other-tools), for a machine of that server.
- What the event says reaches the session as someone else's text: the title, the message or the incident, read again from its tool, and for a webhook only the fields you list.

```toml
triggers = [
  { type = "jira_query", site = "https://acme.atlassian.net", email = "bot@acme.com", token = "${secret:JIRA_TOKEN}", jql = "project = PLAT AND labels = triage", reply = true },
  { type = "slack", event = "mention", channel = "C0123ABCDEF", app_token = "${secret:SLACK_APP}", bot_token = "${secret:SLACK_BOT}", reply = true },
]
```

## Follow the runs

<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-automation-session.webp" alt="A session started by an automation, with a bolt in the sidebar and the name of the automation on the card of the prompt it sent" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session started by an automation.</figcaption>
</figure>

- A session started by an automation has a bolt in the sidebar. Its first card, **Automation**, is the prompt the automation sent: it names the automation, which you open with a click, and what started the run.
- In the **Automations** tab, select an automation to see its runs. Click a run to open its session. A run started by another tool says what was done with its answer.
- The switch of an automation turns its triggers off. The **Running** switch at the top turns off the triggers of all of them. **Run now** always works.

## Good to know

- Automations run while CodeAlta Desktop is open, also when it [stays in the notification area](desktop-and-tui.md#runs-in-the-background). One set to run on a paired machine (**Runs on**) runs there while this computer is closed: see [Run your automations on it](fleet.md#run-your-automations-on-it).
- What was due while CodeAlta was closed is skipped. Turn on **Catch up on what was missed while CodeAlta was closed** in an automation to run it at the next start.
- An automation is stored in **My configuration** (`~/.alta/config.toml`) or in **The project** (`.alta/config.toml` of its folder), which you can commit to share it with your team.
- An automation that came with a project, written by someone else, waits until you read its prompt and click **Allow**. It asks again when its prompt, triggers or what its sessions run with change, including the command of a trigger.
- An agent can list, create and run automations; one it creates waits, disabled, until you enable it. Ask in your own words, for example:

```text
Create an automation that reviews the open pull requests every morning at 9.
```
