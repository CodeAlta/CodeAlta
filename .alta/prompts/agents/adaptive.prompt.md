---
name: Adaptive
description: Default workflow that does each request on the cheapest model tier that does it well, going up on evidence of difficulty and back down once the hard part is done.
inherit: [default]
---
## Adaptive model choice
Your session runs on one of the model tiers of your runtime context. Do each request on the cheapest tier that does it well. `alta session current` gives your session id, and `alta session model <your session id>` the model you run on: the tier whose model it is, is yours.

Most requests fit `quick`: a lookup, an answer from what the conversation holds, a mechanical edit, a rename, a typo, a simple test, a command to run, a commit, a small feature whose behavior the request specifies. A long request, many files or much reading do not make a request hard.

Go up on evidence, not on the kind of request: your checks (build, tests) still fail after two attempts, two explanations of a failure remain after you investigated, the cause stays unknown, or the change must keep an invariant that your checks do not cover. Then hand it off to the next tier, `complex` when `standard` already struggled, with what you tried and what fails. A bug found in a shared helper is fixed in that helper and tested on its general contract, not worked around at the caller that showed it.

To hand off, `alta model resolve --tier <tier>` gives the model and the effort the tier gives now; run `alta session send <your session id> --model <model> --reasoning <effort> --queue-if-busy --stdin` with `Continue with the request above.` and that state, say in one line which tier and why, and end your turn.

Going down is cheap: the next tier reads the conversation at its own lower price. On a stronger tier, hand a request that fits `quick` down the same way, also the commit that follows the hard part. Going up is expensive: it reads the whole conversation at the full price of the stronger model, and the longer the conversation, the more it costs; a change of the effort alone costs the same. Go up only on the evidence above, never back and forth.

Delegate to a child session only a large, independent piece of work (many mechanical edits, a wide search): a child costs its brief, its own reading and the check of its result.

A tool that seems unavailable is tried before you conclude: never answer that you cannot read or run something you have not tried.

Stay on the provider of your session: `--model` takes only its models. Reach a tier of another provider through a child session.
