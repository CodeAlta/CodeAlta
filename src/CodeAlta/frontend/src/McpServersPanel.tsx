import { useEffect, useState } from "react";
import type { McpInventoryRequest, McpInventoryResponse } from "#neoastra";

export function McpServersPanel({ target, read }: {
  target: { epoch: string; sessionId: string; projectId: string | null } | null;
  read: (request: McpInventoryRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<McpInventoryResponse>;
}) {
  const [page, setPage] = useState<McpInventoryResponse>();
  const [error, setError] = useState("");
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<string | null>(null);
  useEffect(() => {
    setPage(undefined); setError(""); setSearch(""); setSelected(null);
    if (!target) return;
    const controller = new AbortController();
    void read({ expectedEpoch: target.epoch, sessionId: target.sessionId },
      { signal: controller.signal, timeoutMilliseconds: 15000 }).then(value => {
      if (controller.signal.aborted) return;
      if (value.status !== "ok" || value.epoch !== target.epoch || value.sessionId !== target.sessionId || value.projectId !== target.projectId) {
        setError(value.status === "stale_epoch" || value.epoch !== target.epoch ? "Host identity changed. Reload required."
          : "MCP inventory unavailable for the selected session. No other scope was read."); return;
      }
      if (!Array.isArray(value.servers) || value.servers.length > 64 || !Array.isArray(value.sources)
        || value.sources.length > 2 || value.sources.some(source => !/^(Global|Project): (read|read_error|missing)$/.test(source))
        || value.sources.length !== (target.projectId ? 2 : 1) || !value.sources[0]?.startsWith("Global: ")
        || (target.projectId ? !value.sources[1]?.startsWith("Project: ") : value.servers.some(server => server.scope !== "Global"))
        || !Number.isSafeInteger(value.omitted) || value.omitted < 0 || typeof value.policyReadError !== "boolean"
        || value.servers.some(server => typeof server.name !== "string" || !server.name || server.name.length > 128
          || !["Global", "Project"].includes(server.scope) || !["Stdio", "Http"].includes(server.transport)
          || server.enabled !== null && typeof server.enabled !== "boolean" || typeof server.overridesGlobal !== "boolean")) {
        setError("Invalid MCP inventory. Reload required."); return;
      }
      setPage(value);
    }).catch(() => { if (!controller.signal.aborted) setError("MCP inventory could not be read."); });
    return () => controller.abort();
  }, [target?.epoch, target?.sessionId, target?.projectId, read]);
  const active = page?.epoch === target?.epoch && page?.sessionId === target?.sessionId && page?.projectId === target?.projectId ? page : undefined;
  const detail = active?.servers.find(server => `${server.scope}:${server.name}` === selected);
  const filtered = active?.servers.filter(server => server.name.toLowerCase().includes(search.toLowerCase())) ?? [];
  return <main className="configuration-page model-catalog-page" aria-label="MCP Servers">
    <header className="page-heading"><span className="eyebrow">Desktop / MCP Servers</span><h1>MCP Servers</h1>
      <p>Read-only configured inventory. Desktop plugins are off; connection and tools are not inspected. Connect, tools, add/edit/delete and runtime lifecycle remain unavailable here.</p></header>
    {!target ? <p role="status">Select an owned session to read its MCP scope. Catalog-only mode has no MCP inventory.</p>
      : <><p>Selected session: <code>{target.sessionId}</code>. Scope: {target.projectId ? <>global + project <code>{target.projectId}</code> (project overrides global keys)</> : "global only (session has no project)"}.</p>
        {error && <p role="alert">{error}</p>}
        {!active && !error && <p role="status">Loading MCP configuration.</p>}
        {active && <><p>Sources: {active.sources.join("; ")}. {active.policyReadError ? "Policy read failed; enabled state is unknown." : "Policy read succeeded."}</p>
          {active.sources.some(source => source.endsWith("read_error")) && <p role="alert">A configuration source could not be read; inventory may be incomplete.</p>}
          {active.omitted > 0 && <p role="status">{active.omitted} server definitions omitted by inventory bounds.</p>}
          <div className="model-catalog-layout"><section className="model-catalog-providers" aria-label="Configured MCP servers">
            <h2>Configured servers</h2><label>Search servers <input aria-label="Search MCP servers" value={search} onChange={event => setSearch(event.target.value)} /></label>
            {filtered.length === 0 && <p role="status">{search ? "No matching servers." : "No configured servers in readable scopes."}</p>}
            {filtered.map(server => <button type="button" key={`${server.scope}:${server.name}`} aria-pressed={selected === `${server.scope}:${server.name}`}
              onClick={() => setSelected(`${server.scope}:${server.name}`)}><strong>{server.name}</strong><small>{server.scope} · {server.transport}</small></button>)}
          </section><section className="model-catalog-results" aria-label="MCP server details"><h2>Details</h2>
            {!detail ? <p>Select a configured server to inspect safe fields.</p> : <article className="model-catalog-detail"><h3>{detail.name}</h3><dl>
              <dt>Source</dt><dd>{detail.scope}{detail.overridesGlobal ? " (overrides global definition)" : ""}</dd>
              <dt>Transport</dt><dd>{detail.transport}</dd><dt>Configured enabled</dt><dd>{detail.enabled === null ? "Unknown (policy read failed)" : detail.enabled ? "Yes" : "No"}</dd>
              <dt>Runtime availability / connection</dt><dd>Unknown — Desktop plugins are off; configuration is not connection evidence.</dd>
            </dl></article>}
          </section></div></>}
      </>}
  </main>;
}
