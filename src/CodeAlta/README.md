# CodeAlta desktop (in development)

`CodeAlta` is the native desktop .NET tool (`alta`), built with published NeoAstra 0.1.0,
generated RPC, React/strict TypeScript and packaged local Vite assets. Node/npm is needed
only to build. The installed application has no UI server or external asset origin.

**This is an in-development workspace browser with a separately opted-in text-submission mode,
not full agent parity.** Use `CodeAlta.Tui` (`altatui`) for normal agent workflows. Without
catalog opt-in, only the boot surface is available and no provider, plugin, configuration or
default profile is initialized. Catalog browsing alone does not compose a runtime host.

```powershell
dotnet build -c Release
./bin/desktop/Release/net10.0/alta.exe --help
./bin/desktop/Release/net10.0/alta.exe --version
./bin/desktop/Release/net10.0/alta.exe --data-root "$env:TEMP/codealta-desktop-$([guid]::NewGuid())"
```

Native startup requires an explicitly injected **new absolute task-owned directory**, outside
`.alta`. Help/version and rejected arguments do not initialize native services or create storage.
This restriction is not the future cross-head ownership guard. Do not use a production profile.

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

Selecting a session loads a bounded page of its persisted canonical events. Next-page
navigation replaces the current page rather than growing an unbounded transcript in the
browser. Content deltas, completed content and activity phases remain distinct records;
this is not a reconstructed conversation or a live stream. Permissions and other stored
requests are historical records, not actionable approvals. Previews can be shortened or
omitted explicitly; arbitrary provider payloads are not sent to the frontend.

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

## Explicit owned text submission

An additional experimental mode borrows command and cached-read services from one shared host:

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
default, user input is cancelled, and this path supplies no custom tools or plugins. **Refresh
submissions** explicitly retrieves receipts; **Abort submission** targets one pending send,
not a later run. Submitted means dispatch completed, not that the conversation/run completed.
An uncertain response retains the exact epoch/key/session/text for explicit retry, never
automatic resend. Epoch mismatch disables mutations across selection changes; reload recovers
receipts, not prompt text. The host retains at most256 receipts, paged64 at a time.

Add **`--review-owned-command-permissions`** to the complete owned-mode command above to opt
into manual review of supported plain command requests. The selected-session review shows the
complete command, working directory and optional reason, with **Allow once / Deny / Cancel**.
Refresh pending commands manually; this is not a notification stream. Unsupported permission
payloads remain denied, user input remains cancelled, and there is no Allow for Session option.
Approval can execute a command with the host's privileges: discovery roots are not a sandbox.
Changing selection or reloading the renderer does not cancel a pending request; returning and
refreshing reads the application-owned pending state. An uncertain response is not a denial or
proof that nothing executed. An uncertain decision disables review across selections until the
renderer is reloaded; then refresh pending commands. No response is automatically replayed.
Aborting the owning submission or closing the application invalidates still-pending requests,
but cannot revoke a decision already accepted by the backend. Native/provider qualification and
the broader permission, file-review and ask workflows remain incomplete.

The selected session also has a **live status/text window**, separate from persisted-history
browsing and submission receipts. It shows retained lifecycle, queue count, configuration labels
and up to eight text items; it is not a complete transcript, usage/tool view or interaction UI.
No retained state means not yet observed or evicted—not idle or completed. Text and labels may
be shortened, and replacement/eviction indicators make omissions explicit. **Reconnect live
display** explicitly starts a new observation; it never resends a prompt. A stale host epoch
explicitly requires reloading the UI, not reconnecting with the old identity. Selection changes,
renderer detach and cancellation close only the observation, not a run or the host. Only explicit
owned mode registers this channel; default/catalog-only startup behavior is unchanged.

**Refresh runtime state** is a separate manual, point-in-time observation of the actual runtime
entry, coordinator transition, recorded run, Shutdown, retirement and queue-drain facts. It does
not poll or automatically refresh; steering uses its captured target, subject to host revalidation. No entry/no recorded run is not
idle or completed, and queue depth is explicitly unknown. Configuration is runtime-captured, not
verified provider-effective settings; pending prompt selection is shown separately. This query
does not read catalogs/history or start/discover providers. Its runtime instance/attachment
identity is not a Display revision, effect acknowledgement or history-recovery handshake.
Host epoch and selection are checked, obsolete responses are discarded, and stale host/runtime
identity requires reload rather than retrying the old epoch. Malformed/oversized output is refused
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

Actual cached-store reads are host-owned (eight active reads, excess rejected); cancelling an
RPC wait does not stop them. Shutdown joins command/read work before runtime dependencies.
Five seconds triggers a pending diagnostic, not termination. Unconfirmed host/native cleanup
keeps the lease; external termination is not confirmed cleanup. Managed fake-provider and
helper tests pass, but configured-provider behavior, React mounting and this mode's native
lifecycle remain unqualified. See [runtime contracts](../../doc/runtime.md#explicit-desktop-submissions-and-owned-reads).

Build outputs use `bin/desktop` and `obj/desktop` to avoid reusing the pre-rename TUI residue.
Generated frontend contracts/client remain build-only inputs under `obj/neoastra`.

The desktop RID intent is Windows, macOS and glibc Linux x64/ARM64 (six RIDs, **no musl**).
Only Windows x64 has executed native evidence so far. Ordinary `Exe` output and the Common
Controls v6 manifest are retained; redirected CLI success does not qualify interactive
console/Explorer behavior. See [native qualification](../../doc/desktop-native-qualification.md)
and [test instructions](../CodeAlta.Desktop.Tests/README.md).
