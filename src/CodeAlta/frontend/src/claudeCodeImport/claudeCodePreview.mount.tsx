// The preview of a Claude Code conversation started elsewhere, as its tab shows it, with transports the test answers:
// the banner, the timeline read from the end a page at a time, and the prompt whose first message continues it.
import { useLayoutEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import type {
  HistoryCursor, HistoryEntry, WorkspaceClaudeCodeSessionConversationResponse, WorkspaceClaudeCodeSessionPreviewRequest,
  WorkspaceClaudeCodeSessionPreviewResponse, WorkspaceClaudeCodeSessionRequest, WorkspaceProject,
} from "#neoastra";
import { claudeCodeTab, type FileTab } from "../fileTabs";
import type { Locale } from "../localization";
import { createDraftIndicators, createLocalDrafts } from "../promptDraft";
import { createImageDrafts } from "../promptImages";
import { ShellLanguageContext } from "../shellLanguage";
import { ClaudeCodePreviewPane } from "./ClaudeCodePreviewPane";
import type { PreviewPrompt } from "./previewContinuation";

const epoch = "11111111-1111-4111-8111-111111111111";
const project = { id: "p1", path: "C:\\dev\\p1" };
// The projects a conversation can be continued in: the one that lists it, and another one.
const projects: WorkspaceProject[] = [{ id: "p1", name: "Parser", path: "C:\\dev\\p1", archived: false }, { id: "p2", name: "Docs", path: "C:\\dev\\p2", archived: false }];
const pageSize = 50;
// A conversation of four turns (or as many as the test says) of thirty records each: the prompt, then the answers.
function journalOf(cliSessionId: string): HistoryEntry[] {
  const journal: HistoryEntry[] = [];
  for (let turn = 1; turn <= (state.turns[cliSessionId] ?? 4); turn++) for (let index = 0; index < 30; index++) {
    const user = index === 0;
    journal.push({ offset: String(journal.length + 1), eventType: "contentCompleted", providerId: "claude-code", sessionId: `preview:${cliSessionId}`, runId: `external:${turn}`,
      timestamp: new Date(Date.UTC(2026, 9, 15, 8, turn, index)).toISOString(), kind: user ? "User" : "Assistant", phase: null,
      contentId: user ? `external:vscode:${turn}` : `assistant:${turn}:${index}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null,
      name: null, text: user ? `Prompt of turn ${turn}` : `Answer ${index} of turn ${turn}.`, details: null, textTruncated: false, tool: null, files: null, images: null,
      detailsTruncated: false, bodyOmitted: false });
  }
  return journal;
}
const state = {
  reads: [] as { cliSessionId: string; offset: string | null }[], conversations: [] as string[],
  sent: [] as PreviewPrompt[], continued: 0, openedInVsCode: 0, existing: [] as string[], targets: [] as string[],
  // What the host answers instead of a page, by conversation.
  answer: {} as Record<string, string>,
  // The turns of a conversation, by conversation: four when not said.
  turns: {} as Record<string, number>,
  held: { origin: "vsCode", status: "busy" } as { origin: string; status: string } | null,
};
async function preview(request: WorkspaceClaudeCodeSessionPreviewRequest): Promise<WorkspaceClaudeCodeSessionPreviewResponse> {
  state.reads.push({ cliSessionId: request.cliSessionId, offset: request.cursor?.offset ?? null });
  const base = { hostEpoch: epoch, projectId: request.projectId, projectPath: request.projectPath, cliSessionId: request.cliSessionId };
  const answer = state.answer[request.cliSessionId];
  if (answer === "existing") return { ...base, status: "existing", sessionId: "s-old", title: null, page: { status: "existing", entries: [], next: null, tailOmitted: false } };
  if (answer) return { ...base, status: answer, sessionId: null, title: null, page: { status: answer, entries: [], next: null, tailOmitted: false } };
  const journal = journalOf(request.cliSessionId);
  const end = request.cursor ? Number(request.cursor.offset) - 1 : journal.length;
  const start = Math.max(0, end - pageSize);
  const next: HistoryCursor | null = start > 0 ? { version: 2, sessionId: `preview:${request.cliSessionId}`, length: "100000", lastWriteUtcTicks: "7", offset: String(start + 1) } : null;
  return { ...base, status: "ok", sessionId: null, title: "Fix the parser", page: { status: "ok", entries: journal.slice(start, end), next, tailOmitted: false } };
}
async function conversation(request: WorkspaceClaudeCodeSessionRequest): Promise<WorkspaceClaudeCodeSessionConversationResponse> {
  state.conversations.push(request.cliSessionId);
  return { status: "ok", hostEpoch: epoch, projectId: request.projectId, projectPath: request.projectPath, cliSessionId: request.cliSessionId,
    resumeCommand: `claude --resume ${request.cliSessionId}`, workingDirectory: request.projectPath, configDir: null, windows: true, home: null,
    held: state.held ? { origin: state.held.origin, originText: "VS Code", processId: 42, entrypoint: "claude-vscode", kind: "interactive", status: state.held.status, workingDirectory: request.projectPath, name: null } : null };
}
const tabOf = (cliSessionId: string, origin = "vscode"): FileTab => claudeCodeTab(project, { cliSessionId, title: "Fix the parser", origin }, "claude-code");

function Fixture() {
  const [locale, language] = useState<Locale>("en");
  const [tab, setTab] = useState(() => tabOf("0f5c2c1e-7a0e-4a52-9d55-2a8d2b1c0e11"));
  const [busy, setBusy] = useState(false);
  const [importingElsewhere, setImportingElsewhere] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [target, setTarget] = useState<string | null>("p1");
  const [drafts] = useState(createLocalDrafts);
  const [images] = useState(createImageDrafts);
  const [indicators] = useState(createDraftIndicators);
  useLayoutEffect(() => {
    Object.assign(window, { preview: { state, language, busy: setBusy, importingElsewhere: setImportingElsewhere, refresh: setRefresh, show: (id: string, origin?: string) => setTab(tabOf(id, origin)), drafts, target: setTarget,
      setTheme: (theme: string) => { document.documentElement.dataset.theme = theme; document.documentElement.classList.toggle("bp6-dark", theme === "dark"); } } });
  }, []);
  return <ShellLanguageContext value={{ locale, choice: locale, setLanguage: () => { } }}>
    <div className="app-shell ide-shell" style={{ height: "100vh" }}><div className="active-session-content" style={{ height: "100%", display: "flex" }}>
      <ClaudeCodePreviewPane key={tab.key} tab={tab} epoch={epoch} api={{ conversation }} previewApi={preview} drafts={drafts} imageDrafts={images} draftIndicators={indicators}
        busy={busy} importingElsewhere={importingElsewhere} refreshMilliseconds={refresh} onSend={prompt => state.sent.push(prompt)} onContinue={() => { state.continued++; }} onOpenInVsCode={() => { state.openedInVsCode++; }}
        onExisting={(id, keep) => state.existing.push(keep ? `${id}:kept` : id)} projects={projects} target={projects.find(value => value.id === target) ?? null}
        onTargetChange={chosen => { state.targets.push(chosen.id); setTarget(chosen.id); }} />
    </div></div>
  </ShellLanguageContext>;
}

document.documentElement.dataset.theme = "dark";
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
