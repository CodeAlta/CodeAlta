# Read-only file-change inspection

Implemented and independently accepted within the bounded frontend/browser scope.

History file-change activity and DiffUpdated records can expose per-file inspection from their
already-supplied JSON details: `changes[].path`, `changes[].kind.type`, `changes[].diff`, or a
workspace record's `path` and `operation`. Paths, kinds and diffs remain literal text, never links
or filesystem/editor targets. Generic original record details, Wrap and Markdown Copy remain
available and unchanged. Aggregate-only or unrecognized formats stay in those original details;
the frontend does not guess file boundaries or retrieve missing content.

The added projection accepts at most 8,192 UTF-16 units of complete JSON and examines at most
32 candidate rows. Paths are limited to 512 units, kinds to 64, and per-file diffs to 4,096.
Truncated JSON is not repaired. Skipped/oversized/malformed fields are disclosed as partial data.
Missing or unsupported diffs have unavailable counts, not inferred zero changes.

For supported complete unified hunks, counts describe only supplied `+`/`-` lines after validating
hunk line counts and non-overlapping ranges. A single ordered optional preamble/header pair is
accepted; repeated/malformed headers, unsupported metadata, incomplete hunks and misplaced
no-newline markers leave counts unavailable. CRLF and final no-newline markers are supported.
Work is bounded to 512 lines and 128-unit hunk headers per diff. Displayed sums
cover only the explicitly counted displayed records. These are not complete file/run totals, disk
state, successful writes or final run status. Records are not merged into an authoritative recap.

Per-file disclosure is separate from the original raw-details disclosure. Its lifetime
is keyed by the exact supplied history record, with existing App view-lifetime checks; no history
paging, retention, live reconciliation or scroll/follow algorithm changes are introduced. Static
chrome uses the six existing languages. No host DTO, read budget, dependency, CSP or sanitizer change.

Mounted production renderer and actual-App fake-host tests cover literal rendering, original Copy,
six-language chrome, same-offset replacement and session/host/modal disclosure lifetime. These are
finite headless-browser checks, not native WebView2, screen-reader or live-provider acceptance.
Unsupported aggregate diffs are intentionally not split; no file opening or editing is provided.
Exact qualification and preserved regression evidence: `tmp/file-change-timeline-20260927/REPORT.md`.
