# Inspecting a supplied persisted tool record

Eligible persisted `activity` / `ToolCall` timeline records offer **Inspect supplied
tool record**. This read-only dialog inspects one existing history row. It does not
read more history, invoke a tool, open a path, attach to a runtime, or reconcile live
and persisted events. Existing summaries and original Details, Wrap and Copy remain
unchanged and available.

The optional projection accepts these explicit Details JSON paths, already read by
the TUI's `ToolCallEventInterpreter`: `arguments` (string/object/array),
`result.content`, `result.detailedContent`, `output.body`, and `error.message`
(strings only). Each supplied field is shown under its literal path, without merging
outputs or treating an activity message as output. Object/array arguments are
displayed as compact JSON; string values retain whitespace. The literal tool name
comes only from the supplied row's `name`; missing identity is Unknown, not guessed
from command text or output. Provider/session/run/activity/parent/byte-offset/time
and reported phase are shown as supplied provenance JSON.

Reported phase is not proof of execution, success or completion. No success is
inferred from an absent error. Missing fields are not reconstructed, and outputs
may be incomplete even when a supplied field fits the display limit. The original
row's omission notices remain visible; omitted text/body is also disclosed inside
the inspector. Unsupported shapes use only the original raw view.

Limits follow the existing history projection: Details at most8,192 UTF-16 units,
text at most32,768, identities at most256. JSON additionally has at most2,048
structural/string tokens and32 nesting levels. Duplicate decoded property names
at any object depth, malformed JSON, unsupported field types, oversized or
truncated Details refuse optional inspection rather than interpreting partial JSON.
The dialog shows at most4,096 units per field, avoids splitting a surrogate pair,
and discloses display excerpts. **Copy supplied tool JSON** copies the exact supplied
Details string, including whitespace, not a pretty-printed or complete-original
journal claim. Original timeline Copy behavior is unchanged.

Inspection is keyed by the complete bounded supplied row, not just activity ID or
offset. Record replacement, scope changes, Settings and host/session ABA retire
the review. Native modal transitions, including closing/reopening the same dialog,
cannot revive its Copy action. Clipboard calls already admitted before retirement
cannot be undone; late completion does not update detached feedback. Keyboard
Enter/Space opens the dialog, Escape/Close dismisses it, IME/repeat guards prevent
accidental activation, and focus restoration requires the original connected scope.

Six-language chrome does not translate supplied names, errors, values or JSON and
adds no RPCs. Native WebView2/screen-reader behavior is not qualified. This narrow
subset deliberately does not copy TUI command parsing, output-based name inference,
live accumulation, lifecycle merging, path links or tool grouping. Other provider
shapes and standalone output records remain on the original raw route.
