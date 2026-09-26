# CodeAlta desktop (in development)

`CodeAlta` is the native desktop .NET tool (`alta`), built with published NeoAstra 0.1.0,
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
bottom-left project rail (or a supported shortcut/palette action), and
switch themes. Demo messages disappear on refresh. `npm run build:demo` produces the same preview
as static files under `dist/`; `npm run build` builds the production NeoAstra-connected frontend.
The packaged desktop uses the generated bridge and never includes the demo backend.

Settings opens a modal overlay approximately 80% of the desktop viewport; the selected session,
composer draft and timeline remain mounted underneath but cannot be interacted with while it is
open. Use the overlay's sections and Back to settings control, Escape or Close settings to return;
at narrow widths it uses viewport margins and scrolls internally. Configuration, Providers,
Models, Agent prompts, MCP Servers and Logs are **not** workspace tabs. Future session/file tabs
are separate; no multi-workspace tabbing is implemented here. Reminders remains a guarded
selected-session tool, not a Settings section.

**Settings → Overview → General → Appearance & navigator** manages the local dark/light theme,
project sorting and desktop project-rail collapse. The rail's Sort projects selector and
Show/Hide projects button use the same live preferences; changes apply immediately and are
saved only to this WebView's local storage (theme, projectSort and projectRail v1 keys).
If storage is invalid or unavailable, the screen reports the fallback; if a write fails,
the change applies in this window but is **not** reported as saved. Recent visible updates
uses only verified saved session timestamps in the visible snapshot, **not** the TUI's
complete last-active order: missing, unverified or truncated evidence cannot establish
recency, and undated projects follow name/ID order. Narrow-screen Show projects is a
temporary reveal independent of the desktop collapse preference. These controls do not
change the selected project/session, draft, requests, pane widths or timeline position.
The TUI's recent-session count, language and command approval policy are **not** configurable
from this desktop screen; this is not full General/Navigator parity.

**Settings → About** and the implemented-actions palette open the same read-only About dialog
within the Settings overlay. In the packaged desktop it reports the product and informational version
from the running host's boot response, with a separate build field only for a recognized
version `+metadata` suffix. Missing or invalid/overlong fields are marked unavailable,
rather than replaced with an advertised version. The dialog distinguishes a browser demo,
a pending/failed boot, an owned host and catalog-only browsing; it is not a runtime health check. Inspecting it
does not fetch updates or read files, send data, or write configuration. TUI update checks,
downloads and installation are **not supported** in this desktop view; no update status is known.
Closing About or the palette returns focus to its connected opener only when no newer focus move, navigation
or modal has taken precedence.

**Settings → Logs** (also in the implemented-actions palette) offers an
explicit **Refresh logs** and a **Wrap lines** toggle. It displays at most 64 newest
plain-text rows from this desktop process's bounded in-memory capture (128 rows and
128 KiB estimated UTF-8 payload budget, 2,048 characters per message, and a 48 KiB
JSON response cap). Older captured entries and response-limited rows are reported;
long messages are marked truncated. Logging keeps its existing file writer, levels,
rotation and lifetime, but this screen **never opens log files**: it cannot show earlier
processes, all file records, or exception attachments/structured properties. If another
owner initialized logging first, in-memory capture is unavailable and no attempt is made
to replace that logger; the browser demo also has no desktop capture. An available,
nonempty snapshot offers **Clear captured messages…** with a typed confirmation.
The request is tied to this capture's identity and that explicit snapshot's high-water
boundary; it removes only captured in-memory messages through that boundary, including
older entries already omitted by capacity or the bounded read. Newer messages appended
after the snapshot survive. Confirmed counts report remaining captured rows removed
and older capacity-omitted entries covered separately; read omissions are not counted
as lost messages. Refresh remains read-only, and Wrap lines is unaffected. Pending or
uncertain clear requests retain their original identity and boundary across screen
switches; a failed/mismatched reply cannot be retried or unlocked by Refresh. The
application must be restarted to discard uncertain in-process evidence. Clear never
alters persisted log files, the rolling writer, configuration, or logging lifetime;
it cannot clear another process's capture or an unavailable/external logger. No
automatic polling, export, file deletion or logging-policy controls are provided.
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

No-argument startup derives a stable WebView data directory from the platform's local application-data
location, composes the owned host for the current directory, and uses the current `~/.alta` catalog.
The host may update the standard project catalog, journal, cache, provider state and SQLite sidecars;
submissions may authenticate or use configured provider storage/network. It adds no desktop-specific
state to `.alta` and performs no storage migration. Help/version and rejected arguments do not initialize
native services or create storage. Explicit catalog and scoped-owned options retain stricter validation.

The **Models** Settings section (or `Ctrl+G`, then `Ctrl+O`) opens a read-only model catalog in owned
mode. Select a registered provider to request its actual host-reported models; search their names,
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

The **Providers** Settings section (or `Ctrl+G`, then `Ctrl+R`)
lists at most 32 configured providers with adapter type, enabled/default settings, configured
default model and **cached** host initialization availability. Opening the section or selecting a row
does not probe. **Test selected provider** explicitly starts only that enabled provider through
the shared initialization service; it can use configured provider storage or network in an owned
launch. The result is a completed initialization/probe, not proof of authentication or a live
connection, and arbitrary provider error messages/URLs/categories are not shown. An abandoned
probe is still joined by the host before disposal; do not assume its outcome from a timed-out
browser request. Catalog-only mode lists saved descriptors read-only without runtime tests.
Provider enablement, defaults, authentication and configuration writes are not available here;
they require a separately verified safe persisted-source mutation contract.

The **Agent prompts** Settings section (or `Ctrl+G`, then `Ctrl+H`)
shows effective prompts discovered by the shared host catalog for the exact selected owned
session. It shows name, ID, description, effective source scope and a read-only excerpt of the
effective agent prompt body (up to 2,048 characters per prompt). An appended prompt may include
lower-precedence source content; source paths and system prompts are not exposed. The inventory
is capped at 64 prompts and 96 KiB serialized, and identifies omitted or truncated content.
Built-in prompts are read-only; no prompt editing or source storage is available here. Catalog-only
mode and the browser demo have no owned prompt inventory. The recorded prompt and next-Send
prompt are shown separately. Selecting a prompt validates fresh session choices and changes
only that exact session's next Send, retaining its model/effort; pending exact Send, stale host,
session changes and unavailable prompts cannot apply. Create/edit/delete remain TUI workflows.

The **MCP Servers** Settings section shows fixed-file global configuration and the selected owned session's
catalog-resolved project overlay. It displays at most 64 safe server identifiers, transport,
effective configured policy enabled state (merged MCP enabled AND server-local enabled) and
project/global override evidence. Missing or
unreadable config sources, policy read failure, and omitted definitions are disclosed without
paths or raw diagnostics. Configuration is **not** a connection or tool-availability check:
Desktop plugins remain off. This screen is read-only; connect, tools, add/edit/delete and
runtime lifecycle are not implemented. No MCP inventory is available in catalog-only mode.

The **Reminders** session tool is available only with a selected owned session. It lists that session's
active and completed attempts, lets you create a delayed Markdown prompt (whole seconds 1–86400,
1–20 attempts), refresh the list, and delete only after typing the exact reminder ID. At most 32
retained reminders per session and 256 per host are accepted; deleting a completed entry frees a slot.
The compact owned composer displays `?` for an unknown active count, or a number from a successful
bounded list for the exact selected host and session. The accessible icon label says **active at last
observation; may have changed**: completed schedules are excluded, and timer completion or changes
after the read are not tracked live. The composer reads once when opened; **Refresh reminder count**
under Advanced session controls requests a new observation (not a schedule mutation). Errors,
incomplete/mismatched responses, changed host/session/scope, invalid catalog identity and pending
or uncertain reminder admissions leave the count unknown until an eligible explicit refresh. No
polling or automatic retry is performed. The timer icon and `Ctrl+G`, `Ctrl+D` open Reminders as before.
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
control. `Delete` from non-editor focus only focuses the existing exact-ID confirmation field; it
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

The **Commands** button or `Ctrl+P` outside editors and dialogs opens a searchable palette of
implemented navigation and inspection actions. Arrow keys choose a result, Enter opens it and
Escape closes the palette. Session Info requires a verified selected session; Reminders requires
an exact owned session with a mutable host. Selection-sensitive actions are rechecked on activation.
This palette also opens About; it does not expose Send, deletion, configuration writes, Skills or plugin management,
and is not the full TUI command/shortcut set.

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
In **Open project** (`Ctrl+O`), search the saved-project list by name or full path; use Up/Down
and Enter or click a row. It uses deterministic name order, **not** last-active or recent order.
Navigation requires the row's unique, unchanged ID/path/name/archive state in the current bounded
snapshot. It selects existing sessions without importing, creating a runtime, or discarding their
drafts. Archived projects are labeled and their sessions open read-only; a catalog-only launch
can also navigate saved projects read-only. **Details** in the selected project's Sessions
header opens a read-only modal with only the current bounded catalog's ID, display name,
full recorded path and archive flag. The global Other sessions root has no project details;
missing, duplicated or changed ID/path rows cannot open it. A failed/pending catalog refresh
disables inspection until a successful fresh snapshot, and project, session or host changes
dismiss stale details. The dialog discloses partial/shortened snapshot evidence; it has no
authoritative total session count or branch, tags, description or source metadata. Copy ID
and Copy path are explicit, with success, denied or unavailable clipboard feedback; no
catalog/filesystem reads, metadata writes or project actions occur on opening.

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

### Read-only owned usage RPC and compact composer inspector

`sessionUsage.read` is an owned-only generated Desktop RPC for an existing actor and the actual host epoch.
It requires explicit project ID/path or global scope, checks the exact persisted session header using a
bounded first-line read, confirms complete unarchived project ownership when applicable, and rechecks
the original attachment after asynchronous reads. Statuses distinguish unknown, unavailable,
transitioning/closed/stale and read failures from a real zero. It returns only bounded last-observed
typed numeric usage, source/timestamps, omission/invalid flags and decimal-string 64-bit counters; no
history, provider probe, catalog limits, inferred totals or raw paths/details. External catalog and
journal changes are checked point-in-time, not with a cross-process atomic guarantee. There is no
full/native usage parity yet. A compact composer usage icon appears only for an exact, uniquely verified
owned writable project/global session in the untruncated catalog. Opening its HTML read-only dialog
or pressing **Refresh usage** performs one explicit `sessionUsage.read`; it never polls or probes providers.
Values are labeled **last observed**, with nullable reported window/last-operation fields, source/scope/
timestamps, attachment, invalid/omission evidence and safe unavailable states. Long counters display
exact decimal strings; absent fields remain unknown, not zero, and cost has no inferred currency. Closing,
changing scope or host, and replacing the attachment discard prior presentation rather than merging totals.
The dialog does not establish current context, a complete history, atomic ownership against external
writers or native/full TUI parity.

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

Tool and file cards keep available persisted diagnostic/output detail text behind their collapsed **Details**
disclosure. Inside it, **Wrap lines** is on by default and can be switched per card with a pointer or keyboard;
turning it off scrolls long lines inside the detail pane. This only changes how the already loaded plain text
is displayed: Markdown and copied content remain unchanged, and omitted or shortened details remain marked.
There is no request for missing output, additional history, or live provider data when opening or wrapping a card.
Following the loaded timeline stays at the latest visible bottom across layout-only changes. Wheel, scroll-key,
scrollbar (including a held drag after a pause) and touch-drag navigation can still unfollow when it coincides
with a detail layout change; merely
clicking a disclosure/Wrap control or scrolling inside its detail text does not opt out. An explicitly unfollowed
reader is not moved to the newest journal window. This is not full TUI tool-output or timeline parity.

In the focused session workspace, `F3`/`F4` move among **persisted user and assistant
messages in the currently retained window** (not tool/status cards); `Ctrl+F3` moves
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

Select an existing session to send text (32,768 UTF-16 units maximum). Permissions are denied by
default, user input is cancelled unless separately opted in below, and this path supplies no custom tools or plugins. **Refresh
submissions** explicitly retrieves receipts; **Abort original Send operation** targets one pending send,
not a later run. Submitted means dispatch completed, not that the conversation/run completed.
Send and Abort retain up to 256 local intents combined, including their original live waiters,
across selection changes and remounts. Uncertainty keeps the exact epoch/key/session/text or
original Abort target for deliberate retry, never automatic resend. Receipt refresh cannot clear
an intent while its original waiter is live; Abort-only recovery does not erase unrelated composer
text. Late valid epoch mismatch disables mutations even after the old selection is cancelled.
Reload permits manual receipt browsing, not reconstruction of lost text/keys. Abort control
settlement is not rollback, decision retraction or run termination. The host's separate shared
receipt capacity remains 256, paged 64 at a time.

Add **`--review-owned-command-permissions`** to the complete owned-mode command above to opt
into manual review of supported plain command requests. The selected-session review shows the
complete command, working directory and optional reason, with **Allow once / Deny / Cancel**.
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
Commands remain denied unless independently reviewed. **Never enter passwords, tokens or other secrets**:
literal answers may persist in provider tool results and history. Unsupported/secret/oversized forms are
cancelled as a whole; this provides no file review or credential-entry workflow.

Use **Refresh input** to list up to four pending forms. Select an offered option or explicitly enter
freeform text for every prompt, then **Submit literal answers**, or **Cancel this attempt only**.
No answer is silently filled in. Answers are limited to 2,048 UTF-16 units each and 8,192 in aggregate.
An accepted response is an owner decision, not proof of provider continuation or persistence success.

Selection changes and panel remounts retain the original action and prevent competing actions. Use
**Observe original locally (no RPC)**, then **Acknowledge observed terminal original**; a fresh explicit
list is required before acting again. Genuine uncertainty cannot be acknowledged away or replayed.
Renderer reload can re-list still-pending host attempts but loses local action records; an absent attempt
does not reveal a lost outcome. Closing the application or cancelling the original operation/run
invalidates pending attempts; host restart restores no old input authority. Native UI and configured-provider
qualification remain incomplete.

The selected owned-host session also has **Current durable notes — read only**. Use **Refresh notes**
explicitly; selecting a session does not automatically read it. It displays the latest stored notes
in journal order as literal text, not live progress or rendered Markdown. Complete notes up to
16,384 UTF-16 units are shown without truncation; larger or invalid text produces an error. Empty
notes, cleared notes and no notes event share the same empty result; a failed read is distinct.
There is no editing, automatic retry or browser persistence. This feature does not require ask opt-in.

Selection/remount changes detach presentation but retain the original read. While it is pending,
another local refresh is refused; after failure, retry is a new explicit read, not a recovered write
outcome. Host identity change requires reload before further operations. Notes shares the host's
eight actual workspace/history reads, and a cancelled/timed-out wait does not release a still-running
backend read. The display limit does not bound journal scanning or latency. Reload can perform a
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
