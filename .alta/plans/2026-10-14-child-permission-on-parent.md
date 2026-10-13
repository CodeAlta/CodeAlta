---
status: in-progress
---

# A sub-session that waits for you shows on its parent

Branch `feat/child-permission-on-parent`, on top of `fix/permission-mode-applies-at-once` (PR #119).

## Why

A session created by another one (through `alta session create` / `send`) that waits for a permission answer shows it
only in its own pane, its sidebar row and a "{title} waits for you" notice. The parent's pane shows nothing: the user
who watches the parent sees an idle session while it waits for a report that never comes. Through Remote Control it is
worse, since claude.ai only sees the parent's CLI. Discussed on 2026-10-13 and agreed as a follow-up of PR #119.

It happens when a created session asks: with "Same as the session that creates them" (`inheritPermissions`) or a
provider configured with a mode. By default a created session bypasses permissions and does not ask, and its questions
(user-input forms) are cancelled.

## What changes for the user

- The pane of a session shows, above its composer, each sub-session (child, grandchild…) that waits for an answer:
  "<title> waits for your answer", with a chip that opens it.
- When a sub-session waits for a command or file permission, its card is shown there too, labelled with the
  sub-session's title: Allow once or Deny answer it from the parent. The answer goes to the sub-session, and its own
  pane closes the card. "Deny and tell the agent what to do" is not offered there: it belongs to the sub-session's
  composer.
- Questions and forms of a sub-session (`alta ask`) only show the line and the chip: they are answered in its pane.
- The sidebar row and tab of the parent show that a sub-session waits.

## Steps

- [x] Host: `SessionRuntimeService` maps each waiting session to its ancestors (walk up `ParentSessionId` of the
  runtime entries, then the catalog for an ancestor that is not attached; visited set, depth cap, case-insensitive like
  `_entries`).
- [x] Desktop RPC: `SpaceSessionActivity` gets `WaitingDescendants` (ids, nearest first); the activity lists an
  ancestor that is not running itself when a descendant waits (`SpacesRpc.cs`, `DesktopApplication.cs` waiting source).
- [x] Frontend: parse the new field (`spaces/spaces.ts`), a pure helper for the waiting descendants of a session, and a
  `WaitingChildrenPanel` in the session pane above the composer: one row per waiting descendant with
  `SessionReference`; for a permission, a `CommandPermissionPanel` with its own reviewer per child (never the parent's:
  a reviewer has a single selection), no `instruct`, no `takeFocus`, gated on `commandReviewEnabled`, region labelled
  with the child's title.
- [x] Frontend: the parent's sidebar row and tab mark "a sub-session waits for you" (badge or wording), without
  toasting twice for the same wait.
- [x] Tests: host mapping (child, grandchild, unattached ancestor, cycle guard), RPC field, frontend helper, panel markup
  (`renderToStaticMarkup` + `SessionLinksContext`), answering a child's request from the parent leaves the parent's own
  card working (separate reviewers).
- [x] Docs: `doc/desktop.md` (session pane, permissions), `site/docs/workspace.md` (sub-sessions), localization keys
  in all languages.
- [x] Check in the developer instance with Claude Code and `inheritPermissions` on: parent in Ask first creates a child
  that asks; the parent shows it, Allow from the parent runs the child's command, the child's report reaches the
  parent.

## As built

- The host does not build a separate map: each waiting session in `spaces.activity` names its ancestors
  (`SpaceSessionActivity.Ancestors`), from `SessionRuntimeOverview.ParentSessionId` of the runtime entries. The first
  parent is known from the waiting session's own entry; a chain stops at a session the runtime does not hold, at a loop,
  or after 16 levels. No catalog fallback was needed.
- The page groups them by ancestor (`waitingSubSessions`); `WaitingSubSessionsPanel` sits in the composer panel, after
  the session's own permission card. The existing "Waits for your answer" text is reused next to the chip.
- The "{title} waits for you" message is not shown for a session under the one on screen.
- Checked in the developer instance: a child set to Ask first under a parent, sent a command; the parent showed the line,
  the card and the lighter marks; Allow once from the parent ran the command; the marks went away and the child's answer
  reached the parent.

## Remote Control (decision needed, not in the steps above)

claude.ai only sees what the bridged CLI sends: a child's request cannot appear on the parent's claude.ai session
without a model turn of the parent, and nobody could answer it from there. Options:

1. **Remote Control on for the child** when a Claude Code parent with Remote Control on creates a Claude Code child: the
   child's own requests appear on claude.ai and can be answered there, as the parent's are. No tokens, no new protocol.
   Costs: one claude.ai session per child (kept listed offline with `keep_session_on_exit`), the child's process stays
   alive while it is on; turned off when the child ends.
2. **A notice to the parent** (`DeliverParentNotificationAsync`, kind `waiting`): visible on claude.ai, but it starts a
   parent turn (tokens), cannot be answered there, and the parent agent may act on it. Needs guards (Remote Control
   connected only, once per request, after a delay, not forwarded up).

Recommendation: option 1, in a step of its own once the desktop part works.

## Not in this change

- Created sessions following their creator's mode live (setting "Same as the session that creates them").
