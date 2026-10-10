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
  (CodeAlta, Markdig, SharpYaml, Scriban), which is the one shown, **Work**, **Personal** and Default. They are
  made with `alta space create`, `alta space add` and `alta space update --description`, and undone afterwards
  (`alta space remove`, `alta space delete`): the spaces and the `spaces` line of the project files are in the
  profile, which the two instances share.
- The Default space lists every project. When a picture has to show it, hide the projects that are not public by
  the title of their row: `li.project-action-row:has(> button[title^="Name"]) { display: none !important }`.
- **Settings > Spaces** and the **New space** window list every project of the catalog, whatever the space shown.
  Hide the rows that are not public by the folder they have as title, with one `:not([title="C:\\folder"])` for
  each public project: `.space-pool li.space-project:not(...)` for the page, and
  `.space-project-checks > label:has(> input:not(...))` for the window.
- Show only the sessions of the scene:
  `.session-row:not(:has(> button[title^="A title"])):not(...) { display: none !important }`.
- Hide what only says the timeline is long: `.load-more-bar, .timeline-bottom-button`.
- Show hover-only controls (the buttons of a project row in `explorer`) only after real pointer input. Before
  capturing them, check that the row matches `:hover` and that its controls are visible through their computed
  styles. The `hover` tool in the pinned NeoAstra 1.3.0 dispatches synthetic DOM events: its success message does
  not mean CSS `:hover` applies. Until the backend supports real hover, move the pointer manually or leave the
  row unhovered. Do not force `.project-row-action` or `.project-actions-trigger` visible with an injected style
  to stand in for hover.
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
| `spaces`, `space-settings`, `space-new`, `space-activity` | The list of the space switch open over a session; **Settings > Spaces**; the **New space** window with a ready-made space chosen and two projects ticked (a crop of the window, then **Cancel**); the foot of the Explorer (a crop of the bottom left). `spaces` and `space-activity` want sessions at work in the other spaces. |
| `split-side`, `split-stacked`, `split-three` | A session and its sub-agents in panes (the `…` button of a pane, **Split right** or **Split below**). |
| `notes`, `context-usage`, `modified-files`, `tool-details` | One session with its notes, the context popover, a modified file, the window of a tool call. |
| `new-session`, `session-browser`, `search`, `help`, `open-project`, `file-selection` | The dialogs, each at its default size. |
| `settings`, `models`, `model-providers`, `prompts`, `skills`, `mcp`, `logs`, `pull-request-settings`, `copilot-skills`, `copilot-agents`, `copilot-mcp` | A page of Settings over a session. Never select a provider row that shows an account, and never the Configuration file page. `copilot-mcp` needs a `.github/mcp.json` in the project, added for the capture and removed after it. |
| `code-editor`, `editor-search`, `editor-session`, `editor-preview`, `editor-new-file`, `changes` | The code editor and the Changes tab of SharpYaml, with changes made for the capture. |
| `theme-dark`, `theme-light`, `themes` | One scene in each theme; `themes` is a montage of six color schemes. |
| `work-items`, `issues`, `sub-agents`, `pull-request`, `conversation-width` | The Work items and Issues tabs of CodeAlta, a session with more sub-agents than the sidebar lists (five), the pull request menu (a crop), a conversation at 70%. |
| `terminal`, `worktrees`, `automations`, `plugins` and their close-ups | They need state that is built for them (a running terminal, two worktrees, automations in the project configuration, a temporary plugin) and removed afterwards. |
| `worktree-manager` | The **Worktrees…** window of a public project, showing the project folder and existing worktrees. Inspect paths and session titles before capturing. View only: do not select or remove worktrees for the picture. |
| `remote-control` | A crop of the **Remote Control: off** popover of a public-project Claude Code session and its composer bar. Do not turn it on, create a live remote link or manufacture a connected state for a picture. |
| `statistics-overview`, `statistics-activity`, `statistics-models`, `statistics-tools`, `statistics-tools-time`, `statistics-projects`, `statistics-filters` | The Statistics tab beside a session tab, in the dark theme, with the numbers of a real profile for a space of public projects. `overview` is the last 90 days by week, the time by project; the pages are taken from their top, and `tools-time` is the Tools page scrolled to its end; `filters` is a crop of the bar with the **Filter** menu open. |
| `statistics-history`, `statistics-history-reading`, `statistics-sessions` | With the sessions of the developer instance itself: the first card of the tab (a crop), the tab while the history is read, and the Sessions page of the last 30 days. |
| `statistics-session-menu` | The context menu of a public-project session, showing **Statistics of this session**. This does not open or export any Statistics data. |
| `documentation`, `landing` | The shipped guide and the Welcome tab, in the dark theme with the Explorer hidden. Use public guide content and inspect all recent project/session labels before capture; do not create shared catalog entries just to dress the page. |
| `canvases`, `canvas-board`, `canvas-agent` | The Canvases page with the two canvas samples; a session whose agent used `alta canvas` and `alta statistics`, alone, and with the Board tab of the `canvas-board` sample in a pane at its right. |
| `plugin-buttons`, `plugin-buttons-settings` | The top of the window with the buttons of the `canvas-checklist` sample and its Checklist tab (a crop); **Settings > Plugins** on the tab of the project, without the two lines of folders at its top, made tall enough to show the buttons of every plugin (a crop of the Settings window). |

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
- **Add provider.** The menu lists the built-in providers the profile does not have: take the picture on a
  profile of its own (`--catalog-root`, `--data-root` and the other roots of an isolated launch, with
  `--mcp-port`), with one provider that is disabled in its `config.toml`. `add-provider` is the crop of the
  Settings window. The folders of that profile are outside any `.alta` folder, or the start is refused. The
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
  sub-agents are then no longer at the end of the timeline: **Load previous messages** brings them back.
  `home`, `explorer` and the three `split` pictures have a session with sub-agents as their subject too, and
  are retaken with `sub-agents` when that part of the sidebar changes.
- **Space activity.** The marks of a space are those of its sessions, so they need real ones. For a session that
  waits: show the other space, open a new session in one of its projects and send from the page (the **Start
  session** button of the prompt bar: a question asked with `alta ask` is only raised in a run started from the
  application) a prompt that asks the agent to ask one question with `alta ask` and to wait for the answer. Rename
  the session from its row menu, then show the space of the pictures again: a few seconds later the foot of the
  Explorer names the session, and the space switch has its dot. For a running mark, start a session in a project
  of a third space with `alta session create` and `alta session send`, on a prompt that takes a few minutes.
  Afterwards answer the question, stop the run, and send that session a last short prompt, so that no space is
  left with a session that waits or failed.

## Exporting

Save a capture as **lossless WebP** (`PIL`: `image.save(path, "WEBP", lossless=True, method=6)`), about 100 to
250 KB. A capture with a large gradient or a montage is saved with `lossless=False, quality=95`.

The file is `site/img/alta-desktop-<scene>.webp`. A close-up is a crop of the capture, shown in a narrower
figure.

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
