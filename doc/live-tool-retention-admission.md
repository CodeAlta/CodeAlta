# Live-tool and reminder retention admission

This is the live-tool portion of the [retention runtime contract](session-retention-runtime.md), not authorization to mark the host's other owners ready.

## Composition

Before exposing commands or creating reminders, register the host's **same** `ISessionRetentionAdmission` instance in the `IServiceProvider` supplied to `AltaCommandDispatcher` and `AltaReminderService` (including a reminder service with custom `IAltaReminderDelivery`). The existing `AltaServiceCollection` registration is `.Add<ISessionRetentionAdmission>(protection)`. LiveTool resolves it with its existing `Services.Get<T>()` helper, which calls `GetService(typeof(T))`.

A host without automatic retention may omit this optional service. A host with automatic retention must register it before work starts; late registration cannot retroactively protect admitted work. A registered service returning null is a refusal: no resolution, preparation, publication or scheduling proceeds. The command reports a failed invocation; direct reminder creation throws `InvalidOperationException`. Do not replace refusals with an observational snapshot or a successful no-op.

## Routes and lifetimes

| Owner / route | Admission boundary and lifetime |
| --- | --- |
| Plugin command factories and selected callbacks | The registry owns an invocation-local lifetime. The first actual plugin factory claims unknown targets **before** it runs; that claim covers parsing, help rendering and the original selected callback until completion, not just factory enumeration. Unknown custom command contributors are similarly claimed. A non-mutating policy is not a non-session contract and grants no exemption. Empty plugin catalogs acquire no blanket claim. |
| `session send`, `queue`, `message`, `request`, `steer`, including `--queue-if-busy` | Canonical trimmed target is claimed before prompt reading and session resolution. The command keeps its claim through model/provider and plugin preparation, runtime handoff, provenance persistence and output. Runtime ownership must overlap detached submissions and queued work after the command returns. |
| `session compact`, `set_agent`, `abort` | Target claimed before resolution or mutable session work. |
| `session fork`, `rewind` | Canonical trimmed target claimed before the session or its turns are read; a fork also claims its `--parent` before resolution. The claims remain through worktree creation, the runtime operation and output. The runtime claims the same lineage for the operation itself, and owns the new session. `session turns` only reads. |
| `session create` | Unscoped claim before project/parent/model preparation, with an overlapping parent claim after inheritance resolution. Both remain through creation, lineage publication and original callback fault classification. New-session/runtime ownership handles the created session. |
| `reminder create` command | Target claimed before content/session preparation; service creation acquires its own overlapping claims. |
| `AltaReminderService.Create` | Separate creation and worker claims before publication. Creation retains protection through synchronous creation observers. The worker retains protection across pending timers, all repeat firings, delivery and any retries owned by its delivery route, including a firing captured before public removal. |
| Reminder delete / disposal | Removing the public entry or canceling a disposal waiter does not release the worker claim. The original worker releases it only after delivery and cancellation traversal return; disposal joins that worker. Retained completed descriptors do not keep a claim. |
| `job start` | Target claimed before command/session/folder preparation, overlapping the job service's own admitted lifetime. |
| `ask`, `skill activate`, `notes set/clear` | Preparation claim before target resolution or session mutation. The underlying pending-input/runtime owner must retain work after the command returns. |

Known targets only protect their session, not every unrelated session. Unknown-target preparation uses a finite unscoped claim; it is not a permanent external-work exemption. Model refreshes used by session create/send are joined through their original completion, even when the command is canceled; cancellation is checked before and after the refresh so it cannot release protection early or submit a canceled prompt. Ordinary model queries retain their existing cancelable-wait behavior.

Built-in query/list/report handlers do not schedule target work, but constructing a tree containing plugin factories still requires the finite unknown-target claim above. Root help uses the same registry-owned invocation. When admission is registered, directly exporting a raw `CreateCommandApp` tree is refused because no original invocation would own its callbacks and fault classification; use `InvokeAsync` instead.

An original Task that faults can still own unconfirmed dependencies. Command actions, model/query/notes/ask preparation and custom reminder deliveries inspect the **original Task's full exception graph**, not only the first exception exposed by `await`. An `AgentDependencyRetentionException` or `PluginEventDependencyException` anywhere in that graph retains the invocation or reminder and its claims; ordinary terminal faults release normally. Registry claims are released only after this classification, not by a nested handler's `using` unwinding. A retained reminder delivery stops repetition and remains owned after removal/disposal; disposal reports its retained failure rather than proving cleanup complete.

LiveTool has no separate `resume` or `host-prompt` command: those are runtime/host entry points and require their own admission. Automatic captured parent-result notifications are also owned by the host, not the notification metadata emitted by LiveTool.

## Regression evidence

`AltaLiveToolRetentionTests.cs` and `AltaReminderRetentionTests.cs` use synthetic roots/providers and the real `SessionRetentionProtection` arbiter. They exercise refusal before session resolution, unknown-parent preparation refusal, blocked resolution/provider/plugin preparation after observer or command cancellation, a concurrent reminder-create/retirement race, overlapping reminders, protection between repeat timers, a creation observer that outlives worker disposal, and deleted captured firing after a canceled disposal waiter. Original callback aggregates with an ordinary first fault and a sibling dependency-retention marker cover external commands, an alternate model initializer, notes mutation and reminder delivery, with ordinary-fault release controls. The existing LiveTool/reminder suites cover normal send, queue, steer, plugin augmentation, repeat, removal and worker disposal behavior.

Initial scoped qualification on 2026-10-11, before the plugin-marker graph follow-up: `dotnet test src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~AltaLiveToolTests|FullyQualifiedName~AltaReminder"` passed all 246 tests with no failures or skips. The plugin-marker follow-up adds direct-sibling and nested-marker command/reminder regressions; these await integrated verification. The scoped command builds the shared libraries and TUI but does not replace full solution, desktop/frontend, or website integration gates.

This boundary does not qualify desktop tabs/drafts, direct hub/provider entry points, plugin-owned tasks or captured host notifications. Their owners and the host's complete registry wiring must be verified before retention readiness is enabled.
