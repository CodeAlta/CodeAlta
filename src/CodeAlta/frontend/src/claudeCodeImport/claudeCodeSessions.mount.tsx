// The group of the Claude Code sessions started elsewhere at the end of the sessions of a project, with a transport the
// test answers: hidden until it found some, closed with their number, read again when opened, one line per row with
// "Show more" for the next ones, a click opens its preview, and the menu of a row continues it here or opens it elsewhere.
// The fixture keeps whether the group is open, as the window does for each project.
import { useLayoutEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import type {
  WorkspaceClaudeCodeSession,
  WorkspaceClaudeCodeSessionConversationResponse,
  WorkspaceClaudeCodeSessionRequest,
  WorkspaceClaudeCodeSessionsRequest,
  WorkspaceClaudeCodeSessionsResponse,
  WorkspaceImportClaudeCodeSessionRequest,
  WorkspaceImportClaudeCodeSessionResponse,
  WorkspaceOpenClaudeCodeSessionResponse,
} from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ShellLanguageContext } from "../shellLanguage";
import type { Locale } from "../localization";
import { ClaudeCodeSessionsGroup } from "./ClaudeCodeSessionsGroup";
import type { ClaudeCodeEntryOutcome } from "./claudeCodeSessions";

const epoch = "11111111-1111-4111-8111-111111111111";
const project = { id: "p1", path: "C:\\dev\\a-project-with-a-rather-long-name" };
function transport<Request, Response>() {
  const calls: { request: Request; resolve: (value: Response) => void; reject: (error: Error) => void }[] = [];
  return { calls, invoke: (request: Request) => new Promise<Response>((resolve, reject) => calls.push({ request, resolve, reject })) };
}
const lists = transport<WorkspaceClaudeCodeSessionsRequest, WorkspaceClaudeCodeSessionsResponse>();
const imports = transport<WorkspaceImportClaudeCodeSessionRequest, WorkspaceImportClaudeCodeSessionResponse>();
const conversations = transport<WorkspaceClaudeCodeSessionRequest, WorkspaceClaudeCodeSessionConversationResponse>();
const opens = transport<WorkspaceClaudeCodeSessionRequest, WorkspaceOpenClaudeCodeSessionResponse>();
const api = { list: lists.invoke, import: imports.invoke, conversation: conversations.invoke, openInVsCode: opens.invoke };
const session = (cliSessionId: string, title: string, origin: string, held: boolean, holderStatus: string | null = held ? "busy" : null): WorkspaceClaudeCodeSession =>
  ({ cliSessionId, title, lastChangedAt: new Date(Date.now() - 3_600_000).toISOString(), origin, held, holderStatus });
// Sessions numbered from `from`, each with an identifier of its own.
const many = (from: number, count: number) => Array.from({ length: count }, (_, index) => session(
  `00000000-0000-4000-8000-${String(from + index).padStart(12, "0")}`, `Session ${from + index}`, (from + index) % 2 ? "terminal" : "vscode", false));
const listing = (sessions: WorkspaceClaudeCodeSession[], total = sessions.length, offset = 0): WorkspaceClaudeCodeSessionsResponse =>
  ({ status: "ok", hostEpoch: epoch, projectId: project.id, projectPath: project.path, providerId: "claude-code", offset, sessions, total });
const imported = (cliSessionId: string, status = "ok", sessionId: string | null = "s-new"): WorkspaceImportClaudeCodeSessionResponse =>
  ({ status, hostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId, sessionId });
const described = (cliSessionId: string, status = "ok"): WorkspaceClaudeCodeSessionConversationResponse =>
  ({ status, hostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId, resumeCommand: status === "ok" ? `claude --resume ${cliSessionId}` : null,
    workingDirectory: status === "ok" ? project.path : null, held: null, configDir: null, windows: true, home: null });
const opened = (cliSessionId: string, status = "opened"): WorkspaceOpenClaudeCodeSessionResponse =>
  ({ status, hostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId });
const sessionsOpened: { id: string; existing: boolean; title: string; cliSessionId: string }[] = [];
const previews: string[] = [];
const openChanges: boolean[] = [];
const outcomes: ClaudeCodeEntryOutcome[] = [];
const copied: string[] = [];
const typed: { id: string; data: string }[] = [];
let terminals = 0;

function Fixture() {
  const [locale, language] = useState<Locale>("en");
  const [refresh, setRefresh] = useState(0);
  const [open, setOpen] = useState(false);
  const [previewing, setPreviewing] = useState<string | null>(null);
  const [mount, setMount] = useState(0);
  useLayoutEffect(() => {
    Object.assign(window, { group: { language, refresh: () => setRefresh(value => value + 1), remount: () => setMount(value => value + 1), previews, openChanges, lists: lists.calls, imports: imports.calls, conversations: conversations.calls,
      opens: opens.calls, opened: sessionsOpened, outcomes, copied, typed, terminals: () => terminals,
      session, many, listing, imported, described, open: opened,
      setTheme: (theme: string) => { document.documentElement.dataset.theme = theme; document.documentElement.classList.toggle("bp6-dark", theme === "dark"); } } });
  }, []);
  return <ShellLanguageContext value={{ locale, choice: locale, setLanguage: () => { } }}>
    <div className="app-shell ide-shell"><aside className="session-rail" style={{ width: 300 }}><div className="session-list">
      {/* A session of CodeAlta, as the list draws it: the rows of the group have its height. */}
      <div className="session-row codealta-session-row"><button type="button" style={{ paddingLeft: 14 }}>
        <span className="session-icon session-provider"><AppIcon name="assistant" size={13} /></span>
        <span className="session-title">A session of CodeAlta</span>
        <span className="session-meta"><span>claude</span><time>1h</time></span></button>
        <button type="button" className="icon-button session-actions-trigger" aria-label="Actions"><AppIcon name="ellipsis" size={16} /></button></div>
      <ClaudeCodeSessionsGroup key={mount} project={project} epoch={epoch} api={api} refresh={refresh}
        onOpen={(id, existing, title, cliSessionId) => sessionsOpened.push({ id, existing, title, cliSessionId })}
        onPreview={value => { previews.push(value.cliSessionId); setPreviewing(value.cliSessionId); }} previewing={previewing}
        expanded={open} onExpandedChange={value => { openChanges.push(value); setOpen(value); }}
        write={async text => { copied.push(text); }}
        terminal={{ create: async () => ({ id: `t${++terminals}`, profile: "pwsh" }), input: (id, data) => typed.push({ id, data }) }}
        onOutcome={outcome => outcomes.push(outcome)} />
    </div></aside></div>
  </ShellLanguageContext>;
}

document.documentElement.dataset.theme = "dark";
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
