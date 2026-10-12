// Disposable page: the production window of a fork or a rewind, with the turns and the answers the test gives it.
import { StrictMode, useState } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { dismissDialogsOnOutsidePress } from "../modalDialogs";
import { ShellLanguageContext } from "../shellLanguage";
import type { BranchMode, BranchTurn } from "./branchPoints";
import { SessionBranchDialog, type BranchDialogHost, type BranchFacts } from "./SessionBranchDialog";
import type { ForkResult, RewindResult } from "./sessionBranchRpc";

type Shown = { mode: BranchMode; promptOffset: string | null; facts: BranchFacts; keep: boolean; revision: number };
const idle: BranchFacts = { canMutate: true, running: false, question: false, background: 0 };
const state = { reads: [] as (number | null)[], forks: [] as (string | null)[], rewinds: [] as [string, boolean][], closed: 0, points: [] as (string | null)[],
  keeps: [] as boolean[] };
let turns: BranchTurn[] = [];
let activeRunId: string | null = null;
// What the next fork or rewind answers: a status of the host, or nothing for a change that is made.
let answer: { status: string | null; hold: boolean } = { status: null, hold: false };
let release: (() => void) | undefined;
const held = async () => { if (answer.hold) await new Promise<void>(resolve => { release = resolve; }); };

const host: BranchDialogHost = {
  async readTurns(beforeTurn) {
    state.reads.push(beforeTurn);
    const end = beforeTurn === null ? turns.length : turns.findIndex(turn => turn.number === beforeTurn);
    return { kind: "ok", turns: turns.slice(Math.max(0, end - 100), end), total: turns.length, activeRunId };
  },
  async fork(beforeRunId): Promise<ForkResult> {
    state.forks.push(beforeRunId);
    await held();
    return answer.status ? { kind: "error", code: answer.status }
      : { kind: "forked", target: { scope: "global", projectPath: "/chats" }, sourceId: "session", id: "fork", title: "Session (fork)", turnCount: 1, prompt: null, promptTruncated: false };
  },
  async rewind(beforeRunId, keepRemovedTurns): Promise<RewindResult> {
    state.rewinds.push([beforeRunId, keepRemovedTurns]);
    await held();
    return answer.status ? { kind: "error", code: answer.status }
      : { kind: "rewound", target: { scope: "global", projectPath: "/chats" }, id: "session", turnCount: 1, removedTurnCount: 1, prompt: null, promptTruncated: false,
        removedTurnsSessionId: null, removedTurnsTitle: null, rewindCount: 1 };
  },
};

let show: ((value: Shown | null) => void) | undefined;
let revision = 0;
function Fixture() {
  const [shown, setShown] = useState<Shown | null>(null);
  show = setShown;
  return <div className="ide-shell" style={{ display: "block", padding: 16 }}>
    <button type="button" id="origin">Where the focus was</button>
    {shown && <SessionBranchDialog key={shown.revision} title="Parser review" request={{ sessionId: "session", mode: shown.mode, promptOffset: shown.promptOffset }}
      host={host} facts={shown.facts} keepRemovedTurns={shown.keep} onKeepRemovedTurns={keep => state.keeps.push(keep)}
      onPoint={offset => state.points.push(offset)} onClose={() => { state.closed++; setShown(null); }} />}
  </div>;
}

// As the application does: a press outside a window dismisses it.
dismissDialogsOnOutsidePress(document);
Object.assign(window, { branchFixture: { state,
  /** The turns of the session, the newest last, and the run in progress. */
  turns(values: Partial<BranchTurn>[], running: string | null = null) {
    turns = values.map((value, index) => ({ number: index + 1, runId: `run-${index + 1}`, status: "completed", startedAt: "2026-10-12T10:00:00Z", endedAt: "2026-10-12T10:00:30Z",
      promptOffset: String((index + 1) * 1000), prompt: `prompt ${index + 1}`, promptTruncated: false, sourceSessionId: null, answersQuestion: false, steers: 0,
      assistantMessages: 1, toolCalls: 0, compactions: 0, ...value }));
    activeRunId = running;
  },
  /** Opens the window as a command or a message of the timeline does. */
  show(mode: BranchMode, promptOffset: string | null = null, facts: Partial<BranchFacts> = {}, keep = true) {
    document.getElementById("origin")!.focus();
    flushSync(() => show!({ mode, promptOffset, facts: { ...idle, ...facts }, keep, revision: ++revision }));
  },
  close() { flushSync(() => show!(null)); },
  /** What the next change answers, and whether it waits to be released. */
  answer(status: string | null, hold = false) { answer = { status, hold }; },
  release() { release?.(); release = undefined; },
} });
createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => { } }}><Fixture /></ShellLanguageContext.Provider>
</StrictMode>);
