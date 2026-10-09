# Session/content and opt-in split workspace presentation

## Actual App: controlled session/content projection

`main.tsx` uses the App-specific `SessionContentLayout.tsx` for its sessions
rail (including notes) and content. Projects and their existing splitter stay
outside it. Fresh caller nodes enter one stable private two-tabset model;
the existing `SessionWorkspace` key remains `[projectId, selectedSession.id]`.
Session/project/epoch, request, draft, display and preference owners have not
moved into factories. Settings remains a sidebar-entry native modal outside
the factories with the workspace mounted and inert. **Reminders intentionally
unmounts the workspace**; returning mounts new DOM while App-owned drafts and
requests survive. Closing notes still intentionally unmounts NotesPanel.

This is not the library's default splitter behavior. App retains its existing
accessible **Resize sessions** control: live pointer-captured dragging,
ArrowLeft/ArrowRight by 16px, Home and double-click reset. Public
`classNameMapper` plus application CSS suppress the library divider; no package
DOM or ARIA is rewritten. Public model attributes project the App-derived
session width plus the 8px control into the rail tabset's min/max dimensions.
All Layout-originated actions are refused; only App projection updates apply.

`paneLayout.ts` still owns the sizing policy: sessions 220–560px (default 310),
projects 160–440px (default 240), content minimum 480px. Collapsed projects use
no width budget. Only explicit gestures change persisted pixel preferences;
viewport/parent constraints and responsive projection do not save their reduced
sizes. Desktop clipping below the combined minima remains existing policy.

At viewport widths at most 875px the same model projects vertically, without
desktop width minima or an interactive divider. An App-owned hidden CSS sizing
probe measures the existing top-row rule: max(340px, 45vh), or max(180px, 40vh)
at heights at most 600px. Opening projects occupies that top row outside the
adapter. Sessions and notes stay mounted but hidden with zero allocation and
no focusability; content occupies the remaining row. Closing projects restores
the same child identities. Public tab resize subscriptions and model updates
share a coalesced public `Layout.redraw()`; cleanup disconnects the probe observer,
removes subscriptions and cancels any pending frame, including StrictMode replay.

The content slot holds the [dock of the session tabs](session-tabs.md), a
FlexLayout model of its own for each space. Each open tab of the shown space
keeps its pane mounted, and a hidden pane pauses its reads.

## Maintained verification

For the focused App migration checks, from `src/CodeAlta/frontend`,
using existing installed tools (no full suite or other renderer reruns):

```sh
./node_modules/.bin/tsc --noEmit --skipLibCheck false
./node_modules/.bin/tsx --test src/paneLayout.test.ts
npm run build
```
