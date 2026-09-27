# Cached next-Send agent prompt chooser

The owned writable composer retains its quick prompt selector and adds **Search
agent prompts**, also available as **Next Send agent prompt selection** in Commands.
Settings → Agent Prompts remains the separate inventory/body inspection route.

The chooser uses only the composer's already observed `SessionChoicesResponse`.
It performs no catalog refresh, body lookup or provider call. Search is bounded to
256 UTF-16 units and uses case-insensitive literal ID/name terms, not regular
expressions. IDs and names are displayed literally. At most 128 distinct prompts
are accepted, with nonempty IDs up to 256 units and names up to 512. Malformed,
missing, ambiguous or oversized observations do not authorize opening or Apply.
These are limits of this optional prompt chooser only, not admission limits for
the shared composer. Larger valid catalogs still support the existing quick
selectors, model chooser and image Send. An observed prompt absent from a refreshed
inventory remains visible in the quick selector, allowing explicit valid recovery;
the optional prompt chooser remains unavailable for that observation.

Selecting a row is tentative. **Use prompt for next Send** explicitly applies through
the existing next-Send selection owner, preserving provider, model and reasoning
effort. Close/Escape discards the tentative choice. Enter in search does not Apply
or Send; focused buttons retain native keyboard activation. IME and repeat events
cannot implicitly submit. Closing restores an eligible connected original trigger.

**Observed prompt (choices snapshot, not live execution)** is the prompt ID reported
by that cached choices response. **Local next-Send prompt** is the local selection
(or the observed fallback if no valid local selection is available). Neither establishes a running
agent's current prompt. A retained original Send prompt is shown separately and
is not rewritten by local edits. No activation, queue retargeting, source/config
editing, default change, automatic Send or retry is performed.

Apply rechecks choices identity and bounded contents, observed-current data,
selection/store revisions, epoch/session/project/input lifetime, capability and
the submission owner. Modal transitions, including close/reopen of the same native
dialog, invalidate the review. Pending or uncertain originals remain locked.
If refreshed choices omit a stored model/effort, prompt-only editing stays disabled
rather than silently replacing that stored choice with observed defaults. An explicit
valid choice through the quick selectors restores eligibility.
Language changes update static chrome only; they do not refresh choices or create
a new editing lifetime. Six locales share the existing narrow/light/dark dialog
styling. Browser-fixture tests do not establish native WebView2 or screen-reader
qualification.
