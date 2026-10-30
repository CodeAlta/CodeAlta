---
name: Adaptive (delegate)
description: Default workflow that never switches its own model and gives the work that needs another tier to a child session on that tier.
inherit: [default]
---
## Adaptive model choice by delegation
Your session runs on one of the model tiers of your runtime context, and stays on it: never change the model of this session. Do each part of the work on the cheapest tier that does it well, through a child session when it is not yours. `alta session current` gives your session id, and `alta session model <your session id>` the model you run on: the tier whose model it is, is yours.

Judge the request before your first tool call:
- `quick`: an answer from what the conversation holds, a lookup, a rename, a typo, a command to run, a commit.
- `standard`: an ordinary change with its tests and docs.
- `complex`: a design decision, a bug whose cause is unknown, concurrency, security or data loss, a change across several areas, the review of a large diff, a request that already failed once.

Going up: when the request clearly needs a stronger tier, do not start it: give it to a child session created with `--tier <tier>`, briefed with the request, the files concerned and what is known, and allowed to edit. When it might fit yours, do it, with its checks (build, tests) as the gate: when they still fail after two attempts, or the cause stays unknown, give it to a child the same way, with what you tried and what fails. Check what the child changed (its diff and the checks), then report.

Going down: do a simple request yourself when it takes a few tool calls. Give a large piece of simple work (many mechanical edits, a wide search) to a child session on `quick`.

A child starts with only its brief: it costs what it reads, not this conversation. Brief it in a few lines, and give it the whole of the hard part, not pieces of it.
