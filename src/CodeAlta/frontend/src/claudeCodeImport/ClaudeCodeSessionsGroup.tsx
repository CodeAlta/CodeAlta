import { useEffect, useRef, useState } from "react";
import { AppIcon } from "../AppIcon";
import { ProviderIcon } from "../ProviderIcon";
import { SessionTabMenu } from "../SessionTabMenu";
import { isSessionContextKey } from "../sessionRowActions";
import { plainTitle } from "../sessionTitle";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import type { WorkspaceClaudeCodeSession } from "#neoastra";
import {
  claudeCodeFirstPage,
  claudeCodeMorePage,
  copyEntryResumeCommand,
  entryMenuItems,
  importClaudeCodeSession,
  importErrorMessage,
  inUseElsewhere,
  listClaudeCodeSessions,
  notShown,
  openEntryInVsCode,
  originMark,
  readClaudeCodeSessions,
  runEntryInTerminal,
  withPage,
  type ClaudeCodeEntryAction,
  type ClaudeCodeEntryOutcome,
  type ClaudeCodeEntryTerminal,
  type ClaudeCodeListing,
  type ClaudeCodeProject,
  type ClaudeCodeSessionsApi,
} from "./claudeCodeSessions";

type Listed = Extract<ClaudeCodeListing, { kind: "listed" }>;

/**
 * The group at the end of the sessions of a project: the Claude Code sessions started in VS Code or a terminal in its
 * folder, which no session of CodeAlta continues. It shows itself once it found some, closed at first, with their number
 * (the window remembers whether the user left it open); open, it reads them again and lists the newest ones, one line
 * each, with "Show more" for the next ones. A click on one opens its preview, which writes nothing; its menu also
 * continues it in CodeAlta (the import, which opens the session that continues it), or opens it in VS Code or a
 * terminal, where it was started.
 */
export function ClaudeCodeSessionsGroup({ project, epoch, api, refresh = 0, indent = 14, onOpen, onPreview, previewing = null, expanded: shownOpen, onExpandedChange,
  terminal, onOutcome, write }: {
  project: ClaudeCodeProject;
  /** The epoch of the host the window knows; null while it knows none. */
  epoch: string | null;
  api: ClaudeCodeSessionsApi;
  /** Changes when the sessions of the window changed: the group reads its sessions again. */
  refresh?: number;
  /** How far the rows are pushed in, as the sessions of the list are. */
  indent?: number;
  /** The session that continues an imported one is to be opened: just imported, or one that continued it already. */
  onOpen: (sessionId: string, existing: boolean, title: string, cliSessionId: string) => void;
  /** Opens the preview of a session of the group; without it a click imports the session. */
  onPreview?: (session: WorkspaceClaudeCodeSession, providerId: string | null) => void;
  /** The session of the CLI whose preview is shown: its row is marked. */
  previewing?: string | null;
  /** Whether the group is open, when the window keeps it; closed at first otherwise. */
  expanded?: boolean;
  onExpandedChange?: (expanded: boolean) => void;
  /** The built-in terminal of the window, in the folder of the project; absent where there is none (Run in terminal is then disabled). */
  terminal?: ClaudeCodeEntryTerminal;
  /** What an action of the menu did, for the window to say. */
  onOutcome?: (outcome: ClaudeCodeEntryOutcome) => void;
  /** Writes a text to the clipboard; absent where there is no clipboard. */
  write?: (text: string) => Promise<void>;
}) {
  const { t, locale } = useShellLanguage();
  const [listing, setListing] = useState<Listed | null>(null);
  const [ownExpanded, setOwnExpanded] = useState(false);
  const expanded = shownOpen ?? ownExpanded;
  const setExpanded = (value: boolean) => { setOwnExpanded(value); onExpandedChange?.(value); };
  const [importing, setImporting] = useState<string | null>(null);
  const [acting, setActing] = useState<string | null>(null);
  const [error, setError] = useState<MessageKey | null>(null);
  const [menu, setMenu] = useState<{ id: string; anchor: HTMLElement } | null>(null);
  // How many sessions the list shows: the first ones, and what "Show more" added. A new read keeps as many.
  const wanted = useRef(claudeCodeFirstPage);
  const reads = useRef(0);
  const read = () => {
    const id = ++reads.current;
    void readClaudeCodeSessions(api, epoch, project, wanted.current).then(found => {
      // A later read, or another project, answers instead.
      if (id === reads.current && found.kind === "listed") setListing(found);
      else if (id === reads.current && found.kind === "none") setListing(null);
    });
  };
  // Read when the list of the project is shown, again when the sessions of the window change, and when it is opened.
  useEffect(() => { if (epoch) read(); }, [project.id, project.path, project.spaceId, epoch, refresh]);
  useEffect(() => () => { reads.current++; }, []);

  if (!listing || listing.total === 0) return null;
  const toggle = () => {
    setExpanded(!expanded);
    setError(null);
    setMenu(null);
    if (!expanded) read();
  };
  const more = () => {
    const shown = listing;
    const id = ++reads.current;
    void listClaudeCodeSessions(api, epoch, project, shown.sessions.length, claudeCodeMorePage).then(page => {
      if (id !== reads.current || page.kind !== "listed") return;
      const next = withPage(shown, page);
      wanted.current = Math.max(claudeCodeFirstPage, next.sessions.length);
      setListing(next);
    });
  };
  const choose = async (cliSessionId: string, title: string) => {
    if (importing) return;
    setImporting(cliSessionId);
    setError(null);
    const result = await importClaudeCodeSession(api, epoch, project, cliSessionId);
    setImporting(null);
    if (result.kind === "error") { setError(importErrorMessage(result.code)); if (result.code === "not_found") read(); return; }
    onOpen(result.sessionId, result.existing, title, cliSessionId);
  };
  const act = async (action: ClaudeCodeEntryAction, cliSessionId: string, title: string) => {
    if (action === "preview") { const session = listing.sessions.find(value => value.cliSessionId === cliSessionId); if (session) open(session); return; }
    if (action === "continue") { await choose(cliSessionId, title); return; }
    if (acting) return;
    setActing(cliSessionId);
    try {
      const outcome = action === "open-vs-code" ? await openEntryInVsCode(api, epoch, project, cliSessionId)
        : action === "copy-resume-command" ? await copyEntryResumeCommand(api, epoch, project, cliSessionId, write)
        : terminal ? await runEntryInTerminal(api, epoch, project, cliSessionId, terminal) : null;
      if (!outcome) return;
      if (outcome.reread) read();
      onOutcome?.(outcome);
    } finally { setActing(null); }
  };
  // A click looks at a session: its preview. Where there is no preview, it imports the session.
  const open = (session: WorkspaceClaudeCodeSession) => { if (onPreview) onPreview(session, listing.providerId); else void choose(session.cliSessionId, session.title); };
  const show = (id: string, anchor: HTMLElement | null) => setMenu(current => !anchor || current?.id === id ? null : { id, anchor });
  const now = Date.now();
  const hidden = notShown(listing);
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
        const title = plainTitle(session.title);
        const origin = originMark(session.origin);
        const shown = menu?.id === session.cliSessionId ? menu : null;
        const looked = previewing === session.cliSessionId;
        return <div className={`session-row claude-code-session-row${shown ? " menu-open" : ""}${looked ? " selected" : ""}`} role="listitem" key={session.cliSessionId}
          data-cli-session-id={session.cliSessionId}
          onContextMenu={event => {
            event.preventDefault();
            show(session.cliSessionId, event.currentTarget.querySelector<HTMLElement>(".session-actions-trigger"));
          }}
          onKeyDown={event => {
            const composing = event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229;
            if (!isSessionContextKey(event.key, event.shiftKey, composing, false)) return;
            event.preventDefault(); event.stopPropagation();
            show(session.cliSessionId, event.currentTarget.querySelector<HTMLElement>(".session-actions-trigger"));
          }}>
          <button type="button" style={{ paddingLeft: indent }} disabled={!!importing} aria-busy={busy} aria-current={looked ? "page" : undefined}
            title={`${title}\n${t(onPreview ? "Preview; your first message continues it in CodeAlta" : "Import into CodeAlta and open")}`} onClick={() => open(session)}>
            <span className="session-icon session-provider claude-code-session-icon" aria-hidden="true">
              {busy ? <AppIcon name="refresh" size={13} /> : <ProviderIcon providerKey={listing.providerId} size={13} fallback="assistant" />}</span>
            <span className="session-title">{title}</span>
            {inUseElsewhere(session) && <span className="claude-code-session-mark claude-code-session-held" role="img"
              aria-label={t("In use in another Claude process now")} title={t("In use in another Claude process now")}><AppIcon name="eye" size={12} /></span>}
            <span className="claude-code-session-mark claude-code-session-origin" role="img" data-origin={session.origin}
              aria-label={t(origin.label)} title={t(origin.label)}><AppIcon name={origin.icon} size={12} /></span>
            <span className="session-meta">{busy ? <span>{t("Importing…")}</span> : <time dateTime={time.dateTime} title={time.title}>{time.label}</time>}</span>
          </button>
          <button type="button" className="icon-button session-actions-trigger" aria-label={t("Actions for the Claude Code session {title}", { title })}
            aria-haspopup="menu" aria-expanded={!!shown} onClick={event => show(session.cliSessionId, event.currentTarget)}><AppIcon name="ellipsis" size={16} /></button>
          {shown && <SessionTabMenu anchor={shown.anchor} container={document.body} title={t("Actions for the Claude Code session {title}", { title })}
            current={() => true} onClose={() => queueMicrotask(() => setMenu(value => value === shown ? null : value))}
            items={entryMenuItems(t, { busy: !!importing || !!acting, terminal: !!terminal }, action => void act(action, session.cliSessionId, session.title))} />}
        </div>;
      })}
      {hidden > 0 && <div className="session-list-disclosure claude-code-sessions-more" style={{ paddingLeft: Math.max(0, indent - 8) }}>
        <button type="button" className="quiet-button" onClick={more}>{t("Show more…")} <span className="muted-text">({hidden})</span></button>
      </div>}
      {error && <p role="alert" className="notice error-text claude-code-sessions-error">{t(error)}</p>}
    </div>}
  </div>;
}
