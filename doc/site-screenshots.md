# Updating the screenshots of the site

The pictures of CodeAlta Desktop on the website (`site/img/alta-desktop-*.webp`) are captures of the real window.
They are retaken when the window changes in a way every picture shows (the title bar, the sidebar) and when a
feature gets a page. This is how.

## What a capture is

- One capture is **2400x1500** pixels: the window at 1600x1000 CSS pixels on a display at 150%.
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
- The tools of the server move no real pointer: what a row shows under the pointer (the buttons of a project row
  in `explorer`) is shown with a style, `display: inline-flex !important` on the `.project-row-action` and
  `.project-actions-trigger` of that row.
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
- **Add provider.** The menu lists the built-in providers the profile does not have: take the picture on a
  profile of its own (`--catalog-root`, `--data-root` and the other roots of an isolated launch, with
  `--mcp-port`), with one provider that is disabled in its `config.toml`. `add-provider` is the crop of the
  Settings window. The folders of that profile are outside any `.alta` folder, or the start is refused. The
  `mcp-server` picture is taken there too: it shows the address of a normal instance (port 2582), which the
  page of an instance on another port does not have, so the port is changed in the text of the page.
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
