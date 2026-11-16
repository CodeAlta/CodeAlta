---
title: "Let the phone start a session on a team machine"
kind: gap
status: pending
created: 2026-11-16
summary: "A person who is one among others on a team machine can read and drive their sessions from the phone but cannot start one: the machine refuses them its projects, providers and models; found by the review of #456."
---

## Why
`FleetNodeService.ReadSessionAsync` (`src/CodeAlta.Fleet/NodeApi/FleetNodeService.Reads.cs`, the `Target.Machine` branch) refuses the machine-level reads (`project.list`, `provider.list`, `model.list`, `prompt.list`, `model.resolve`) to a caller with `OwnSessionsOnly`. The phone page reads exactly those for its new-session form (`MobileApi.OptionsAsync`, `MobileSessions`), so on a team machine the form has no project and no model. The comment there says such a person reads what `node.info` gives them, but nothing falls back to it. The guide says a new session is started there from a computer.

## What to do
- Answer those reads for an `OwnSessionsOnly` caller with what `node.info` already gives that person (`ReadyProvidersAsync(call.Device)` for providers and models, the projects and agent prompts of `NodeInfoAsync`), without the detailed fields the owner alone reads; or build the records of the phone from `node.info` in `FleetMachineAlta`.
- Test with a member reached through a server, not only a paired owner: choices listed, a session started, another person's sessions still unseen.
- Then remove the sentence about team machines from `site/docs/plugins/mobile.md` (Other machines).

## Where
`src/CodeAlta.Fleet/NodeApi/FleetNodeService.Reads.cs`, `src/CodeAlta.Fleet/NodeApi/FleetNodeService.Sessions.cs`, `src/CodeAlta.Fleet/Client/FleetMachineAlta.cs`, `src/CodeAlta.Node.Tests/FleetNodeReadTests.cs`.
