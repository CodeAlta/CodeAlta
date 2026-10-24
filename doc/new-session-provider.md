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

The provider descriptor control itself uses the cached inventory. In the new-session
composer, opening the draft or changing its provider also loads the selected provider's
model catalog; this can initialize that provider. Agent choices load independently from
the exact project/global prompt catalog, without provider or session creation. Failed
model reads leave available Agent choices accessible and offer an explicit refresh.
Choosing Agent/model/reasoning values changes only local next-send preferences. Merely
translating the controls does not issue new reads or create a session.
The labels/help support en/es/fr/de/ja/zh-CN; canonical IDs are never translated.
Enabled does not mean ready, capable, authenticated or successfully created.

## Admission and confirmation

### Session titles

A title is saved in the summary of the session with who chose it (`AgentSessionSummary.Title`
and `TitleSource`):

- `user`: a title given at creation (`workspace.createSession` with a title, `alta session create
  --title`, `alta session fork --title`), a Rename, `alta session title --set`. Nothing replaces it
  automatically.
- `auto`: the title a session is created with when none is given (the name of its project, or
  `Global Session`), and every title CodeAlta derives.
- no source: a journal written before sources were recorded. It is listed as it always was (its
  saved title, or the first line of its summary when that title is one a session is created with),
  and decided once when the session is next attached or renamed: a title that is one a session is
  created with becomes `auto`, with the title the session was listed under; any other becomes `user`.

A fork, a rewind and a move keep the source of the title unless they are given a title.

How an `auto` title changes is the title mode: the `[sessions] title` setting, or the session's
own `title_mode` in its local state (`alta session title --mode`), which a fork keeps.

| Mode | The title |
| --- | --- |
| `first_prompt` | The title derived from the first prompt, then nothing else. |
| `last_message` | After each assistant message with text, its first line (80 characters at most). |
| `model` (default, `AgentSessionTitles.DefaultMode`) | The content of the last `<session_title>` tag of an assistant message. |

In every mode the first prompt with text names the session (`AgentSessionTitles.FromPrompt`: the
first paragraph without a heading or list marker, cut at the end of its first sentence, on one
line, 80 UTF-16 units at most without splitting a surrogate pair, cut at a word with an ellipsis).
A prompt nobody wrote does not: a turn the provider started by itself, and a message of CodeAlta,
which starts with a `[CodeAlta ...]` line (a delegated-agent message, an update of a child session,
the result of a background job). The host derives the title, so it is the same for a prompt of
CodeAlta Desktop, of `alta session send`, of an automation and of a reminder. The TUI is the
exception: it creates a session that starts from a prompt with a title it derives itself
(`SessionPromptText.CreateInitialSessionTitle`), which is a title that was given (`user`) and so
stays, as it always did there.

The live `AgentSession` is the only writer of an `auto` title. It learns its mode when it is
created or resumed (`AgentSessionCreateOptions.TitleMode`) and while it is attached
(`IAgentSessionTitleProvider.SetTitleMode`, through `AgentHub.SetTitleModeAsync`): at once for
`alta session title --mode`, and with the next prompt of the session when the setting changed.
Every summary write goes through `UpdateSummaryAsync`, which reads the source under the summary
gate, so a Rename during a run wins. Rename updates an attached session through
`IAgentSessionTitleProvider.RenameAsync`; an unattached session is renamed directly in the journal,
and an attached runtime without this capability refuses Rename.

In mode `model`, and while nobody named the session, the session adds one paragraph to the
developer instructions of each run (`SessionRuntimeService.SessionTitleGuidance`): it asks the
model to start its answer with a `<session_title>` line when it answers the first prompt, and
later only when the subject changed. The paragraph is composed per run, so a change of mode needs
no new attachment; Claude Code is told of it by its `<codealta_instructions_update>` note.

The tag is removed where assistant text leaves the session, whatever the mode
(`AgentSessionTitleTag`): from the completed content events (the timeline, the journal, parent
notifications, `alta session result`, the plugins), from the streamed deltas, where a stateful
filter per content holds back what could be the start of a tag, and from the summary. The raw
assistant message, in the conversation and in `local.assistantMessage`, keeps what the model
wrote. A tag in a fenced code block, or right after a backtick, is quoted text and stays, as does
one that spans lines or holds more than 200 characters.

### Prompt suggestions

The model of a session the user writes in suggests the next prompt with its answer, whatever the
provider: no request of its own is made for it. Three things decide that a run asks for one
(`AgentSession.SuggestsNextPrompt`, read once when the run starts):

- the session takes suggestions (`AgentSessionCreateOptions.PromptSuggestions`): `SessionRuntimeService`
  sets it in a host that shows them (`CodeAltaHostOptions.ShowsPromptSuggestions`: CodeAlta Desktop, not
  the terminal UI) for a session without a parent (nobody writes the next prompt of a session an agent
  drives);
- the instruction is given (`PromptSuggestionInstructions`, `SessionRuntimeService.PromptSuggestionGuidance`);
- the setting of the user says so (`PromptSuggestionsEnabled`, which reads `[sessions] prompt_suggestions`
  with `CodeAltaConfigStore.LoadGlobalPromptSuggestions`, on by default), so a change reaches a session
  with its next prompt.

The instruction is a paragraph of its own after the title paragraph, at the end of the developer
instructions (`AgentSession.SessionInstructions`), starting with "Next prompt suggestion:" so that
Claude Code is told by its `<codealta_instructions_update>` note when it comes or goes. It asks the
model to end its final answer of a turn with a line that holds only
`<next_prompt>…</next_prompt>`: the prompt the user is most likely to send next, in their words and
language, in a few words, left out when no next step is obvious.

`AgentSessionTitleTag.Filter` removes this tag as it removes the title tag, from the streamed deltas
and from everything shown (`AgentNextPromptTag`: a content of up to 1,000 characters is removed, so
that a suggestion too long to be one is not shown either), in every session, whatever the setting:
a model may go on writing it after the setting changed. The suggestion of a run is the content of
the tag of the last text of its last assistant message; an answer without one suggests nothing.
`AgentSession` publishes it as an `AgentPromptSuggestionEvent` right after the `Idle` event of the
run, only for a run that asked for it and only when it is one line of at most 256 characters
(`AgentPromptSuggestionEvent.Normalize`). Like `AgentBackgroundTasksEvent`, it is never recorded.
The runtime keeps the last one in the entry of the session until a run starts
(`SessionRuntimeCurrentEntry.PromptSuggestion`), taking it only while it knows no active run, and
CodeAlta Desktop reads it with the runtime state of the session
(`SessionRuntimeStateEntry.PromptSuggestion`, sent as it is or not at all); see `doc/desktop.md`,
"Prompt suggestions".

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
Draft creation captures the effective provider and Agent/model/reasoning preference.
After the created session is confirmed by a fresh catalog read, its authoritative choices
must validate those preferences before copying the draft into an empty destination and
installing its next-send selection. Failed validation preserves the source; nothing is sent.
The explicit owned new-session draft now permits [bounded PNG handoff](prompt-images.md)
only with current immutable input and an empty eligible destination/capacity preflight.
Storage failure retains the source and reports uncertainty; there is no cross-storage
transaction or reload-durable image promise. No automatic Send is added.

## Verification scope

Disposable fake-provider owned-host tests exercise the actual non-default runtime
creation path, exact/disabled/missing/malformed IDs, default behavior, canceled
waits, held originals, archive exclusion and shutdown drain.

Evidence and exact commands: `tmp/new-session-provider-20260927/REPORT.md` (ignored).
Native WebView2, real providers/authentication and concurrent external registry/config
writers are not qualified. Existing-session provider switching, model/reasoning/auth
workflows and broad provider-management parity remain separate.
