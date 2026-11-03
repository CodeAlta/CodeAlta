import { useEffect, useState, type KeyboardEvent } from "react";
import { Button, Callout, Card, NumericInput, Radio, RadioGroup, type Intent } from "@blueprintjs/core";
import { ActivitySpinner } from "../../ActivitySpinner";
import { AppIcon } from "../../AppIcon";
import { ConfirmPopover } from "../../ConfirmPopover";
import type { MessageKey } from "../../localization";
import { SettingsField as Field } from "../../SettingsField";
import { useShellLanguage } from "../../shellLanguage";
import { changedSettings, draftOf, fieldsOf, maximumPort, minimumPort, readPort, type MobileSettingsChange, type MobileSettingsDraft, type MobileSettingsView } from "./model";

/** The calls of the settings page to its plugin. Each answer is the settings of now, read as `readMobileSettings` reads them. */
export type MobileSettingsApi = Readonly<{
  get: (signal: AbortSignal) => Promise<MobileSettingsView>;
  set: (change: MobileSettingsChange) => Promise<MobileSettingsView>;
  reset: () => Promise<MobileSettingsView>;
  /** Calls the listener with the settings each time the plugin says they changed; the function it gives stops it. */
  changes: (listener: (settings: MobileSettingsView) => void) => () => void;
}>;

type Notice = Readonly<{ text: string; intent: Intent }>;

/** How the page speaks of a provider: its name in the choice, a line on it, and its address card. */
type ProviderPage = Readonly<{
  /** Its name: a brand as it is, or words of the page. */
  name: string;
  translated?: boolean;
  help?: MessageKey;
  address: MessageKey;
  unserved: MessageKey;
  apply: MessageKey;
}>;

/** The words of each provider. A provider the page does not know is named by its id, with general words. */
const pages: Readonly<Record<string, ProviderPage>> = {
  tailscale: {
    name: "Tailscale", address: "Address on the tailnet", unserved: "Not served on the tailnet", apply: "Apply to publish the page on the tailnet.",
    help: "Phones on your tailnet open the page, signed in to Tailscale with your account. This computer and the phone need Tailscale.",
  },
  devtunnel: {
    name: "Dev Tunnels", address: "Address of the Dev Tunnel", unserved: "Not served through Dev Tunnels", apply: "Apply to open the Dev Tunnel.",
    help: "Any phone opens the page through a Dev Tunnel, signed in with your GitHub account. This computer needs the devtunnel command line.",
  },
};

const pageOf = (provider: string): ProviderPage => pages[provider] ?? { name: provider, address: "Address of the page", unserved: "Not served", apply: "Apply to publish the page." };

/**
 * The settings page of the Mobile plugin, in Settings > Extensions > Mobile: the provider phones connect through and its fields, the port
 * of the view on this computer, the address while it is served, and Reset access, which gives the phones a new code to scan.
 */
export function MobileSettings({ api }: { api: MobileSettingsApi }) {
  const { t } = useShellLanguage();
  const [settings, setSettings] = useState<MobileSettingsView | null>(null);
  const [unread, setUnread] = useState(false);
  // What is chosen and typed, until it is applied; null follows what the plugin says.
  const [draft, setDraft] = useState<MobileSettingsDraft | null>(null);
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

  const fields = draft ?? draftOf(settings);
  const provider = fields.provider, page = pageOf(provider), shown = fieldsOf(provider);
  const edit = (change: Partial<MobileSettingsDraft>) => setDraft({ ...fields, ...change });
  const changed = changedSettings(settings, fields);
  const portProblem = readPort(fields.port) === null, httpsProblem = shown.includes("httpsPort") && readPort(fields.httpsPort) === null;
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
  const enter = (event: KeyboardEvent) => { if (event.key === "Enter") void apply(); };
  const portField = (id: string, value: string, problem: boolean, onEdit: (text: string) => void) =>
    <NumericInput id={id} value={value} min={minimumPort} max={maximumPort} stepSize={1} minorStepSize={null} majorStepSize={10} clampValueOnBlur={false}
      allowNumericCharactersOnly buttonPosition="none" disabled={busy} intent={problem ? "danger" : "none"} aria-invalid={problem}
      aria-describedby={problem ? "mobile-settings-port-problem" : undefined} inputClassName="mobile-settings-port"
      onValueChange={(_value, text) => onEdit(text)} onKeyDown={enter} />;
  const portNotice = (portProblem || httpsProblem) && <p id="mobile-settings-port-problem" className="settings-field-help" role="alert">{t("A port is a number from 1024 to 65535.")}</p>;
  const name = (id: string) => { const of = pageOf(id); return of.translated ? t(of.name as MessageKey) : of.name; };
  // The address card follows the provider chosen on the page; until it is applied, the address of the other provider is not this one.
  const applied = provider === settings.provider.id;
  return <div className="settings-grid mobile-settings">
    {notice && <Callout className="mobile-settings-notice" intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>{notice.text}</Callout>}
    <Card className="appearance-settings" aria-labelledby="mobile-settings-ports">
      <h2 id="mobile-settings-ports">{t("Connection")}</h2>
      <Field label={t("Phones connect through")} notice={page.help && <p className="settings-field-help">{t(page.help)}</p>}>
        <RadioGroup inline name="mobile-settings-mode" className="mobile-settings-mode" selectedValue={provider} disabled={busy}
          onChange={event => edit({ provider: event.currentTarget.value })}>
          {settings.providers.map(id => <Radio key={id} value={id} label={name(id)} />)}
        </RadioGroup>
      </Field>
      <Field label={t("Port on this computer")} htmlFor="mobile-settings-port" notice={!shown.includes("httpsPort") && portNotice}>
        {portField("mobile-settings-port", fields.port, portProblem, text => edit({ port: text }))}
      </Field>
      {shown.includes("httpsPort") && <Field label={t("Port on the tailnet")} htmlFor="mobile-settings-https-port" notice={portNotice}>
        {portField("mobile-settings-https-port", fields.httpsPort, httpsProblem, text => edit({ httpsPort: text }))}
      </Field>}
      <div className="mobile-settings-actions">
        {draft && <Button variant="minimal" text={t("Cancel")} disabled={busy} onClick={() => setDraft(null)} />}
        <Button intent="primary" text={t("Apply")} disabled={!changed || busy} loading={busy && !!changed} onClick={() => void apply()} />
      </div>
    </Card>
    <Card className="appearance-settings" aria-labelledby="mobile-settings-address">
      <h2 id="mobile-settings-address">{t(page.address)}</h2>
      {applied && settings.address
        ? <p className="mobile-settings-address"><code>{settings.address}</code></p>
        : <><p className="mobile-settings-address" data-unserved="true">{t(page.unserved)}</p>
          <p className="settings-field-help">{applied ? settings.serverDetail ?? settings.provider.detail : t(page.apply)}</p></>}
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
