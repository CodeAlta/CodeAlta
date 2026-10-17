---
title: "Fix the title-bar dot browser test of activityRail.browser.test.ts"
kind: problem
status: pending
created: 2026-10-17
summary: "\"the dot of a button of the title bar is on that button\" fails on macOS at fdfe8235 (feature/local-search) without any change: the dot was seen on the Work items button and on the menu button."
---

## Why
`node node_modules/tsx/dist/cli.mjs --test --test-name-pattern="the dot of a button" src/activityRail.browser.test.ts` (from `src/CodeAlta/frontend`) fails on an untouched checkout of `fdfe8235` as well as with the Search canvas work on top: `AssertionError: the dot was seen on the Work items button and on the menu button`. `npm test` therefore is not green on this branch. The other two failing browser tests of that run are covered by the existing 75% width task.

## What to do
Find whether the dot of a title-bar button really shows on two buttons (a product bug) or the test measures a layout that changed (overflow menu of the title bar on macOS fonts), and fix the one that is wrong.

## Where
`src/CodeAlta/frontend/src/activityRail.browser.test.ts`, the title-bar buttons (`src/CodeAlta/frontend/src/pluginButtons/`).
