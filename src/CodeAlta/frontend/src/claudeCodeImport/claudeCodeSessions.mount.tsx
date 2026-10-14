// The group of the Claude Code sessions started elsewhere at the end of the sessions of a project, with a transport the
// test answers: hidden until it found some, closed with their number, read again when opened, a click imports.
import { useLayoutEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import type {
  WorkspaceClaudeCodeSession,
  WorkspaceClaudeCodeSessionsRequest,
  WorkspaceClaudeCodeSessionsResponse,
  WorkspaceImportClaudeCodeSessionRequest,
  WorkspaceImportClaudeCodeSessionResponse,
} from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import type { Locale } from "../localization";
import { ClaudeCodeSessionsGroup } from "./ClaudeCodeSessionsGroup";

const epoch = "11111111-1111-4111-8111-111111111111";
const project = { id: "p1", path: "C:\\dev\\a-project-with-a-rather-long-name" };
function transport<Request, Response>() {
  const calls: { request: Request; resolve: (value: Response) => void; reject: (error: Error) => void }[] = [];
  return { calls, invoke: (request: Request) => new Promise<Response>((resolve, reject) => calls.push({ request, resolve, reject })) };
}
const lists = transport<WorkspaceClaudeCodeSessionsRequest, WorkspaceClaudeCodeSessionsResponse>();
const imports = transport<WorkspaceImportClaudeCodeSessionRequest, WorkspaceImportClaudeCodeSessionResponse>();
const api = { list: lists.invoke, import: imports.invoke };
const session = (cliSessionId: string, title: string, origin: string, held: boolean): WorkspaceClaudeCodeSession =>
  ({ cliSessionId, title, lastChangedAt: new Date(Date.now() - 3_600_000).toISOString(), origin, held });
const listing = (sessions: WorkspaceClaudeCodeSession[], total = sessions.length): WorkspaceClaudeCodeSessionsResponse =>
  ({ status: "ok", hostEpoch: epoch, projectId: project.id, projectPath: project.path, providerId: "claude-code", sessions, total });
const imported = (cliSessionId: string, status = "ok", sessionId: string | null = "s-new"): WorkspaceImportClaudeCodeSessionResponse =>
  ({ status, hostEpoch: epoch, projectId: project.id, projectPath: project.path, cliSessionId, sessionId });
const opened: { id: string; existing: boolean; title: string }[] = [];

function Fixture() {
  const [locale, language] = useState<Locale>("en");
  const [refresh, setRefresh] = useState(0);
  useLayoutEffect(() => {
    Object.assign(window, { group: { language, refresh: () => setRefresh(value => value + 1), lists: lists.calls, imports: imports.calls, opened,
      session, listing, imported,
      setTheme: (theme: string) => { document.documentElement.dataset.theme = theme; document.documentElement.classList.toggle("bp6-dark", theme === "dark"); } } });
  }, []);
  return <ShellLanguageContext value={{ locale, choice: locale, setLanguage: () => { } }}>
    <div className="app-shell ide-shell"><aside className="session-rail" style={{ width: 300 }}><div className="session-list">
      <div className="session-row"><button type="button" style={{ paddingLeft: 14 }}><span className="session-title">A session of CodeAlta</span></button></div>
      <ClaudeCodeSessionsGroup project={project} epoch={epoch} api={api} refresh={refresh}
        onOpen={(id, existing, title) => opened.push({ id, existing, title })} />
    </div></aside></div>
  </ShellLanguageContext>;
}

document.documentElement.dataset.theme = "dark";
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
