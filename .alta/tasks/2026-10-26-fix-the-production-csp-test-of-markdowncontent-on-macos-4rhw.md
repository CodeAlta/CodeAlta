---
title: "Fix the production CSP test of MarkdownContent on macOS"
kind: problem
status: pending
created: 2026-10-26
summary: "markdownContent.browser.test.ts fails on macOS (Edge 156) with an inline event handler blocked by the production CSP, also when run alone and without any frontend change."
---

## Why

On macOS (2026-10-26, `main` at `b8dc1afb` plus changes that touch no frontend file, Microsoft Edge 156.0.4314.42), `npm test` fails one test, also when its file runs alone (`node node_modules/tsx/dist/cli.mjs --test src/markdownContent.browser.test.ts`):

```text
✖ production MarkdownContent retains useful HTML without resource or app authority under production CSP
  + "Executing inline event handler violates the following Content Security Policy directive 'script-src 'self' 'sha256-…''. … The action has been blocked."
  - []
    at check (src/markdownContent.browser.test.ts:77)
```

Something on the page tries to run an inline event handler (a fixture's authored HTML, or an attribute that the sanitizer lets through), and the production CSP blocks it. That a blocked handler is not executed is right; the test counts the console violation as a failure. It was green on this Mac on 2026-10-13.

## What to do

- Find which case of `markdownFixture.cases` produces the violation (log the case id at each `check()`).
- If the sanitizer keeps an `on*` attribute, remove it there (a security fix); if the violation comes from the fixture or from a change of Edge, adjust the fixture or the test.

## Where

`src/CodeAlta/frontend/src/markdownContent.browser.test.ts` (around line 77), the Markdown sanitizer of `MarkdownContent`.
