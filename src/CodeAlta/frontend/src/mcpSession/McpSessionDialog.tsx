import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { Button, InputGroup, SegmentedControl } from "@blueprintjs/core";
import type { ComposerStatusRequest } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindowSurface } from "../AppWindow";
import { useShellLanguage } from "../shellLanguage";
import { CopyButton } from "../ToolCallChrome";
import { activeToolCount, filterMcpServers, mcpSessionSummary, mcpSessionView, type McpServerView, type McpSessionView, type McpToolScope, type McpToolView } from "./mcpSession";

/** Reads the MCP servers of a composer's project and session (`composerStatus.readMcp`). */
export type ReadMcp = (request: ComposerStatusRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<unknown>;

type Read = { state: "loading" } | { state: "failed" } | { state: "ok"; view: McpSessionView };

/**
 * The MCP servers and tools of a session: which servers it uses, the tools it has, and the ones it could activate,
 * with what each tool does. It is read when the window opens and again from its title bar; nothing connects to a
 * server. Settings → MCP is one button away.
 */
export function McpSessionDialog({ epoch, projectId, sessionId, read, onClose, onOpenSettings }: {
  epoch: string; projectId: string | null; sessionId: string | null; read: ReadMcp; onClose: () => void; onOpenSettings: () => void;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const close = useRef<HTMLButtonElement>(null);
  const composing = useRef(false);
  const [loaded, setLoaded] = useState<Read>({ state: "loading" });
  const [query, setQuery] = useState("");
  const [scope, setScope] = useState<McpToolScope | null>(null);
  const [revision, setRevision] = useState(0);
  useLayoutEffect(() => {
    const element = dialog.current;
    element?.showModal();
    if (element?.isConnected && element.matches(":modal")) close.current?.focus();
    return () => { if (element?.open) element.close(); };
  }, []);
  useEffect(() => {
    const abort = new AbortController();
    setLoaded(previous => previous.state === "ok" ? previous : { state: "loading" });
    void read({ expectedEpoch: epoch, projectId, sessionId }, { signal: abort.signal, timeoutMilliseconds: 10_000 })
      .then(reply => {
        if (abort.signal.aborted) return;
        const view = mcpSessionView(reply, projectId);
        setLoaded(view ? { state: "ok", view } : { state: "failed" });
      }, () => { if (!abort.signal.aborted) setLoaded({ state: "failed" }); });
    return () => abort.abort();
  }, [epoch, projectId, sessionId, read, revision]);
  const view = loaded.state === "ok" ? loaded.view : null;
  const summary = useMemo(() => view && mcpSessionSummary(view), [view]);
  // What the session has first, when it has something; every tool otherwise.
  const shownScope: McpToolScope = scope ?? (summary && summary.activeServers > 0 ? "active" : "all");
  const servers = useMemo(() => view ? filterMcpServers(view.servers, query, shownScope) : [], [view, query, shownScope]);
  const searching = query.trim().length > 0;

  return <dialog ref={dialog} className="app-dialog mcp-session-dialog" aria-modal="true" aria-labelledby="mcp-session-title"
    onKeyDown={event => {
      event.stopPropagation(); // Do not run shell chords while this native modal is open.
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) composing.current = true;
      else onClose();
    }} onKeyUp={() => { composing.current = false; }} onCompositionEnd={() => { composing.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composing.current) onClose(); }}>
    <AppWindowSurface storageKey="codealta.desktop.window.mcp-session.v1" title={t(sessionId ? "MCP in this session" : "MCP servers and tools")} titleId="mcp-session-title"
      preferredSize={viewport => ({ width: Math.min(820, viewport.width - 40), height: Math.min(760, viewport.height - 40) })}
      onClose={onClose} closeLabel={t("Close")} closeRef={close}
      headerActions={<Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={15} />} disabled={loaded.state === "loading"}
        aria-label={t("Refresh")} title={t("Refresh")} onClick={() => setRevision(value => value + 1)} />}>
      <div className="mcp-session-page">
        {summary && view?.enabled && <div className="mcp-session-summary" role="group" aria-label={t("Summary")}>
          <Stat value={summary.activeServers} total={summary.servers} label={t("Servers in use")} tone="active" />
          <Stat value={summary.activeTools} total={summary.tools || null} label={t("Active tools")} tone="active" />
          <Stat value={summary.servers - summary.activeServers - summary.disabledServers} label={t("Available to activate")} tone="available" />
          {summary.disabledServers > 0 && <Stat value={summary.disabledServers} label={t("Turned off")} tone="disabled" />}
        </div>}
        {view?.enabled && view.servers.length > 0 && <div className="mcp-session-toolbar">
          <SegmentedControl size="small" value={shownScope} onValueChange={value => setScope(value as McpToolScope)}
            options={[{ value: "active", label: t("In this session") }, { value: "all", label: t("All servers") }]} />
          <InputGroup className="mcp-session-search" type="search" size="small" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={query}
            placeholder={t("Search servers and tools")} aria-label={t("Search servers and tools")} onChange={event => setQuery(event.target.value)} />
        </div>}
        <div className="mcp-session-list">
          {loaded.state === "loading" && <div className="mcp-session-empty" role="status"><ActivitySpinner size={16} /><span>{t("Loading…")}</span></div>}
          {loaded.state === "failed" && <div className="mcp-session-empty" role="alert"><AppIcon name="warning" size={18} /><span>{t("The MCP servers could not be read.")}</span></div>}
          {view && !view.enabled && <div className="mcp-session-empty"><AppIcon name="server" size={22} /><span>{t("MCP is turned off.")}</span></div>}
          {view?.enabled && view.servers.length === 0 && <div className="mcp-session-empty"><AppIcon name="server" size={22} /><span>{t("No MCP servers are defined yet.")}</span></div>}
          {view?.enabled && view.servers.length > 0 && servers.length === 0 && <div className="mcp-session-empty"><AppIcon name="server" size={22} />
            <span>{t(searching ? "Nothing matches the search." : "This session has not activated any MCP server yet.")}</span>
            {!searching && <Button size="small" text={t("Show all servers")} onClick={() => setScope("all")} />}</div>}
          {servers.map(server => <ServerCard key={`${server.key}:${searching}`} server={server} scope={shownScope}
            initiallyOpen={searching || servers.length === 1 || server.state === "active"} />)}
          {view && view.omitted > 0 && <p className="mcp-session-note">{t("{count} more servers are not shown.", { count: view.omitted })}</p>}
        </div>
        <footer className="mcp-session-footer">
          <span>{t("Agents activate servers and tools with alta mcp; the tools shown are the ones CodeAlta last listed.")}</span>
          <Button icon={<AppIcon name="settings" size={15} />} text={t("MCP settings")} onClick={onOpenSettings} />
        </footer>
      </div>
    </AppWindowSurface></dialog>;
}

function Stat({ value, total = null, label, tone }: { value: number; total?: number | null; label: string; tone: "active" | "available" | "disabled" }) {
  return <div className="mcp-session-stat" data-tone={tone}>
    <strong>{value}{total !== null && <small>/{total}</small>}</strong><span>{label}</span>
  </div>;
}

// A server opens at first when the session uses it, and when a search shows what matched in it.
function ServerCard({ server, scope, initiallyOpen }: { server: McpServerView; scope: McpToolScope; initiallyOpen: boolean }) {
  const { t } = useShellLanguage();
  const [shown, setShown] = useState(initiallyOpen);
  const active = activeToolCount(server);
  const total = server.tools.length + server.omittedTools;
  const state = server.state === "active" ? t("In use") : server.state === "disabled" ? t("Turned off") : t("Available");
  return <section className="mcp-session-server" data-state={server.state} aria-label={server.key}>
    <button type="button" className="mcp-session-server-header" aria-expanded={shown} onClick={() => setShown(value => !value)}>
      <AppIcon name={shown ? "chevronDown" : "chevronRight"} size={14} className="mcp-session-chevron" />
      <span className="mcp-session-server-icon"><AppIcon name="server" size={16} /></span>
      <span className="mcp-session-server-title">
        <strong>{server.key}</strong>
        <span className="mcp-session-server-meta">{[server.transport === "http" ? "HTTP" : "stdio", server.source, t(server.scope === "project" ? "Project" : "Global")].filter(Boolean).join(" · ")}</span>
      </span>
      <span className="mcp-session-badges">
        {server.required && <span className="mcp-session-badge" data-tone="info">{t("Required")}</span>}
        {server.activated && <span className="mcp-session-badge" data-tone="info">{t("All tools")}</span>}
        <span className="mcp-session-badge" data-tone={server.state}>{state}</span>
        <span className="mcp-session-count" title={t("Active tools")}>
          {server.state === "active" ? `${active}/${total || "?"}` : total || "–"} <AppIcon name="tool" size={12} />
        </span>
      </span>
    </button>
    {shown && <div className="mcp-session-tools">
      {server.tools.length === 0
        ? <p className="mcp-session-note">{total === 0 && !server.listedAt
          ? t("Its tools have not been listed yet: they are when a session activates the server, or when it is tested in Settings.")
          : scope === "active" ? t("This session has none of its tools yet.") : t("Nothing to show.")}</p>
        : server.tools.map(tool => <ToolRow key={tool.name} tool={tool} />)}
      {server.omittedTools > 0 && <p className="mcp-session-note">{t("{count} more tools are not shown.", { count: server.omittedTools })}</p>}
      {server.listedAt && <p className="mcp-session-note mcp-session-listed">{t("Tools listed {time}", { time: new Date(server.listedAt).toLocaleString() })}</p>}
    </div>}
  </section>;
}

function ToolRow({ tool }: { tool: McpToolView }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const state = tool.active ? "active" : tool.enabled ? "available" : "disabled";
  return <div className="mcp-session-tool" data-state={state}>
    <button type="button" className="mcp-session-tool-header" aria-expanded={open} onClick={() => setOpen(value => !value)}
      title={t(state === "active" ? "The session has this tool" : state === "available" ? "The session can activate this tool" : "Turned off by the policy")}>
      <span className="mcp-session-tool-dot" aria-hidden="true" />
      <span className="mcp-session-tool-text">
        <span className="mcp-session-tool-name"><code>{tool.name}</code>{tool.title && tool.title !== tool.name && <span>{tool.title}</span>}</span>
        {tool.description && <span className="mcp-session-tool-description" data-open={open || undefined}>{tool.description}</span>}
      </span>
      {state === "disabled" && <span className="mcp-session-badge" data-tone={state}>{t("Turned off")}</span>}
    </button>
    {open && <div className="mcp-session-tool-details">
      <div className="mcp-session-alias"><span>{t("Called as")}</span><code>{tool.alias}</code><CopyButton text={tool.alias} label={t("Copy the tool name")} /></div>
      {tool.arguments.length > 0 ? <dl className="mcp-session-arguments">
        {tool.arguments.map(argument => <div key={argument.name}><dt><code>{argument.name}</code></dt><dd>{argument.description ?? ""}</dd></div>)}
      </dl> : <p className="mcp-session-note">{t("No arguments.")}</p>}
    </div>}
  </div>;
}
