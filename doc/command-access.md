# Bounded Desktop command access

The Desktop implemented-actions palette and shortcut Help share registry metadata for
Skills inspection, current-process Application Logs, last-observed session usage,
Open Project and Help. Search accepts translated labels in en/es/fr/de/ja/zh-CN and
canonical English labels/aliases (`skill`, `logs`, `show_logs`, `context_usage`,
`open_project`, `help`). Action IDs remain stable and are never translated.

| Surface | Access | Boundary |
| --- | --- | --- |
| Skills | Palette; Ctrl+G then Ctrl+K outside text in the workspace | Verified owned selected scope only through these entry points. Opens the existing Settings section; no scan until explicit Scan. Not effective skill discovery/activation/management. |
| Application Logs | Palette; Ctrl+G then Ctrl+L outside text in the workspace | Existing Settings log panel and explicit read/clear semantics. Current-process captured memory, not logfile browsing. |
| Usage | Palette; existing composer icon | Exact eligible owned writable session and connected, enabled original trigger. Invokes the existing modal/read path; one explicit-open read, no search/hover/locale probes. |
| Open Project | Palette; existing Ctrl+O outside text | Existing saved-project/import dialog and handlers; navigation is not import authority. |
| Help | Palette; existing F1 / ? outside text, ? in an exactly empty regular prompt | Existing Help handler, close and focus ownership. |

TUI `BuiltinShellCommands` maps Skills to Ctrl+G, Ctrl+K and Logs to Ctrl+G, Ctrl+L.
Its usage mapping Ctrl+G, Ctrl+U **conflicts with existing WebView Context state**.
That existing mapping is preserved; usage has no new keyboard chord in this slice.
Ctrl+E remains reserved for TUI Edit File, not a Desktop file-editor implementation.
Slash-command execution, new plugin/provider/config/host functionality, complete TUI
shortcut parity and native keyboard qualification are not added.

Availability is only presentation. Production palette selection and deferred close
dispatch recheck original generation, selected session/project, epoch, exact usage
scope/path and trigger readiness/identity. Revocation without a catalog/generation
change still rejects an earlier capture. Skills and usage are not offered in archived,
catalog-only, ambiguous or unavailable owned scopes. Global Help/Open Project/Logs can
be available without owned runtime authority, but cannot execute a stale capture.

An instance-owned command generation accompanies existing scope/input/modal
invalidation. Only the palette's own open/close transitions are exempt from this *new*
command fence. Existing creation-generation invalidation, synchronous native
`beforetoggle` checks and normal state publication remain unchanged and run for those
transitions too. Locale and palette search do not establish a new command lifetime.

New chords require Ctrl on both strokes, an unmodified suffix, a nonediting workspace
target, no modal and no IME/229/repeated/defaultPrevented event. Unavailable suffixes
remain unconsumed; in particular Ctrl+L is not stolen without an eligible captured
prefix. Meta is not substituted for Ctrl. Settings stays the sidebar-only ~80vw/80dvh
modal over the mounted workspace; Reminders intentional unmount is unchanged.

Evidence: `tmp/command-access-20260927/REPORT.md`. Final bounded run: **138/139 pass,
zero skips**; the existing transient composer expanded-editor character assertion
failed at line 230. No retry-to-green or causal claim. New registry and complete actual
App tests pass; strict TS/build/whitespace pass. Earlier bounded run passed 139/139
before the final capability-without-generation capture guard. All red evidence remains.
Independent parent acceptance, native/full qualification, prior split-layout cleanup
and parked composer/15px residuals remain open.
