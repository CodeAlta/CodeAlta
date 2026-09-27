# Retained Queue intent review

The experimental owned web/Desktop composer exposes **Review retained intent** for local unresolved
Queue admission and cancellation originals. This is read-only evidence from the existing App-owned
`createQueueSubmissions` instance, not a queue inventory, run-state observation or durable recovery.
The separate `RetainedRequestStrip` remains present and unchanged.

## Source and authority

- `retainedQueueEvidence.ts` projects only originals matching the selected host epoch and session.
  The owner retains at most one Queue original per session and separate cancellation originals.
- Pending means the exact frontend waiter is pending. A settled waiter without definitive evidence
  means outcome unknown, not rejection, insertion, execution or completion.
- Queue review shows literal request text and exact host/session/key/runtime/attachment provenance.
  Cancellation review shows its own key and original target operation; no Queue text is inferred.
- Definitive admission/rejection or valid explicit reconciliation may remove originals. The strip
  disappears when none remain. Receipts cannot reconstruct settled text or lost renderer state.
- Opening, translating, closing and Copy perform no RPC. There are no new retry, cancellation,
  refresh, edit, delete, reorder, repeat, convert-to-steer or bulk-clear controls. Existing guarded
  actions retain all mutation authority; uncertainty does not unlock a competing original.

## Review lifetime and Copy

`QueueIntentReview.tsx` subscribes to the existing owner revision. `OwnedSessionPanel.tsx` captures
the current scope signal/capability, input lifetime/revision, image-owner revision, runtime observation
and secondary Queue draft revision. A changed owner or captured lifetime retires stale review/Copy.
Native modal transitions retire the review except its single initial opening. StrictMode cleanup
does not manufacture a native close/reopen. Disconnected originals cannot copy.

Only an original Queue text has Copy. The literal text, including whitespace, is passed to the
clipboard; the composer, selections and images are not replaced. Duplicate pending Copy attempts
are excluded. Failed/unavailable clipboard feedback is local; a late result after retirement cannot
publish success into a newer review. A clipboard write already issued cannot be revoked.

The native dialog contains focus, supports Escape and focus return, and ignores composing/repeated
activation keys. Finite chrome is translated in English, Spanish, French, German, Japanese and
Simplified Chinese; request keys, IDs and supplied text remain literal. The bounded preview is not
the source of Copy. Full text is scrollable, with grouped provenance and wrapping at narrow widths.

## Verification limits

Disposable actual-App coverage exercises Queue and cancellation-only/coexisting originals, pending
uncertainty and definitive settlement, exact text/Copy, modal/own-dialog/session/host/input/image
retirement, project navigation, attachment observation replacement without retargeting, clipboard
failure/unavailable/late completion, keyboard/focus, six-language no-added-call checks and narrow
German/Japanese light/dark layouts. Owner tests separately cover source projection and settlement.
This is not native WebView, live-host, screen-reader, persistence or full TUI queue parity qualification.
Independent parent acceptance remains required; durable execution logs are listed in the batch report.
