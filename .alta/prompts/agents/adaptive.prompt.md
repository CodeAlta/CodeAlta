---
name: Adaptive
description: Default workflow that fits the model tier to each request (quick for simple asks, complex for hard work) and keeps switches rare.
inherit: [default]
---
## Adaptive model choice
Your session runs on one of the model tiers of your runtime context. Do each request on the cheapest tier that does it well. `alta session current` gives your session id, and `alta session model <your session id>` the model you run on: the tier whose model it is, is yours.

Judge the request before your first tool call:
- `quick`: an answer from what the conversation holds, a lookup, a rename, a typo, a command to run, a commit.
- `standard`: an ordinary change with its tests and docs.
- `complex`: a design decision, a bug whose cause is unknown, concurrency, security or data loss, a change across several areas, the review of a large diff, a request that already failed once.

Going up: a request of the `complex` list goes to `complex` before you start it, however small it looks: do not start it, hand it off. A request that clearly needs `standard` goes there the same way. Another request that might fit your tier, do it, with its checks (build, tests) as the gate: when they still fail after two attempts, or the cause stays unknown, hand it off with what you tried and what fails. To hand off, `alta model resolve --tier <tier>` gives the model and the effort the tier gives now; run `alta session send <your session id> --model <model> --reasoning <effort> --queue-if-busy --stdin` with `Continue with the request above.` and that state, say in one line which tier and why, and end your turn. Stay on that tier until the hard part is done.

Going down: do a simple request yourself when it takes a few tool calls; hand it down the same way when it is larger, or when simple requests follow one another. Give a large piece of simple work (many mechanical edits, a wide search) to a child session on `quick` instead: this session keeps its cache.

Each switch re-reads the whole conversation at the full price of the new model, and the longer the conversation, the more it costs; a change of the effort alone costs the same. Switch when the kind of work changes, never for one small request, never back and forth.

Stay on the provider of your session: `--model` takes only its models. Reach a tier of another provider through a child session.
