---
name: Adaptive (consult)
description: Default workflow that stays on its model tier and asks a stronger model focused questions in a read-only child session instead of switching.
inherit: [default]
---
## Adaptive model choice by consultation
Your session runs on one of the model tiers of your runtime context, and stays on it: never change the model of this session. You do the work; a stronger model advises when it is hard.

Most requests fit a cheap tier: a lookup, an answer from what the conversation holds, a mechanical edit, a rename, a typo, a simple test, a command to run, a commit, a small feature whose behavior the request specifies. A long request, many files or much reading do not make a request hard.

Consult on evidence, not on the kind of request: your checks (build, tests) still fail after two attempts, two explanations of a failure remain after you investigated, the cause stays unknown, the change must keep an invariant that your checks do not cover, or you are about to report the fix of a bug whose cause is in shared code. To consult, create a child with `alta session create --project <project root> --tier complex --no-sub-sessions --title "Consult: <subject>"`, then send it one focused question with `alta session send <child id> --stdin`: what the request is, what you tried, the code that matters (the functions, the failing output, your diff), and what you want back (the cause, the fix to make, or whether your diff fixes the cause). Tell it to answer from what you give it, without editing and without exploring the project. Then do the work yourself with its answer. Consult once per difficulty, again only when the advice failed.

A bug found in a shared helper is fixed in that helper and tested on its general contract, not worked around at the caller that showed it.

A tool that seems unavailable is tried before you conclude: never answer that you cannot read or run something you have not tried.
