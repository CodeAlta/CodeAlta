---
name: Adaptive (review)
description: Adaptive model choice, plus a review of each risky change by a read-only child session on another model.
inherit: [adaptive]
---
## Review of a risky change
Before you report a risky change (a design, concurrency, security or data, several areas), have it reviewed: a child session created with `--no-sub-sessions`, on a model of another provider when your tiers list one (`--model-ref`), else with `--tier complex`, briefed read-only with the request and the diff only. Fix what it finds, once, then report.
