# Runtime-owned provider-event forwarding

> Qualification: parent-audited offline tests pass for nine controlled ownership
> methods, ten real Host/Runtime/Hub methods using only a registered fake provider,
> four source methods and the historical Desktop preservation reader. This is
> not a completed shutdown or compatibility qualification for configured providers,
> native frontends or outer-owner failure paths.

`SessionRuntimeService` owns forwarding through the runtime-specific
`OwnedProviderEventForwarding` helper. This does not introduce a replacement host,
a revisioned shared projection, or a lossless provider/runtime event stream.

## Ownership boundaries

Three kinds of work have different release points:

1. **Admitted runtime bodies and setup.** An already-cancelled caller is rejected
   before external admission. After admission, cancellation of a caller's wait is
   not evidence that the actual body, actor command, or provider operation ended.
2. **Complete callback tails.** Callback admission synchronously retains both work
   and its observer before launch. The runtime owns projection, parent delivery,
   queued submission, completion bookkeeping, and recursive queue probing until
   their actual completion, independently of the callback's discarded return task.
3. **Attachment dependency uses.** Projection and handle uses refer to their
   captured attachment/entry. Retirement joins these uses, not the complete
   forwarding tails. A queued tail can therefore release projection and request
   attachment replacement without retirement joining that same tail.

The bookkeeping gate protects records only. Provider calls, subscription disposal,
cancellation callbacks, and joins run outside it. Actor commands prepare and
revalidate transitions; callers join transition tickets outside the mailbox.
Publication verifies the exact ticket identity. Send and Compact recheck compatible
options and pending prompt in the same actor command that acquires a handle use.
Setup's durable local-state read/modify/append shares the queue mutation mailbox.
Detached-session actors remain
owned until runtime shutdown rather than being removed and recreated during
replacement.

## Retirement and shutdown

Retirement closes handle admission and independently launches retained cancellation
and provider abort. Neither must finish before the other can start. Setup and
captured handle/completion uses settle while provider and actor dependencies remain
available. Callback admission closes before actual subscription disposal; callbacks
already admitted retain projection uses, and rejected callbacks cannot access a
replacement attachment.

Successful subscription disposal and completed projection uses precede provider
stop. A throwing subscription disposal is **not** successful unsubscription: the
helper skips stop and retains runtime-owned attachment references and actors.
Runtime shutdown faults before disposing actors or completing the event stream if
attachment release remains unconfirmed.
The failure retains the forwarding owner for inspection; there is no forced release,
hidden retry, or fabricated infinite wait.

**Outer-owner limitation:** unchanged `CodeAltaHost.DisposeCoreAsync` catches a
runtime disposal failure and still attempts `AgentHub` and model-provider registry
disposal. Also, `AgentHub.StopSessionAsync` removes a handle from its table before
awaiting entry disposal. A settled retirement failure therefore does **not**
guarantee that outer Host/Hub/provider dependencies remain undisposed or that a
failed stop leaves its handle registered. Retaining runtime-owned references is
not the same as preserving the lifetime of the objects they reference. This
prerequisite does not change either outer-owner behavior.

Acquired handles are registered before metadata awaits. Late setup installs its
subscription receipt even during shutdown, completes its setup record, and retires
instead of publishing. Shutdown closes external admission, retires attachments,
joins actual runtime bodies and complete tails, and tears down actors/event output
last on the confirmed-success path. It does not infer drainage from one dictionary
snapshot. Noncooperative actual work keeps shutdown pending and dependencies alive;
there is no timeout-based production release.

## Queue and parent delivery

Queued run completion and finally bookkeeping use captured entry identity rather
than whatever attachment currently occupies the session dictionary. Projection
uses end before queue or parent orchestration, and handle uses end before recursive
queue probing or entering a parent. This separation supports normal pending-prompt
replacement, self-parent delivery, and cyclic parent routes without holding the
source actor/use while entering the target.

The private parent-finishing lookup awaits the existing cached journal store and
project metadata directly. It does not use an early-exit catalog enumeration as
proof that the catalog's background store producer has settled. General public
catalog enumeration ownership is outside this prerequisite.

## Failure reporting, retention, and limitations

Callback failures escaping existing runtime catches are independently observed;
retirement stages retain and observe their actual work. Reportable failures are
ordered by admission ordinal and stage, not delivered solely through runtime
events. Attachment failure wrappers retain immutable captured session/handle identity
and the original exception; they do not resolve identity through a replacement entry.
External operation failures remain results of their owned tasks.

There is no new backpressure or drop policy. Outstanding work, failures, failed
attachment receipts, and detached-session actors can be retained without a fixed
bound. The existing destructive bounded runtime event stream remains unchanged:
this work does not promise replay, multiple-reader delivery, revision consistency,
or losslessness. The provider's `Action`/`IDisposable` subscription contract does
not itself establish a general provider callback-drain acknowledgement.

The nine ownership tests and ten fake-provider runtime integration tests passed
after separate exact-method parent admission. They retain original work and
observers, release controlled gates during cleanup, independently start bounded
observations, preserve unexpected failures, and retain failed/unconfirmed fixture
roots. Their five-second observations are test diagnostics, not production release
deadlines. Whole-original reconstruction passed for three frozen originals using
95 ordered inverse tuples / 96 occurrences and nine LF/CRLF/mixed representations,
including preservation negatives and inherited reader routing. The historical
Desktop 16-original / 42-edit / 48-reconstruction boundary also passed unchanged.

Initial verification exposed fixture-only nullable seed assignments and expected
cancellation accounting: canceled tasks may produce different exception instances
for separate awaits. The corrected fixture associates expected cancellation with
the exact task and asserted type; faulted tasks still require exception identity.
No configured provider, authentication, network, default profile, frontend/native
application or broad suite ran. Fresh explicit fixture roots and existing cached
SQLite journal/cache I/O were admitted; all forwarding fixture roots are retained.
