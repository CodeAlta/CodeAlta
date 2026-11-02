import { useEffect, useState } from "react";
import { Button, Callout, Card, NumericInput, Radio, RadioGroup, type Intent } from "@blueprintjs/core";
import { ActivitySpinner } from "../../ActivitySpinner";
import { AppIcon } from "../../AppIcon";
import { ConfirmPopover } from "../../ConfirmPopover";
import type { MessageKey } from "../../localization";
import { SettingsField as Field } from "../../SettingsField";
import { useShellLanguage } from "../../shellLanguage";
import { changedSettings, maximumPort, minimumPort, readPort, type MobileMode, type MobileSettingsChange, type MobileSettingsView } from "./model";

/** The calls of the settings page to its plugin. Each answer is the settings of now, read as `readMobileSettings` reads them. */
export type MobileSettingsApi = Readonly<{
  get: (signal: AbortSignal) => Promise<MobileSettingsView>;
  set: (change: MobileSettingsChange) => Promise<MobileSettingsView>;
  reset: () => Promise<MobileSettingsView>;
  /** Calls the listener with the settings each time the plugin says they changed; the function it gives stops it. */
  changes: (listener: (settings: MobileSettingsView) => void) => () => void;
}>;

type Notice = Readonly<{ text: string; intent: Intent }>;

/**
 * The settings page of the Mobile plugin, in Settings > Extensions > Mobile: the port of the view on this computer and its port on the
 * tailnet, the address while it is served, and Reset access, which gives the phones a new code to scan.
 */
export function MobileSettings({ api }: { api: MobileSettingsApi }) {
  const { t } = useShellLanguage();
  const [settings, setSettings] = useState<MobileSettingsView | null>(null);
  const [unread, setUnread] = useState(false);
  // What is chosen and typed, until it is applied; null follows what the plugin says.
  const [draft, setDraft] = useState<Readonly<{ mode: MobileMode; port: string; httpsPort: string }> | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [confirming, setConfirming] = useState(false);
  useEffect(() => api.changes(setSettings), [api]);
  useEffect(() => {
    const controller = new AbortController();
    api.get(controller.signal).then(value => { if (!controller.signal.aborted) { setSettings(value); setUnread(false); } },
      () => { if (!controller.signal.aborted) setUnread(true); });
    return () => controller.abort();
  }, [api]);

  if (!settings) return unread
    ? <Callout intent="danger" compact role="alert">{t("The settings could not be read.")}</Callout>
    : <div className="mobile-settings-loading" role="status" aria-busy="true"><ActivitySpinner size={18} label={t("Loading…")} /></div>;

  const mode = draft?.mode ?? settings.mode, port = draft?.port ?? String(settings.port), httpsPort = draft?.httpsPort ?? String(settings.httpsPort);
  const tunnel = mode === "devtunnel";
  const changed = changedSettings(settings, mode, port, httpsPort);
  const portProblem = readPort(port) === null, httpsProblem = !tunnel && readPort(httpsPort) === null;
  const say = (key: MessageKey, intent: Intent) => setNotice({ text: t(key), intent });
  async function apply() {
    if (!changed || busy) return;
    setBusy(true); setNotice(null);
    try {
      const answer = await api.set(changed);
      setSettings(answer);
      if (answer.error) setNotice({ text: answer.error, intent: "danger" });
      else { setDraft(null); say("Saved.", "success"); }
    } catch { say("The settings could not be changed.", "danger"); }
    finally { setBusy(false); }
  }
  async function reset() {
    setBusy(true); setNotice(null);
    try { setSettings(await api.reset()); setConfirming(false); say("The access was reset.", "success"); }
    catch { setConfirming(false); say("The access could not be reset.", "danger"); }
    finally { setBusy(false); }
  }
  const portField = (id: string, value: string, problem: boolean, edit: (text: string) => void) =>
    <NumericInput id={id} value={value} min={minimumPort} max={maximumPort} stepSize={1} minorStepSize={null} majorStepSize={10} clampValueOnBlur={false}
      allowNumericCharactersOnly buttonPosition="none" disabled={busy} intent={problem ? "danger" : "none"} aria-invalid={problem}
      aria-describedby={problem ? "mobile-settings-port-problem" : undefined} inputClassName="mobile-settings-port"
      onValueChange={(_value, text) => edit(text)} onKeyDown={event => { if (event.key === "Enter") void apply(); }} />;
  return <div className="settings-grid mobile-settings">
    {notice && <Callout className="mobile-settings-notice" intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>{notice.text}</Callout>}
    <Card className="appearance-settings" aria-labelledby="mobile-settings-ports">
      <h2 id="mobile-settings-ports">{t("Connection")}</h2>
      <Field label={t("Phones connect through")}
        notice={tunnel && <p className="settings-field-help">{t("A Dev Tunnel of Microsoft gives the page an HTTPS address any phone opens, without installing anything on it. This computer needs the Dev Tunnels command line and a Microsoft or GitHub account.")}</p>}>
        <RadioGroup inline name="mobile-settings-mode" className="mobile-settings-mode" selectedValue={mode} disabled={busy}
          onChange={event => setDraft({ mode: event.currentTarget.value as MobileMode, port, httpsPort })}>
          <Radio value="tailscale" label="Tailscale" />
          <Radio value="devtunnel" label="Dev Tunnels" />
        </RadioGroup>
      </Field>
      <Field label={t("Port on this computer")} htmlFor="mobile-settings-port"
        notice={tunnel && portProblem && <p id="mobile-settings-port-problem" className="settings-field-help" role="alert">{t("A port is a number from 1024 to 65535.")}</p>}>
        {portField("mobile-settings-port", port, portProblem, text => setDraft({ mode, port: text, httpsPort }))}
      </Field>
      {!tunnel && <Field label={t("Port on the tailnet")} htmlFor="mobile-settings-https-port"
        notice={(portProblem || httpsProblem) && <p id="mobile-settings-port-problem" className="settings-field-help" role="alert">{t("A port is a number from 1024 to 65535.")}</p>}>
        {portField("mobile-settings-https-port", httpsPort, httpsProblem, text => setDraft({ mode, port, httpsPort: text }))}
      </Field>}
      <div className="mobile-settings-actions">
        {draft && <Button variant="minimal" text={t("Cancel")} disabled={busy} onClick={() => setDraft(null)} />}
        <Button intent="primary" text={t("Apply")} disabled={!changed || busy} loading={busy && !!changed} onClick={() => void apply()} />
      </div>
    </Card>
    <Card className="appearance-settings" aria-labelledby="mobile-settings-address">
      <h2 id="mobile-settings-address">{settings.mode === "devtunnel" ? t("Address of the Dev Tunnel") : t("Address on the tailnet")}</h2>
      {settings.address
        ? <p className="mobile-settings-address"><code>{settings.address}</code></p>
        : <><p className="mobile-settings-address" data-unserved="true">{settings.mode === "devtunnel" ? t("Not served through Dev Tunnels") : t("Not served on the tailnet")}</p>
          <p className="settings-field-help">{settings.serverDetail ?? (settings.mode === "devtunnel" ? settings.devTunnel.detail : settings.tailscale.detail)}</p></>}
    </Card>
    <Card className="appearance-settings" aria-labelledby="mobile-settings-access">
      <h2 id="mobile-settings-access">{t("Access")}</h2>
      <Field label={t("Access of the phones")}>
        <span className="mobile-settings-reset">
          <Button intent="danger" variant="outlined" icon={<AppIcon name="refresh" size={15} />} text={t("Reset access")} disabled={busy} onClick={() => setConfirming(true)} />
          {confirming && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Reset access?")} subject="Mobile"
            detail={t("Every phone will need to scan the new code.")} confirmLabel={t("Reset")} onConfirm={() => void reset()} onCancel={() => setConfirming(false)} />}
        </span>
      </Field>
    </Card>
  </div>;
}
