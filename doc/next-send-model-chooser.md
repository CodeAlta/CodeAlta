# Cached next-Send model chooser

The owned-session composer retains its quick model, prompt and reasoning selects.
**Search models** opens a native dialog; **Commands / Ctrl+P → Next Send model
selection** reaches the same control when it is available. This is distinct from
Models Settings and never switches providers or changes a global default.

The dialog searches only the already observed eligible session choices (at most
128 models; oversized or malformed observations disable this additional control).
It displays literal model names/IDs, supplied efforts and tri-state image support.
Unknown image support is not “No.” Empty effort lists do not invent supported
efforts. Opening, searching, locale changes and closing acquire no inventory.

Model and effort edits remain tentative until **Use model for next Send**. Model
changes reset effort using the existing selection normalization. The existing
selection owner validates and stores the result; normal Send captures it. Prompt,
text and image drafts are not changed, and running, queued or already captured
requests are not retargeted. Close/Escape discards edits; initial Enter in search
does not apply. Tab navigation uses native controls.

Apply rechecks the exact choices object, selection and revision, input revision,
host/session scope, original input lifetime, capability, submission-owner revision
and pending Send exclusion. Native modal transitions retire stale edits. Matching
IDs after a scope/modal transition cannot revive the captured editor. A refused
Apply keeps newer local selection intact. Focus restoration requires a connected,
enabled origin in the original available scope, without another modal or newer
focus move.

This is bounded cached UI behavior, not live capability verification, provider
initialization/authentication, catalog refresh or native WebView2 qualification.
