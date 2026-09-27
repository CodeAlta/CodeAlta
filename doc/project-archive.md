# Desktop single-project archive and unarchive

Select an exact saved project in the project rail and choose **Archive project…**
or **Unarchive project…**. An owned host is required; catalog-only browsing cannot
mutate metadata. The dialog reads source evidence, then requires a separate
confirmation of the captured project ID, project path, source, raw-byte revision
and expected archive state. Scope, host, catalog refresh and dialog changes
invalidate an unsubmitted confirmation, including changes away and back.

This changes catalog metadata only. It does not delete directories or journals,
cancel/stop existing work, establish readiness, create a session, or start a
provider. Sessions, tabs and local drafts remain. Archived interaction uses the
existing read-only presentation. Unarchive is explicit; it does not start work.
Desktop directory import refuses an already archived project and directs the
user to this confirmation instead of restoring it. General Catalog/TUI
`EnsurePersistedAsync`/open restore behavior is unchanged.

## Ownership and outcomes

Creation, directory import, project-name reads/renames and archive writes share
one **Desktop WorkspaceService instance admission monitor**. Their original
tasks remain separate. An admitted create excludes archive until it finishes,
so its general catalog persistence cannot restore a concurrently archived
project within that owner. An archive admitted first excludes creation; a later
create observes the archived project and refuses. No monitor spans an async
wait. Canceling the caller's wait does not release the original reservation.
Desktop shutdown drains session and catalog originals before host disposal.
Other Desktop persistence call sites are not introduced by this feature.

The App retains original operation evidence across dialog dismissal, project or
session navigation and Settings. Reopening does not resubmit a write. A matching
terminal reply is retained as confirmed even if a later display refresh fails.
A definite conflict/refusal is distinct from a transport failure or an exception
after write admission: the latter stays uncertain and blocks further archive
writes in that window. Snapshot reads cannot unlock uncertainty. Evidence is
window-local, not a durable operation ledger across process restart.

## Persistence and limits

`ProjectCatalog.ReadArchiveAsync` uses bounded ownership validation and derives
the source from the catalog, never from renderer authority. `SetArchivedAsync`
rechecks identity, source, expected state and revision. It edits only an explicit
root `archived: true`/`false` scalar using the same `TextFileCodec` instance as
display-name updates. Unknown front matter, comments, Markdown, newline style,
supported Unicode encoding and BOM are preserved. Ordinary serializer-produced
projects contain the supported scalar. Missing, tagged, anchored, complex or
otherwise unsupported archive fields fail closed instead of being rewritten.
Missing/malformed/ambiguous ownership and observed reparse-point links refuse.
Canonical and supported legacy sources are supported; a moved source cannot
receive a stale confirmation.

The codec serializes its cooperating saves and compares raw-byte revisions
before replacement. **This is not cross-process atomic compare-and-swap.**
External editors, other Catalog instances, general descriptor saves and path
swaps can still race after checks. Bounded ownership scans are not filesystem
snapshots. Desktop admission is not a process-wide or external-writer lock.
No automatic conflict retry or stronger exclusion guarantee is implied.

Focused disposable tests cover the Catalog scalar update, Desktop held-worker
admission/drain and actual App confirmation/evidence flow. They are not native
WebView, screen-reader, real-provider, full-suite or bulk-operation qualification.
Bulk archive/delete and native/full parity remain separate work.
