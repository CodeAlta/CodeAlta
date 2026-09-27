# Desktop project prompt references

Owned project composers and the scope-local prompt draft support `@` file and
directory search. This is an end-to-end **typed path-reference** feature, not a
file upload or a promise that a provider has read file contents.

## Editing

Interact with an `@` token to search. Quoted references support spaces, for example
`@"src/my file.cs"`; a trailing slash denotes a directory suggestion. Arrow keys
select, Enter/Tab insert, and Escape dismisses. Popup Enter cannot fall through to
Send, including while loading or when no match exists. Composition, repeats and
modified keys retain editor ownership. The same picker serves the regular and
expanded editor and local prompt draft. Insertion preserves an existing range.

Search shows at most 64 metadata matches, including up to five recent resolved
references. Recency is bounded and host-local, not persisted. Search is initiated
by editor interaction, debounced and canceled on input/scope/lifetime changes;
there is no watcher, background refresh or retry loop. Old replies cannot revive
results across input/project/session ABA changes. Typing/searching in a local
draft does not create a host session or prepare a provider.

Only an owned, verified, nonarchived project enables search. Global, catalog-only,
unknown/ambiguous project identities and unavailable hosts do not invent roots.
Search results are suggestions, not dispatch authority. Quoted insertion retains
the reference syntax; normal Send produces escaped Markdown links plus typed
`AgentInputItem.File` / `Directory` values after host-side resolution.

## Inline metadata presentation

Regular, expanded and local-draft project composers offer **Check reference
metadata**. This explicit read produces an inline raw-prompt preview beneath the
native textarea: resolved metadata is accent-colored/solid-underlined, unresolved
or over-budget references are error-colored/wavy-underlined, and escaped `@@` is
dotted-underlined. Status text and per-span accessible labels accompany color.
The preview preserves the original text (including whitespace and escape syntax);
it is not rendered Markdown, a replacement editor, or a textarea overlay.

The host returns at most 256 UTF-16 spans using the **same parser and bounded path
policy as original Send**, including ranges, malformed/literal fallback and the
32-attachment budget. The observation does not update recent references. It reads
metadata for referenced paths only: no directory traversal, watchers, polling,
automatic validation on typing/navigation, provider preparation or session creation.
Malformed descending ranges conservatively mark the whole original prompt unresolved,
matching dispatch's whole-prompt literal fallback. Omitted spans are explicitly reported.

Editing or composition invalidates observations; scope/input/component changes
cancel pending work, and late equal-text ABA replies cannot restore it. A new
check requires another explicit activation. The native textarea retains raw text,
selection, caret and IME ownership; the expanded dialog's metadata/picker buttons
retain native keyboard activation instead of invoking the editor's Enter-to-close.
Observation is not attachment admission: normal Send independently revalidates
paths/ranges in its original worker. Queue/Steer remain literal.

Picker insertion's deferred focus is one-shot. It is canceled on scope/component
changes and input edits/composition; its callback additionally checks the original
element and edit generation. Equal text on a replacement/current editor cannot
authorize a stale focus/caret change. The intended insertion commit still permits
the original callback, with no repeated scheduling.

Native child-dialog open/close transitions synchronously invalidate the existing
presentation generation and publish it through an App state update. This includes
Info, project Details and the expanded editor. Reference consumers must not first
learn of an old transition on an unrelated later render (such as a locale change):
a read started after the settled transition retains its owner, while older reads
and deferred insertion callbacks remain fenced. Publication itself requests no
search or metadata observation and grants no creation/Send authority.

## Original Send ownership

The immutable Send envelope includes an optional expected project ID/path. It is
part of request-key equality. The renderer never supplies an authoritative root or
arbitrary typed input objects. The original owned worker resolves the exact session
and unique catalog project, checks nonarchived identity and working-directory
agreement, and uses the **catalog's** root. Resolution occurs once before provider
preparation. Original pending/terminal receipt replay does not re-resolve files or
start another Send. Changed reference scope with the same key conflicts.

`@file.cs:12`, `@file.cs:12-24`, quoted paths with a range, existing relative Markdown
references, and escaped `@@` use the shared parser. Unsafe, missing, disappeared,
unsupported, over-budget and malformed references remain literal. A descending
range rejected by the shared parser conservatively retains the entire original
prompt. Directory ranges remain literal. Line ranges are metadata selections,
not verification that those lines exist. File contents are neither read nor frozen:
the immutable provider input contains paths/ranges, not a byte snapshot. A later
provider tool read remains a distinct operation under its normal permissions.

Queue and Steer retain their existing **literal text-only** contracts; they do not
gain attachment semantics. The composer states this explicitly. Local drafts must
still be explicitly created/transferred and then sent normally.

## Bounds and filesystem boundary

- Search: depth six below the project root, 4,096 visited entries, 64 results,
  256-character query and a cooperative 250 ms traversal budget. Dot-prefixed
  entries are omitted. Partial/error responses explicitly report omissions.
- Input: 32,768 original UTF-16 units, up to 32 typed references, relative paths
  up to 1,024 units/eight components, catalog root up to 4,096 units and normalized
  text up to 131,072 units. Ranges are positive, ascending, at most 2,000 lines,
  with final line at most 1,000,000.
- Rooted/drive/UNC paths, parent traversal, empty/dot components, control characters,
  alternate-stream colons and ambiguous platform filename syntax are refused.
  Project identity/path agreement is exact; filesystem lookup uses platform rules.
- Every path component is checked for reparse points, including project-root
  ancestors. Linked roots and linked descendants are refused. Metadata errors
  produce literal fallback or explicit search errors, not broader access.

These checks follow the repository's bounded-directory-reader conventions. They
are **not** handle-relative protection against a hostile process swapping ancestors
concurrently. The time budget is checked between filesystem calls; it cannot
preempt a blocked OS metadata call. No native containment/finality claim follows.

## Verification and remaining parity

Focused tests use only disposable roots and fake providers: typed provider-facing
input, range/escape/fallback, deletion plus original receipt replay, scope mismatch,
archive refusal, no provider preparation during search, bounded results/attachments,
cancellation and a real disposable linked-directory escape refusal. Actual-App
tests cover popup insertion, IME-safe Enter, input ABA, stale scope results, local
draft navigation and the frozen Send envelope. Desktop tests cover RPC admission,
unowned/stale-host refusal and scope forwarding.

Inline observed-reference coloring is available in the explicit raw-text preview,
not continuously inside the native textarea as in the TUI. No native/screen-reader or
hostile concurrent filesystem qualification, content-upload semantics, image
attachments or Queue/Steer attachment support is claimed.
