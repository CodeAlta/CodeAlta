# CodeAlta desktop (in development)

## Observation errors and pasted images

Runtime/receipt observation errors appear inside the selected session timeline, never below
the prompt. An Ask read failure is an error, not a pending question. “Pending asks” is reserved
for a real pending backend ask; retained answers and recovery drafts remain available.

Owned PNG paste no longer imposes the former 64 KiB per image / 96 KiB aggregate, three-image
or 2048px/4MP limits. Image-bearing prompts use the normal 32,768-character text limit.
The owned inbound RPC frame allowance is 128 MiB including base64/JSON overhead. PNG integrity,
observed model capability, exact draft revisions and host/session ownership remain validated.

Clipboard PNG, JPEG, WebP, GIF and BMP files now pass through the browser decoder and are
normalized to PNG before wire validation. This avoids rejecting valid editor-generated PNG
metadata, palettes or interlacing. Paint.NET clipboard data must be exposed as an image file by
Chrome/WebView2; there is no direct native clipboard reader. Conversion errors no longer imply
that the selected model lacks image support. Animation is reduced to one frame and source
metadata is not retained.

`CodeAlta` is the native desktop .NET tool (`alta`), built with published NeoAstra 0.3.1,
generated RPC, React/strict TypeScript and packaged local Vite assets. Node/npm is needed
only to build. The installed application has no UI server or external asset origin.

**This is an in-development interactive workspace, not full agent parity.** Use `CodeAlta.Tui`
(`altatui`) for the complete agent workflow. Running `alta` with no options now matches the TUI's
normal startup: it owns the current project and `~/.alta` runtime, so an existing session can send
a prompt immediately. It can start configured providers and acquires the shared runtime lock; plugins,
command permission review and provider input remain disabled by default. WebView-only data stays in
the platform-local application-data directory, and existing `.alta` storage is not migrated.

## Try the web workspace now

The frontend includes an interactive, in-memory browser demo. It does not need a .NET host,
credentials, a profile or production data, and it never sends provider requests:

```powershell
cd src/CodeAlta/frontend
npm ci                  # first checkout only; generated NeoAstra inputs must already exist
npm run demo            # opens http://127.0.0.1:5173
```

If `../obj/neoastra` does not exist in a fresh checkout, first run the desktop build command in
the next section to generate the typed contracts/client, then return here and run `npm ci`. This
generates build inputs only; it does not launch the native host or read a profile.

Select projects and sessions, send messages in the local composer, open **Settings** from the
gear button in the title bar (or a supported shortcut/palette action), and
switch themes. Demo messages disappear on refresh. `npm run build:demo` produces the same preview
as static files under `dist/`; `npm run build` builds the production NeoAstra-connected frontend.
The packaged desktop uses the generated bridge and never includes the demo backend.

Session Info's **Recorded creation time** is the already-loaded catalog summary's
creation timestamp (possibly cached), not runtime start, elapsed time or last activity.
Year-1/default values are unavailable; no header, update-time, clock or filesystem
fallback is used. The dialog preserves the supplied ISO representation, whose offset
may already be UTC after cache projection. Missing/null/malformed client fields show
unavailable; this tolerance does not promise cross-version RPC contract compatibility.
Opening the dialog adds no metadata reads, and Copy remains session ID only. Existing
snapshot identity/scope checks, catalog freshness and epoch/clipboard limits remain.

Settings opens a modal window, initially about 80% of the viewport; the selected session,
composer draft and timeline remain mounted underneath but cannot be interacted with while it is
open. Drag its title bar to move it and its edges or corners to resize it; the geometry is saved in
this WebView's local storage and the title bar's restore button (or a double-click on the title bar)
returns to the default. The expanded prompt editor (F6) is the same kind of window with its own saved
geometry. Use the window's sections, Escape or Close settings to return. Configuration, Providers,
Models, Agent prompts, MCP Servers and Logs are **not** workspace tabs. Session tabs belong to the
global workspace, not the selected project: sessions from different projects can stay open together.
Drag a tab along the tab strip to reorder it, to a pane edge to create a split view, or to its center to merge
(up to 32 open sessions); drag the divider between panes to resize them. The presentation uses
stable content slots and one vertical Explorer with Projects above Sessions. Its width is locally
saved (220–720 pixels, never more than 60% of the window); the Explorer button in the title bar hides it without discarding the width.
The command palette and Settings are the two buttons next to it, right after the CodeAlta mark. The compact composer keeps secondary
actions under **More composer actions**, with retained-request recovery separate. Alta notes belong
to each session, start collapsed when empty, and open when meaningful content arrives. A small disclosure
at the top right of the timeline expands/collapses their floating panel without resizing the timeline or
composer. The panel (or its collapsed disclosure) can be dragged anywhere over the timeline and the open
panel resized; its size and position are kept across collapse/expand and saved locally, relative to the
pane's top-right corner, and its header restores the default. There is no nested notes dock or notes divider.
Project and session rows open floating action menus from their **…** button, context menu or Shift+F10; the
Projects header has a filter box, a sort/actions menu and an Open project button. A dot-matrix spinner on a
session tab, its sidebar row and its project marks an observed running session, including sessions whose
tab is closed. Existing session-keyed transitions and
draft/uncertain-action guards remain in place.

Queued text and steering use compact rows outside and above the prompt card. The Send button is a
split button: its caret chooses the default action, **Send now** or **Enqueue until idle**, which
stages text for the next idle observation; queued rows expose repeat count, editing, steering
and deletion. Claimed requests retain their exact targets and keys; uncertain outcomes require
explicit retry, and receipt-confirmed consumption removes the row. Staged rows are app-memory
only, not durable across reloads. Image prompts still use immediate Send. The bottom bar shows the
next-Send agent prompt, provider, model and reasoning effort as one clickable summary; it opens a
popover to change them (reasoning is a stepped slider over the model's supported efforts) and to
browse the agent-prompt and model catalogs. Enabled-provider readiness appears in the same bar.
The expanded prompt editor retains file insertion without reference-inspection diagnostics.

The provider indicator is a compact active-provider count, green when ready and orange when
providers fail or are unsupported. Owned startup initializes the configured providers, as in
the TUI; inventory reads themselves do not probe. Compaction has a persistent icon, disabled
until an idle attachment is verified. Send and Stop occupy one slot, not two adjacent buttons.
Missed background reads retain receipts and ask drafts and recover on the next observation;
prolonged unavailability shows a small status indicator rather than raw timeline diagnostics.

For a Send lockup, preserve the developer-console entries prefixed `[CodeAlta Send]` and
`[CodeAlta RPC]`. They report composer guards, dispatch, safe framework error codes, elapsed time
and UUID request keys, never prompt text or provider credentials. Match a dispatch key with
`Send reached backend (<key>)` in application logs. With NeoAstra 0.3.1, `duplicate_request`
means an active or retained completed request identity was reused;
`too_many_requests` means admission/rate/channel pressure; `connection_closed` means transport
loss. An uncancelled eight-second `operation_canceled` wait is consistent with the client timeout.
Completed request identities now use bounded fingerprint history rather than accumulating ID strings
for the document lifetime. `request_id_capacity_exhausted` separately identifies capacity pinned by
active work/subscriptions; it is not a duplicate. No automatic
reload, capability reset or mutation retry is performed. Inspect receipts before deliberate retry.

Session info, Reminders, context usage, timeline details, file changes, tool records, the raw history
source, About, project details, the saved-session browser and the model/prompt choosers open as
windows: drag the title bar to move them, drag an edge to resize them, and use the title bar's restore
button (or double-click it) to return to the default size and position. Each kind of window remembers
its own geometry. Closing Reminders does not cancel an admitted action or retry an uncertain Save.
In the explorer, a project row has one **…** menu (also on right-click): **New session**, **Search
sessions…** and **Browse saved sessions** for that project, then **Open**, **Details**, **Rename
project…** and **Archive project…**. **Other sessions** has the same session actions. Session search
is an inline field above the session list; Escape or its clear button hides it.
Clicking a project opens one temporary **New session** tab, reused when selecting another project
before creation. Selecting an existing session tab or sidebar session removes it. Real session
panes, drafts and split geometry are retained, rather than replaced with a project placeholder.
The welcome view renders the same 3-D FIGlet/ASCII logo asset as the TUI, with the project name,
folder and launch guidance below it. Its composer uses the same surface, status line, editor,
toolbar, expansion and keyboard handling as existing sessions. **Start session** (or Enter) creates
the session, moves the prompt into it and sends it in one step; a text-only prompt needs no second
Enter. Agent/model/reasoning choices are available before creation in the same shared
selector rows as an existing session. Agent choices use the exact project/global prompt catalog;
model choices load for the selected enabled provider. These are local preferences, revalidated
against the created session before transfer, not authority to send or switch a running provider.
Unavailable model catalogs do not remove independently available Agent choices; use the labelled
Refresh composer choices button to retry. The prompt's divider
reserves space within its own pane, independently of session dividers. Sizing observes the
actual portal DOM mount, including late-mounted panes. Session tab dragging uses pointer capture
and public FlexLayout move actions, avoiding native HTML5 drag/drop handling. A highlighted target
previews the split/merge/insertion, and Escape or loss of capture cancels. The tab menu's **Split session
right/below** actions provide a keyboard alternative when a pane contains multiple session tabs.
Real FlexLayout session tabs project
existing bounded identities, with compact status/close chrome and secondary Reopen/Refresh menus.
The tab menu uses themed Blueprint controls with an opaque popup and keyboard navigation.
Open sessions retain independent display/runtime/review/notes owners and drafts. Hidden panes
pause automatic history, live, runtime, receipt, Ask, notes and choice reads until visible again;
visible split panes continue observing. A pane that becomes visible again keeps its loaded timeline
without reading history again; only an interrupted page chain continues with its next page.
Hiding a pane does not cancel admitted commands. The focused pane
owns global composer shortcuts. Running indicators in the project/session sidebar refresh in bounded
batches, prioritizing visible and open sessions (at most 32 verified sessions per batch). Missing or
stale observations never imply idle or authorize mutations. Provider switching remains blocked
on failed-switch persistence/recovery semantics. Timeline loading now supports records up to
8 MiB, with bounded previews and an explicit raw-source inspector showing at most 16 KiB per
chunk. Previously loaded rows survive later page errors with a partial-state notice. Temporary history-read
failures can resume the existing live-revision refresh, with at most two retries for an unchanged live
revision. Explicitly older views and source inspection are not replaced by this recovery. The spinner
uses independent runtime observations; if the live display channel fails, use **Reconnect live activity**
when offered, not Send retry. A spinner alone does not prove that transcript updates are arriving. The window
is bounded to 1,000 events, 2 Mi text units or 32 automatic pages; load older explicitly to continue.
Larger records produce an explicit error, not a silent skip. Visual/browser acceptance is deferred; known browser
fixture failures are preserved, not reported as fixed. See the
[current runnable checkpoint and verification gaps](../../doc/desktop-ide-checkpoint.md).
The earlier single-pane session-tab candidate passed 33 scoped functional tests. The split-pane
follow-up adds regression coverage but is build-verified only; mounted-lifetime qualification remains pending.
Typing `@` at a word start in a prompt opens the **Project files** window. It lists the project's
files and folders from the same index as the TUI (`.gitignore`-aware, recently used first, fuzzy-ranked
as the query grows, at most 64 rows) with a colored icon per file type, the name and its folder.
Up/Down, PageUp/PageDown and Home/End move the selection, Enter replaces the `@query` with a
Markdown link (`[name](relative/path)`) and Escape leaves the text as typed. Typing `#` the same
way opens **GitHub issues** for the project's `github.com` remote: issue number, title, state and
last update, most recently updated first, 50 at most. The search field matches a number or title
words, **Include closed** (`Ctrl+I`) filters closed issues, and Enter inserts `[#123](url)`.
The token comes from `GITHUB_TOKEN`, `GH_TOKEN` or `gh auth token`; pull requests are not listed.
Both windows are resizable and remember their size. Catalog-only and unverified inputs have no picker.

**Settings → Appearance** manages the language, the theme (Dark, Light, or System, which follows the
operating system), the color scheme, project sorting, the recent-session count and desktop project-rail
collapse. The button before the window controls at the top right switches between the three themes. The rail's Sort projects selector and
Show/Hide projects button use the same live preferences; changes apply immediately and are
saved only to this WebView's local storage (theme, colorScheme, projectSort and projectRail v1 keys).

The color scheme is **Blueprint** (Blueprint's own palette, the default) or one of the RootLoops
schemes that the TUI gets from XenoAtom.Terminal.UI: Cherry, Tomato, Orange, Pineapple, Apple, Kiwi,
Kale, Blueberry, Plum, Elderberry, Blackberry and Raspberry. Each has two variants that follow the
theme: its *Dark Soft* recipe for the dark theme and its *Light Soft* recipe for the light one. A scheme
recolors the whole window, Blueprint components and the code editors included.

A scheme is nothing but a redefinition of Blueprint's palette variables (`--bp-palette-*`), selected by
the root's `data-color-scheme` attribute:

- `frontend/src/colorSchemes.gen.css` and `colorSchemes.gen.ts` are generated by
  `dotnet run --project src/CodeAlta.ColorSchemes.Generator`. Run it again when the scheme recipes of
  XenoAtom.Terminal.UI or Blueprint's palette change, and commit the result. It reads Blueprint's palette
  from `frontend/node_modules`, so the frontend must have been built once.
- Every palette step keeps Blueprint's lightness, because Blueprint derives more colors from its
  palette by lightness arithmetic and tuned its contrast for those steps. The grays take the hue and
  chroma of the scheme's neutral ramp (background, black, bright black, white, bright white, foreground)
  at their relative place between window background and text; each color family (blue, green, red,
  orange, …) takes the hue and the share of chroma of the nearest scheme accent.
- Blueprint compiles most component colors to literals. The Vite build (`blueprintPalette` in
  `vite.config.ts`) rewrites every literal of Blueprint's stylesheet that equals a palette color into the
  palette variable, so a scheme reaches all components and the default renders exactly as before.
- The app's own colors (`--bg`, `--panel`, `--text`, `--accent`, … in `style.css`) are defined from the
  palette variables and follow the scheme. Monaco takes its surface colors from them while a scheme is
  selected and keeps its own with Blueprint.
If storage is invalid or unavailable, the screen reports the fallback; if a write fails,
the change applies in this window but is **not** reported as saved. Recent visible updates
uses only verified saved session timestamps in the visible snapshot, **not** the TUI's
complete last-active order: missing, unverified or truncated evidence cannot establish
recency, and undated projects follow name/ID order. Narrow-screen Show projects is a
temporary reveal independent of the desktop collapse preference. These controls do not
change the selected project/session, draft, requests, pane widths or timeline position.
The command approval policy is edited in **Settings → Configuration file**.

**Settings → About** is an inline page with the product, version, build metadata (only for a recognized
version `+metadata` suffix) and the mode of the running app (desktop app, catalog only, browser demo or
unavailable host). The implemented-actions palette opens the same facts as a window. Nothing is fetched
or written; update checks are not available in the desktop app.

**Settings → Application Logs** is a live log view of this desktop process: it reads the captured
messages when the page opens and every two seconds after, one line per message with its local time,
level, logger and text, coloured by level (warnings in gold, errors in red). The view stays at its end
unless you scroll up. A filter box and **All / Info / Warnings / Errors** narrow the lines, **Wrap
lines** wraps long ones, and **Clear** (one confirmation) removes the captured messages. The capture
holds the newest 1,000 messages (1 MiB, 2,048 characters per message, an exception appended to its
message); one read returns at most the newest 400 (256 KiB). Logging keeps its existing file writer,
levels, rotation and lifetime, but this screen **never opens log files**: it cannot show earlier
processes. If another owner initialized logging first, in-memory capture is unavailable; the browser
demo also has no desktop capture. A clear is tied to this capture's identity and the boundary of the
snapshot it was asked from: messages appended after it survive. Pending or
uncertain clear requests retain their original identity and boundary across screen
switches; a failed/mismatched reply cannot be retried or unlocked by Refresh. The
application must be restarted to discard uncertain in-process evidence. Clear never
alters persisted log files, the rolling writer, configuration, or logging lifetime;
it cannot clear another process's capture or an unavailable/external logger. No
export, file deletion or logging-policy controls are provided.
Logs may contain sensitive content; this screen renders them as inert text locally,
without link activation or external requests.

For the real local desktop, build from `src` (the frontend dependency/build switches are shown for
repeat builds that already have generated contracts and `node_modules`):

```powershell
cd src
dotnet build CodeAlta/CodeAlta.csproj
./CodeAlta/bin/Debug/net10.0/alta.exe
```

The ordinary .NET output layout is used: Debug builds are under `bin/Debug/net10.0` and Release
builds are under `bin/Release/net10.0`. Use the explicit catalog/scoped-owned flags documented below
only for isolated copies or qualification.

```powershell
dotnet build -c Release
./CodeAlta/bin/Release/net10.0/alta.exe --help
./CodeAlta/bin/Release/net10.0/alta.exe --version
./CodeAlta/bin/Release/net10.0/alta.exe
```

The main window opens centered at 80% of the primary work area on Windows; other platforms use a
centered 1280×860 window until NeoAstra exposes display metrics.

The window has no separate title bar: the page draws it. The CodeAlta mark and name sit at the top
left, the session tabs continue the same strip, and the platform's minimize, maximize and close
buttons stay at the top right. Drag the mark or any empty part of a tab strip along the top edge to
move the window, and double-click it to maximize or restore; tabs and buttons in that strip keep
their own clicks and drags. With the Explorer hidden the tabs start right after the name. In a split
layout only the panes along the top edge are part of the title bar.

The view is an application shell rather than a browser page: the browser's own find, print, reload
and zoom shortcuts, its context menu and its status bubble are turned off, so those keys reach
CodeAlta's commands. Text-editing keys work as usual.

No-argument startup derives a stable WebView data directory from the platform's local application-data
location, composes the owned host for the current directory, and uses the current `~/.alta` catalog.
The host may update the standard project catalog, journal, cache, provider state and SQLite sidecars;
submissions may authenticate or use configured provider storage/network. It adds no desktop-specific
state to `.alta` and performs no storage migration. Help/version and rejected arguments do not initialize
native services or create storage. Explicit catalog and scoped-owned options retain stricter validation.

The **Models** Settings section (or `Ctrl+G`, then `Ctrl+O`) opens a model catalog in owned
mode. It requests the actual host-reported models of every enabled provider and shows them in one grid
with resizable columns (provider, name, ID, token limits, capabilities, default effort), like the TUI's
Models window; a provider whose read fails is named above the grid. Search provider and model names,
IDs and descriptions and open a model for supported efforts, capabilities and token limits. Provider
reads are capped at 32 and model reads at 128 and 96 KiB serialized, with omitted results identified; availability and
missing metadata, including pricing not reported by this inventory, remain explicit. Loading a
provider can start its configured provider runtime/probe and may access its normal configured
storage or network. The catalog-only copy and browser demo cannot discover model inventory from
saved defaults and show an unavailable state instead. From a model's details, an owned session
can explicitly select that provider's available model and supported reasoning effort for its
**next Send**. This uses the existing per-session composer choices and Send validation, retains
the selected agent prompt, and cannot change an in-flight/uncertain exact Send; it does not change
the running turn or queued work. A different provider, missing choices or changed host/session
cannot apply the selection. Provider authentication and global defaults remain TUI workflows.

The owned Send service independently checks the provider during preparation: a structurally valid
different-provider request can receive an **accepted** retained receipt, then settle as **Failed** with
`preparation_failed`. This is not a pre-admission refusal. Fake-provider integration coverage establishes
that an existing idle source attachment is unchanged and the target is not started/resumed/sent; exact
retries replay the same receipt, while changed text or selection on that key conflicts. Canceling a waiter
does not settle preparation or free its occupied slot. Cross-provider switching remains blocked pending
a host-owned transition/readiness/queue and recoverable-publication contract; this is not switching support.

The **Providers** Settings section (or `Ctrl+G`, then `Ctrl+R`)
lists at most 32 configured providers with adapter type, enabled/default settings, configured
default model and **cached** host initialization availability. Opening the section or selecting a row
does not probe. **Test selected provider** explicitly starts only that enabled provider through
the shared initialization service; it can use configured provider storage or network in an owned
launch. The result is a completed initialization/probe, not proof of authentication or a live
connection, and arbitrary provider error messages/URLs/categories are not shown. An abandoned
probe is still joined by the host before disposal; do not assume its outcome from a timed-out
browser request. Catalog-only mode lists saved descriptors read-only without runtime tests.
In an owned launch the **Providers** section is an editor: the configured definitions (including
disabled ones) are listed on the left with their cached availability and the configured default, and
the selected one has a form for its key, adapter type, enabled state, display name, default model,
reasoning effort, API URL, API key environment variable and API key, plus **Use as the default
provider**. **Add provider** starts a new definition and the trash button removes one after
confirmation. **Save and apply** writes the global `config.toml` and re-registers the providers in
the running host; it is refused as a conflict when the file changed on disk since the page read it.
A stored API key is never sent to the page: leaving the field blank keeps it, and **Remove the
stored key** clears it. Settings the form does not show (timeouts, request overrides, compaction and
so on) are preserved, but this structured save rewrites the file without its comments and blank
lines, exactly like the TUI's provider dialog; use **Configuration file** to keep hand formatting.
A field left blank uses its default: the field shows that default as its placeholder with a
**Default** tag (the provider's built-in value, otherwise the one of its adapter type), and a field
with a value has a button that returns it to the default. Codex, Copilot and xAI sign in with their
account from the form's **Account** block: it shows whether the stored sign-in is valid and for which
account, a button per sign-in method (**Sign in with the browser**, **Sign in with a device code**)
and **Sign out**. A sign-in opens the provider's page in the system browser and shows the address,
and the code to enter for a device flow, each with a copy button, until it completes or is canceled;
a provider that was disabled is enabled when its sign-in succeeds. Save a new or edited provider
before signing in. The default provider shown here is `[chat] default_provider`; the host's own inventory marks every registered provider as
default, so it is not used for that.

**Provider defaults.** `globalConfig.providers` also reports what a blank field falls back to. Each provider carries
`defaults` (`displayName`, `model`, `reasoningEffort`, `apiUrl`, `apiKeyEnv`; null when nothing is
known): the built-in template entry with the same key and adapter type, then what the adapter type
fills in (the Codex, Copilot and xAI display names, the Codex API URL), and finally the provider key
as the display name. `typeDefaults` lists the same record per offered adapter type, in the order of
`providerTypes`, for a provider that has no definition yet.

**The `providerLogin` RPC.** The owned host's `providerLogin` service signs the Codex, Copilot and xAI providers in to their
accounts with the flows and credential storage of the TUI. Requests name the host epoch and a
provider key.

- `status` reads the stored sign-in state without network access: `supported` (false for an adapter
  type without account sign-in), the `modes` (`browser` for Codex, `device` for Copilot, both for
  xAI), `signedIn`, an account label, a short detail and a token expiry when known. Status codes are
  `ok`, `unavailable`, `stale_epoch`, `invalid`, `unknown_provider`, `config_invalid` and
  `read_failed`. Copilot's state follows its short-lived cached token.
- `login` takes an optional mode (the provider's first mode when blank) and returns a channel of
  events. A `prompt` event carries the address to open, for a device flow the user code and its
  expiry, and `browserOpened`: the host hands `https` addresses to the system browser. Exactly one
  terminal event follows: `completed` with the signed-in state, or `failed` with a code
  (`unavailable`, `stale_epoch`, `invalid`, `unknown_provider`, `unsupported`, `busy`, `timeout`,
  `canceled`, `login_failed`) and at most an exception type name as detail. Only one sign-in runs at
  a time in the host; a second one fails with `busy`. Closing the channel cancels the sign-in, and
  closing the window cancels and joins it before the host is disposed. A completed sign-in enables a
  disabled provider and re-registers the providers like **Save and apply**, so the configuration
  revision changes; a ChatGPT sign-in that declined plan usage (detail `No plan usage permission`)
  does not.
- `logout` removes the stored credential and reports `removed` (false when the provider was not
  signed in). It is refused with `busy` while a sign-in runs; other codes are those of `status` plus
  `unsupported` and `logout_failed`.

**Settings → Configuration file** edits the global `config.toml` (providers and their credentials,
the default provider, plugin and skill policy) in an owned launch. The text is validated shortly
after typing stops, with the first diagnostic marked on its line; Save is offered only for changed,
valid text of at most 256 Ki UTF-16 units. A save is refused as a conflict when the file on disk no
longer has the revision the editor read (for example after an edit in the TUI or a text editor):
reload, then reapply the edit. **Save** only writes the file. **Save and apply providers** also
re-registers the enabled provider definitions in the running host and unregisters providers that
are no longer configured or enabled, like the TUI's Save and Apply; replacing a provider discards
its cached runtime. The file is written as typed, without reformatting, and not atomically. The
editor shows the file's content, including any credentials stored in it. Catalog-only launches and
the browser demo report the editor as unavailable.

ChatGPT subscription providers use the same token-sharing implementation and global credential
store as the TUI. Sign in from **Settings → Providers** (or with **Continue with ChatGPT** in `altatui`),
then use that configured provider in the owned desktop. Existing legacy Codex credentials require a new sign-in; device
login and credential import are no longer supported. No desktop-only OAuth flow or storage is added.

The **Agent prompts** Settings section (or `Ctrl+G`, then `Ctrl+H`) lists agent prompts and system
prompts from the built-in, global and (when an unarchived project is selected) project scopes, and
edits them: display name, description, system prompt, whether the text is added to the system prompt
or replaces it, and the prompt text in a Markdown editor. **New prompt** creates an agent or system
prompt in the global or project scope; **Customize a copy** on a built-in prompt creates a global
prompt with the same name, which then overrides it; **Remove** deletes a global or project prompt
file. Built-in prompts are read-only. A save is refused, without overwriting, when the file changed
on disk since it was read. A session's prompt for the next Send is chosen from the prompt bar.

The **MCP Servers** Settings section lists the servers defined in the global and selected project
`mcp.json` and edits them: name, local command (command, arguments, working directory, environment
variables) or HTTP (URL, headers), where it is stored, and whether it is enabled. The switch in the
list enables or disables a server without opening it. Stored environment and header values are never
sent to the page; leaving a value blank keeps the stored one. Connection tests, sign-in and per-tool
switches remain TUI workflows. Enabling or disabling rewrites `config.toml` without its comments.

The end of the status line above every prompt shows the status items of plugins. The MCP plugin shows
`MCP {enabled}/{configured}`, `· {n} unavailable` in the warning colour when servers are disabled or
invalid, and the state of their tools; it is absent when no MCP configuration exists or the plugin is
disabled. Clicking it opens **Settings → MCP Servers**. The items are read again every ten seconds and
when the window gets the focus back.

The **Skills** Settings section lists discovered skills (project, user, plugin and built-in) with a
switch per skill, a filter, **Enable all** / **Disable all** for the shown skills, and **New skill**,
which creates `<name>/SKILL.md` under the global or project skills folder. Selecting a skill shows its
details beside the list: source and state, the path of its `SKILL.md`, the skill that overrides it,
license, compatibility and allowed tools when declared, related files, validation diagnostics, and the
instructions of the `SKILL.md` rendered as Markdown (the first 64 Ki characters of a file up to 256 KiB).
The **Models** section's table fills the page height. The **Plugins** Settings
section has a switch per plugin, including the built-in MCP, GitHub and Statistics plugins; a change
applies the next time CodeAlta starts, except for the Statistics rows described below, which follow
the switch from their next read on. With an unarchived project selected, both pages can store a
change globally or for that project. Every Settings section has its own icon in the sidebar.

The **Reminders** session tool is available only with a selected owned session. Its window lists that
session's reminders on the left (state, attempts sent, interval) and shows either the **New reminder**
form or the selected reminder on the right. A new reminder has a Markdown prompt, a delay (`5m`,
`1h 30m`, `90s`, whole seconds or `HH:mm:ss`, at most 24 hours; quick buttons for 5m, 15m, 1h, 4h)
and 1–20 attempts. **Delete reminder** asks for one confirmation. At most 32 reminders per session and
256 per host are kept; they live in memory and are lost when CodeAlta stops. The composer's timer
button shows the session's active count, and the explorer marks every session and project that has
active reminders; both follow reminder changes made in the window and are re-read every 20 seconds.
The timer icon and `Ctrl+G`, `Ctrl+D` open Reminders.
Select a schedule to inspect its full stored message, beyond the list preview. **Use as new reminder**
copies that message, delay and repeat count into the local Create form (with confirmation before
discarding an edited draft); it does not change the selected schedule or create a new one until
Create is explicitly submitted. **Save message** edits only the full message of an active selected
reminder, using the exact host, owning session, reminder ID and the detail snapshot's edit revision.
It is separate from Create/Use as new and the exact-ID Delete confirmation. Unsaved edits require
confirmation before switching reminders or discarding the edit draft; a concurrent edit (including
one later changed back to the original message), missing reminder or completed schedule refuses Save
without overwriting it. Save preserves the delay, due time, repeat count and attempt counts: it only
affects future firing captures, never an already captured or admitted send. Pending or uncertain Save
admissions remain held for their original host/session, with no automatic retry; refresh observes
state but cannot certify an uncertain response. The exact pending/uncertain Save's host epoch,
session, reminder ID, original revision and full submitted message remain visible for that target
even if its list/detail read fails or the reminder disappears; switching targets does not show
another session's retained message. While viewing that target, a local unsaved edit remains
selectable for recovery after conflict, failed detail/list refresh or deletion, even with an empty
list; discarding that draft requires confirmation and never clears an outstanding Save admission.
A refreshed detail does not silently rebase an old draft or retry it.
Within the eligible panel, `Ctrl+R` refreshes from non-editor focus, `Ctrl+E` focuses the selected
active message editor, and `Ctrl+S` saves a changed message from that editor or a non-editor panel
control. `Delete` from non-editor focus only focuses the **Delete reminder** button; it
never submits deletion, and Delete in a text field remains text editing. Shortcuts do not run during
inline discard confirmations, modal/IME input or without current host authority. Pending or uncertain
Save blocks mutation/focus shortcuts; `Ctrl+R` remains read-only observation, never a retry. Create
still requires its explicit button; `Ctrl+Enter` is not mapped in this panel.
Schedules and results are **in memory, not persisted**:
closing/restarting the host loses them. The host
owns the timers and attempts one normal owned Send per firing, with the same permission policy
and host drain as other sends. Busy, unavailable or failed sends count as failed attempts; there
is no automatic retry beyond the requested repeats. A completed reminder means all attempts
finished, not that the agent responded. A deletion cannot retract an already captured delivery
or an admitted run. Counts are point-in-time as of Refresh; a lost mutation response is held as
uncertain for that host/session, with no automatic retry. Catalog-only mode cannot schedule work.

### Running sessions

While a session works, an activity spinner shows in three places: before the title of its tab, on its
row and on its project's row in the Explorer, and in the status line above the prompt. For a session
open in a visible pane the three follow the run itself and start and stop together. Sessions that are
not open are checked every five seconds, so their Explorer spinner can lag by that much.

### Turn statistics

When a turn ends, the built-in Statistics plugin adds a **Turn statistics** row after it, as in the
terminal UI: duration, input and output tokens (the provider's totals when it reports them, an
estimate otherwise), tool calls with their total time, and compactions. The row's **Details** button
opens the full tables: sizes of the prompt, answer, reasoning and tool traffic, latencies and speeds,
the provider's usage figures, and one line per tool bucket. The rows are not stored in the session;
they are computed from its events each time, so earlier turns get theirs when **Load previous
messages** brings them into view, and a failed turn has one too.

The desktop app does not start the plugin runtime, so source plugins cannot add rows of their own yet.
The host runs the Statistics projection itself through the `sessionPluginEvents.read` RPC: it reads
the journal backwards from its end until the oldest turn shown is complete (at most 64 pages of 100
records; a turn that begins further back gets no row) and returns at most 32 rows, newest kept.
With the Statistics plugin turned off in **Settings > Plugins**, the next read (the next turn, or
reopening the session) returns no rows.

### File editor

`Ctrl+E` (or `/edit` in the command palette) opens **Open file** for the selected project: the `@`
search limited to files, recently used first, with the same colored icon per file type. Up/Down and
PageUp/PageDown move the selection, Enter opens the file and Escape closes the window. The command
needs a selected project that is not archived and an owned host.

The file opens in a tab of the same strip as the sessions: the icon of its file type at the leading
edge, the file name (the project-relative path and the project name as tooltip) and a dot while it
has unsaved edits. There is one tab per file; opening a file that is already open selects its tab.
File tabs close (`Ctrl+W`), reopen (`Ctrl+Shift+T`, in the order tabs were closed), cycle
(`Ctrl+Alt+Left` / `Ctrl+Alt+Right`, after the session tabs), drag and split like session tabs. Up
to 32 open files and the selected one are restored with the window (`codealta.desktop.fileTabs.v1`
in local storage); a file whose project is gone or archived is not restored. While a file tab is
selected, the commands that act on a session (send, abort, message navigation, notes, session info
and the like) are unavailable; selecting a session tab, a session or project in the Explorer, or
the New session tab returns to the session.

The editor is Monaco. The file name or extension chooses the highlighting (C#, F#, TypeScript,
JavaScript, JSON, XML and MSBuild files, Markdown, TOML/INI, YAML, HTML, CSS, Python, Rust, Go, Java,
Kotlin, C/C++, shell, PowerShell, SQL, Dockerfile and others; anything else is plain text), and a
language is loaded the first time a file needs it. The footer shows the state (**Saved**,
**Modified**, **Saving…**, **Read-only**, **Changed on disk**), the caret line and column, the
path, a **Wrap lines** switch (on by default), **Reload** and **Save**.

- `Ctrl+S` saves through `projectFiles.write` with the revision that was read, from the editor or
  from anywhere else in the tab. Text is written with the line endings the editor shows: a file
  with mixed line endings gets its dominant one once it is edited and saved.
- When the file changed on disk since it was read, nothing is written and the tab offers **Reload**
  (replace the edits with the file on disk), **Overwrite** (write the edits anyway) or **Cancel**
  (keep editing; the next save asks again).
- Closing a tab with unsaved edits asks **Save**, **Discard** or **Cancel**; a save that is refused
  keeps the tab open. **Reload** with unsaved edits asks before dropping them.
- A file with the read-only attribute opens read-only. A binary file, a file over 1 MiB, a missing
  file, a path outside the project folder and an archived or unavailable project show the reason in
  place of the editor, with **Reload** to read again.
- Unsaved edits live in the open tab only: they are not stored, and closing the window drops them.

### Project files and git status

Two host RPC services give the page a project's files and repository state. Both take a project id,
never a folder, refuse another host epoch (`stale_epoch`) and answer `unavailable` in catalog-only
mode. Other refusals shared by both are `invalid` (no project id, or a malformed request),
`unknown_project`, `project_unavailable` (the folder is gone) and `read_failed`.

`projectFiles.read` and `projectFiles.write` read and replace one existing text file, addressed by a
path relative to the project folder (forward or back slashes, at most 1024 characters; responses use
forward slashes). The service does not create, rename or delete files, and refuses archived projects
(`archived_project`).

- A path that is rooted, names a drive or a stream (`:`), has a `..` segment or crosses a link
  (a symbolic link or junction below the project folder) is refused as `outside_root`. Empty or `.`
  segments, control characters and names ending in a dot or a space are `invalid`.
- A file larger than 1 MiB on disk is `too_large` and is not read; the same limit applies to the
  bytes a write would produce. A file that is not UTF-8, or UTF-16/UTF-32 with a BOM, or that
  contains a NUL character, is `binary`. A missing file, and a folder, are `not_found`.
- A read returns the text with its newlines unchanged, its size in bytes, whether the file has the
  read-only attribute, and a revision (SHA-256 of the file's bytes). It also records the file among
  the project's recent files.
- A write keeps the file's encoding and BOM and writes the newlines it is given. It must name the
  revision that was read: when the file on disk has another one the answer is `conflict` with the
  current revision and nothing is written. With `overwrite` the revision is not compared. A
  read-only file is `read_only`; a failure while replacing the file is `write_failed`.

`composerStatus.read` returns the plugin status items of a composer for a project id (or none): each
has the plugin id, a name, a label, a text, a tone (`info`, `success`, `warning`, `error`, `muted`) and
the Settings page it opens, which is what a plugin's session status contribution carries. The desktop
host does not start the plugin runtime, so it asks the built-in MCP plugin for its item itself, from
the configuration only: no server is contacted and the tools read `tools not loaded`.

`projectGit.status` returns the branch of the repository containing the project folder and how much
its tracked files differ from the last commit. Archived projects are answered too.

- The repository is the nearest `.git` at or above the project folder; a `.git` file (a linked
  worktree or a submodule) is followed to the directory it names. Without one the answer is
  `not_repository`.
- The branch is read from the `HEAD` file, without starting git. On a detached `HEAD` the branch is
  the first seven digits of the commit and `detached` is set.
- `insertions`, `deletions` and `changedFiles` come from one `git diff --shortstat HEAD`: staged and
  unstaged changes of tracked files in the whole repository. Untracked files are not counted.
- Git runs without a shell or prompts and is stopped after 3 seconds. When it is not installed,
  fails (for example in a repository without a commit) or is stopped, the answer is still `ok` with
  the branch and the three counts unset.
- An answer is reused for 5 seconds per project folder, and the host runs one git process at a time.

### Commands, help and keyboard shortcuts

The desktop app uses the TUI's key map. `Ctrl+P` (or `/` in an empty prompt, or the search icon on
the activity rail) opens the **command palette**: every command with its slash name, description and
shortcut, grouped by category and searchable by any of them; Enter runs the selected command and
commands that cannot run right now are dimmed. The palette is a window like Settings: drag its title
bar to move it and its edges to resize it. `F1` (or `?` in an empty prompt) opens **Commands and
shortcuts**, a filterable window listing the same commands by category.

| Keys | Command |
|---|---|
| `Ctrl+P`, `F1` | Command palette, help |
| `Ctrl+Q` | Exit (`/exit`); works from any window |
| `Ctrl+O` | Open project |
| `Ctrl+E`, `Ctrl+S` in a file tab | Open a project file in an editor tab (`/edit`), save the file |
| `Ctrl+Alt+Left` / `Ctrl+Alt+Right` (also `Ctrl+PageUp` / `Ctrl+PageDown`) | Previous / next tab |
| `Ctrl+W`, `Ctrl+Shift+T` | Close tab, reopen the last closed tab |
| `Enter`, `Ctrl+Enter`, `Shift+Enter` | Send, steer the running turn, new line |
| `Alt+Up` / `Alt+Down` in the prompt | Previous / next prompt sent from this window |
| `F6`, `Ctrl+T` | Full prompt editor, next agent prompt |
| `F8`, `F10`, `Ctrl+F11` | Abort the running turn, clear the queue, compact |
| `F3` / `F4`, `Ctrl+F3` / `Ctrl+F4` | Previous / next message, first / latest message |
| `Ctrl+Alt+B`, `Ctrl+F` | Browse sessions, filter the project's sessions |
| `Alt+Up` / `Alt+Down`, `Alt+Left` / `Alt+Right` outside text | Previous / next session, previous / next project |
| `Ctrl+Shift+N` | Show or hide the session notes |
| `Ctrl+G` then `Ctrl+P` / `Ctrl+S` / `Ctrl+G` | Go to prompt, go to sidebar, toggle the navigator |
| `Ctrl+G` then `Ctrl+T` / `Ctrl+U` / `Ctrl+D` | Session info, context usage, reminders |
| `Ctrl+G` then `Ctrl+W` / `Ctrl+R` / `Ctrl+O` / `Ctrl+H` | Settings, providers, models, agent prompts |
| `Ctrl+G` then `Ctrl+K` / `Ctrl+N` / `Ctrl+Y` / `Ctrl+L` / `Ctrl+A` | Skills, plugins, MCP servers, logs, about |

The second stroke of a `Ctrl+G` chord works with or without `Ctrl` held. Shortcuts work while the
prompt editor has focus; the ones marked "outside text" stay ordinary caret keys in text fields. An
open window keeps the keyboard, except that the Settings window follows the commands that move to
another Settings page.

### When the host stops responding

The window pings the host every 10 seconds; when three pings in a row get no answer it shows
**CodeAlta is not responding.** with a **Reload** button under the title bar. Reloading the window
opens a new RPC session with the same host; sessions keep running and prompt drafts are restored, but
image drafts and requests that were still waiting for an answer are lost. The message goes away by
itself if the host answers again.

The host side of an RPC session can be closed without the page being told, after which every request
is dropped and only times out. NeoAstra 0.2.1 did this when the page closed a channel (the live view
of a session, closed whenever its pane goes away) at the moment one of the channel's items was being
posted; NeoAstra 0.3.1 no longer treats that canceled post as a transport failure. The notice stays
as the way back from any other loss of the session.

## Browse a task-owned catalog copy

To display persisted projects and sessions, supply an existing **trusted, task-owned catalog
copy** separately from the new browser data directory:

```text
alta --data-root <new-absolute-browser-directory> --catalog-root <existing-absolute-catalog-copy> --allow-catalog-cache
```

Both catalog options are required together. The roots must not overlap, and neither may be
under a `.alta` path component. Path spelling does not prove ownership or protect against
symlinks/reparse points: do not point this mode at a production profile or an untrusted tree.
Preparing the copy is an explicit operator action; the application does not copy a profile.
Prepare it without copying `cache/cache.sqlite3` or its sidecars: existing cache rows can
contain absolute paths into the original tree. Normal shared cache creation then indexes
the copy. The desktop does not silently repair copied cache paths or switch discovery
strategies after an error; history refuses resolved paths outside the copy's sessions root.
Existing cache lookup can still probe whether a stored path exists before that refusal.

**This is not read-only filesystem access.** The opt-in permits the shared durable catalog
to create, rebuild and update `cache/cache.sqlite3` and SQLite sidecars in the copy. It does
not enable config migration, provider/plugin startup, agent execution or journal editing.
Cache/read failures are surfaced; the desktop does not substitute a header scan.

The screen shows a bounded persisted snapshot, project/session selection and metadata, not
live run status. Wire responses are capped at 200 projects and 500 sessions with a truncation
notice; these limits do not bound the underlying catalog load or implement paging/history.
**Open project** (`Ctrl+O`) is a resizable window with one field for a saved project's name or a
folder path. Saved projects that match by name or path are listed first; for an absolute path, the
folders below it are suggested as you type (at most 16, never at a filesystem root). Up/Down move, Enter
opens the selected project, Tab or Enter on a suggested folder completes it into the field, and Enter
on the typed folder itself checks it and offers **Trust and open folder**, which adds it as a project.
Escape closes the window. Saved projects use deterministic name order, **not** last-active or recent order.
Navigation requires the row's unique, unchanged ID/path/name/archive state in the current bounded
snapshot. It selects existing sessions without importing, creating a runtime, or discarding their
drafts. Archived projects are labeled and their sessions open read-only; a catalog-only launch
can also navigate saved projects read-only. **Details** in a project's **…** menu
opens a read-only window with the display name, full recorded path, ID and archive flag, each value
with a copy button at the end of its row. The global Other sessions root has no project details;
missing, duplicated or changed ID/path rows cannot open it. A failed/pending catalog refresh
disables inspection until a successful fresh snapshot, and project, session or host changes
dismiss stale details. It has no total session count, tags, description or source metadata. A copy
button shows whether the copy succeeded; no catalog/filesystem reads, metadata writes or project
actions occur on opening.

**Browse saved sessions** (`Ctrl+Alt+B`, or a project's **…** menu) lists the sessions of the project
(or the global ones) in a table: title, provider, last update and message count. Click a column to
sort, type to filter by title or ID, use Up/Down and Enter or double-click to open. A session that is
running shows a spinner. Exact deletion of several sessions stays in the collapsed panel under the table.

Owned project/global session creation is effectful even while the new session is labeled
**Draft**: it starts a provider runtime, creates a provider session and persists its identity,
without sending a prompt. The host selects its default enabled provider (or first enabled
descriptor); this is not a provider-free draft or a readiness/reservation guarantee. Creation
is single-flight: a concurrent request is `busy`, and canceling a caller's wait does not cancel
the admitted original, which shutdown drains. After settlement, an identical request creates
another session with a new identity; there is no exact receipt-replay guarantee.
`create_unconfirmed` does not establish that no effects occurred. Inspect uncertain outcomes;
neither refresh nor repeating the request proves that an earlier creation did not complete.
Create-and-open completion is bound to its original host, exact scope/path, selection and
view/input lifetime. Leaving and returning, opening Settings or another modal, changing
selection/title/search, closing the form, or publishing another catalog snapshot ends that
navigation authority. A stale completion leaves an inspection notice without clearing newer
input or closing Settings. Its late catalog read cannot publish over the newer context; an
unchanged valid completion still opens the confirmed new session. This is presentation fencing,
not a retained-create receipt, retry permission or proof that an uncertain create had no effects.

In an owned host, if the *current* project becomes
archived after an action was captured, its exact pending/uncertain Send, Steer, host-only Queue,
compaction, observed-run cancellation and Abort/cancel-Queue intents remain visible in a
read-only recovery card for the same unique project path, session and host epoch. The Reminders
view likewise retains exact pending/uncertain Save evidence while disabling Create, Save,
Delete and panel shortcuts. Neither card sends, retries or retargets an operation; a late
  definite outcome can remove an owner intent. Already-admitted Ask answer/cancel, nonsecret
  provider input and command-permission decisions also show their original host, session, handle,
  decision, captured text and pending/late result in a read-only archived card. It never lists,
  observes or acknowledges those owners: in particular, explicitly observing a terminal
  command-permission decision in the mutable review would enable a fresh review, but this card
  does not. No archived controls can resolve, cancel or retry an interaction. These records are
  bounded app-instance evidence, not host execution/completion proof. The archived and catalog-only
  composer is a compact, auto-growing **draft-only** editor: ordinary Enter makes a new line, Send
  is unavailable; Settings opens only from the project rail, shortcut or palette (Ctrl+G, Ctrl+U
  opens Settings when no owned context refresh is available).
  Model/prompt/reasoning are not inferred from unavailable runtime state. Draft restoration uses
  WebView-local storage when available; a failed write does not certify an off-session draft badge.
  Recovery evidence does not persist across app reloads. Missing, changed, duplicated, omitted
  and unreadable entries cannot be opened from a stale list. The separate absolute-folder field
  still requires an owned host, preview and explicit trust confirmation to import an arbitrary
  existing directory.
  Selecting a folder suggestion returns keyboard focus to the absolute-folder field before the
  focused option disappears, so Escape can close the dialog and cancel its pending suggestion
  wait. A canceled waiter does not establish that a blocking host enumeration has stopped.
If Refresh projects fails, saved selection pauses until a successful refresh.
During an admitted import, its original host epoch, requested/verified paths and pending or
uncertain outcome remain visible when Open project is closed and reopened. Saved selection and
new folder requests remain blocked across dialog remount and project-list refresh; refresh only
observes the catalog and never retries the import. A definite refusal unlocks the dialog; a
confirmed import only navigates after exact catalog verification without changing the selected
project in the meantime. An uncertain outcome requires app reload after external inspection.
This is not directory completion, archive mutation or full project-management parity.
The shared catalog caches its snapshot, and closing/reloading the document is not a refresh
or run-abort contract. Relaunch with a fresh browser data directory to load another snapshot.
Canceling an RPC waiter does not stop the shared catalog's background load. Existing RPC
teardown waits only a bounded time, so cache work can outlive bridge teardown; complete
native lifecycle qualification remains open.

### Context usage meter and window

The composer of an owned session shows a compact context meter: a small bar, the used percentage and
the token counts (`46% 125k / 272k`), green below 75%, amber from 75% and red from 90%, as in the TUI.
It reads `sessionUsage.read` when the session is shown and again, at most every four seconds, while a
run produces events; until the host has observed usage (for example right after opening a saved
session) it shows the last usage record of the loaded timeline. Clicking it opens the **Context usage**
window with what the TUI popup shows: provider and model, the context window (percentage, used and
limit, messages, active context against input headroom, indicative model limits), the last operation
(input, output, cache read, cache write, cached input and reasoning tokens as a breakdown bar, with
effort, initiator, duration and cost), rate limits (plan, primary and secondary windows with used
percentage, window length and reset time) and the provider's session totals. **Copy as Markdown**
copies the same content; **Refresh usage** reads again.

`sessionUsage.read` is an owned-only generated Desktop RPC for an existing actor and the actual host
epoch. It requires explicit project ID/path or global scope, checks the exact persisted session header,
confirms unarchived project ownership when applicable, and rechecks the original attachment after
asynchronous reads. It returns the last admitted usage event: numeric counters as decimal strings,
bounded single-line text (model, effort, initiator, labels, plan; anything longer than 128 characters
is dropped), rate-limit windows and the provider's cumulative session totals. Providers split usage
across events, so the window keeps the newest value of each field seen on the same attachment and
starts over when the attachment changes. Copilot quota snapshots, named Codex limits and compaction
details are not shown yet.

### Persisted event history

Selecting a session accumulates bounded pages of its persisted canonical events up to a 1,000-event
display limit. The chronological timeline folds duplicate stream completions and tool lifecycle/output
rows for presentation; this does not change persisted records. Tool cards identify the tool and primary
command/input. Prompt, usage, model and secondary event data use compact summaries and disclosures, and
internal raw persistence records duplicated by typed events are hidden rather than shown as empty cards.
Permissions and other stored requests are historical records, not actionable approvals. Previews can be
shortened explicitly; arbitrary provider payloads are not sent to the frontend.

Persisted user and assistant message bodies longer than 1,200 UTF-16 units initially show a plain-text,
inert excerpt of at most 240 units (not sliced/rendered Markdown). **Show full message** renders the entire
already retained body through the normal sanitized Markdown renderer; **Collapse message** restores the excerpt.
Both controls work with keyboard or pointer. Copy Markdown still copies all retained text while collapsed;
shortening/omission notices stay outside the disclosure. Copy success/failure feedback belongs only to the
current retained record and latest click; changing source or leaving the row clears it. An already dispatched
clipboard write cannot be undone. This is window-local per-record presentation state,
not a read for omitted text, a stored preference or a reconstruction of live output.
Equivalent refreshes and retained older-page records preserve intentional expansion. A changed body,
displayed record identity or omission evidence clears it, including a short-body interlude or A→B→A
replacement; returning to an earlier source does not restore its previous expansion.

### Markdown and useful HTML boundary

Timeline, live text and Notes use markdown-it **15.0.2** with the CommonMark preset,
raw HTML enabled, explicit pipe-table/strikethrough/linkify rules and hard line breaks.
This is not full GFM or a CommonMark-conformance claim. Task markers remain literal;
strike renders as `s` rather than `del`. Grid tables, footnotes, task-list plugins,
Prism highlighting/autoload and Mermaid rendering are not implemented. Footnote-like
syntax may parse as an ordinary reference link instead. Mixed inline HTML/Markdown
renders; Markdown inside block HTML follows CommonMark blank-line boundaries, not
arbitrary nested Markdown interpretation.

The browser-independent parser returns **untrusted HTML**, never directly injected.
Each mounted `MarkdownContent` owns its parser and DOMPurify **3.4.14** instance/hooks.
The explicit boundary retains paragraphs, breaks, headings, blockquotes, lists and
definition lists, pre/code, ordinary text formatting, links, span/div, authored
tables/captions/sections/cells, and details/summary. The precise tag list is
`p br hr h1 h2 h3 h4 h5 h6 blockquote pre code ul ol li dl dt dd strong em s del b i u
sub sup kbd samp var abbr a span div table caption thead tbody tfoot tr th td details summary`.
Attributes are limited to title, safe anchor href, bounded code language class,
details open, th scope, cell alignment/spans (1–100), and bounded list start/value/reversed.
Table alignment uses `align`, never authored CSS. Authored IDs, app classes, handlers,
ARIA/data/role/tabindex authority, scripts, styles, forms, frames, objects, embeds,
images/media and SVG/MathML are not accepted. Sanitized pre/code can receive only the
renderer-owned harmless timeline region attributes; no markup can create app controls.

Markdown images become **escaped alt text**; authored image/fetch nodes are removed.
Only credential-free absolute HTTP(S) anchor href survives; relative, mailto, custom,
executable and credentialed URLs do not. Fuzzy www/email/IP linkification is disabled.
Trusted container click, auxiliary-click and Enter handling suppresses link navigation;
there is no external opener or bridge action. Native context-menu and custom-origin
behavior remain separate qualification gates, not a promise that every native gesture
is suppressed. Raw source Copy, including CRLF and fences, remains independent of the
rendered display. Parser/sanitizer errors display inert original source, never exception
details. Equivalent source preserves rendered DOM/focus/selection/inner scroll.

Production CSP is unchanged and **`base-uri 'none'` remains required**: hostile base
markup can produce blocked base-assignment diagnostics during parsing/removal even
when final DOM is clean. Mounted production-component tests use fake in-memory HTTPS
assets, pre-navigation interception and 600 ms deferred observation; unexpected requests,
navigation or execution fail even if intercepted. This finite browser evidence is not
a security audit or equivalence to `app://codealta`/native WebView2. No CSP relaxation,
iframe workaround or blanket HTML escaping is used.

Code blocks in persisted timeline Markdown (including Markdown details) wrap within the card and
use at most 14 rendered text lines plus padding/border; short blocks keep their natural height.
All retained code remains selectable. Tab enters the labelled code region; arrows, Page Up/Down,
Home and End navigate it, and Tab/Shift+Tab leave it. A visible focus ring identifies the region.
Wheel/touch input scrolls code while it can move; at a boundary it may scroll the timeline and change
follow normally. Whole-message Copy still captures the full retained Markdown, not the code viewport.
Equivalent history refreshes preserve code focus, selection and position. This opt-in does not change
Notes, live Markdown, or the separate diagnostic Wrap/details control; native accessibility and live
streaming code-region behavior are not qualified by this slice.

Tool and file cards keep available persisted diagnostic/output detail text behind their collapsed **Details**
disclosure. Inside it, **Wrap lines** is on by default and can be switched per card with a pointer or keyboard;
turning it off scrolls long lines inside the detail pane. This only changes how the already loaded plain text
is displayed: Markdown and copied content remain unchanged, and omitted or shortened details remain marked.
There is no request for missing output, additional history, or live provider data when opening or wrapping a card.
Persisted FileChange activities label this disclosure **File change record details**, regardless of phase
or command presence. This identifies the supplied record, not applied changes, a complete diff or file-navigation authority.
Persisted ToolCall messages hidden by a primary command/query/path/prompt summary remain available inside
that disclosure as **Supplied activity message**, and in full retained-source Copy. The exact bounded message
is not interpreted as an output or verified outcome; existing phase labels and omission/shortening warnings
remain unchanged. Failed/no-summary messages still appear in the body without duplication. Live tool cards
continue to show only their supplied name, reported phase and identity.
Following the loaded timeline stays at the latest visible bottom across layout-only changes. Wheel, scroll-key,
scrollbar (including a held drag after a pause) and touch-drag navigation can still unfollow when it coincides
with a detail layout change; merely
clicking a disclosure/Wrap control or scrolling inside its detail text does not opt out. An explicitly unfollowed
reader is not moved to the newest journal window. This is not full TUI tool-output or timeline parity.

In the focused session workspace, `F3`/`F4` move among **persisted user and assistant
messages in the currently retained window** (not live projection, tool, reasoning,
unknown-kind or status cards); a live-only window does not enable these keys. `Ctrl+F3` moves
to its first retained message, which may not be the journal's first message. These
keys unfollow the timeline and report retained-window boundaries. They do not fetch
older pages or infer that a running session has finished persisting events. Use
**Load older history** to browse older pages. **Refresh newest history** and `Ctrl+F4`
explicitly read the newest persisted window (up to 1,000 events), including when
browsing an older page; only `Ctrl+F4` opts into follow after all matching pages
have settled. A failed or superseded read cannot claim success or move to an older
window's bottom. Scrolling, changing selection/host or another navigation action
cancels the pending keyboard follow intent; it does not cancel a completed read or
automatically retry. The bottom button follows the displayed window only and labels
older windows accordingly. This is a snapshot of the last successful explicit read,
not proof that a running provider has finished writing its journal.

Loaded-message navigation keeps keyboard focus and uses the current viewport or
retained DOM-row anchor. Unlike TUI index navigation, next-at-last remains paused
at the retained-window boundary; `Ctrl+F4` is an explicit read, not a loaded-last
jump. Composer/editor and modal shortcuts retain ownership. Browser coverage does
not qualify native keyboard delivery, screen readers, or full TUI four-key parity.

The shared reader limits page input to 256 KiB, individual records to 128 KiB and work to
100 physical records, plus bounded framing probes. Blank and metadata-only records count,
so an empty visible page can still offer continuation. UTF-8 journals with optional BOM
and LF/CRLF framing are supported; other legacy encodings/framing and oversized records
surface explicit limitations instead of unbounded reads. A malformed final record may be
omitted with a notice; interior corruption is an error. Existing complete-history readers
are unchanged.

Cursors belong to one session and journal length/time stamp. A detected change requires
restarting history from the beginning; it does not refresh the shared session catalog.
Length/time detects ordinary changes, not same-stamp rewrites or every external-writer
race. Selecting another session, changing page or closing the view cancels the waiter and
suppresses late results. These checks do not establish native shutdown safety, reparse
isolation, bounded catalog discovery, live recovery or full timeline parity.

## Explicit scoped owned text submission

The default launch already borrows command and cached-read services from one shared host. The following
experimental form provides the same ownership route with isolated explicit roots:

```text
alta --data-root <new-absolute-browser-directory> --catalog-root <existing-absolute-trusted-copy> --allow-catalog-cache --allow-owned-host --project-root <existing-absolute-project-directory> --discovery-home <existing-absolute-discovery-directory> --instruction-root <existing-absolute-project-ancestor> --builtin-skill-root <existing-absolute-directory>
```

All flags are required together. The added roots must exist, be absolute and stay outside
`.alta`; browser data must not overlap project/discovery/builtin roots. The instruction root
is an inclusive ancestor of the project. No profile or HOME inference is performed. These
lexical checks and the copy's `alta.lock` are not a reparse sandbox or race-free ownership proof.

**This consent is broader than browsing:** lock/project/journal/cache/provider-state writes,
configuration/instruction/skill reads and configured-provider registration are permitted.
Registration can read declared credential environment variables and shipped defaults; later
submissions can authenticate and use provider storage/network. Plugins and automatic provider probes remain off; explicit Models reads and Providers tests may probe.
Use only trusted task-owned roots, never a production profile or an untrusted copied cache.

Select an existing session to send text (32,768 UTF-16 units maximum). Tool permissions are automatically
approved by default, matching TUI AutoApprove. Commands and file writes run with the host's privileges;
project/discovery roots are not a sandbox. Explicit command-review mode below disables this default.
User input is cancelled unless separately opted in below, and this path supplies no custom tools or plugins.
Receipts are observed automatically; **Abort original Send operation** targets one pending send,
not a later run. Submitted means dispatch completed, not that the conversation/run completed.
Send and Abort retain up to 256 local intents combined, including their original live waiters,
across selection changes and remounts. Uncertainty keeps the exact epoch/key/session/text or
original Abort target for deliberate retry, never automatic resend. Receipt refresh cannot clear
an intent while its original waiter is live; Abort-only recovery does not erase unrelated composer
text. Late valid epoch mismatch disables mutations even after the old selection is cancelled.
Reload permits manual receipt browsing, not reconstruction of lost text/keys. Abort control
settlement is not rollback, decision retraction or run termination. The host's separate shared
receipt capacity remains 256, paged 64 at a time.

The composer visibly emphasizes **Cancel observed run** when an eligible point-in-time target
is available, keeping **Send** as a separate secondary button. This is not a live-running indicator.
A retained original Send keeps **Retry exact request** primary; retained run cancellation instead
reads **Retry exact cancellation**, preserving its original target and manual recovery. Neither
presentation changes nor cancellation clear the draft or initiate runtime reads. The two controls
keep separate identities and handlers: editor Enter still sends, Ctrl+Enter steers, and in the expanded
editor Enter inserts a new line while Escape or Ctrl+Enter closes without sending. Cancellation
signalling does not establish run completion.
On narrow screens the labelled controls wrap rather than clip; exact-target evidence and the
separate **Abort original Send operation** remain available.

Add **`--review-owned-command-permissions`** to the complete owned-mode command above to opt
into manual review of supported plain command requests. The selected-session review shows the
complete command, working directory and optional reason, with **Allow once / Deny / Cancel**.
After a manual refresh, deliberately choose **Review command permission** to open the native
HTML dialog for that exact observed entry. Opening or dismissing it performs no read or decision;
Close/Escape dismisses presentation, unlike the explicit **Cancel** permission decision.
Pending and uncertain original outcomes remain retained when the dialog closes.
Refresh pending commands manually; this is not a notification stream. Unsupported permission
payloads remain denied, this review flag alone leaves user input cancelled, and there is no Allow for Session option.
Approval can execute a command with the host's privileges: discovery roots are not a sandbox.
Changing selection does not cancel a pending permission or its original decision-response wait.
Use **Observe retained decision** to check that response locally, labelled with its original session
and complete handle; it does not contact the host or resend a decision. A pending observation does
not unlock another decision. Explicitly observe a terminal response, then refresh for a fresh review
before deciding again; merely displaying the result does not acknowledge it. Acceptance is not proof
of execution, and rejection does not identify an earlier decision. Genuine uncertainty or epoch
invalidation disables review across selections until renderer reload. Reload loses the local record;
then manually refresh still-pending requests under the existing opt-in. An empty list cannot recover
a lost decision, and host restart restores no old permission authority. No decision is replayed.
Aborting the owning submission or closing the application invalidates still-pending requests,
but cannot revoke a decision already accepted by the backend. Native/provider qualification and
the broader permission, file-review and ask workflows remain incomplete.

Owned submissions also enable restricted caller-session **`alta ask --stdin`** questions; ordinary
host composition still defaults to no owned asks. This does not expose the general LiveTool command
dispatcher or activate provider user-input requests. In **Pending asks**, use **Refresh asks** to
read the original retained head, then **Answer original ask** or **Cancel original ask**. An answer
starts a new normal text submission to the original session with the original AskId. Cancel removes
only an unclaimed pending ask; it does not stop the producer run or an admitted answer. A committed
ask can outlive its producer. Requests and answers each have an aggregate 8,192 UTF-16-unit text limit.
For a validated multi-question head, **Previous question** and **Next question** show the current
position/title and move between questions without wrapping. The panel retains every question's
local choices and text when switching and on same-head refresh; only an explicit **Answer original
ask** submits all question indexes, including unvisited/empty answers under existing validation.
With focus in the current question's input or the question-navigation buttons, Ctrl+N/P moves
through available questions using those same guarded controls; at boundaries, for single-question
asks, and everywhere else (including Answer/Cancel, Refresh, dialogs and the composer), browser
shortcuts retain their defaults. Navigation never submits, reads or acknowledges an action, and
does not implement the TUI's SubmitOrAdvance command. A changed head resets the visible question
rather than rebinding an old draft; single-question asks keep the original simple editor.
Unsubmitted text and selected-choice drafts survive explicit refreshes of the same validated
pending head, including edits made while its read is in flight. The editor compares the full
handle and all validated question, choice and freeform fields, not just AskId; answer/cancel
remain disabled while a refresh is pending. A missing, failed, malformed or replaced head leaves
its former unsent draft visible only as local read-only recovery, never as an answer for another
head (even if an earlier shape returns). A two-step **Discard local draft** removes only that
local copy, not a captured backend action. At most eight drafts are kept by the mounted panel;
discard recovery to free capacity. These drafts are **component-lifetime only**: navigating
away, archival or closing the window unmounts the panel and loses unsubmitted drafts. A different
host epoch/session cannot see another scope's local recovery. This adds no reads beyond the
existing mount/selection read and explicit Refresh; it adds no persistence, retry, rebind or
submission route.
After an answer is captured, the panel exposes its **original immutable action** separately
from those discardable local drafts: full captured freeform text, question/choice indexes and
original host epoch, session, complete handle and action ID remain inspectable during pending
and uncertain transport, including failed or changed list reads and same-host panel remount.
Question/choice wording is not stored in that action and is never invented from a later head.
Discarding a local draft cannot remove or acknowledge a submitted answer. An actual failed
list read is reported as failed, not as a still-pending refresh; neither state permits stale
submission. Archived scopes continue using their existing read-only recovery gate.

Original actions survive selection changes and panel remounts. An eight-second timeout permanently
marks the original transport result uncertain and prevents competing actions; it is not proof of
failure to submit. **Observe original action** reads separate backend evidence without resending or
rewriting that uncertain result. Reload can read same-host retained heads/dispositions but cannot
reconstruct lost action intent; absence is not acknowledgment. Host restart restores no authority.
The host retains at most 256 asks and 256 action records without evicting uncertain evidence. No
attachments, file review, target override, polling or automatic retry is provided. Qualification
uses isolated inert providers, not native UI or configured-provider workflows.

Add **`--enable-owned-user-input`** to the complete owned-mode command for **Nonsecret provider input**.
It is off by default, requires owned mode, and is independent of command review and restricted asks.
Command permissions follow the host's auto-approval or explicit review policy. **Never enter passwords, tokens or other secrets**:
literal answers may persist in provider tool results and history. Unsupported/secret/oversized forms are
cancelled as a whole; this provides no file review or credential-entry workflow.

Use **Refresh input** to list up to four pending forms, then deliberately choose **Review provider input**
for one exact observed form. The native HTML dialog shows complete literal questions/options and handle
provenance. Select an offered option or explicitly enter freeform text for every prompt, then
**Submit literal answers**, or **Cancel this attempt only**. Close/Escape dismisses presentation only,
without reading, answering or cancelling. Local edits survive ordinary close/reopen only for the same
validated form and lifetime; refresh, another form, scope changes and stale modal controls discard them.
They are not persisted or retained across sessions. Bounded fake-host/browser qualification passes;
native WebView2 and real-provider qualification remain open.
No answer is silently filled in. Answers are limited to 2,048 UTF-16 units each and 8,192 in aggregate.
An accepted response is an owner decision, not proof of provider continuation or persistence success.

Selection changes and panel remounts retain the original action and prevent competing actions. Use
**Observe original locally (no RPC)**, then **Acknowledge observed terminal original**; a fresh explicit
list is required before acting again. Genuine uncertainty cannot be acknowledged away or replayed.
Renderer reload can re-list still-pending host attempts but loses local action records; an absent attempt
does not reveal a lost outcome. Closing the application or cancelling the original operation/run
invalidates pending attempts; host restart restores no old input authority. Native UI and configured-provider
qualification remain incomplete.

Each open owned-host session has a read-only **Alta notes** overlay at the top right above its timeline.
It travels with that session pane, starts collapsed when empty, and can be expanded/collapsed with its
labelled buttons or the existing notes shortcut. It does not consume composer space. It reads current durable notes
automatically, refreshes every ten seconds after the previous read completes, and supports explicit
**Refresh notes**. It displays the latest stored notes as sanitized Markdown. Complete notes up to
16,384 UTF-16 units are shown without truncation; larger or invalid text produces an error. Empty
notes, cleared notes and no notes event share the same empty result; a failed read is distinct.
There is no editing, automatic mutation retry or browser persistence. This feature does not require ask opt-in.

Selection/remount changes detach presentation but retain the original read. While it is pending,
another local refresh is refused; after failure, the next refresh is a new read, not a recovered write
outcome. Host identity change requires reload before further operations. Notes shares the host's
eight actual workspace/history reads, and a cancelled/timed-out wait does not release a still-running
backend read. The display limit does not bound journal scanning or latency: the first notes read of a
session after host start scans its whole journal. Later reads reuse that scan, so the ten-second refresh
of an unchanged journal does not open it and a grown journal is read only past the records already
scanned. A notes scan does not hold the journal gate, so it does not delay timeline pages or event
appends for the same session. Reload can perform a
fresh durable read without restoring run/queue/interaction authority.

With a provider supporting run-bound review, cancellation of the actual owning run also invalidates
its pending requests, even when individual requests omit a run ID. A request naming another run is
denied. Previously accepted decisions remain accepted; trusted TUI approval policy is unchanged.

The compact prompt toolbar offers **Cancel observed run** (square icon with an accessible label)
only for an eligible point-in-time runtime observation or a retained exact cancellation intent.
Send and exact Send recovery remain separate. This action is distinct from **Abort original Send
operation**, which controls its receipt, not the running provider. The observed target names the
exact epoch, session, runtime, attachment and run; it is not inferred from host availability or
display state. Unsupported, stale, retiring, transitioning and draining targets fail closed
without fallback. Refresh submissions
for the result: **Cancellation signalled; run completion is not confirmed.** Failure can occur
after signalling; neither failure nor success promises rollback of accepted decisions or effects.
An uncertain request retains its original target/key across selection changes and remounts.
Reconcile manually or deliberately **Retry exact cancellation request** from the toolbar after the
previous wait settles; its exact target and key remain visible next to the composer and in advanced
diagnostics. New observations never retarget it, and replay never repeats cancellation. Closing the
panel cancels only its wait; host shutdown retains and joins original work. No automatic retry,
refresh or polling is added. This remains experimental, not full session-command/native parity.

The selected session also has a **live status/text window**, separate from submission receipts.
Unmatched retained text and reported tools appear after the persisted journal rows in the same
timeline, with a compact status/coverage indicator instead of an empty live panel. A run-scoped
completion with an unambiguous matching provider replaces its live copy after manual history
refresh; uncertain identities stay visible rather than being guessed away. The live projection
has no timestamps or shared text/tool ordering, so its tail is **not** an exact chronological
interleaving, a history watermark or automatic journal refresh. The window shows retained
lifecycle, queue count, configuration labels
and up to eight text items, plus two most recently updated **reported plain ToolCall activities**.
Tool names may be shortened; arguments/results are not shown. Reported phases can regress, and
missing/evicted activity is unknown—not idle or complete. Started may precede permission resolution
and proves neither approval nor process launch; other phases are not run-completion or exactly-once
effect acknowledgments. This is not a complete transcript, tool-results/usage view or interaction UI.
No retained state means not yet observed or evicted—not idle or completed. Text and labels may
be shortened, and replacement/eviction indicators make omissions explicit. **Reconnect live
display** explicitly starts a new observation; it never resends a prompt. A stale host epoch
explicitly requires reloading the UI, not reconnecting with the old identity. Selection changes,
renderer detach and cancellation close only the observation, not a run or the host. Owned mode registers this channel, including the normal no-argument startup; the explicit catalog-only route does not.
Same-host reload obtains a new baseline of retained partial values only, not omitted history,
tool results, permission decisions or effects. Host restart restores no prior authority.

Live text **Copy Markdown** captures the full currently retained text at the explicit click,
including text marked as truncated or missing an earlier prefix; copying does not recover those
omissions. **Copied** / **Copy failed** feedback belongs only to the latest copy request for the
current row identity, text and completion/omission flags. Streaming changes and row removal clear
feedback timers; late clipboard results or queued old resets cannot overwrite newer feedback.
Failures are announced without exposing clipboard exception details. This fences presentation,
not the clipboard write itself: an already dispatched write cannot be undone by these guards.

Rapid selection/reconnect requests retain only the latest desired observation. Reopening waits for
the previous iterator's successful cleanup. If cleanup fails, **Reconnect live display** is blocked
and the failed owner is retained; renderer reload does not prove that old backend work terminated.
Valid late host/runtime identity changes disable shared mutation controls even after selection changes.

Live file-change/diff notifications invalidate the host's shared file-search cache even without a
terminal reader or when its event stream drops a notification. History replay and Display reconnect
do not repeat that cache effect. This best-effort dirty mark does not scan files or confirm a write.
Live plugin-effect observation now also belongs to the shared runtime rather than a terminal event
reader; shared history/live recovery remains separate work.

**Refresh runtime state** is a separate manual, point-in-time observation of the actual runtime
entry, coordinator transition, recorded run, Shutdown, retirement and queue-drain facts. It does
not poll or automatically refresh; steering uses its captured target, subject to host revalidation. No entry/no recorded run is not
idle or completed, and queue depth is explicitly unknown. Configuration is runtime-captured, not
verified provider-effective settings; pending prompt selection is shown separately. This query
does not read catalogs/history or start/discover providers. Its runtime instance/attachment
identity is not a Display revision, effect acknowledgement or history-recovery handshake.
Host epoch and selection are checked; obsolete presentation is discarded, but valid late identity
changes still require reload rather than retrying the old epoch. Repeated explicit refreshes retain
one frontend waiter and the latest pending refresh, not an unlimited set of overlapping requests.
This is not a bound on actual backend work. Malformed/oversized output is refused
without truncating authoritative fields (32 KiB transport budget, 256 UTF-16 units per identity/
configuration string, decimal-string attachment generation). Canceling a wait does not cancel
admitted runtime-owned work. This unary RPC is registered only in explicit owned mode.

After **Refresh runtime state**, **Steer observed run** submits text to that exact runtime,
attachment and recorded non-null run. It never creates or replaces a runtime, falls back to a
send/queue, or silently targets a later run. The host rechecks ownership and target identity;
stale, retiring, transitioning, terminated or unsupported targets fail rather than retarget.
When an eligible run is observed, the compact composer also offers a labelled **Steer current composer**
icon beside Send; it uses the same current-draft action as `Ctrl+Enter` and is disabled for empty text,
retained steering or unavailable mutation authority. It does not retry a retained request; use the
separate steering controls for explicit exact-target recovery. A pending Send is not an editable steering draft.
Steering preserves the existing run's permission authority and cannot reopen a closed review
window. Text is limited to 32,768 UTF-16 units. Success means input submitted, not run completed.
An uncertain steering request retains its immutable target, key and text across selection changes.
Use **Refresh submissions** to reconcile it or **Retry exact steering request** deliberately;
there is no automatic retry. A fresh runtime observation does not change that retained target.
Only one steering dispatch per session is in flight, independently of an owned send; steering
shares the host's 256-receipt limit. Closing presentation cancels only the waiter, while host
shutdown retains and joins accepted steering and cancellation work.

The compact prompt toolbar offers **Compact observed idle attachment** (or `Ctrl+F11` from the
selected owned workspace/prompt) only for an eligible point-in-time runtime observation or a
retained exact compaction intent. Send, exact Send recovery and observed-run cancellation remain
separate. An uncertain intent shows its original epoch, session, runtime, attachment and request
key next to the composer; deliberate **Retry exact compaction request** never retargets after
refresh or session switch and waits for the original waiter to settle. No polling or automatic
retry occurs. The action requires no recorded run or queue drain. That is only eligibility: the
host and supported provider must admit compaction without waiting for active work. Unsupported
or stale targets fail without fallback, discovery or replacement. Compaction summarizes the
attachment's context **at provider admission**, not a history snapshot captured by the UI, and
may use the configured model/network and persist context changes. It creates no new permission
authority. One compaction per session
may be in flight and shares the 256-receipt limit. Refresh submissions for the settled outcome;
busy, unsupported and unsuccessful compaction are not success. Uncertain requests survive
selection changes for manual receipt reconciliation. Replaying a busy receipt does not try again:
a new explicit action uses a fresh key. Closing the panel cancels only the wait; shutdown retains
and joins accepted compaction and cancellation work.

**Queue text — this host only** uses an explicitly refreshed runtime/attachment observation, including
busy/draining attachments. `sessions.queue` reserves exact text for that attachment only; the receipt
separately reports reservation, host-only insertion and execution/cleanup. `queue_accepted` is not
durable or executed, and `queue_dispatched` is not proof of run completion. When the attachment is
eligible, the compact composer also offers **Queue current composer in this host**. It captures
the current editable draft for that observed attachment (idle or active, never a run target) using
the same queue owner as the separate editor. A pending Send or retained queue request disables it;
only the separate controls can manually retry the original queue key, attachment and text. The
composer draft stays editable and is **not cleared by owner reservation**, because that reservation
does not establish insertion, durability or execution; even newer draft edits are preserved. The
secondary queue editor keeps its own volatile draft per host epoch/session across panel selection
changes. While a queue request is retained, that disabled editor shows the exact request text;
after manual retry or receipt reconciliation of a composer-originated request, its separate draft
returns unchanged. A successful direct secondary-editor submission clears only that editor's
submitted draft if it has not been edited since capture, never a distinct later edit. These drafts
are not persisted across document reload or host-owner replacement. Refresh receipts manually to
inspect distinct phases.
The regular owned composer shows a compact **Retained requests** strip only while this app's
current host epoch/session has an exact queue or steering owner intent. Expand each request to
inspect its full retained text and original epoch, session, runtime, attachment, request key and
(for steering) run; **Copy text** copies the exact retained text to the clipboard or reports
unavailable/failed access without editing either draft. Pending means an exact-request waiter is still active; outcome unknown/manual recovery
means it did not establish a definite result.
The strip is not an authoritative host queue or receipt list and cannot establish insertion,
durability, execution or run completion. Opening or copying does not refresh, admit, retry,
clear or acknowledge anything. Use the existing explicit advanced receipt/recovery controls
to investigate uncertainty; archived sessions retain their separate read-only recovery view.
**Cancel this queued operation** uses `sessions.cancelQueue` with the original operation ID,
never a later/current run.
Cancellation signalling is not rollback or target cleanup completion. Uncertain queue/cancel intents
retain exact keys and targets in App memory across selection/remount, with manual refresh/retry only
and a combined 256-intent bound. Cancellation-only recovery preserves unrelated queue draft text.
After document reload, receipts can be browsed manually, but lost local text/keys are not reconstructed.
No durable/restart recovery is provided. Existing terminal/live-tool queues and permission policy remain separate.

Actual cached-store reads are host-owned (eight active reads, excess rejected); cancelling an
RPC wait does not stop them. Shutdown joins command/read work before runtime dependencies.
Five seconds triggers a pending diagnostic, not termination. Unconfirmed host/native cleanup
keeps the lease; external termination is not confirmed cleanup. Managed fake-provider and
helper tests pass, but configured-provider behavior, React mounting and this mode's native
lifecycle remain unqualified. See [runtime contracts](../../doc/runtime.md#explicit-desktop-submissions-and-owned-reads).

Build outputs use the standard SDK `bin/<Configuration>/<TFM>` and `obj/<Configuration>/<TFM>`
layout. Generated frontend contracts/client remain build-only inputs under `obj/neoastra`.

The desktop RID intent is Windows, macOS and glibc Linux x64/ARM64 (six RIDs, **no musl**).
Only Windows x64 has executed native evidence so far. Ordinary `Exe` output and the Common
Controls v6 manifest are retained; redirected CLI success does not qualify interactive
console/Explorer behavior. See [native qualification](../../doc/desktop-native-qualification.md)
and [test instructions](../CodeAlta.Desktop.Tests/README.md).
