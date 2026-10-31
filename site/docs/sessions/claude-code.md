---
title: Claude Code Sessions
---

# Claude Code Sessions

A session of [Claude Code](../model-providers/setup.md#claude-code) is a conversation that the Claude Code CLI keeps in its own transcript, under `~/.claude/projects/`. The Claude Code extension of VS Code and `claude` in a terminal can continue the same conversation. CodeAlta Desktop works alongside them:

- it lists the conversations started in VS Code or a terminal, and continues them;
- it asks before it continues a conversation that another program has open;
- it adds to a session the turns made in the other program;
- it opens its sessions in VS Code or a terminal in one click;
- it [runs a session in the cloud](#run-in-the-cloud), on Anthropic's machines, and [brings it back](#continue-a-cloud-session-here).

For Remote Control from claude.ai or the Claude app, see [Remote Control](../workspace/prompt.md#remote-control).

## Conversations from VS Code

When the space the window shows has a Claude Code provider, the Explorer lists the Claude Code conversations that no CodeAlta session continues:

- **From Claude Code (N)**, at the end of the sessions of a project, lists the conversations of that project.
- **From Claude Code, no project (N)**, after the projects, lists the others.

Both groups start closed, and CodeAlta remembers whether you opened them. With [several Claude Code accounts](../model-providers/setup.md#claude-code) in the space, each row shows the logo of its provider, and that provider is used to preview, import or open it.

The folder a conversation started in decides where it is listed:

| The conversation started in | It is listed under |
| --- | --- |
| the folder of a project, a subfolder, or a worktree Claude Code made in it (`claude -w`) | that project (the deepest one, when projects are nested) |
| another checkout of the repository of a project, such as a [worktree](../worktrees.md) | the project whose sessions work in that checkout, else the project at the main checkout, else the first project of that repository |
| a folder of another repository inside a project folder | that project |
| anything else: a removed worktree, your home folder, a WSL folder, a repository that is not a project | no project |

- **What is listed** is what VS Code lists: conversations with a title or a prompt, newest first, among the newest 5,000 under `~/.claude/projects`. Not listed: conversations a CodeAlta session continues (archived ones included), conversations run through the Claude Code SDK (`claude -p`, and CodeAlta itself), background sessions of Claude Code, and conversations continued in another one.
- A group shows 20 conversations; **Show more…** adds 50 at a time.
- Each row has the title, the time of the last change, and an icon for where it started (VS Code, a terminal or another program). A label says where it started when that is not the project folder. An eye says another Claude process works on it now; a quieter arrow says it is merely open there.
- The menu of a row (right-click, `Shift+F10` or `…`) has **Open preview**, **Import**, **Import into project…**, **Open in VS Code**, **Copy resume command** and **Run in terminal** (not for a conversation of no project). Nothing in it deletes a conversation.
- `Ctrl+P` finds these conversations by title, project or folder, and **Filter sessions** at the top of the Explorer narrows the groups.

## Preview a conversation

Click a row to preview the conversation in a tab with an italic title. Like a [tab you only look at](../workspace/readme.md#tabs), the next preview takes its place, and it is not reopened at the next start.

- The conversation is read-only, latest turns first: scroll up, or click **Load all messages**. Pictures show as `[Image]`.
- A banner in place of the prompt says where the conversation started and whether another Claude process has it open now.
- **Your first message continues it in** names the project: the one that lists it, or, for a conversation of no project, the project the window shows. Choose another project there before you send.
- The preview reads the conversation again every few seconds, so turns made in VS Code appear in it.
- A conversation larger than 64 MiB is not previewed: continue it in CodeAlta, or open it in VS Code.

## Continue in CodeAlta

To continue a conversation, write in the prompt of its preview and press `Enter`. **Continue in CodeAlta**, or a click on the banner, puts the keyboard in the prompt. To import it without a message, choose **Import** in the menu of its row, or drag the row onto any project.

CodeAlta then creates a session in that project with the turns of the conversation, and sends your message there:

- The session belongs to the space the window shows, with that space's Claude Code provider. It has the title of the conversation, and the default model, effort and permission mode of the provider.
- It works in the [worktree](../worktrees.md) the conversation was last in, if that worktree still exists in the project; otherwise in the project folder.
- Its imported turns are marked **in VS Code** or **in a terminal**, and [Statistics](../plugins/statistics.md) do not count them.
- The transcript of Claude Code is read, never moved: VS Code keeps the conversation, and you can go on in either program.
- The conversation leaves the group. Delete the session and it is listed again, CodeAlta's turns included.
- What you write under the preview is never lost. If a session already continues that conversation and is busy or holds a draft, the preview keeps your message, and **Open the session** shows that session beside it.
- One conversation is imported at a time; other previews wait and say so.

## A conversation open elsewhere

Before each prompt you send, CodeAlta checks whether another Claude process of this computer has the conversation open: VS Code, `claude` in a terminal, or another application. If one has, the prompt waits and a card above it asks:

| Choice | What happens |
| --- | --- |
| **Wait until it is free** | The card shows **Waiting for VS Code to release the conversation…**, with **Stop the run**. The prompt is sent once the other program closes the conversation. |
| **Continue as a copy** | Claude Code continues a copy. The session follows the copy, and the other program keeps the original. |
| **Open here anyway** | Both programs write the same conversation. CodeAlta does not ask again while that process keeps it open. |
| **Cancel** | The run ends as stopped. Your next prompt carries this one with it. |

- If turns were added in the other program while the prompt waited, the prompt is not sent: it goes back into the composer, marked **Not sent: the conversation changed while waiting**. Send it again: the new turns are added to the session first. If the composer holds a draft, the draft is kept and a message offers the prompt to copy.
- A background session of Claude Code never gives its conversation away: the card offers only **Wait until it is free** and **Continue as a copy**.
- While Claude Code runs background tasks for the session, **Continue as a copy** is not offered: it would restart Claude Code and end them.
- CodeAlta TUI asks the same question in a dialog (`Esc` cancels). A compaction, or Remote Control connecting, cannot ask: it fails with a message naming the other program.

While another process has the conversation of the session you look at, a banner takes the place of the prompt: **Open now in VS Code**, or **In use now in VS Code** (with an eye) while it works on it. Your draft is kept. Click **Write a message**, or the banner, to turn it into a line above the prompt. The banner comes back when that process starts working, or another process opens the conversation.

VS Code asks the same question when CodeAlta has the conversation open: CodeAlta keeps Claude Code running up to ten minutes after a turn. **Open in VS Code**, **Run in terminal** and **Copy resume command** close it first. `claude --resume` in a terminal asks nothing.

## Turns made elsewhere

When you open a Claude Code session in CodeAlta Desktop, and before each prompt, CodeAlta adds to the session the turns made elsewhere since its last turn. The next turn continues the conversation as it is, without sending anything twice.

- Such a prompt says where it was made: **in VS Code**, **in a terminal** or **in another application**. A compaction made there is a notice, such as "The conversation was compacted in VS Code."
- They are not turns of CodeAlta: [Statistics](../plugins/statistics.md) do not count them or their cost.
- They are added once the session is idle: not while it works, has queued prompts, waits for an answer, has background tasks of Claude Code or has Remote Control on, nor while the other program is in the middle of a turn.
- If the conversation was rewound or resumed at an earlier point in the other program, the timeline says "This conversation was changed outside CodeAlta in a way it cannot follow; the model has the full conversation." Claude Code still has the whole conversation; only the timeline misses the change.

## Run in the cloud

Claude Code can run a session on Anthropic's machines instead of this computer. It goes on while the computer sleeps, and you follow it on claude.ai or in the Claude app. It needs Claude Code signed in with a claude.ai account (Pro, Max, Team or Enterprise), not an API key, and it counts against your Claude plan.

1. Open a new session in a project that is a git repository.
2. In the **Workspace** chip above the prompt, click **Project folder** and choose **Cloud**. **New cloud session…** in the menu of the project, or `/new_cloud_session`, does the same.
3. Describe the task, and click **Send to the cloud**.

<figure class="alta-figure my-4" style="max-width: 44rem;">
  <img src="{{site.basepath}}/img/alta-desktop-cloud-new.webp" alt="A new session of a project with Cloud chosen in the Workspace chip: the welcome lists the account, the branch and what stays on this computer, the account chip shows Claude Code, and the send button reads Send to the cloud" loading="lazy">
  <figcaption class="small text-secondary mt-2">A new session that runs in the cloud: <strong>Cloud</strong> in the <strong>Workspace</strong> chip, and what Claude Code starts from.</figcaption>
</figure>

- **Cloud** is offered when the space has a Claude Code provider. Choosing it moves the prompt to a Claude Code account: the one last used for the cloud, else the first one. The chip of the prompt then lists only the Claude Code accounts, since the cloud session picks its own model; **Another provider: work on this computer** goes back to the project folder.
- Under the welcome, lines say what the cloud session starts from: the account, the branch, commits that are not on GitHub, and files with changes that are not committed. Those stay here, unless Claude Code uploads the folder with them.
- Claude Code starts the cloud session in a terminal of the window. Its tab opens only when Claude Code asks something, such as whether it trusts the folder: answer there. **Stop** ends Claude Code.
- If the start fails, the prompt keeps its text, and Claude Code's own words are shown, with **Show the terminal**.

The session is listed with a cloud mark. Its tab shows a card in place of a conversation:

- its title, when and from which branch it started, and its account;
- **Open on claude.ai**, and **Phone**: a QR code of its link, for the Claude app;
- the task and the messages you sent.

<figure class="alta-figure my-4" style="max-width: 40rem;">
  <img src="{{site.basepath}}/img/alta-desktop-cloud-session.webp" alt="The tab of a session in the cloud: a card with In the cloud and its title, Open on claude.ai, Phone and Continue here, then the task it started with" loading="lazy">
  <figcaption class="small text-secondary mt-2">A session in the cloud: its card, and the task it started with.</figcaption>
</figure>

The prompt under the card sends a message to the cloud session, and Claude answers on claude.ai and in the Claude app. CodeAlta does not read the cloud session: it runs Claude Code's documented commands, and shows only what you sent until you [continue it here](#continue-a-cloud-session-here).

- Deleting the session in CodeAlta leaves the cloud session on claude.ai: archive or delete it there.
- CodeAlta TUI lists such a session, and refuses its prompts with the reason.

## Continue a cloud session here

**Continue here…**, in the card, in the menu of the session or with `/continue_here`, brings a cloud session to this computer. Claude Code brings back the whole conversation and checks out the branch the cloud session worked on.

- **Where**: a new [worktree](../worktrees.md), or the project folder, which switches to that branch. The project folder is not offered while it has changes that are not committed.
- The conversation then appears in the same session, each prompt marked **in the cloud**, and the card becomes one line, **Started in the cloud**. The next prompt runs on this computer.
- The cloud session stays on claude.ai.
- The branch comes back only when the cloud session could push it, to the repository's GitHub remote. For a repository that is not on GitHub, the conversation comes back without the changes made in the cloud.
- If Claude Code refuses, for a session of another repository or a branch it cannot fetch, the dialog shows its words and offers **Try in a terminal**. A terminal tab then runs `claude --teleport`: answer Claude Code there and exit it, then click **Bring it into this session** in the card.

**Bring a cloud session here…**, in the menu of a project or with `/bring_cloud_session`, does the same for a session started on claude.ai or in the Claude app: paste its link.

## Open in VS Code

Once Claude Code has answered its first prompt, the menu of a Claude Code session in the Explorer has three more commands. The search has them for the selected session (`/open_in_vscode`, `/run_in_terminal`, `/copy_resume_command`):

| Command | What it does |
| --- | --- |
| **Open in VS Code** | Opens the conversation in the Claude Code extension of VS Code. |
| **Run in terminal** | Opens a [terminal](../workspace/code.md#terminals) in the folder of the session and runs `claude --resume <id>`. |
| **Copy resume command** | Copies `claude --resume <id>`. Run it in the folder of the session: Claude Code looks for the conversation in the folder it runs in. |

These commands first close the Claude Code process CodeAlta keeps for the session. They cannot while the session works, waits for an answer, has Claude Code background tasks or has Remote Control on: **Open in VS Code** and **Run in terminal** then refuse and say what to do, and **Copy resume command** copies with a warning. A prompt you send in CodeAlta afterwards continues after the turns made in VS Code or the terminal.

A provider with its own **Account folder** ([several accounts](../model-providers/setup.md#claude-code)) keeps its conversations there:

- The resume command sets `CLAUDE_CONFIG_DIR` first: `$env:CLAUDE_CONFIG_DIR="$HOME\.claude-work"; claude --resume <id>` in PowerShell on Windows, `CLAUDE_CONFIG_DIR="$HOME/.claude-work" claude --resume <id>` in a POSIX shell.
- **Run in terminal** types it only in a shell that takes that form (PowerShell on Windows; bash, zsh, sh, dash, ksh or fish elsewhere).
- **Open in VS Code** is refused: VS Code looks for the conversation in its own account folder (its `claudeCode.environmentVariables` setting sets it for every conversation). Use the other two commands.

**Session Info** has a **Claude Code conversation** row: the identifier of the conversation, a copy button, and **Open now in VS Code** (or in a terminal) while another program has it open.

VS Code does not list CodeAlta's sessions: it leaves out conversations run through the Claude Code SDK, and has no setting to show them. Use **Open in VS Code**. A conversation started in VS Code and continued in CodeAlta stays in its list, with CodeAlta's turns.
