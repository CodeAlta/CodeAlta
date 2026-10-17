---
title: "Fix the two browser tests broken by the 75% default conversation width"
kind: problem
status: pending
created: 2026-10-17
summary: "settingsOrganization.browser.test.ts and bookmarks/timelineBookmarks.browser.test.ts fail since c1b04f48 changed the default session width to 75%: one expects 100% in a window without a host, the other finds the bookmark rail over the message text."
---

## Why
Both tests fail on `feature/local-search` with the frontend changes of the search work stashed away, so they come from commit c1b04f48 ("Enhance the default width of the conversation to 75%"):
- `src/CodeAlta/frontend/src/settingsOrganization.browser.test.ts:95` expects `.settings-session-width output` to read `100%` in a window without a host (`owned` false); it reads `75%`.
- `src/CodeAlta/frontend/src/bookmarks/timelineBookmarks.browser.test.ts:41` expects the bookmark rail to be clear of the message body (`rail.left >= body.right - 1`); it is not, with the narrower centered timeline.

## What to do
- Decide what a window without a host shows as its default width (75% like the host, or 100%), and make the test and the code agree.
- Check where the bookmark rail sits when the conversation is 75% wide and centered: fix its placement if it covers the text, or the test if the layout is now intended.
- Run `npm test` in `src/CodeAlta/frontend`.

## Where
`src/CodeAlta/frontend/src/settingsOrganization.browser.test.ts`, `src/CodeAlta/frontend/src/bookmarks/timelineBookmarks.browser.test.ts`, the session width defaults in the frontend (Settings › Appearance) and the bookmark rail styles.
