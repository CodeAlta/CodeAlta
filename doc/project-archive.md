# Desktop single-project archive and unarchive

Select an exact saved project in the project rail and choose **Archive project…**
or **Unarchive project…**, in the menu of its row or in the menu of the Projects
header. An owned host is required; catalog-only browsing cannot mutate metadata.
A popover beside the row asks once, with **Do not ask again** (taken back with
**Ask before archiving a project** of **Settings → Appearance**). Once the answer
is yes, the window reads the source evidence and writes with it in one go: the
project ID, project path, source, raw-byte revision and expected archive state the
read returned are the ones the write requires. A scope, host or catalog change
between the read and the write sends nothing.

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

The App owns the operation across project or session navigation and Settings: a
write is never sent twice. A matching terminal reply is confirmed even if a later
display refresh fails. A definite conflict/refusal is said in the popover (in a
notice when nothing was asked) and leaves the project as it was. A transport
failure or an exception after write admission stays uncertain and blocks further
archive writes in that window, which the Explorer says under the projects.
Snapshot reads cannot unlock uncertainty; reloading the window does.

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

The conditional edits of one `ProjectCatalog` (an archive change, a rename of
the display name) run one at a time, from their first read of the project file
to its replacement: a second edit that read the file while the first replaced
it got a sharing violation on Windows where its answer is a conflict. The codec
serializes its cooperating saves and compares raw-byte revisions before
replacement. **This is not cross-process atomic compare-and-swap.**
External editors, other Catalog instances, general descriptor saves and path
swaps can still race after checks. Bounded ownership scans are not filesystem
snapshots. Desktop admission is not a process-wide or external-writer lock.
No automatic conflict retry or stronger exclusion guarantee is implied.

Focused disposable tests cover the Catalog scalar update, Desktop held-worker
admission/drain and the read-then-write owner of the window (`projectArchive.test.ts`). They are not native
WebView, screen-reader, real-provider, full-suite or bulk-operation qualification.
Bulk archive/delete and native/full parity remain separate work.
