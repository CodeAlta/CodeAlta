import { useEffect, useRef, useState } from "react";
import { Button, Callout, Tag } from "@blueprintjs/core";
import { globalConfig, type GlobalConfigPluginProviderDetailsResponse, type GlobalConfigPluginSignInMethod, type ModelCatalogCapability } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { PaintedProgressBar } from "../PaintedProgressBar";
import { Copyable } from "../ProviderAccount";
import { useShellLanguage } from "../shellLanguage";
import { downloadSize } from "./pluginProviderForm";
import { terminalCommandLine } from "./terminalCommand";

export type PluginProviderApi = Pick<typeof globalConfig, "pluginProviderDetails" | "updatePluginProvider" | "pluginProviderSignIn">;

/**
 * What a provider of a plugin runs, as its plugin says it (the command of an ACP agent), with the update its catalog
 * offers, what it can and cannot do once tested, and its sign-in.
 */
export function PluginProviderPanel({ epoch, providerKey, api = globalConfig, capabilities, signedOut, blocked, onChanged, onSignedIn, runInTerminal }: {
  epoch: string; providerKey: string; api?: PluginProviderApi;
  /** What the provider can and cannot do, from its last test; null before one. */
  capabilities: readonly ModelCatalogCapability[] | null;
  /** Whether its last test found it signed out: its sign-in shows at once. */
  signedOut: boolean;
  /** Why it cannot be signed in now (unsaved changes), or null. */
  blocked: string | null;
  /** Called when the provider was updated: the page reads it again. */
  onChanged: () => void;
  /** Called when the provider was signed in: the page tests it again. */
  onSignedIn: () => void;
  /** Runs a command in a terminal of the window, when the window can. */
  runInTerminal?: (command: string) => void;
}) {
  const { t } = useShellLanguage();
  const [details, setDetails] = useState<GlobalConfigPluginProviderDetailsResponse | null>(null);
  const [methods, setMethods] = useState<readonly GlobalConfigPluginSignInMethod[] | null>(null);
  const [loadingMethods, setLoadingMethods] = useState(false);
  const [busy, setBusy] = useState<"update" | "signIn" | null>(null);
  const [progress, setProgress] = useState<Readonly<{ message: string | null; received: number | null; total: number | null }> | null>(null);
  const [notice, setNotice] = useState<Readonly<{ text: string; intent: "success" | "danger" | "none" }> | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  useEffect(() => {
    const abort = new AbortController();
    setDetails(null); setMethods(null); setNotice(null);
    void api.pluginProviderDetails({ expectedEpoch: epoch, key: providerKey, signIn: false }, { signal: abort.signal, timeoutMilliseconds: 30000 })
      .then(reply => { if (!abort.signal.aborted) setDetails(reply); }, () => undefined);
    return () => abort.abort();
  }, [api, epoch, providerKey]);

  async function readMethods() {
    if (loadingMethods || blocked) return;
    setLoadingMethods(true); setNotice(null);
    try {
      // Reading the ways to sign in starts the agent: it is done on demand.
      const reply = await api.pluginProviderDetails({ expectedEpoch: epoch, key: providerKey, signIn: true }, { timeoutMilliseconds: 120000 });
      if (!alive.current) return;
      if (reply.status === "ok") setMethods(reply.signInMethods);
      else setNotice({ text: t("The sign-in did not complete: {reason}", { reason: reply.detail ?? reply.status }), intent: "danger" });
    } catch { if (alive.current) setNotice({ text: t("The sign-in did not complete: {reason}", { reason: "timeout" }), intent: "danger" }); }
    finally { if (alive.current) setLoadingMethods(false); }
  }

  async function signIn(method: GlobalConfigPluginSignInMethod) {
    if (busy) return;
    setBusy("signIn"); setNotice(null);
    try {
      const reply = await api.pluginProviderSignIn({ expectedEpoch: epoch, key: providerKey, methodId: method.id }, { timeoutMilliseconds: 360000 });
      if (!alive.current) return;
      if (reply.status === "ok") { setNotice({ text: t("Signed in. Test the provider to see its models."), intent: "success" }); onSignedIn(); }
      else setNotice({ text: t("The sign-in did not complete: {reason}", { reason: reply.message ?? reply.status }), intent: "danger" });
    } catch { if (alive.current) setNotice({ text: t("The sign-in did not complete: {reason}", { reason: "timeout" }), intent: "danger" }); }
    finally { if (alive.current) setBusy(null); }
  }

  async function update() {
    if (busy || !details?.update) return;
    const version = details.update;
    setBusy("update"); setNotice(null); setProgress({ message: null, received: null, total: null });
    try {
      const events = await api.updatePluginProvider({ expectedEpoch: epoch, key: providerKey }, { timeoutMilliseconds: 900000 });
      for await (const event of events) {
        if (!alive.current) return;
        if (event.state === "progress") setProgress({ message: event.message, received: event.received, total: event.total });
        else if (event.state === "done") { setNotice({ text: t("Updated to {version}.", { version }), intent: "success" }); onChanged(); }
        else setNotice({ text: t("The update did not complete: {reason}", { reason: event.message ?? event.status ?? "failed" }), intent: "danger" });
      }
    } catch { if (alive.current) setNotice({ text: t("The update did not complete: {reason}", { reason: "failed" }), intent: "danger" }); }
    finally { if (alive.current) { setBusy(null); setProgress(null); } }
  }

  const windows = typeof navigator !== "undefined" && /Windows/i.test(navigator.userAgent);
  return <div className="provider-settings-wide plugin-provider-panel">
    <div className="plugin-provider-command">
      <span className="plugin-provider-label">{t("Command")}</span>
      {details?.summary ? <code title={details.summary}>{details.summary}</code> : details ? <span className="bp6-text-muted">—</span> : <ActivitySpinner size={12} />}
      {details?.update && <Button size="small" intent="primary" variant="outlined" disabled={!!busy} icon={busy === "update" ? <ActivitySpinner size={12} /> : <AppIcon name="download" size={13} />}
        onClick={() => void update()}>{busy === "update" ? t("Updating…") : t("Update to {version}", { version: details.update })}</Button>}
    </div>
    {details?.detail && <small className="plugin-provider-detail">{details.detail}</small>}
    {progress && <div className="plugin-provider-progress" role="status">
      <span>{progress.message}{progress.total ? ` — ${t("{received} of {total}", { received: downloadSize(progress.received), total: downloadSize(progress.total) })}` : ""}</span>
      <PaintedProgressBar value={progress.total ? (progress.received ?? 0) / progress.total : undefined} />
    </div>}
    <div className="plugin-provider-capabilities" aria-label={t("What it can do")}>
      {capabilities && capabilities.length > 0
        ? capabilities.map(capability => <Tag key={capability.id} minimal round intent={capability.supported ? "success" : "none"}
            className={capability.supported ? undefined : "plugin-provider-unsupported"} title={capability.detail ?? undefined}
            icon={<AppIcon name={capability.supported ? "check" : "minus"} size={12} />}>{capability.label}</Tag>)
        : <small className="bp6-text-muted">{t("Test the provider to see what it can do.")}</small>}
    </div>
    <div className="plugin-provider-sign-in">
      {methods === null
        ? <Button size="small" intent={signedOut ? "warning" : "none"} disabled={loadingMethods || !!busy || !!blocked} title={blocked ?? undefined}
            icon={loadingMethods ? <ActivitySpinner size={12} /> : <AppIcon name="user" size={13} />} onClick={() => void readMethods()}>{t("Sign in")}</Button>
        : methods.map(method => method.terminalCommand
          ? <div key={method.id} className="plugin-provider-terminal-method">
              <span>{method.description ?? t("Run this command in a terminal to sign in, then test the provider again.")}</span>
              <Copyable value={terminalCommandLine(method.terminalCommand, method.terminalEnvironment ?? [], windows)} label={method.label} code />
              {runInTerminal && <Button size="small" icon={<AppIcon name="terminal" size={13} />}
                onClick={() => runInTerminal(terminalCommandLine(method.terminalCommand!, method.terminalEnvironment ?? [], windows))}>{t("Run in a terminal")}</Button>}
            </div>
          : <Button key={method.id} size="small" intent="primary" disabled={!!busy} title={method.description ?? undefined}
              icon={busy === "signIn" ? <ActivitySpinner size={12} /> : <AppIcon name="user" size={13} />} onClick={() => void signIn(method)}>
              {busy === "signIn" ? t("Signing in…") : method.label}</Button>)}
    </div>
    {notice && <Callout compact intent={notice.intent} role={notice.intent === "danger" ? "alert" : "status"}>{notice.text}</Callout>}
  </div>;
}
