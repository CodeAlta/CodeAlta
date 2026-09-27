# Desktop provider choice for new owned sessions

The inline new-session form and owned local draft **Create and transfer** action share
a provider choice. It applies only to the next deliberately created session, never
to an existing session or a pending original. The choice is retained in the current
App window; it does not edit provider configuration or the global default.

**Default or first enabled provider** preserves the existing host behavior: choose
the enabled default, otherwise the first enabled descriptor in registry order.
Explicit choices are literal canonical IDs from the already-cached configuration
snapshot, limited to its first 32 descriptors. Disabled, ambiguous and malformed
identities are not offered. Oversized owned-registry IDs are omitted rather than
truncated into a different identity. Catalog-only configuration is not creation
authority. The list can be stale or incomplete.

Opening, editing, choosing, closing or translating this control performs no new
inventory read, initialization, model read, authentication, probe or session creation.
The labels/help support en/es/fr/de/ja/zh-CN; canonical IDs are never translated.
Enabled does not mean ready, capable, authenticated or successfully created.

## Admission and confirmation

Create remains effectful. `WorkspaceCreateSessionRpc` validates the bounded optional
provider ID and exact epoch/scope, then resolves exactly one currently enabled
descriptor under the existing shared catalog admission gate. Explicit selection
never falls back to the default. After awaited project resolution it rechecks that
the original descriptor is still registered and enabled before invoking the owned
creation callback. It does not substitute a replacement descriptor. This is an
admission-time check, not a configuration lease or general registry transaction.

The original task survives caller-wait cancellation and is drained at shutdown.
Archive/import/rename/delete exclusion remains with the existing catalog owner.
Provider initialization and journal/provider writes may occur on deliberate Create;
failure is not proof that creation had no effects.

The immutable renderer request includes the provider choice. Reply correlation
checks that original choice along with host epoch and project/global scope. An
explicit-provider result must also match the provider key on a unique newly read
catalog session before existing navigation/draft transfer is allowed. Input/provider,
scope, host, Settings and modal lifetime changes invalidate publication, including
same-value ABA. Locale alone is presentation, not an operation identity.

An admitted but unconfirmed original blocks further creation in this App window,
including after form closure or changing provider/scope. Only a correlated definite
refusal or successful fresh matching publication releases it. Manual list refresh
does not unlock it. This is in-memory uncertainty retention, not durable receipts,
reload-safe reconciliation or a guarantee that reloading permits a safe retry.
Local text and image drafts are not discarded: existing text-transfer fences remain.
The explicit owned new-session draft now permits [bounded PNG handoff](prompt-images.md)
only with current immutable input and an empty eligible destination/capacity preflight.
Storage failure retains the source and reports uncertainty; there is no cross-storage
transaction or reload-durable image promise. No automatic Send is added.

## Verification scope

Disposable fake-provider owned-host tests exercise the actual non-default runtime
creation path, exact/disabled/missing/malformed IDs, default behavior, canceled
waits, held originals, archive exclusion and shutdown drain. Complete actual-App
browser tests cover descriptor-only choice, six-language canonical IDs, pending and
uncertain retention, mismatched reply/catalog provider, input/provider/modal ABA,
draft preservation and narrow light/dark keyboard/focus/synthetic IME behavior.

Evidence and exact commands: `tmp/new-session-provider-20260927/REPORT.md` (ignored).
Native WebView2, real providers/authentication and concurrent external registry/config
writers are not qualified. Existing-session provider switching, model/reasoning/auth
workflows and broad provider-management parity remain separate.
