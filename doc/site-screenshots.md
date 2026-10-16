# Updating the screenshots of the site

The pictures of CodeAlta Desktop on the website (`site/img/alta-desktop-*.webp`) are captures of the real window.
They are retaken when the window changes in a way every picture shows (the title bar, the sidebar) and when a
feature gets a page. This is how.

## What a capture is

- One capture is **2400x1500** pixels: the window at 1600x1000 CSS pixels on a display at 150%.
- Use the app's **Dark** theme to match the website's default dark background. Feature screenshots must not
  have a white background. Select Dark with the real theme control before capturing; do not recolor the
  picture or override theme colors with capture styles. Light captures are only for an explicit theme comparison.
- It is taken on the **developer instance** (`alta --dev`), which runs beside the normal one and has its own
  sessions and view state. Build the configuration the normal instance does not run from (`Release` when it runs
  from `bin/Debug` of this checkout, `Debug` otherwise: see "Debug or Release" in `AGENTS.md`) and start it:

  ```bash
  dotnet build src/CodeAlta/CodeAlta.csproj -c <Configuration>
  src/CodeAlta/bin/<Configuration>/net10.0/alta.exe --dev
  ```

- The developer instance serves an MCP server on `http://127.0.0.1:2583/mcp`. Three of its tools are enough:
  `resize_page` (the size of the page), `evaluate_script` (a function run in the page) and `take_screenshot`
  (the picture, as PNG). A script must return within about seven seconds: wait in the caller, not in the page.

A small client is all the tooling there is:

```python
import base64, json, urllib.request

def call(tool, arguments):
    body = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": tool, "arguments": arguments}}
    request = urllib.request.Request("http://127.0.0.1:2583/mcp", json.dumps(body).encode(), method="POST")
    request.add_header("Content-Type", "application/json")
    request.add_header("Accept", "application/json, text/event-stream")
    text = urllib.request.urlopen(request, timeout=180).read().decode()
    lines = [line[5:].strip() for line in text.splitlines() if line.startswith("data:")]
    return json.loads(lines[-1] if lines else text)["result"]

call("resize_page", {"width": 1600, "height": 1000})
call("evaluate_script", {"function": "async () => { /* set the scene */ return 1; }"})
image = next(block for block in call("take_screenshot", {})["content"] if block["type"] == "image")
open("capture.png", "wb").write(base64.b64decode(image["data"]))
```

## Dressing the page

Before a capture, a script adds a `<style>` and a small overlay to the page. Nothing is renamed or deleted, and
a restart of the instance removes both.

- Hide the tag of the developer instance: `.window-brand-tag { display: none !important }`.
- Show a [space](../site/docs/spaces.md) that holds public projects only, instead of hiding the others: the
  Explorer, the search and **Open project** then list nothing else. The pictures use four spaces: **Open source**
  (CodeAlta, Markdig, SharpYaml, Scriban and about ten more public projects, so that the Explorer looks like a
  real one), which is the one shown, **Work**, **Personal** and Default. They are made with `alta space create`,
  `alta space add` and `alta space update --description`, and undone afterwards (`alta space remove`,
  `alta space delete`, then `alta space reorder` to give Work and Personal their first `order` again): the spaces
  and the `spaces` line of the project files are in the profile, which the two instances share. Compare
  `~/.alta/spaces` and `~/.alta/projects` with a copy taken before: they must be byte-identical.
- The Default space lists every project. When a picture has to show it, hide the projects that are not public by
  the title of their row: `li.project-action-row:has(> button[title^="Name"]) { display: none !important }`.
- **Settings > Spaces** and the **New space** window list every project of the catalog, whatever the space shown.
  Hide the rows that are not public by the folder they have as title, with one `:not([title="C:\\folder"])` for
  each public project: `.space-pool li.space-project:not(...)` for the page, and
  `.space-project-checks > label:has(> input:not(...))` for the window.
- Show only the sessions of the scene:
  `.session-row:not(:has(> button[title^="A title"])):not(...) { display: none !important }`.
- Hide what only says the timeline is long: `.load-more-bar, .timeline-bottom-button`. A timeline now loads its
  earlier messages as it is scrolled up: click **Load all messages** before the dressing hides its bar.
- Hide the rows that name a folder of the account, and the empty rows that hold one:
  `.settings-file-location, .settings-file-locations` and `dl > div:has(> dd > .settings-file-location)` (the
  **Folder** and **Skill file** rows of Skills), the **Path** row of the Workspace popover and the **Created in**
  row of the work place choice, and the line of a tool call that names a worktree under `~/.alta/worktrees`.
- The keep-list of sessions also hides the rows of terminals, which are `.session-row`s: list their titles too.
- Hide toasts (`.bp6-toast-container`).
- Show hover-only controls (the buttons of a project row in `explorer`) with the `hover` tool: since NeoAstra
  1.5.0 it moves the browser's own pointer over the element (the system cursor stays where it is), so CSS
  `:hover` applies. It does so only while the page has the focus. Its answer says when it only dispatched
  events, but it also says so when the hover shows a control under the pointer that covers the element (the
  favorite star of a project row): so before capturing, check that the row matches `:hover` and that its
  controls are visible through their computed styles. Do not force
  `.project-row-action` or `.project-actions-trigger` visible with an injected style to stand in for hover.
- The minimize, maximize and close buttons of the window are drawn by the system and are not in the capture. Add a
  fixed 138x38 box at the top right with the glyphs `E921`, `E922`, `E8BB` of *Segoe Fluent Icons*, in
  `var(--text)`. A dialog that is open covers the page: append the box to the dialog.
- A window of the application (Settings, a tool call) remembers its size: click **Restore default size and
  position** in its header first.

## The scenes

Use real work on public repositories: CodeAlta itself, Markdig and SharpYaml. A scene is a short script that
closes every tab and dialog, opens what it shows and takes the capture.

| Picture | Scene |
|---|---|
| `home`, `explorer` | Three favorite projects open, a session with tool calls and a result. `explorer` is the left 1000 pixels of it, over the whole height: its foot has the spaces. |
| `spaces`, `space-settings`, `space-new`, `space-activity` | The list of the space switch open over a session; **Settings > Spaces > Overview** with its cards; the **New space** window with a name, a description, **Start from: Copy the setup of Open source** and two projects ticked (a crop of the window, then **Cancel**); the foot of the Explorer (a crop of the bottom left). `spaces` and `space-activity` want sessions at work in the other spaces. |
| `space-page` | **Settings > Spaces > Open source** on its **Resources** tab, with one provider turned off for the space so that the tab summary and the list show it. Turning it off writes `spaces = [...]` on that provider in `~/.alta/config.toml`: copy the file before and put it back after the capture (then **Reload** on Providers). |
| `split-side`, `split-stacked`, `split-three` | A session and its sub-agents in panes (the `…` button of a pane, **Split right** or **Split below**). |
| `notes`, `context-usage`, `modified-files`, `tool-details` | One session with its notes, the context window of the context ring, a modified file, the window of a tool call. |
| `prompt`, `more-menu`, `remote-control` | A crop of the prompt of a **Claude Code** session (so that the prompt-cache timer shows) with a background job running (`alta job start` of a ten-minute sleep) and a draft typed in; the **⋯** menu of that prompt (a crop); **Remote Control: off** chosen in that menu, its popover (a crop). Never turn Remote Control on. |
| `session-config`, `model-search`, `model-tiers`, `models` | The Agent chip picker (tiers on top, the **Permissions** row) and its model list filtered by "sol" (crops); the top of **Settings > Models** (a crop of the window) and the page filtered by "opus". The tiers are a `[models.tiers]` table appended to `~/.alta/config.toml` for the capture and the file put back byte-identical after it. |
| `new-session`, `worktree-new`, `new-session-popover` | The New session tab of markdig; the work place popover of its Workspace chip on **New worktree** (a crop; set **Project folder** again after); the form of the **+** of a project row. |
| `pull-request`, `workspace-popover` | The Workspace popover of a session with its **Pull request** menu open (a crop); the Workspace popover of a session in a worktree (turquoise chip), a crop: CodeAlta's "Mention worktrees in the readme" with its `bronze-teide` worktree made for it (`worktrees_temp.py on`, `off` after), the timeline hidden behind the popover, and the path shown from `~` (its text is rewritten in the page so that it does not name the account). |
| `session-browser`, `archived-sessions`, `search`, `help`, `open-project`, `file-selection`, `move-to-space`, `child-sessions` | The dialogs, each at its default size. `archived-sessions` and the **Archived** group of `search` need archived sessions: `alta session archive` three old test sessions of markdig and SharpYaml and `alta session restore` them after. `move-to-space` and `child-sessions` are crops of their window, opened from the menu of a session row (**Cancel**). |
| `settings`, `model-providers`, `prompts`, `skills`, `mcp`, `logs`, `pull-request-settings`, `session-settings`, `plugins`, `speech-settings`, `copilot-skills`, `copilot-agents`, `copilot-mcp` | A page of Settings over a session. Never select a provider row that shows an account, and never the Configuration file page. The `copilot-*` pictures need `.github/agents/*.agent.md`, `.github/skills/<name>/SKILL.md` and `.github/mcp.json` in the CodeAlta folder, added for the capture (**Reload** on the page) and removed after it. `speech-settings` is a crop of the window. |
| `speech` | The **Set up dictation** popover of the microphone of the prompt (a crop). It shows once the choice of the microphone is not confirmed: set `codealta.desktop.speech.microphone.v1` to `{"device":null,"confirmed":false}` in the local storage of the developer instance, reload the page, press the microphone, take the picture, **Cancel**, and put the key back. Check that the name of the microphone says nothing private. |
| `code-editor`, `editor-search`, `editor-session`, `editor-preview`, `editor-new-file`, `changes` | The code editor and the Changes tab of SharpYaml, with changes made for the capture. |
| `theme-dark`, `theme-light`, `themes` | One scene in each theme; `themes` is a montage of six color schemes. |
| `work-items`, `issues`, `sub-agents`, `conversation-width` | The Work items and Issues tabs of CodeAlta, a session with more sub-agents than the sidebar lists (five), a conversation at 70% (`alta appearance set --session <id> --session-width 70`, then `default`). |
| `terminal`, `worktrees`, `automations`, `plugins` and their close-ups | They need state that is built for them (a running terminal, two worktrees, automations in the project configuration, a temporary plugin) and removed afterwards. |
| `worktree-manager` | The **Worktrees…** window of SharpYaml with two worktrees made for it beside the project (`git worktree add -b alta/<name> C:\code\SharpYaml.worktrees\<name>`), so that no path names the account; removed with their branches after. View only: do not select or remove worktrees in the window. |
| `statistics-overview`, `statistics-activity`, `statistics-models`, `statistics-tools`, `statistics-tools-time`, `statistics-projects`, `statistics-filters` | The Statistics tab beside a session tab, in the dark theme, with the numbers of a real profile for a space of public projects. `overview` is the last 90 days by week, the time by project; the pages are taken from their top, and `tools-time` is the Tools page scrolled to its end; `filters` is a crop of the bar with the **Filter** menu open. |
| `statistics-history`, `statistics-history-reading`, `statistics-sessions` | With the sessions of the developer instance itself: the first card of the tab (a crop), the tab while the history is read, and the Sessions page of the last 30 days. |
| `statistics-session-menu` | The context menu of a public-project session, showing **Statistics of this session**. This does not open or export any Statistics data. |
| `documentation`, `landing` | The shipped guide and the Welcome tab, in the dark theme with the Explorer hidden. Use public guide content and inspect all recent project/session labels before capture; do not create shared catalog entries just to dress the page. |
| `canvases`, `canvas-board`, `canvas-agent` | The Canvases page with the two canvas samples; a session whose agent used `alta canvas` and `alta statistics`, alone, and with the Board tab of the `canvas-board` sample in a pane at its right. |
| `plugin-buttons`, `plugin-buttons-settings` | The top of the window with the buttons of the `canvas-checklist` sample and its Checklist tab (a crop); **Settings > Plugins** on the tab of the project, without the two lines of folders at its top, made tall enough to show the buttons of every plugin (a crop of the Settings window). For 1.9.0 it was taken with the two samples in `C:\code\CodeAlta\.alta\plugins` and a CodeAlta session in front: `resize_page` to 1600x1110, then the `.app-window` of Settings given `top: 0`, `height: 1110px` and no transform; `resize_page` 1600x1000 again after. |
| `bookmarks`, `bookmark-rail` | The Bookmarks tab beside two session tabs, with bookmarks of messages of three public projects and one bookmarked session; a session with a bookmarked message and the rail of marks at the right edge of its timeline. |
| `fork-rewind`, `session-menu`, `new-session-popover` | The **Fork or rewind** window of a session of four turns, with the third turn selected (`/fork` from the search); a crop of the Explorer with the menu of a session that has sub-agents (**Fork…**, **Child sessions…**, **Move to space…**, **Archive…**); the form of **+** on a project row over a session. |
| `system-prompt`, `entry-appearance` | The window of a **System prompt changed** row of a session whose agent prompt went from Default to Plan (**Changes** tab); **Settings > Appearance > Entry appearance > Customize** on a type of entry, over the Settings window. |
| `model-search`, `session-settings`, `tabs` | A crop of the picker of the prompt bar with the model list filtered by a word; **Settings > Sessions**; a crop of the tab strip with kept tabs and a tab with an italic title. |
| `issues-jira` | The Issues tab of CodeAlta with **Jira** chosen, the list of its projects (**All projects**) and a Jira issue open, over three Jira projects of a test site. |

Changes made for a capture are applied as patches and reversed after it. Record `git status`, `HEAD` and
`git stash list` of each repository before, and compare after: they must be the same.

Settings changed for a capture (theme, color scheme, width of the conversation, number of recent sessions) are
put back. So are favorites and open projects.

What the scenes with state of their own need:

- **Terminals.** `alta terminal create --project <folder> --title <name>` opens one, `alta terminal send <id> --text
  "<command>" --enter --wait <seconds>` types in it, `alta terminal show <id>` brings its tab to the front and
  `alta terminal close <id>` ends it. Start the application from a shell where `NO_COLOR` is not set: a terminal
  inherits it and draws its prompt without colors. Use a prompt theme that does not show the account.
- **Worktrees.** A session keeps the folder of its worktree. Create the worktrees again in the same folders
  (`git worktree add -b <branch> <folder> <commit>`), restart the application so that it stops showing them as
  removed, and remove the worktrees and their branches after the capture.
- **Automations.** They are in `.alta/config.toml` of the project, which git tracks: add them for the capture
  and put the file back. Automations are paused on the developer instance: let them run for the capture only.
- **Plugins.** `alta plugin create <name>` makes a plugin of the user in `~/.alta/plugins`, which the two
  instances share: remove its folder and its build cache right after the capture.
- **Statistics.** Keep the existing, approved real-data pictures unless a new capture has its own authorization
  and reviewed isolation procedure. Never move, rename or delete user journals to shape a picture, and do not
  swap exported tables into the regular developer database. A Statistics database is not "numbers only":
  cursor state can retain session titles and project references, and the application database also contains
  session projections. A public-project filter on the page does not sanitize those stored values. A future
  export procedure must account for every retained field, backups and SQLite sidecars, startup plugins and
  automations, and crash-safe restoration before it is run. Read every legend, tool name and program name of
  a picture before keeping it; do not expose real session titles on Sessions or Agents pages.
- **The Sessions page and history controls.** New pictures of session lists, **Reset statistics…** or
  **Read all the history** require an approved disposable fixture containing only its own scene data. Do not
  reset an existing developer profile or edit its projection metadata for screenshots. The pages share one
  period: set it again before each page.
- **Canvases and plugin buttons.** The `canvas-board` and `canvas-checklist` samples of the
  `codealta-plugin-runtime` skill are copied to `<folder>/.alta/plugins/` of the folder the developer instance
  is started in, and removed afterwards with their build folder under `~/.alta/cache/plugins/build/project`.
  The canvases of a project plugin are listed for every project, but its buttons are shown, and
  **Settings > Plugins** lists it, only for the project of the catalog it belongs to: for `plugin-buttons` the
  sample is in a public project of the catalog, the developer instance is started in that folder, and a session
  of that project is in front. The items of the checklist are added with `alta canvas invoke checklist add`;
  they are kept in `~/.alta/plugin-data/plugin_canvas-checklist`, which the two instances share: remove it, or
  put back what was there. The session of `canvas-agent` is a real one, sent with a small model: its prompt asks
  for `alta canvas list`, `open` and `show`, then `alta statistics summary`, and for no file change.
  Use the actual **Notes** and task-proposal placement rather than moving overlays only for the picture.
  The default Notes chip now leaves room for the first message's timestamp; saved geometry is retained.
  The Plugins page names the folder of the plugins of the user, which has the name of the account:
  `.settings-file-locations` is hidden for `plugin-buttons-settings`.
- **Scenes made of sessions (bookmarks, fork and rewind, system prompt).** Create real sessions of the developer
  instance on public projects with `alta session create --project <folder> --title "<title>" --model-ref
  codex:gpt-6.1-sol@low`, and queue short read-only prompts (`alta session send <id> --queue-if-busy --message
  "..., no edits."`) so that a session of four turns is ready in a minute. `alta bookmark add --session <id> --turn <n>
  --role assistant --label "..."` marks a message. A system prompt that changes needs
  `alta session set_agent <id> --prompt-id plan` before the next prompt: `--prompt-id` on `send` does not change the
  agent prompt of the session. The first rows of a session (the model, the system prompt) are above its first prompt:
  they are not loaded until the timeline is scrolled up, and the dressing hides the **Load all messages** bar above it.
- **The Issues tab with Jira.** Add `[plugins.jira]` (`site`, `project`, `projects`) to `.alta/config.toml` of the
  project of the capture and put the file back with `git checkout` afterwards. The site is the one the Atlassian CLI
  is signed in to; a project is made with `acli jira project create --from-json` and an issue with `acli jira
  workitem create`, and an issue made for a test is deleted with `acli jira workitem delete --key <key> --yes`.
- **Pointer input.** `click_at` sends real input, so a control that shows on hover (the **+** and the **…** of a
  row) is clicked in one `click_at` at its place, once the row is shown. The **work items** badge of a project row is
  next to its name: click the row to its right. A tab click hands the keyboard to the prompt, so `Shift+F10` then
  opens the menu of the editor, not the one of the tab.
- **Notes and settings of the window.** The Notes chip is placed by `codealta.desktop.notes-window.v1` of the local
  storage of the developer instance: remove the key and reload when an earlier test moved it over the first message.
  The pages of Settings that name a file (`.settings-file-location`) are hidden, as the folders of the user have the
  name of the account.
- **Add provider.** The menu lists the built-in providers the profile does not have: take the picture on a
  profile of its own (`--catalog-root`, `--data-root` and the other roots of an isolated launch, with
  `--mcp-port`), with one provider that is disabled in its `config.toml`. `add-provider` is the crop of the
  Settings window. The folders of that profile are outside any `.alta` folder, or the start is refused. Such a
  profile still finds the Claude Code CLI and the Claude account of the user: the form of a Claude Code provider
  there shows the path of the CLI and the usage of the account, so it is not a picture to take. The
  `mcp-server` picture can instead be taken on the ordinary developer instance and cropped to Settings.
  Keep the actual address and client configuration in the picture: the developer instance normally uses
  port 2583, while the normal instance uses 2582. Explain that distinction in the caption; never rewrite
  a port or configuration value in the page just for the capture.
- **The sidebar.** A project lists its recent sessions only, and the sessions of the scenes get older: click
  **Show more** until they are all listed, then hide the others and the row of **Show more** itself
  (`.session-list-disclosure`).
- **Sub-agents.** A session has an arrow and lists four sub-agents, with a **Show more** of its own under them
  (`.sub-agent-disclosure`, which is a `.session-list-disclosure` too). `sub-agents` shows that line: leave it
  as it comes instead of clicking it, and keep it for the session of the picture only, since a hidden test
  session can have one as well. More sub-agents are made with `alta session create --parent <id>`, which
  takes a parent that ran since the instance started: send it a one-word prompt first. The answers of the
  sub-agents are then no longer at the end of the timeline: going up in the timeline brings them back.
  `home`, `explorer` and the three `split` pictures have a session with sub-agents as their subject too, and
  are retaken with `sub-agents` when that part of the sidebar changes.
- **Space activity.** The marks of a space are those of its sessions, so they need real ones. For a session that
  waits: show the other space, open a new session in one of its projects and send from the page (the **Start
  session** button of the prompt bar: a question asked with `alta ask` is only raised in a run started from the
  application) a prompt that asks the agent to ask one question with `alta ask` and to wait for the answer. Rename
  the session (its row menu, or `alta session title <id> --set "<title>"`), then show the space of the pictures
  again: a few seconds later the foot of the Explorer names the session, and the space switch has its dot. A project
  that is in both spaces lists the waiting session in the shown space too, and the foot then says nothing: take that
  project out of the shown space for the capture. For a running mark, start a session in a project
  of a third space with `alta session create` and `alta session send`, on a prompt that takes a few minutes.
  Afterwards answer the question, stop the run, and send that session a last short prompt, so that no space is
  left with a session that waits or failed.

## Exporting

Save a capture as **lossless WebP** (`PIL`: `image.save(path, "WEBP", lossless=True, method=6)`), about 100 to
250 KB. A capture with a large gradient or a montage is saved with `lossless=False, quality=95`.

The file is `site/img/alta-desktop-<scene>.webp`. A close-up is a crop of the capture, shown in a narrower
figure.

A picture of a page must be at least 200x100 pixels: the test of the shipped guide refuses a thinner crop (a strip of tabs
takes the activity bar above it, for example).

## In a page

A picture that exists for both applications uses the switch of the site:

```text
{{ alta_shot "alta-desktop-notes.webp" "alta-notes.png" "Alternative text" "Caption." }}
```

A picture of the desktop application only is a figure:

```html
<figure class="alta-figure my-4">
  <img src="{{site.basepath}}/img/alta-desktop-issues.webp" alt="Alternative text" loading="lazy">
  <figcaption class="small text-secondary mt-2">Caption.</figcaption>
</figure>
```

Build the site (`lunet build` in `site/`) and look at the pages. The pictures also ship with the application,
in its user guide (`content/user-guide/img`): a test checks that every picture a page names is there.

## The banner

`site/img/alta-banner.png` (1280x640) is the picture of a link to the website or to the repository. It is drawn by
`python img/make-banner.py` from the repository root (Pillow, Windows fonts) with a capture of the site,
`alta-desktop-split-three.webp`: retake that capture first, then run the script again.
