import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Button, Checkbox, InputGroup } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindowSurface } from "../AppWindow";
import { modalDialogOpen } from "../modalDialogs";
import { sessionTime, timelineTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { branchRows, forkConsequence, initialSelection, keepSelection, mergeTurnPages, rewindBlock, rewindConsequence, rowId, runningTurnNumber,
  turnForOffset, turnSender, turnText, type BranchMode, type BranchRow, type BranchTurn, type RewindBlock, type TurnSender } from "./branchPoints";
import type { BranchRequest } from "./branchRequests";
import { branchMessage, outcomeUnknown, turnsMessage, turnsOutdated, type ForkResult, type RewindResult, type TurnsRead } from "./sessionBranchRpc";

const pageStep = 6;
/** The pages read to find the turn of a message that is not among the last turns: 100 turns each. */
const maximumSearchPages = 30;

/** What the window asks for: the turns of its session, a fork, a rewind. */
export type BranchDialogHost = Readonly<{
  readTurns: (beforeTurn: number | null, signal: AbortSignal) => Promise<TurnsRead>;
  fork: (beforeRunId: string | null) => Promise<ForkResult>;
  rewind: (beforeRunId: string, keepRemovedTurns: boolean) => Promise<RewindResult>;
}>;

/** What the pane knows of its session that decides whether it can be changed now. */
export type BranchFacts = Readonly<{ canMutate: boolean; running: boolean; question: boolean; background: number;
  /** The composer of the session holds a draft: a rewind keeps it, and offers the prompt of the removed turn beside it. */
  draft?: boolean }>;

type Translate = (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => string;

const senderLabel: Readonly<Record<Exclude<TurnSender, "you">, MessageKey>> = { agent: "Agent", answer: "Answer to a question", job: "Background job",
  provider: "Provider" };
const senderLine: Readonly<Record<TurnSender, MessageKey>> = { you: "Sent by you", agent: "Sent by an agent session", answer: "Your answer to a question of the agent",
  job: "Result of a background job", provider: "Started by the provider itself" };
const blockLine: Readonly<Record<RewindBlock, MessageKey>> = {
  end: "A rewind goes back to a turn: choose the turn to rewind to.",
  read_only: "This session is read-only here: it cannot be forked or rewound.",
  running: "The session is working: it can be rewound once it is idle. A fork is possible now.",
  question: "A question of this session waits for your answer. Answer it before a rewind.",
  background: "A command or a task of this session still runs in the background. Wait for it or stop it before a rewind.",
};

function turnCountText(t: Translate, count: number): string { return count === 1 ? t("1 turn") : t("{count} turns", { count }); }

/** What a turn did, in a few words: the answers, the tool calls, what was sent while it ran, a compaction. */
function turnActivity(t: Translate, turn: BranchTurn): string[] {
  const parts: string[] = [];
  if (turn.assistantMessages > 0) parts.push(turn.assistantMessages === 1 ? t("1 answer") : t("{count} answers", { count: turn.assistantMessages }));
  if (turn.toolCalls > 0) parts.push(turn.toolCalls === 1 ? t("1 tool call") : t("{count} tool calls", { count: turn.toolCalls }));
  if (turn.steers > 0) parts.push(turn.steers === 1 ? t("1 message sent while it ran") : t("{count} messages sent while it ran", { count: turn.steers }));
  if (turn.compactions > 0) parts.push(t("compacted the conversation"));
  return parts;
}

const stateLabel = (status: string): MessageKey | null => status === "failed" ? "Failed" : status === "unfinished" ? "Unfinished" : status === "running" ? "Running" : null;

/** The lines that say what a fork at a point does. */
function forkLines(t: Translate, point: BranchTurn | null, total: number, runningTurn: number | null): { text: string; warning?: boolean }[] {
  const { kept } = forkConsequence(point, total, runningTurn);
  const lines: { text: string; warning?: boolean }[] = [];
  if (point) {
    lines.push({ text: kept === 0 ? t("A new session starts empty: no turn is before this one.")
      : kept === 1 ? t("A new session starts with turn 1 of this session.") : t("A new session starts with turns 1 to {last} of this session.", { last: kept }) });
    lines.push({ text: t("This session is not changed.") });
    lines.push({ text: turnSender(point) === "you" ? t("The prompt of turn {number} is put in the composer of the new session, for you to edit and send.", { number: point.number })
      : t("Turn {number} was not started by a prompt of yours: the composer of the new session stays empty.", { number: point.number }) });
  } else {
    lines.push({ text: kept === 0 ? t("A new session starts empty: this session has no finished turn yet.")
      : kept === 1 ? t("A new session starts with the one turn of this session.") : t("A new session starts with all {count} turns of this session.", { count: kept }) });
    if (runningTurn !== null) lines.push({ text: t("The turn that is running now is not copied.") });
    lines.push({ text: t("This session is not changed.") });
    lines.push({ text: t("The composer of the new session starts empty.") });
  }
  return lines;
}

/** The lines that say what a rewind to a turn does. */
function rewindLines(t: Translate, point: BranchTurn, total: number, keep: boolean, title: string, draft: boolean): { text: string; warning?: boolean }[] {
  const { first, last, count, kept } = rewindConsequence(point, total);
  return [
    { text: count === 1 ? t("Turn {first}, the last one, is removed from this session.", { first })
      : t("Turns {first} to {last} ({count} turns) are removed from this session.", { first, last, count }) },
    { text: kept === 0 ? t("The session is then empty.") : t("The session then ends after turn {kept}.", { kept }) },
    { text: turnSender(point) !== "you" ? t("Turn {number} was not started by a prompt of yours: nothing is put in the composer.", { number: point.number })
      : draft ? t("The composer holds a draft, which is kept: the prompt of turn {number} is offered for you to copy instead.", { number: point.number })
      : t("The prompt of turn {number} is put back in the composer, for you to edit and send.", { number: point.number }) },
    { text: count === 1 ? t("Files changed by this turn are not restored.") : t("Files changed by these turns are not restored."), warning: true },
    keep ? { text: t("The session as it is now is kept in a separate session, named after this one and marked “before rewind”.") }
      : { text: t("The removed turns are deleted: they cannot be brought back."), warning: true },
  ];
}

/**
 * The window of a fork or a rewind of a session. It lists the points of the session, the newest first (its end,
 * then the start of each turn), shows what the selected turn was and what the action does there, and makes
 * it on Enter. Typing filters the turns by their prompt; Tab in the filter switches between Fork and Rewind.
 */
export function SessionBranchDialog({ title, request, host, facts, keepRemovedTurns, onKeepRemovedTurns, onPoint, onClose }: {
  /** The title of the session. */
  title: string;
  /** What the window opens on: the action, and the message of the timeline that was pointed at. */
  request: BranchRequest;
  host: BranchDialogHost;
  facts: BranchFacts;
  /** Whether a rewind keeps what it removes, as last chosen, and what remembers another choice. */
  keepRemovedTurns: boolean; onKeepRemovedTurns: (keep: boolean) => void;
  /** Tells the turn that is selected, by the journal offset of its prompt: the conversation behind scrolls to it. */
  onPoint?: (promptOffset: string | null) => void;
  onClose: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const origin = useRef<Element | null>(null);
  const listId = useId();
  const [mode, setMode] = useState<BranchMode>(request.mode);
  const [query, setQuery] = useState("");
  const [onlyMine, setOnlyMine] = useState(true);
  const [keep, setKeep] = useState(keepRemovedTurns);
  const [loaded, setLoaded] = useState<{ turns: readonly BranchTurn[]; total: number; activeRunId: string | null } | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState<BranchMode | null>(null);
  const [failure, setFailure] = useState<MessageKey | null>(null);
  // An outcome that is not known: nothing more is sent from this window.
  const [uncertain, setUncertain] = useState(false);
  const alive = useRef(true);
  const reads = useRef(new AbortController());
  const hostRef = useRef(host); hostRef.current = host;
  const placed = useRef(false);
  // The selection is the one the window opens on: from then on the conversation behind follows it.
  const [ready, setReady] = useState(false);

  useLayoutEffect(() => {
    const element = dialog.current;
    alive.current = true;
    origin.current = document.activeElement;
    element?.showModal();
    search.current?.focus();
    return () => {
      alive.current = false;
      reads.current.abort();
      if (element?.open) element.close();
      // The focus goes back to where it was, unless that is gone (the pane was read again) or another window opened.
      const restore = origin.current;
      requestAnimationFrame(() => {
        if (restore instanceof HTMLElement && restore.isConnected && !restore.closest("[inert], [hidden]") && !modalDialogOpen()
          && (document.activeElement === document.body || document.activeElement === null)) restore.focus();
      });
    };
  }, []);

  // The last turns, then the pages before them until the turn of the message that was pointed at is among them.
  async function readFirst() {
    reads.current.abort();
    const abort = reads.current = new AbortController();
    setLoadError(null);
    let turns: BranchTurn[] = [], total = 0, activeRunId: string | null = null;
    for (let page = 0; page < maximumSearchPages; page++) {
      const read = await hostRef.current.readTurns(page === 0 ? null : turns[0].number, abort.signal);
      if (abort.signal.aborted || !alive.current) return;
      if (read.kind === "error") { if (page === 0) { setLoadError(read.code); return; } break; }
      turns = mergeTurnPages(turns, read.turns); total = read.total; activeRunId = read.activeRunId;
      if (!request.promptOffset || turnForOffset(turns, request.promptOffset) || turns.length >= total || read.turns.length === 0) break;
    }
    setLoaded({ turns, total, activeRunId });
  }
  useEffect(() => { void readFirst(); }, []);

  async function loadOlder() {
    if (!loaded || loadingMore || loaded.turns.length >= loaded.total || loaded.turns.length === 0) return;
    setLoadingMore(true);
    const read = await hostRef.current.readTurns(loaded.turns[0].number, reads.current.signal);
    if (!alive.current || reads.current.signal.aborted) return;
    setLoadingMore(false);
    if (read.kind === "error") { setFailure(turnsMessage(read.code)); return; }
    setLoaded(current => current && { turns: mergeTurnPages(current.turns, read.turns), total: read.total, activeRunId: read.activeRunId });
  }

  const turns = loaded?.turns ?? [];
  const total = loaded?.total ?? 0;
  const target = useMemo(() => request.promptOffset ? turnForOffset(turns, request.promptOffset) : null, [turns, request.promptOffset]);
  // Nothing is listed before the turns are read: the end of the session is a point of a session that is known.
  const rows = useMemo(() => loaded ? branchRows(turns, total, { onlyMine, query }) : [], [loaded, turns, total, onlyMine, query]);
  // The first turns read choose the selection: the turn that was asked for, or what the action starts from.
  useLayoutEffect(() => {
    if (!loaded || placed.current) return;
    placed.current = true;
    // A turn that was pointed at is listed whoever started it.
    const everyTurn = !!target && turnSender(target) !== "you";
    const first = everyTurn ? branchRows(turns, total, { onlyMine: false, query: "" }) : rows;
    if (everyTurn) setOnlyMine(false);
    setSelected(rowId(first[initialSelection(first, request.mode, target?.runId ?? null)] ?? { kind: "end" }));
    setReady(true);
  }, [loaded]);
  const index = keepSelection(rows, selected, 0);
  const row: BranchRow | undefined = rows[index];
  const point = row?.kind === "turn" ? row.turn : null;
  const runningTurn = runningTurnNumber(turns, loaded?.activeRunId ?? null);
  const block = mode === "rewind" ? rewindBlock(point, facts, loaded?.activeRunId ?? null) : !facts.canMutate ? "read_only" : null;
  const actionable = !!loaded && !!row && row.kind !== "more" && !block && !busy && !uncertain;

  useLayoutEffect(() => { list.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: "nearest" }); }, [index, rows]);
  // The conversation behind the window follows the selection, when the message of the turn is loaded there.
  const pointOffset = point?.promptOffset ?? null;
  const tellPoint = useRef(onPoint); tellPoint.current = onPoint;
  useEffect(() => { if (ready) tellPoint.current?.(pointOffset); }, [pointOffset, ready]);

  const move = (delta: number) => { const next = rows[Math.max(0, Math.min(rows.length - 1, index + delta))]; if (next) setSelected(rowId(next)); };
  function switchMode(next: BranchMode) {
    if (busy || next === mode) return;
    setMode(next); setFailure(null);
    // A rewind has no end of the session to go to: the last turn listed is where it starts from.
    if (next === "rewind" && row?.kind === "end") { const first = rows.find(value => value.kind === "turn"); if (first) setSelected(rowId(first)); }
  }
  // Closing never waits for the host: a fork or a rewind under way goes on and shows its session when it is done.
  function close() { onClose(); }
  function chooseKeep(value: boolean) { setKeep(value); onKeepRemovedTurns(value); }

  async function act() {
    if (row?.kind === "more") { void loadOlder(); return; }
    if (!actionable || !row) return;
    setBusy(mode); setFailure(null);
    const result = mode === "fork" ? await hostRef.current.fork(point?.runId ?? null) : await hostRef.current.rewind(point!.runId, keep);
    if (!alive.current) return;
    setBusy(null);
    if (result.kind !== "error") { onClose(); return; }
    setFailure(branchMessage(result.code, mode));
    if (outcomeUnknown(result.code)) setUncertain(true);
    // The turns that are listed are no longer those of the session: they are read again, and the selection stays where it can.
    if (turnsOutdated(result.code)) void readFirst();
    search.current?.focus();
  }

  const now = Date.now();
  const status = loadError ? null : !loaded ? t("Loading turns…") : turnCountText(t, total);
  const actionLabel = mode === "fork" ? t("Fork") : !keep && point ? t("Rewind and delete") : t("Rewind");
  const lines = !loaded || !row || row.kind === "more" ? [] : mode === "fork" ? forkLines(t, point, total, runningTurn)
    : point ? rewindLines(t, point, total, keep, title, facts.draft === true) : [];
  const activity = point ? turnActivity(t, point) : [];
  const pointState = point ? stateLabel(point.status) : null;
  const pointTime = point ? timelineTime(point.startedAt, locale) : null;
  return <dialog ref={dialog} className="app-dialog reference-palette session-branch-dialog" aria-modal="true" aria-labelledby={`${listId}-title`} data-mode={mode}
    onCancel={event => { event.preventDefault(); close(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.metaKey) return;
      const inSearch = event.target === search.current;
      if (event.ctrlKey && !event.altKey && !event.shiftKey && event.key.toLowerCase() === "o") { event.preventDefault(); setOnlyMine(value => !value); return; }
      if (event.ctrlKey || event.altKey) return;
      // In the filter Tab switches the action, as it switches the category in the search window; elsewhere it moves the focus.
      if (event.key === "Tab" && inSearch && !event.shiftKey) { event.preventDefault(); switchMode(mode === "fork" ? "rewind" : "fork"); return; }
      // A button, a check box and a choice answer Enter and Space themselves.
      const element = event.target instanceof HTMLElement ? event.target : null;
      const control = !inSearch && !!element?.closest("button, input");
      // The text of a long prompt is scrolled with the keys that move the selection elsewhere.
      if (element?.closest(".branch-prompt-text") && ["ArrowDown", "ArrowUp", "PageDown", "PageUp", "Home", "End"].includes(event.key)) return;
      const handled = event.key === "ArrowDown" ? (move(1), true) : event.key === "ArrowUp" ? (move(-1), true)
        : event.key === "PageDown" ? (move(pageStep), true) : event.key === "PageUp" ? (move(-pageStep), true)
        : event.key === "Home" && !inSearch ? (move(-rows.length), true) : event.key === "End" && !inSearch ? (move(rows.length), true)
        : event.key === "Enter" && !control ? (void act(), true) : event.key === "Escape" ? (close(), true) : false;
      if (handled) event.preventDefault();
    }}>
    <AppWindowSurface storageKey="codealta.desktop.window.session-branch.v1" titleId={`${listId}-title`}
      title={<><AppIcon name="branch" size={14} /> {t("Fork or rewind")}<span className="reference-project"> · {title}</span></>}
      preferredSize={viewport => ({ width: Math.min(980, viewport.width - 40), height: Math.min(640, viewport.height - 40) })}
      minimumSize={{ width: 420, height: 340 }} onClose={close} closeLabel={t("Close")}
      headerActions={status && <span className="reference-status" role="status">{!loaded && <ActivitySpinner size={12} />}{status}</span>}>
      <div className="branch-bar">
        <div className="branch-mode" role="radiogroup" aria-label={t("Action")}>
          {(["fork", "rewind"] as const).map(value => <button key={value} type="button" role="radio" aria-checked={mode === value} disabled={!!busy}
            data-mode={value} onClick={() => { switchMode(value); search.current?.focus(); }}>
            <AppIcon name={value === "fork" ? "branch" : "rewind"} size={14} />{t(value === "fork" ? "Fork" : "Rewind")}</button>)}
        </div>
        <p className="branch-mode-text">{t(mode === "fork" ? "Continue from a point of this session in a new session. This session stays as it is."
          : "Take this session back to before a turn. That turn and the later ones are removed from it.")}</p>
      </div>
      <div className="issue-filter branch-filter">
        <InputGroup inputRef={search} className="reference-search" type="search" maxLength={256} value={query} spellCheck={false}
          leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} placeholder={t("Filter turns by their prompt…")}
          role="combobox" aria-expanded="true" aria-controls={listId} aria-label={t("Filter turns by their prompt…")}
          aria-activedescendant={row ? `${listId}-${index}` : undefined}
          onChange={event => { setQuery(event.target.value); setSelected(null); }} />
        <Checkbox checked={onlyMine} label={t("Only my prompts")} onChange={event => setOnlyMine(event.currentTarget.checked)} />
      </div>
      <div className="branch-panes">
        <div id={listId} ref={list} role="listbox" aria-label={t("Points of the session")} className="reference-list branch-list">
          {rows.map((value, at) => {
            const id = rowId(value), chosen = at === index;
            if (value.kind === "end") return <div role="option" id={`${listId}-${at}`} key={id} aria-selected={chosen} className="branch-row branch-row-end"
              aria-disabled={mode === "rewind"} onClick={() => setSelected(id)}>
              <span className="branch-number"><AppIcon name="arrowDown" size={13} /></span>
              <span className="branch-prompt"><strong>{t("End of the session")}</strong></span>
              <span className="branch-meta">{t("Everything so far")}</span>
            </div>;
            if (value.kind === "more") return <div role="option" id={`${listId}-${at}`} key={id} aria-selected={chosen} className="branch-row branch-row-more"
              onClick={() => { setSelected(id); void loadOlder(); }}>
              <span className="branch-number">{loadingMore ? <ActivitySpinner size={12} /> : <AppIcon name="history" size={13} />}</span>
              <span className="branch-prompt">{t("Load older turns")}</span>
              <span className="branch-meta">{value.missing === 1 ? t("1 more") : t("{count} more", { count: value.missing })}</span>
            </div>;
            const turn = value.turn, sender = turnSender(turn), state = stateLabel(turn.status), started = sessionTime(turn.startedAt, locale, now);
            const did = turnActivity(t, turn);
            return <div role="option" id={`${listId}-${at}`} key={id} aria-selected={chosen} className="branch-row" data-sender={sender} data-status={turn.status}
              onClick={() => setSelected(id)}>
              <span className="branch-number">{turn.number}</span>
              <span className="branch-prompt">{turnText(turn) || t("(no text)")}</span>
              <span className="branch-meta">
                {sender !== "you" && <span className="branch-tag" data-sender={sender}>{t(senderLabel[sender])}</span>}
                {state && <span className="branch-tag" data-status={turn.status}>{turn.status === "running" && <ActivitySpinner size={9} />}{t(state)}</span>}
                <time title={started.title} dateTime={started.dateTime}>{started.label}</time>
                {did.length > 0 && <span className="branch-did">{did.join(" · ")}</span>}
              </span>
            </div>;
          })}
          {loadError && <p className="reference-empty" role="alert">{t(turnsMessage(loadError))}</p>}
          {loaded && !rows.some(value => value.kind === "turn") && <p className="reference-empty">{t(total === 0 ? "This session has no turn yet."
            : query.trim() ? "No turn matches." : "No turn was started by a prompt of yours. Clear “Only my prompts” to list every turn.")}</p>}
        </div>
        <section className="branch-preview" aria-label={t("Selected point")} aria-live="polite">
          {!loaded && !loadError && <p className="reference-empty">{t("Loading turns…")}</p>}
          {loaded && row?.kind === "more" && <p className="reference-empty">{t("Press Enter to list the turns before turn {number}.", { number: turns[0]?.number ?? 1 })}</p>}
          {loaded && row && row.kind !== "more" && <>
            <header className="branch-preview-heading">
              <strong>{point ? t("Turn {number}", { number: point.number }) : t("End of the session")}</strong>
              {pointTime && <time title={pointTime.title} dateTime={pointTime.dateTime}>{pointTime.label}</time>}
              {pointState && <span className="branch-tag" data-status={point!.status}>{t(pointState)}</span>}
            </header>
            {point && <>
              <p className="branch-sender"><AppIcon name={turnSender(point) === "you" ? "user" : turnSender(point) === "job" || turnSender(point) === "provider" ? "terminal" : turnSender(point) === "answer" ? "ask" : "branch"} size={13} />{t(senderLine[turnSender(point)])}</p>
              <pre className="branch-prompt-text" tabIndex={0}>{turnText(point) || t("(no text)")}{point.promptTruncated && "…"}</pre>
              {point.promptTruncated && turnSender(point) === "you" && <p className="branch-note">{t("Only the beginning of this prompt is shown here. The composer gets the whole prompt.")}</p>}
              {activity.length > 0 && <p className="branch-note">{t("This turn: {activity}.", { activity: activity.join(", ") })}</p>}
            </>}
            <div className="branch-consequence" data-mode={mode}>
              <strong>{t(mode === "fork" ? point ? "What a fork before this turn does" : "What a fork at the end does" : "What a rewind to before this turn does")}</strong>
              {lines.length > 0 && <ul>{lines.map(line => <li key={line.text} data-warning={line.warning || undefined}>
                {line.warning && <AppIcon name="warning" size={13} />}{line.text}</li>)}</ul>}
              {block && <p className="branch-block" role="status"><AppIcon name="info" size={14} />{t(blockLine[block])}</p>}
            </div>
          </>}
        </section>
      </div>
      <div className="branch-actions">
        {mode === "rewind" && <Checkbox className="branch-keep" checked={keep} disabled={!!busy} label={t("Keep the removed turns in a separate session")}
          onChange={event => chooseKeep(event.currentTarget.checked)} />}
        <span className="branch-outcome">
          {busy && <span role="status"><ActivitySpinner size={12} />{t(busy === "fork" ? "Creating the fork…" : "Rewinding the session…")}</span>}
          {!busy && failure && <span role="alert" className="error-text">{t(failure)}</span>}
        </span>
        <Button text={t("Cancel")} onClick={close} />
        <Button className="branch-act" intent={mode === "rewind" && !keep ? "danger" : "primary"} text={actionLabel} loading={!!busy} disabled={!actionable}
          icon={<AppIcon name={mode === "fork" ? "branch" : "rewind"} size={14} />} onClick={() => void act()} />
      </div>
      <footer className="reference-hint"><span><kbd>↑</kbd><kbd>↓</kbd> {t("move")}</span><span><kbd>Enter</kbd> {t(mode === "fork" ? "fork" : "rewind")}</span>
        <span><kbd>Tab</kbd> {t("fork or rewind")}</span><span><kbd>Ctrl</kbd><kbd>O</kbd> {t("only my prompts")}</span><span><kbd>Esc</kbd> {t("close")}</span></footer>
    </AppWindowSurface>
  </dialog>;
}
