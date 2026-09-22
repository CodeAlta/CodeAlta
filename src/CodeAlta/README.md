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

Select projects and sessions, send messages in the local composer, visit **Configuration**, and
switch themes. Demo messages disappear on refresh. `npm run build:demo` produces the same preview
as static files under `dist/`; `npm run build` builds the production NeoAstra-connected frontend.
The packaged desktop uses the generated bridge and never includes the demo backend.

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
The shared catalog caches its snapshot, and closing/reloading the document is not a refresh
or run-abort contract. Relaunch with a fresh browser data directory to load another snapshot.
Canceling an RPC waiter does not stop the shared catalog's background load. Existing RPC
teardown waits only a bounded time, so cache work can outlive bridge teardown; complete
native lifecycle qualification remains open.

### Persisted event history

Selecting a session accumulates bounded pages of its persisted canonical events up to a 1,000-event
display limit. The chronological timeline folds duplicate stream completions and tool lifecycle/output
rows for presentation; this does not change persisted records. Tool cards identify the tool and primary
command/input. Prompt, usage, model and secondary event data use compact summaries and disclosures, and
internal raw persistence records duplicated by typed events are hidden rather than shown as empty cards.
Permissions and other stored requests are historical records, not actionable approvals. Previews can be
shortened explicitly; arbitrary provider payloads are not sent to the frontend.

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
submissions can authenticate and use provider storage/network. Plugins and probes remain off.
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

After **Refresh runtime state**, **Signal cancellation for observed run** targets only that exact
runtime, attachment and run. This is distinct from **Abort original Send operation**. Unsupported, stale,
retiring, transitioning and draining targets fail closed without fallback. Refresh submissions
for the result: **Cancellation signalled; run completion is not confirmed.** Failure can occur
after signalling; neither failure nor success promises rollback of accepted decisions or effects.
An uncertain request retains its original target/key across selection changes and remounts.
Reconcile manually or deliberately **Retry exact cancellation request** after the previous wait
settles. New observations never retarget it, and replay never repeats cancellation. Closing the
panel cancels only its wait; host shutdown retains and joins original work. No automatic retry,
refresh or polling is added. This remains experimental, not full session-command/native parity.

The selected session also has a **live status/text window**, separate from persisted-history
browsing and submission receipts. It shows retained lifecycle, queue count, configuration labels
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
Steering preserves the existing run's permission authority and cannot reopen a closed review
window. Text is limited to 32,768 UTF-16 units. Success means input submitted, not run completed.
An uncertain steering request retains its immutable target, key and text across selection changes.
Use **Refresh submissions** to reconcile it or **Retry exact steering request** deliberately;
there is no automatic retry. A fresh runtime observation does not change that retained target.
Only one steering dispatch per session is in flight, independently of an owned send; steering
shares the host's 256-receipt limit. Closing presentation cancels only the waiter, while host
shutdown retains and joins accepted steering and cancellation work.

**Compact observed attachment if idle** uses the same manual runtime observation, but requires
no recorded run or queue drain. That is only eligibility: the host and supported provider must
admit compaction without waiting for active work. Unsupported or stale targets fail without
fallback, discovery or replacement. Compaction summarizes the attachment's context **at provider
admission**, not a history snapshot captured by the UI, and may use the configured model/network
and persist context changes. It creates no new permission authority. One compaction per session
may be in flight and shares the 256-receipt limit. Refresh submissions for the settled outcome;
busy, unsupported and unsuccessful compaction are not success. An uncertain request survives
selection changes with its exact key and attachment for manual reconciliation or deliberate retry.
Replaying a busy receipt does not try again: a new explicit action uses a fresh key. Closing the
panel cancels only the wait; shutdown retains and joins accepted compaction and cancellation work.

**Queue text — this host only** uses an explicitly refreshed runtime/attachment observation, including
busy/draining attachments. `sessions.queue` reserves exact text for that attachment only; the receipt
separately reports reservation, host-only insertion and execution/cleanup. `queue_accepted` is not
durable or executed, and `queue_dispatched` is not proof of run completion. **Cancel this queued
operation** uses `sessions.cancelQueue` with the original operation ID, never a later/current run.
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
