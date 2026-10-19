# Lightweight orchestration actor model

CodeAlta orchestration uses small internal mailbox actors rather than a general actor framework. The goal is single-writer per-session mutation, bounded event flow, and testable shutdown/error behavior without exposing actor concepts through public APIs. Some internal type names still use `SessionView` as legacy session-view terminology.

## Ownership boundaries

- Public callers use runtime request records, session ids, snapshots, handles, and events.
- `SessionActor`, `SessionActorRegistry`, and `OrchestrationMailboxActor` are internal implementation details with legacy names.
- A session actor owns same-session command ordering, lifecycle cancellation, supervisor decisions, and mailbox cleanup.
- Different session actors may run independently; commands for one session are serialized.
- Frontend and plugin code must not mutate actor-owned state directly. Async completions and observer results are converted into commands/events first.

## Command and event naming

- Mutating runtime APIs use named request records and return structured command results.
- Orchestration events use stable session ids and optional per-session sequence numbers for frontend/plugin projection idempotency.
- Lifecycle events identify session/run/queue transitions without requiring frontend tab state.
- Plugin-derived events are transient projections; they do not become canonical persisted transcript entries.

## Plugin orchestration bridge

- Plugin orchestration hooks live behind orchestration-layer adapter services, not in the TUI frontend.
- Agent-event observers receive normalized `AgentEvent` values with project/session/run scope and are dispatched in deterministic plugin order.
- Derived event projections use one replay/live path so restored events and newly observed events produce equivalent plugin projection output.
- Plugin observer/projection failures are diagnosed and isolated from the actor command path and from later plugins.

## Mailbox and backpressure

- `OrchestrationMailboxActor` uses a bounded channel and completes command replies on validation failures, recoverable handler failures, disposal, and unrecoverable supervisor stops.
- Normal commands are admitted **in the order they are posted**. A command posted while the slots of the mailbox (its capacity) are all taken waits in a first-come line, and the start of each command writes the first waiters of that line to the mailbox, under one lock. A provider posts its events one after the other without waiting for them (`SessionRuntimeService` forwards each callback at once): with a semaphore, waiters resumed on the thread pool could write in any order, and a session could see a delta after its `Idle` and stay running. A waiter canceled before its turn leaves the line without a slot; one still waiting when the actor stops fails as a write to a completed mailbox does (`ChannelClosedException`). Reserved commands keep their own room and free room for the line when they start.
- `SessionActorRegistry.GetOrCreate` makes one actor per session even when callers race: an actor starts its mailbox when it is made.
- `BoundedRuntimeEventStream<TEvent>` uses a bounded channel with a newest-event drop policy when readers fall behind; dropped events are counted for diagnostics.
- Callers must not depend on unbounded buffering to preserve UI responsiveness or plugin observer progress.

## Supervisor decisions

- Validation failures fail the current command without stopping the actor.
- Recoverable handler/provider/plugin failures are represented as structured command outcomes where possible.
- Unrecoverable failures stop the actor, complete pending replies, and allow the registry to clean up the actor entry.

## Actor framework decision

Akka.NET is intentionally not a default dependency. The local mailbox primitives keep public APIs framework-neutral and minimize runtime surface area. Reconsider a full actor framework only after tests and measured complexity show that the local primitives no longer simplify orchestration ownership, supervision, testing, shutdown, and host integration.
