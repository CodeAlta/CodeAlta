import { useEffect, useRef, useState } from "react";
import { Button, Callout, InputGroup, Switch, Tag } from "@blueprintjs/core";
import { mcpHost } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { mcpClientConfiguration, mcpServerName, type McpHostState } from "./mcpHost";

type Copy = (text: string) => Promise<void>;

/**
 * Settings page for the MCP server of the application: the switch that turns it on and off, the address other
 * applications connect to, the configuration to give them, and the tools they get.
 */
export function McpHostSettings({ epoch, developer, api = mcpHost, copy = text => navigator.clipboard.writeText(text) }: {
  epoch: string | null;
  /** Whether this is the developer instance, which clients know under another name. */
  developer: boolean;
  api?: Pick<typeof mcpHost, "status" | "setEnabled">;
  copy?: Copy;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(options => api.status({ expectedEpoch: epoch }, options), `${epoch}`);
  return <SettingsPage className="mcp-host-settings" label={t("CodeAlta MCP server")} group="Advanced" title="CodeAlta MCP server"
    description="Other applications see and drive this window and run CodeAlta commands through this server."
    notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="remote" title="MCP server unavailable" />
      : <McpHostView state={listing} developer={developer} busy={busy} copy={copy}
        onEnable={enabled => void mutate(async () => ({ status: (await api.setEnabled({ expectedEpoch: epoch, enabled }, { timeoutMilliseconds: 30_000 })).status }))} />}
  </SettingsPage>;
}

/** What the page shows of the server: its switch, and what a client needs while it runs. */
export function McpHostView({ state, developer, busy, onEnable, copy }: {
  state: McpHostState; developer: boolean; busy: boolean; onEnable: (enabled: boolean) => void; copy: Copy;
}) {
  const { t } = useShellLanguage();
  const running = state.state === "running" && !!state.url;
  const configuration = running ? mcpClientConfiguration(mcpServerName(developer), state.url!, state.token) : "";
  return <>
    <Switch className="mcp-host-switch" checked={state.enabled} disabled={busy} label={t("Run the MCP server")}
      onChange={event => onEnable(event.currentTarget.checked)} />
    {state.state === "failed" && <Callout intent="warning" compact role="alert">
      {t("The MCP server is not listening.")}{state.error && <div className="config-editor-diagnostic">{state.error}</div>}</Callout>}
    {running && <>
      <CopyField label="Address" copyLabel="Copy address" value={state.url!} copy={copy} />
      {state.token && <CopyField label="Access token" copyLabel="Copy token" value={state.token} copy={copy} />}
      <section className="mcp-host-block" aria-label={t("Client configuration")}>
        <header><h2>{t("Client configuration")}</h2><CopyButton label="Copy configuration" text={configuration} copy={copy} /></header>
        <pre className="mcp-host-configuration">{configuration}</pre>
      </section>
      <section className="mcp-host-block" aria-label={t("Tools")}>
        <header><h2>{t("Tools")}</h2><span className="mcp-host-count">{state.tools.length}</span></header>
        <div className="mcp-host-tools">{state.tools.map(tool => <Tag key={tool} minimal>{tool}</Tag>)}</div>
      </section>
    </>}
  </>;
}

function CopyField({ label, copyLabel, value, copy }: { label: MessageKey; copyLabel: MessageKey; value: string; copy: Copy }) {
  const { t } = useShellLanguage();
  return <label className="mcp-host-field"><span>{t(label)}</span>
    <InputGroup readOnly value={value} aria-label={t(label)} onFocus={event => event.currentTarget.select()}
      rightElement={<CopyButton label={copyLabel} text={value} copy={copy} minimal />} />
  </label>;
}

function CopyButton({ label, text, copy, minimal }: { label: MessageKey; text: string; copy: Copy; minimal?: boolean }) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(timer.current), []);
  return <Button size="small" variant={minimal ? "minimal" : undefined} icon={<AppIcon name={copied ? "check" : "copy"} size={14} />}
    aria-label={t(label)} title={t(copied ? "Copied" : label)}
    onClick={() => void copy(text).then(() => {
      setCopied(true);
      window.clearTimeout(timer.current);
      timer.current = window.setTimeout(() => setCopied(false), 1400);
    }, () => { /* The clipboard is not available: nothing is copied. */ })}>{minimal ? undefined : t(copied ? "Copied" : "Copy")}</Button>;
}
