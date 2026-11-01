import { useEffect, useState } from "react";
import { Button, Callout, Card, HTMLSelect, InputGroup, Switch, Tag, type Intent } from "@blueprintjs/core";
import { ActivitySpinner } from "../../ActivitySpinner";
import { AppIcon } from "../../AppIcon";
import { ConfirmPopover } from "../../ConfirmPopover";
import type { MessageKey } from "../../localization";
import { SettingsField as Field } from "../../SettingsField";
import { useShellLanguage } from "../../shellLanguage";
import { isProfileName, persistentName, profileKind, readSite, withSite, type BrowserSettingsChange, type BrowserSettingsView } from "./model";

/** The calls of the settings page to its plugin. Each answer is the settings of now, read as `readBrowserSettings` reads them. */
export type BrowserSettingsApi = Readonly<{
  get: (signal: AbortSignal) => Promise<BrowserSettingsView>;
  set: (change: BrowserSettingsChange) => Promise<BrowserSettingsView>;
  /** Opens a persistent profile in its browser, with no agent, for the user to sign in. */
  open: (name: string, browser: string | null) => Promise<BrowserSettingsView>;
  remove: (name: string) => Promise<BrowserSettingsView>;
  /** Calls the listener with the settings each time the plugin says they changed; the function it gives stops it. */
  changes: (listener: (settings: BrowserSettingsView) => void) => () => void;
}>;

type Notice = Readonly<{ text: string; intent: Intent }>;
const program = "program";

/**
 * The settings page of the Browser plugin, in Settings > Extensions > Browser: the browser sessions get (Automatic, one found, or a
 * program), the profile they browse in, the persistent profiles, the use of the user's own browser and its sites, and the browsers found
 * with the evidence Automatic reads.
 */
export function BrowserSettings({ api }: { api: BrowserSettingsApi }) {
  const { t, locale } = useShellLanguage();
  const [settings, setSettings] = useState<BrowserSettingsView | null>(null);
  const [unread, setUnread] = useState(false);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [path, setPath] = useState<string | null>(null);
  const [newProfile, setNewProfile] = useState("");
  const [site, setSite] = useState({ allowed: "", blocked: "" });
  const [deleting, setDeleting] = useState<string | null>(null);
  useEffect(() => api.changes(setSettings), [api]);
  useEffect(() => {
    const controller = new AbortController();
    api.get(controller.signal).then(value => { if (!controller.signal.aborted) { setSettings(value); setUnread(false); } },
      () => { if (!controller.signal.aborted) setUnread(true); });
    return () => controller.abort();
  }, [api]);

  if (!settings) return unread
    ? <Callout intent="danger" compact role="alert">{t("The settings could not be read.")}</Callout>
    : <div className="browser-settings-loading" role="status" aria-busy="true"><ActivitySpinner size={18} label={t("Loading…")} /></div>;

  const say = (key: MessageKey, intent: Intent) => setNotice({ text: t(key), intent });
  async function run(call: () => Promise<BrowserSettingsView>, saved: MessageKey | null = "Saved.") {
    if (busy) return;
    setBusy(true); setNotice(null);
    try {
      const answer = await call();
      setSettings(answer);
      if (answer.error) setNotice({ text: answer.error, intent: "danger" });
      else if (answer.notice) setNotice({ text: answer.notice, intent: "success" });
      else if (saved) say(saved, "success");
    } catch { say("The settings could not be changed.", "danger"); }
    finally { setBusy(false); }
  }
  const change = (value: BrowserSettingsChange) => run(() => api.set(value));
  const found = settings.browsers.some(browser => browser.id === settings.browser);
  const choice = settings.browser === "auto" ? "auto" : found ? settings.browser : program;
  const kind = profileKind(settings.profile);
  const typedPath = path ?? (choice === program ? settings.browser : "");
  const date = (value: string) => new Date(value).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
  const addSite = (list: "allowed" | "blocked") => {
    const value = readSite(site[list]);
    if (!value) { say("A site is a host name, such as github.com.", "danger"); return; }
    setSite({ ...site, [list]: "" });
    void change(list === "allowed" ? { allowedSites: withSite(settings.user.allowedSites, value) } : { blockedSites: withSite(settings.user.blockedSites, value) });
  };
  const sites = (list: "allowed" | "blocked", values: readonly string[]) => <div className="browser-settings-sites">
    {values.length === 0
      ? <span className="settings-field-help">{t("None")}</span>
      : values.map(value => <Tag key={value} minimal onRemove={busy ? undefined : () => void change(list === "allowed"
        ? { allowedSites: values.filter(item => item !== value) } : { blockedSites: values.filter(item => item !== value) })}>{value}</Tag>)}
    <span className="browser-settings-add">
      <InputGroup id={`browser-settings-${list}`} small value={site[list]} placeholder="github.com" disabled={busy} aria-label={t(list === "allowed" ? "Allowed sites" : "Blocked sites")}
        onChange={event => setSite({ ...site, [list]: event.target.value })} onKeyDown={event => { if (event.key === "Enter") addSite(list); }} />
      <Button size="small" text={t("Add")} disabled={busy || !site[list].trim()} onClick={() => addSite(list)} />
    </span>
  </div>;

  return <div className="settings-grid browser-settings">
    {notice && <Callout className="browser-settings-notice" intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>{notice.text}</Callout>}
    <Card className="appearance-settings" aria-labelledby="browser-settings-browser-title">
      <h2 id="browser-settings-browser-title">{t("Browser")}</h2>
      <Field label={t("Browser of the sessions")} htmlFor="browser-settings-browser"
        notice={<p className="settings-field-help" data-browser-choice>{settings.choice
          ? t("A session gets {name}: {reason}.", { name: settings.choice.name, reason: settings.choice.reason })
          : settings.refusal}</p>}>
        <HTMLSelect id="browser-settings-browser" value={choice} disabled={busy} onChange={event => {
          const value = event.target.value;
          if (value === program) { setPath(""); return; }
          setPath(null);
          void change({ browser: value });
        }}>
          <option value="auto">{settings.automatic ? t("Automatic ({name})", { name: settings.automatic.name }) : t("Automatic")}</option>
          {settings.browsers.map(browser => <option key={browser.id} value={browser.id}>{browser.name}</option>)}
          <option value={program}>{t("A program…")}</option>
        </HTMLSelect>
      </Field>
      {(choice === program || path !== null) && <Field label={t("Program")} htmlFor="browser-settings-program">
        <span className="browser-settings-add">
          <InputGroup id="browser-settings-program" small value={typedPath} disabled={busy} placeholder={t("The path of the program")} onChange={event => setPath(event.target.value)} />
          <HTMLSelect value={settings.engine ?? "chromium"} disabled={busy} aria-label={t("Engine")} onChange={event => void change({ engine: event.target.value as "chromium" | "firefox" })}>
            <option value="chromium">Chromium</option>
            <option value="firefox">Firefox</option>
          </HTMLSelect>
          <Button size="small" intent="primary" text={t("Apply")} disabled={busy || !typedPath.trim()} onClick={() => { setPath(null); void change({ browser: typedPath.trim(), engine: settings.engine ?? "chromium" }); }} />
        </span>
      </Field>}
      <Field label={t("Profile of the sessions")} htmlFor="browser-settings-profile"
        notice={<p className="settings-field-help">{t(kind === "isolated" ? "A new private profile for each session, deleted when its browser closes."
          : kind === "persistent" ? "A profile CodeAlta keeps for agents: sign in once, and sessions keep those sign-ins."
          : "The user's own browser and sign-ins: each session asks first, then each new site.")}</p>}>
        <HTMLSelect id="browser-settings-profile" value={kind === "persistent" ? `persistent:${persistentName(settings.profile)}` : kind} disabled={busy}
          onChange={event => void change({ profile: event.target.value === "persistent:default" ? "persistent" : event.target.value })}>
          <option value="isolated">{t("Isolated")}</option>
          {[...new Set(["default", ...settings.persistentProfiles])].map(name => <option key={name} value={`persistent:${name}`}>{t("Persistent: {name}", { name })}</option>)}
          <option value="user" disabled={!settings.user.enabled}>{t("Your own browser")}</option>
        </HTMLSelect>
      </Field>
      <Field label={t("Without a window")} htmlFor="browser-settings-headless">
        <Switch id="browser-settings-headless" className="settings-checkbox" checked={settings.headless} disabled={busy} onChange={event => void change({ headless: event.currentTarget.checked })} />
      </Field>
      <Field label={t("Scripts in pages")} htmlFor="browser-settings-script"
        notice={<p className="settings-field-help">{t("Gives sessions evaluate_script, which runs JavaScript in the page.")}</p>}>
        <Switch id="browser-settings-script" className="settings-checkbox" checked={settings.evaluateScript} disabled={busy} onChange={event => void change({ evaluateScript: event.currentTarget.checked })} />
      </Field>
    </Card>
    <Card className="appearance-settings" aria-labelledby="browser-settings-profiles-title">
      <h2 id="browser-settings-profiles-title">{t("Persistent profiles")}</h2>
      <p className="settings-field-help">{t("Open a profile to sign in to the sites agents use: the browser opens it with no agent. Close it before sessions browse in it.")}</p>
      {[...new Set(["default", ...settings.persistentProfiles])].map(name => <div key={name} className="browser-settings-profile" data-browser-profile={name}>
        <code>{name}</code>
        <span className="browser-settings-profile-actions">
          <Button size="small" icon={<AppIcon name="open" size={14} />} text={t("Open to sign in")} disabled={busy} onClick={() => void run(() => api.open(name, null), null)} />
          {settings.persistentProfiles.includes(name) && <span className="browser-settings-delete">
            <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete {name}", { name })} disabled={busy} onClick={() => setDeleting(name)} />
            {deleting === name && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Delete this profile?")} subject={name}
              detail={t("Its sign-ins and its data are deleted.")} confirmLabel={t("Delete")}
              onConfirm={() => { setDeleting(null); void run(() => api.remove(name), null); }} onCancel={() => setDeleting(null)} />}
          </span>}
        </span>
      </div>)}
      <span className="browser-settings-add">
        <InputGroup id="browser-settings-new-profile" small value={newProfile} placeholder={t("New profile name")} disabled={busy} intent={newProfile && !isProfileName(newProfile) ? "danger" : "none"}
          onChange={event => setNewProfile(event.target.value)} />
        <Button size="small" text={t("Open to sign in")} disabled={busy || !isProfileName(newProfile)} onClick={() => { const name = newProfile; setNewProfile(""); void run(() => api.open(name, null), null); }} />
      </span>
    </Card>
    <Card className="appearance-settings" aria-labelledby="browser-settings-user-title">
      <h2 id="browser-settings-user-title">{t("Your own browser")}</h2>
      <Field label={t("Let sessions ask for your browser")} htmlFor="browser-settings-user"
        notice={<p className="settings-field-help">{t("A session asks you first, then for each new site. Chrome and Edge 144 or later need remote debugging turned on at chrome://inspect/#remote-debugging (edge://inspect/#remote-debugging); Firefox is started again on your profile once you close it.")}</p>}>
        <Switch id="browser-settings-user" className="settings-checkbox" checked={settings.user.enabled} disabled={busy} onChange={event => void change({ userProfileEnabled: event.currentTarget.checked })} />
      </Field>
      <Field label={t("Allowed sites")}>{sites("allowed", settings.user.allowedSites)}</Field>
      <Field label={t("Blocked sites")}>{sites("blocked", settings.user.blockedSites)}</Field>
    </Card>
    <Card className="appearance-settings" aria-labelledby="browser-settings-found-title">
      <h2 id="browser-settings-found-title">{t("Browsers found")}</h2>
      {settings.browsers.length === 0
        ? <p className="settings-field-help">{t("No browser was found on this computer.")}</p>
        : <table className="browser-settings-found"><tbody>
          {settings.browsers.map(browser => <tr key={browser.id} data-browser={browser.id}>
            <td><strong>{browser.name}</strong>{browser.version && <span className="browser-settings-version"> {browser.version}</span>}
              {browser.isDefault && <Tag minimal className="browser-settings-default">{t("Default")}</Tag>}</td>
            <td className="browser-settings-used">{browser.lastUsed ? t("Used {date}", { date: date(browser.lastUsed) }) : browser.measurable ? t("No use found") : t("Use not readable")}</td>
          </tr>)}
        </tbody></table>}
    </Card>
  </div>;
}
