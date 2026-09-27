# Long supplied timeline bodies

This implementation extends the existing persisted user/assistant disclosure
to other inline supplied prose: reasoning, plans, notes and long diagnostic/status
bodies. Bodies over 1,200 UTF-16 units initially show at most 240 units of inert
plain text, shortened by one unit when necessary to avoid splitting a surrogate
pair. Deliberate expansion renders the original through the unchanged Markdown
sanitizer. The existing full-source Copy is available while collapsed.

Terse messages are not collapsed. Headings, summaries, outcome labels and
omission/truncation notices remain outside the disclosure. Messages already routed
through Details retain that single route, with original raw details and Wrap
unchanged. Supplied plan or reasoning text is not interpreted as execution state.

The disclosure captures projected record values and existing App inspection lifetime
for disclosure; equivalent rerenders/locales retain expansion, replacements retire
it. Identity covers the values of the complete `TimelineItem`, including metadata,
details and Copy source; it is not an identity of the original journal bytes or
JSON whitespace normalized by projection. Native modal transitions also dismiss
expansion. Only long inline records are serialized for this identity and subscribe
to modal transitions; terse/detail-only messages do neither. No reads, timers, history
merging or scroll/follow algorithm changes are introduced.

Bounded frontend qualification includes the existing user/assistant regressions,
category-specific following/paused reading/older paging tests, and actual-App
scope/host/Settings/session-switching scenarios. Original Copy remains exact;
Markdown DOM container whitespace is not a source-fidelity contract. Independent
parent verification passed all 67 related tests, strict TypeScript and the production
build. Native accessibility, live-provider and full
suite qualification are outside this slice. Exact results and preserved initial
failure evidence: `tmp/long-timeline-bodies-20260927/REPORT.md`.
