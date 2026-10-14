import { useEffect, useRef, useState } from "react";
import { AppIcon } from "../AppIcon";
import { plainTitle } from "../sessionTitle";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import {
  importClaudeCodeSession,
  importErrorMessage,
  listClaudeCodeSessions,
  originLabel,
  type ClaudeCodeListing,
  type ClaudeCodeProject,
  type ClaudeCodeSessionsApi,
} from "./claudeCodeSessions";

/**
 * The group at the end of the sessions of a project: the Claude Code sessions started in VS Code or a terminal in its
 * folder, which no session of CodeAlta continues. It shows itself once it found some, closed, with their number; open,
 * it reads them again and lists them, and a click on one imports it and opens the session that continues it.
 */
export function ClaudeCodeSessionsGroup({ project, epoch, api, refresh = 0, indent = 14, onOpen }: {
  project: ClaudeCodeProject;
  /** The epoch of the host the window knows; null while it knows none. */
  epoch: string | null;
  api: ClaudeCodeSessionsApi;
  /** Changes when the sessions of the window changed: the group reads its sessions again. */
  refresh?: number;
  /** How far the rows are pushed in, as the sessions of the list are. */
  indent?: number;
  /** The session that continues an imported one is to be opened: just imported, or one that continued it already. */
  onOpen: (sessionId: string, existing: boolean, title: string) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [listing, setListing] = useState<ClaudeCodeListing | null>(null);
  const [expanded, setExpanded] = useState(false);
  const [importing, setImporting] = useState<string | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const reads = useRef(0);
  const read = () => {
    const id = ++reads.current;
    void listClaudeCodeSessions(api, epoch, project).then(found => {
      // A later read, or another project, answers instead.
      if (id === reads.current && found.kind !== "error") setListing(found);
    });
  };
  // Read when the list of the project is shown, again when the sessions of the window change, and when it is opened.
  useEffect(() => { if (epoch) read(); }, [project.id, project.path, epoch, refresh]);
  useEffect(() => () => { reads.current++; }, []);

  if (listing?.kind !== "listed" || listing.total === 0) return null;
  const toggle = () => {
    setExpanded(!expanded);
    setError(null);
    if (!expanded) read();
  };
  const choose = async (cliSessionId: string, title: string) => {
    if (importing) return;
    setImporting(cliSessionId);
    setError(null);
    const result = await importClaudeCodeSession(api, epoch, project, cliSessionId);
    setImporting(null);
    if (result.kind === "error") { setError(importErrorMessage(result.code)); if (result.code === "not_found") read(); return; }
    onOpen(result.sessionId, result.existing, title);
  };
  const now = Date.now();
  return <div className="claude-code-sessions" data-claude-code-sessions={project.id}>
    <div className="session-list-disclosure claude-code-sessions-head" style={{ paddingLeft: Math.max(0, indent - 8) }}>
      <button type="button" className="quiet-button claude-code-sessions-toggle" aria-expanded={expanded} onClick={toggle}
        title={t("Claude Code sessions started in VS Code or a terminal in this project")}>
        <AppIcon name="chevronDown" size={12} className={expanded ? "tree-chevron expanded" : "tree-chevron"} />
        <span>{t("From Claude Code ({count})", { count: listing.total })}</span>
      </button>
    </div>
    {expanded && <div className="claude-code-sessions-list" role="list" aria-label={t("Claude Code sessions started in VS Code or a terminal in this project")}>
      {listing.sessions.map(session => {
        const time = sessionTime(session.lastChangedAt, locale, Math.max(now, Date.parse(session.lastChangedAt) || 0));
        const busy = importing === session.cliSessionId;
        return <div className="session-row claude-code-session-row" role="listitem" key={session.cliSessionId} data-cli-session-id={session.cliSessionId}>
          <button type="button" style={{ paddingLeft: indent }} disabled={!!importing} aria-busy={busy}
            title={`${plainTitle(session.title)}\n${t("Import into CodeAlta and open")}`} onClick={() => void choose(session.cliSessionId, session.title)}>
            <span className="session-icon claude-code-session-icon" aria-hidden="true"><AppIcon name={busy ? "refresh" : "open"} size={13} /></span>
            <span className="session-title">{plainTitle(session.title)}</span>
            <span className="claude-code-session-meta">
              <span className="claude-code-session-origin">{t(originLabel(session.origin))}</span>
              {session.held && <span className="claude-code-session-held">{t("open elsewhere")}</span>}
              {busy ? <span>{t("Importing…")}</span> : <time dateTime={time.dateTime} title={time.title}>{time.label}</time>}
            </span>
          </button>
        </div>;
      })}
      {listing.total > listing.sessions.length && <div className="muted-text claude-code-sessions-more" style={{ paddingLeft: indent }}>
        {t("{count} more not shown", { count: listing.total - listing.sessions.length })}</div>}
      {error && <p role="alert" className="notice error-text claude-code-sessions-error">{t(error)}</p>}
    </div>}
  </div>;
}
