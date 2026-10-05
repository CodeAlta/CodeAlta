import { useEffect, useState } from "react";
import { Button, Card, CardList, FormGroup, InputGroup, PopoverNext, Section, SectionCard, SegmentedControl, Switch, Tag, TextArea } from "@blueprintjs/core";
import { mcpServers, type McpServerEntry } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { McpServerAuthorization } from "./McpServerAuthorization";
import { authorizationBlocked } from "./mcpAuthorization";
import { ScopeChoice, SettingsPage, SettingsUnavailable, useSettingsEditor, type SettingsProject } from "./SettingsPage";
import { mcpServerEdit, mcpServerForm, mcpServerFormDirty, scopedKey, validateMcpServerForm, type McpServerForm, type NameValueRow } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

const added = "\u0000new";

/** Editable name/value rows (environment variables or HTTP headers). Stored values are never shown. */
function NameValueRows({ rows, onChange, disabled, addLabel, namePlaceholder }: {
  rows: readonly NameValueRow[]; onChange: (rows: NameValueRow[]) => void; disabled: boolean; addLabel: MessageKey; namePlaceholder: string;
}) {
  const { t } = useShellLanguage();
  const edit = (index: number, change: Partial<NameValueRow>) => onChange(rows.map((row, at) => at === index ? { ...row, ...change } : row));
  return <div className="settings-name-values">
    {rows.map((row, index) => <div className="settings-name-value" key={index}>
      <InputGroup value={row.name} disabled={disabled} spellCheck={false} placeholder={namePlaceholder} aria-label={t("Name")} onChange={event => edit(index, { name: event.target.value })} />
      <InputGroup value={row.value} disabled={disabled} spellCheck={false} type="password" autoComplete="off" aria-label={t("Value")}
        placeholder={row.stored ? t("Stored — leave blank to keep") : t("Value")} onChange={event => edit(index, { value: event.target.value })} />
      <Button variant="minimal" icon={<AppIcon name="trash" size={15} />} disabled={disabled} aria-label={t("Remove")} title={t("Remove")}
        onClick={() => onChange(rows.filter((_, at) => at !== index))} />
    </div>)}
    <Button variant="outlined" size="small" icon={<AppIcon name="plus" size={14} />} disabled={disabled} text={t(addLabel)}
      onClick={() => onChange([...rows, { name: "", value: "", stored: false }])} />
  </div>;
}

/** Settings page for MCP servers: the defined servers on the left, an edit form on the right. */
export function McpServerSettings({ epoch, project, api = mcpServers }: {
  epoch: string | null; project: SettingsProject; api?: typeof mcpServers;
}) {
  const { t } = useShellLanguage();
  const projectId = project?.id ?? null;
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor(
    options => api.list({ expectedEpoch: epoch, projectId }, options), `${epoch}:${projectId}`);
  const [selected, setSelected] = useState<string | null>(null);
  const [form, setForm] = useState<McpServerForm | null>(null);
  const servers = listing?.servers ?? [];
  const original = selected && selected !== added ? servers.find(server => scopedKey(server.scope, server.key) === selected) ?? null : null;
  // Keep the selection across reloads; fall back to the first server.
  useEffect(() => {
    if (!listing) { setForm(null); return; }
    if (selected === added) return;
    const current = listing.servers.find(server => scopedKey(server.scope, server.key) === selected) ?? listing.servers[0] ?? null;
    setSelected(current ? scopedKey(current.scope, current.key) : null);
    setForm(current ? mcpServerForm(current) : null);
  }, [listing]);
  const baseline = selected === added ? mcpServerForm(null, form?.scope ?? "Global") : original ? mcpServerForm(original) : null;
  const dirty = !!form && !!baseline && mcpServerFormDirty(form, baseline);
  const problem = form ? validateMcpServerForm(form, servers, original) : null;
  const edit = (change: Partial<McpServerForm>) => setForm(current => current ? { ...current, ...change } : current);
  function choose(server: McpServerEntry | null) {
    if (busy) return;
    setNotice(null);
    setSelected(server ? scopedKey(server.scope, server.key) : added);
    setForm(mcpServerForm(server));
  }
  async function save() {
    if (!form || problem || busy) return;
    const wire = mcpServerEdit(form);
    if (await mutate(() => api.save({ expectedEpoch: epoch, projectId, scope: form.scope, originalKey: original?.key ?? null,
      originalScope: original?.scope ?? null, server: wire }, { timeoutMilliseconds: 30000 }), "Saved."))
      setSelected(scopedKey(form.scope, wire.key!));
  }
  async function remove() {
    if (!original || busy) return;
    if (await mutate(() => api.remove({ expectedEpoch: epoch, projectId, scope: original.scope, key: original.key }, { timeoutMilliseconds: 30000 }), "Removed.")) setSelected(null);
  }
  const toggle = (server: McpServerEntry, enabled: boolean) => void mutate(() => api.setEnabled({ expectedEpoch: epoch, projectId, scope: server.scope, key: server.key, enabled }, { timeoutMilliseconds: 30000 }));

  return <SettingsPage className="mcp-settings" label={t("MCP Servers")} group="Extensions" title="MCP Servers" description="Model Context Protocol servers that give agents extra tools."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={<Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!listing || busy} onClick={() => choose(null)}>{t("Add server")}</Button>}>
    {!listing ? <SettingsUnavailable loading={loading} icon="server" title="MCP servers unavailable" />
      : <div className="settings-editor-layout">
        <CardList compact className="settings-editor-list" aria-label={t("MCP Servers")}>
          {servers.map(server => { const id = scopedKey(server.scope, server.key); return <Card key={id} interactive selected={selected === id}
            aria-current={selected === id ? "true" : undefined} onClick={() => choose(server)}>
            <span className="settings-editor-name"><strong>{server.key}</strong>
              <small>{server.transport === "Http" ? server.url ?? "HTTP" : [server.command, ...server.arguments].filter(Boolean).join(" ")}</small></span>
            <span className="settings-editor-tags"><Tag minimal round>{server.scope === "Project" ? t("Project") : t("Global")}</Tag>
              {server.shadowed && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
              <Switch checked={server.enabled} disabled={busy} aria-label={t("Enabled")} onClick={event => event.stopPropagation()}
                onChange={event => toggle(server, event.currentTarget.checked)} /></span>
          </Card>; })}
          {selected === added && <Card interactive selected><span className="settings-editor-name"><strong>{form?.key || t("New server")}</strong></span>
            <Tag minimal round intent="warning">{t("Unsaved changes")}</Tag></Card>}
          {servers.length === 0 && selected !== added && <Card><span className="bp6-text-muted">{t("No MCP servers are defined yet.")}</span></Card>}
        </CardList>
        {form && <Section className="settings-editor-form" title={original ? original.key : t("New server")}
          rightElement={<Switch checked={form.enabled} disabled={busy} label={t("Enabled")} alignIndicator="end" onChange={event => edit({ enabled: event.currentTarget.checked })} />}>
          <SectionCard className="settings-editor-fields">
            <FormGroup label={t("Name")} labelFor="mcp-key">
              <InputGroup id="mcp-key" value={form.key} disabled={busy} maxLength={128} spellCheck={false} placeholder="my-server" onChange={event => edit({ key: event.target.value })} /></FormGroup>
            <FormGroup label={t("Connection")}>
              <SegmentedControl size="small" disabled={busy} value={form.transport} onValueChange={value => edit({ transport: value as McpServerForm["transport"] })}
                options={[{ label: t("Local command"), value: "Stdio" }, { label: "HTTP", value: "Http" }]} /></FormGroup>
            {project && <FormGroup label={t("Stored in")} className="settings-editor-wide"><ScopeChoice value={form.scope} project={project} disabled={busy} onChange={scope => edit({ scope })} /></FormGroup>}
            {form.transport === "Stdio" ? <>
              <FormGroup label={t("Command")} labelFor="mcp-command">
                <InputGroup id="mcp-command" value={form.command} disabled={busy} spellCheck={false} placeholder="npx" onChange={event => edit({ command: event.target.value })} /></FormGroup>
              <FormGroup label={t("Working directory")} labelFor="mcp-directory">
                <InputGroup id="mcp-directory" value={form.workingDirectory} disabled={busy} spellCheck={false} onChange={event => edit({ workingDirectory: event.target.value })} /></FormGroup>
              <FormGroup label={t("Arguments")} labelFor="mcp-arguments" helperText={t("One argument per line.")} className="settings-editor-wide">
                <TextArea id="mcp-arguments" fill value={form.arguments} disabled={busy} spellCheck={false} rows={4} className="bp6-monospace-text" onChange={event => edit({ arguments: event.target.value })} /></FormGroup>
              <FormGroup label={t("Environment variables")} className="settings-editor-wide">
                <NameValueRows rows={form.environment} disabled={busy} addLabel="Add variable" namePlaceholder="API_TOKEN" onChange={environment => edit({ environment })} /></FormGroup>
            </> : <>
              <FormGroup label={t("Server URL")} labelFor="mcp-url" className="settings-editor-wide">
                <InputGroup id="mcp-url" value={form.url} disabled={busy} spellCheck={false} placeholder="https://" onChange={event => edit({ url: event.target.value })} /></FormGroup>
              <FormGroup label={t("HTTP headers")} className="settings-editor-wide">
                <NameValueRows rows={form.headers} disabled={busy} addLabel="Add header" namePlaceholder="Authorization" onChange={headers => edit({ headers })} /></FormGroup>
              {original?.transport === "Http" && <FormGroup label={t("Authorization")} className="settings-editor-wide mcp-authorization-group"
                helperText={t("Sign in to this server in your browser. The tokens stay on this computer, outside the server definition.")}>
                <McpServerAuthorization key={scopedKey(original.scope, original.key)} epoch={epoch} projectId={projectId} server={original} api={api}
                  blocked={authorizationBlocked(original, dirty)} onChanged={reload} /></FormGroup>}
            </>}
          </SectionCard>
          <SectionCard className="settings-editor-footer">
            {problem && dirty && <span className="settings-editor-problem" role="alert">{t(problem)}</span>}
            <Button intent="primary" disabled={!dirty || !!problem || busy} onClick={() => void save()}>{t("Save")}</Button>
            <Button disabled={selected !== added && !dirty || busy} onClick={() => { if (selected !== added) { if (baseline) setForm(baseline); } else if (servers[0]) choose(servers[0]); else { setSelected(null); setForm(null); } }}>{t(selected === added ? "Cancel" : "Revert")}</Button>
            <span className="settings-editor-spacer" />
            {original && <PopoverNext placement="top-end" content={<div className="provider-settings-confirm"><p>{t("Remove {name}?", { name: original.key })}</p>
              <Button intent="danger" disabled={busy} onClick={() => void remove()}>{t("Remove")}</Button></div>}>
              <Button variant="minimal" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={busy} text={t("Remove")} />
            </PopoverNext>}
          </SectionCard>
        </Section>}
      </div>}
  </SettingsPage>;
}
