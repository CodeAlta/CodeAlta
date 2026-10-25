# Development guide

Rules that hold across the repository. The reasons are in [architecture](architecture.md) and the area docs; a rule that applies to one piece of code is a comment in that code, not a line here.

## Architecture rules

1. **Never read session journals in normal operation.** A fact about sessions that a listing, a relation, a state, a resume or a search needs is written to the application database when the session is written (a column, or a table of its own). Journals are read only to rebuild the database, to project the sessions another process writes (their head and tail), to page the history of the open session, by the statistics and search followers from their cursors, and to build what a provider needs from the last compaction. Each such path has a test that holds the journals locked.
2. **One writer per session state**: the session actor. No process-wide lock around providers, tools, compaction or journals; no other actor framework.
3. **No static mutable data**: instance-owned state, services, or frozen static data.
4. **Layers**: nothing below a frontend references a frontend; orchestration logic never lives in a frontend; `CodeAlta.Hosting` is composition only; Desktop never references the TUI.
5. **New features are for CodeAlta Desktop.** The TUI gets no new screen, dialog, command or setting unless asked, but it builds and its tests pass after a shared change.
6. **Instance state goes through the state root** (`CatalogOptions.StateRoot`), shared files through the global root.
7. **Persist through the owning store** (config, journals, drafts, plugin state, UI state, the application database). A file another process may write is read with sharing and replaced atomically (`SharedTextFile`, `StagedFileReplacement`).
8. **The system prompt stays byte-stable**; session state reaches the agent as a host-context note.
9. **Anything that starts programs or runs code declares it** where the existing ones do, so a session with the shell turned off cannot run programs.
10. **Providers own protocol, credentials, readiness and models only**; they never own sessions. Provider details of a failure travel on the exception; retries and fallbacks are journal records.
11. **Native engines run in a worker process**, never in the host; their native files are downloaded on first use, pinned by hash, never packaged.
12. **Sensitive data**: logs, journals, prompts, outputs and payloads are sensitive; credentials never go in logs, errors or diagnostics.
13. **Public APIs have XML docs** with their exceptions; inputs are validated early; nullability is respected; no warning is suppressed without a reason.

## Async and threading

- UI flow uses plain `await`; `ConfigureAwait(false)` is for infrastructure code whose continuation touches no UI state.
- Desktop RPC handlers run concurrently on the thread pool: shared state is thread safe, the window is touched through its dispatcher, one file has one writer.
- No page call to the host is given up on a timer: it ends with its answer, its abort signal or a closed connection.
- Async completions and provider callbacks become runtime commands or events before they touch actor-owned state.

## Desktop frontend

- One folder per feature under `src/CodeAlta/frontend/src`; logic without DOM is in plain tested functions; tests sit beside their module.
- Keystrokes, tokens and timers never re-render the whole app; animations in window regions never move a box.
- A UI performance change is measured before and after in the developer instance.
- Several windows: no "the window" singleton; route per window.
- Markdown and foreign HTML are security boundaries (`markdownBoundary`, the sandboxed `mcpapp` scheme).

## Tests

- New or changed behavior has tests; a bug fix starts with a failing regression test.
- Tests hold on a loaded machine: bound by counts, start concurrent sides on a signal, deadlines only tell a hang. A test that fails only while two suites run is rerun alone before it is called a regression.
- Browser tests use `browserTarget.ts` and `waitForBrowserPort`, end browsers with `endBrowser`, and render fixtures under StrictMode.
- All .NET tests, `npm test` and `lunet build` pass before submitting; CI runs on Ubuntu, so no Windows-only path assumptions.

## Documentation

Two audiences, two rules:

- **`site/docs`** is the user guide, also read by CodeAlta's agents to answer users. Update it when what a user sees or does changes: edit the page of the feature, keep it concise and task-oriented, short headings (the page table of contents shows them), pictures with the sections they illustrate, links instead of repetition. Split a page that grows too long into sub-pages of substance (a folder with `readme.md` and `menu.yml`), never into fragments. Every page is listed in its `menu.yml` and uses only the markup `ShippedDocumentationMarkup` reads.
- **`doc/`** is the architecture: responsibilities, boundaries, main flows, invariants and their reasons. Update it only when one of these changes, not for a feature, a fix or a setting. No history, plans, decision logs, test inventories or per-option detail. A "why" that belongs to one piece of code is a comment there.
- `readme.md` lists the features at a glance; it follows the user guide, not each commit.
- Plans and work items live under `.alta/`, never in `doc/`.
