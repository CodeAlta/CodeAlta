# Saved-browser multi-selection and exact deletion

The saved-session browser offers **Select visible eligible**, **Invert visible
eligible**, individual checkboxes and **Clear selection**. These are presentation
operations, never active-session selection or attachment commands. Select/invert
replace selection using only currently displayed, eligible rows in the chosen
browser scope; they do not silently include hidden search/count results or other
scopes. A batch contains at most **32** exact targets. Over-cap selection is refused
with feedback rather than silently truncated. Existing 200-match browser and loaded
snapshot bounds remain unchanged.

Only owned-host, exact project/global saved identities are eligible. Unconfigured,
archived, ambiguous, missing-path, overlong/malformed-title and truncated identity
or snapshot evidence is refused. Saved browsing remains available read-only where
deletion is unavailable. Runtime/activity observations never authorize deletion.

## Explicit review and original requests

**Review exact deletion targets** freezes each exact full title, session ID, scope,
project ID/path and host epoch, together with the App capture's catalog/navigation
revision and browser input lifetime. The review displays those exact targets.
It also captures the originating shared mutation capability by identity. A late
response is never redirected to whichever capability currently belongs to App.
The user must type **DELETE N** and click **Delete reviewed sessions**. Each unary
request still supplies the reviewed exact title as the existing backend's
`confirmedTitle`; backend title/scope/lifecycle checks remain mandatory.

Static selection/count/review controls, target labels, known outcome presentation
and accessible names follow the six supported [WebView languages](webview-localization.md).
The instruction around **DELETE N** translates, but those exact typed bytes do
not. Language changes retain selection, review, entered confirmation and original
requests without invalidation or additional dispatch. Titles, IDs, paths, epochs,
backend codes and controller messages remain literal; `confirmedTitle` is unchanged.
Uncertain outcomes never become success or unlock continuation when translated.

Search, order, count/show-all, scope, selection, catalog/host/session/project changes
and dialog dismissal invalidate the review. Same-value ABA cannot restore it.
Changing selected checkboxes also requires a new review. Native checkbox/summary
keyboard controls and text-input Enter are not session-opening shortcuts; IME and
repeated Enter do not admit deletion.

The App-owned controller issues only one original request at a time, with the
existing ten-second bridge wait. A timeout/rejection is uncertainty, not proof that
backend work stopped. Dismissal, navigation or **Stop after pending original** stop
successors but do not cancel an admitted original. Results remain available when
the browser reopens, including when viewing another scope. No implicit resume or
automatic retry exists.

Outcomes distinguish **not-started**, **pending**, **deleted**, **refused** (with
backend reason), and **uncertain**. Correlated `session_in_use`, `has_children` and
`session_missing` refusals may continue to the next already-reviewed target. Scope,
host, busy/closing or capture-authority failures stop further dispatch; unrecognized
or uncorrelated responses are uncertain. A confirmed old-host response remains the
truth about its original request even after navigation; it never authorizes a new
request on the current host.

Response evidence is checked against the original session/scope/project/path,
the deletion protocol's known statuses and a canonical nonzero host epoch before
notifying that original capability, even after dismissal or navigation. Valid
`stale_epoch` evidence is a definite refusal and invalidates shared old-host
Send/create/etc authority. A correlated recognized reply from a changed host also
invalidates that authority; a changed-host `ok` cannot establish success for the
old-host original and remains uncertain. Malformed, unrelated and unknown-status
replies are not promoted to shared authority evidence. This deletion contract has
no runtime identity or `stale_runtime` response contract; none is invented here.

The successor stop is committed before shared notification. Synchronous reentrant
or throwing subscribers cannot start a successor, redirect capability identity or
turn a confirmed original success into uncertainty. Notification faults remain
diagnostic only. A valid exact old-host success still records deletion even when
its original capability was invalidated independently in the meantime.

Single deletion and rename admission are locally blocked while review/work/results
are retained. A wholly definite settled report can be explicitly acknowledged and
cleared; this never resumes not-started items. **Uncertain reports cannot be cleared
or unlocked by catalog refresh.** Independently inspect any uncertain removal;
reloading is not evidence of completion and does not make retry safe.

## Authority and limits

This reuses `WorkspaceDeleteSessionRpc` and owned exact-session deletion unchanged.
The backend remains final authority for exact title/scope, active or queued work,
retained operations, retirement and children. Its existing child check remains;
the browser adds no discovery, child sorting, cascade, project archive, provider
preparation or widened lifecycle authority. The TUI `ProjectSessionsDialog` has
select-all/invert and count confirmation; this WebView batch is deliberately more
bounded and does not claim parity with broader TUI deletion behavior.

There is **no automatic catalog reconciliation** after success. Stale loaded rows
may remain visible until an explicit existing catalog refresh. Deletion selection
and outcomes do not clear drafts/tabs, switch workspace, restart Send, alter follow
or scroll owners, or attach hidden Display streams. Partial completion is not an
atomic or durable transaction. Retention lasts for the current App lifetime, not
across WebView destruction. Native/screen-reader qualification remains separate
from disposable browser tests.
