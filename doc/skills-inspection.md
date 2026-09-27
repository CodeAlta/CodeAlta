# Desktop raw Skills inspection

Open **Settings → Skills**, choose a source root, then explicitly select **Scan raw candidates**. This read-only surface lists **raw `SKILL.md` file candidates**, not loaded, visible, enabled, trusted, usable or active skills. Ignored paths can appear. An empty result, or a `complete` raw traversal, never establishes absence or a complete effective inventory.

## Supported scope and reads

An owned host and uniquely verified, nonarchived saved session are required. Catalog-only, ambiguous, truncated, unknown and archived scopes cannot start a scan. Each action selects exactly one root:

- **Project CodeAlta:** `<verified project>/.alta/skills`, available only for a project session.
- **User CodeAlta:** `<host CatalogOptions.GlobalRoot>/skills`, available for verified project or global sessions. This uses the explicitly configured host root, not process-home discovery.

The renderer sends session creation identity, expected host epoch, project identity/path for comparison, root kind and request key—not a filesystem root to traverse. The host reads the exact bounded persisted session header, verifies project ownership against the bounded catalog (including archive state), and derives the root from that verified header or host catalog options. No ordinary catalog traversal, Git walker, ignore files, profile/ancestor/global discovery, plugin callbacks, provider probes, session activation or Display attachment are performed.

`RawSkillCandidateReader` and `SkillCandidateMetadataReader` perform the reads. Name and description are returned only when supported parsing and exact parent-directory/name validation succeed. Other outcomes remain distinct: invalid, unsupported YAML, too large, missing, linked, denied, read error, invalid encoding and omitted. Only relative paths, fixed diagnostic codes and bounded metadata are returned; no source body, other frontmatter values, related files, configuration contents or exception strings are exposed. Metadata is author-supplied text, not proof of trust.

## Combined bounds per explicit action

- One root; 256 traversal entries plus one sentinel, 64 directory attempts, 32 pending directories, depth 6.
- At most 16 raw candidates; paths at most 1024 UTF-16 units, names at most 255, aggregate candidate paths at most 8192. Fixed VCS directories and observed reparse/link entries are omitted with diagnostics.
- At most **4 metadata read attempts**, each at most 256 KiB plus one sentinel: **1,048,580 bytes maximum attempted file consumption**, regardless of reported byte counts. Remaining candidates are explicitly `omitted/read_budget`; selecting one does not cause a second read. Reported bytes can be zero after an I/O failure and are not an exact failed-read accounting claim.
- Preflight additionally permits one 32 KiB-plus-sentinel header and the existing project ownership scan (128 catalog entries plus one sentinel, 32 KiB per source plus its bounded sentinel, 512 KiB aggregate). Global scope does not enumerate the project catalog. Including project preflight, one action permits at most **386 counted entries, 65 directory-enumeration attempts and 1,605,637 consumed file bytes**. Ancestor attribute checks are additional, path-length/component-bounded operations, not directory traversals. These preflight costs are not included in the raw-scan counters shown in the UI.
- Displayed parsed name/description are at most 64/1024 UTF-16 units. Unsafe display text is omitted, not silently truncated. Response serialization is capped at **64 KiB**; tail row omissions are counted. The existing 208 KiB bridge and CSP are unchanged.
- One original read per host service and one per App. A five-second cooperative host deadline checks between filesystem operations. Blocking OS operations/disposal cannot be forcefully interrupted; this is not a hard wall-clock bound.

## Lifetime and UI

Opening Settings or its Skills section, choosing a root, searching, selecting metadata, navigation and dismissal never scan. Search is local and capped at 128 characters; selected details contain only already returned metadata. No Create/Edit/Delete/enable/activate or retry control is added. Another scan requires another explicit action after the original definitively settles.

The App owns the original read independently of the dialog. Exact host/session/project/catalog/input revisions and same-value ABA fences discard late metadata after dismissal, root/search changes or scope/authority loss. Validated correlated host evidence is delivered only to the original shared capability, even after dismissal; malformed replies cannot invalidate unrelated authority. A transport failure/30-second waiter timeout cannot prove host completion: the original slot stays blocked for the rest of that App lifetime, with explicit unknown status and no automatic retry. Reopening the dialog does not release it. At most one response subset is retained; scope changes clear visible metadata, and no scan result is persisted to localStorage.

Settings retains its existing bottom-left entry, native approximately 80vw × 80dvh dialog and mounted workspace. Skills inspection does not alter drafts, immutable pending Sends, runtime ownership or workspace navigation.

Static headings, source/search/scan controls, metadata labels and omission explanations follow the six supported [WebView languages](webview-localization.md). Candidate names, descriptions, relative paths, status/diagnostic codes and owner messages remain literal. Switching language does not scan, invalidate the input capture, clear search/selection, release a pending original or retry an uncertain read.

## Limits of the claim

Filesystem attribute and ownership checks are point-in-time, not pinned handles or a cross-process snapshot. External path replacement/edit races and blocking/network-mounted filesystem behavior remain possible; these readers are not a sandbox against noncooperating writers. No effective inventory, ordinal/case/no-I/O bounded ignore matcher, enablement/config write, provider/plugin lifecycle, activation or native UI qualification is completed by this slice. Tests use disposable generated roots and fake bridge data only; permission-denial/symlink checks depend on platform support.
