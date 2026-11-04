import { useState } from "react";
import { Button, Callout, HTMLSelect, InputGroup, Switch, TextArea } from "@blueprintjs/core";
import type { AutomationIntegrationItem, AutomationTriggerItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { canReply, integrationEvents, isHooked, secretName, secretReference, type IntegrationType } from "./automations";
import type { AutomationsHub, TriggerCheck } from "./automationsHub";

/** What the editor knows around a trigger of another tool: where the automation runs, what it sends, whether it is saved as shown. */
export type IntegrationContext = Readonly<{
  projectId: string | null;
  /** The machine it runs on; null for this computer. */
  runsOn: string | null;
  prompt: string;
  /** The automation as saved, and whether the form still shows it as saved: a try runs what is saved. */
  savedId: string | null;
  saved: boolean;
  index: number;
}>;

const eventLabels: Readonly<Record<string, MessageKey>> = {
  matches: "starts to match", updated: "changes while it matches", mention: "mentions the app", message: "is posted", declared: "is declared",
};

const guides: Readonly<Record<IntegrationType, MessageKey>> = {
  jira_version: "Create an API token at id.atlassian.com and keep it in Settings > Secrets under the name given here. The token is read on the machine that runs the automation, never written in its configuration.",
  jira_query: "Create an API token at id.atlassian.com and keep it in Settings > Secrets under the name given here. The token is read on the machine that runs the automation, never written in its configuration.",
  slack: "Create a Slack app with Socket Mode on, an app-level token with connections:write and a bot token with channels:history, app_mentions:read and chat:write. Keep both in Settings > Secrets and invite the bot to the channel. Nothing connects to the machine: it connects to Slack.",
  incident_io: "Make a hook on the server of the machine, give its address to incident.io (Settings > Webhooks), then keep the signing secret it shows and an API key in Settings > Secrets of that machine. The incident is read again from incident.io before it reaches the prompt.",
  webhook: "Make a hook on the server of the machine, give its address to the tool and keep its signing secret in Settings > Secrets of that machine. Only the fields listed here reach the prompt.",
};

/** A setting of a trigger that names a secret: the field takes its name, the trigger keeps the reference. */
function SecretField({ label, value, disabled, onChange }: { label: MessageKey; value: string | null; disabled: boolean; onChange: (reference: string | null) => void }) {
  const { t } = useShellLanguage();
  return <InputGroup className="automation-integration-secret" leftIcon={<AppIcon name="secret" size={13} />} value={secretName(value)} disabled={disabled} spellCheck={false}
    aria-label={t(label)} title={t("The name of a secret of Settings > Secrets")} placeholder={t(label)} maxLength={64}
    onChange={event => onChange(secretReference(event.target.value))} />;
}

function TextField({ label, value, placeholder, disabled, onChange, wide }: { label: MessageKey; value: string | null; placeholder?: string; disabled: boolean; onChange: (value: string | null) => void; wide?: boolean }) {
  const { t } = useShellLanguage();
  return <InputGroup className={wide ? "automation-integration-wide" : undefined} value={value ?? ""} disabled={disabled} spellCheck={false} aria-label={t(label)} title={t(label)}
    placeholder={placeholder ?? t(label)} maxLength={2000} onChange={event => onChange(event.target.value || null)} />;
}

/**
 * The settings of a trigger of another tool (Jira versions and queries, Slack, incident.io, webhooks) and its setup: what
 * to create where, a test of the connection, a sample event with the prompt it would send, a hook of the server for the
 * machine, and a try of the saved automation on that event, its answer kept instead of written back.
 */
export function IntegrationTriggerFields({ hub, trigger, context, disabled, onChange }: {
  hub: AutomationsHub; trigger: AutomationTriggerItem; context: IntegrationContext; disabled: boolean; onChange: (value: AutomationTriggerItem) => void;
}) {
  const { t } = useShellLanguage();
  const type = trigger.type as IntegrationType;
  const settings = trigger.integration as AutomationIntegrationItem;
  const set = (change: Partial<AutomationIntegrationItem>) => onChange({ ...trigger, integration: { ...settings, ...change } });
  const [busy, setBusy] = useState(false);
  const [check, setCheck] = useState<TriggerCheck | null>(null);
  const [example, setExample] = useState("{\n  \"action\": \"triggered\"\n}");
  const [address, setAddress] = useState<string | null>(null);
  const events = integrationEvents[type];
  const body = type === "webhook" ? example : null;

  async function run(action: () => Promise<TriggerCheck>) {
    setBusy(true);
    try { setCheck(await action()); } finally { setBusy(false); }
  }

  async function makeHook() {
    if (!context.runsOn) { setCheck({ ok: false, message: t("A hook is an address of a server for one of its machines: choose that machine in Runs on.") }); return; }
    setBusy(true);
    try {
      const made = await hub.createHook(context.runsOn, type === "incident_io" ? "incident_io" : "webhook", null);
      if (made.ok && made.hookId) { set({ hook: made.hookId }); setAddress(made.url); setCheck(null); }
      else setCheck({ ok: false, message: made.message });
    } finally { setBusy(false); }
  }

  async function tryIt() {
    if (!context.savedId || !context.saved) { setCheck({ ok: false, message: t("Save the automation first: a try runs it as it is saved.") }); return; }
    setBusy(true);
    try {
      const tried = await hub.tryRun(context.savedId, context.index, body);
      setCheck({ ok: tried.ok, message: tried.ok ? t("The try started: its answer is kept on the run, not written back.") : tried.message ?? null });
    } finally { setBusy(false); }
  }

  const off = disabled || busy;
  return <div className="automation-integration">
    <div className="automation-integration-fields">
      {events.length > 1 && <HTMLSelect value={events.includes(trigger.event) ? trigger.event : events[0]} disabled={off} aria-label={t("Event")}
        onChange={event => onChange({ ...trigger, event: event.target.value })}>
        {events.map(name => <option key={name} value={name}>{t(eventLabels[name] ?? "Event")}</option>)}
      </HTMLSelect>}
      {(type === "jira_version" || type === "jira_query") && <>
        <TextField label="Site" value={settings.site} placeholder="https://acme.atlassian.net" disabled={off} onChange={site => set({ site })} />
        <TextField label="Account email" value={settings.email} placeholder="bot@acme.com" disabled={off} onChange={email => set({ email })} />
        <SecretField label="API token" value={settings.token} disabled={off} onChange={token => set({ token })} />
        {type === "jira_version" ? <TextField label="Project key" value={settings.jiraProject} placeholder="PLAT" disabled={off} onChange={jiraProject => set({ jiraProject })} />
          : <TextField wide label="JQL query" value={settings.query} placeholder={"project = PLAT AND labels = triage"} disabled={off} onChange={query => set({ query })} />}
      </>}
      {type === "slack" && <>
        <TextField label="Channel id" value={settings.channel} placeholder="C0123ABCDEF" disabled={off} onChange={channel => set({ channel })} />
        <TextField label="Text it contains" value={settings.match} disabled={off} onChange={match => set({ match })} />
        <SecretField label="App-level token" value={settings.appToken} disabled={off} onChange={appToken => set({ appToken })} />
        <SecretField label="Bot token" value={settings.botToken} disabled={off} onChange={botToken => set({ botToken })} />
      </>}
      {isHooked(type) && <>
        <TextField label="Hook" value={settings.hook} disabled={off} onChange={hook => set({ hook })} />
        <Button size="small" disabled={off} icon={<AppIcon name="server" size={13} />} onClick={() => void makeHook()}>{t("Make a hook")}</Button>
        <SecretField label="Signing secret" value={settings.secret} disabled={off} onChange={secret => set({ secret })} />
        {type === "incident_io" && <SecretField label="API key" value={settings.apiKey} disabled={off} onChange={apiKey => set({ apiKey })} />}
      </>}
      {type === "webhook" && <>
        <HTMLSelect value={settings.signature ?? "standard"} disabled={off} aria-label={t("Signature")}
          onChange={event => set({ signature: event.target.value, signatureHeader: event.target.value === "hmac_sha256" ? settings.signatureHeader : null })}>
          <option value="standard">Standard Webhooks</option><option value="github">GitHub</option><option value="hmac_sha256">HMAC-SHA256</option>
        </HTMLSelect>
        {settings.signature === "hmac_sha256" && <TextField label="Signature header" value={settings.signatureHeader} placeholder="X-Signature" disabled={off}
          onChange={signatureHeader => set({ signatureHeader })} />}
        <TextField wide label="Fields that reach the prompt" value={settings.fields.join(", ")} placeholder="/action, /alert/title" disabled={off}
          onChange={value => set({ fields: (value ?? "").split(",").map(field => field.trim()).filter(Boolean) })} />
      </>}
      {canReply(type) && <Switch className="automation-integration-reply" checked={settings.reply} disabled={off} label={t("Write the answer back where the event came from")}
        onChange={event => set({ reply: event.currentTarget.checked })} />}
    </div>
    {address && <p className="automation-integration-address"><span>{t("Give this address to the tool:")}</span><code>{address}</code>
      <Button variant="minimal" size="small" icon={<AppIcon name="copy" size={13} />} aria-label={t("Copy the address")} title={t("Copy the address")}
        onClick={() => void navigator.clipboard?.writeText(address)} /></p>}
    <details className="automation-integration-setup">
      <summary>{t("Set up")}</summary>
      <p>{t(guides[type])}</p>
      {type === "webhook" && <TextArea className="automation-integration-example" fill value={example} disabled={off} spellCheck={false} aria-label={t("Example event (JSON)")}
        onChange={event => setExample(event.target.value)} />}
      <div className="automation-integration-actions">
        <Button size="small" disabled={off} icon={<AppIcon name="check" size={13} />} onClick={() => void run(() => hub.testTrigger(trigger, context.projectId))}>{t("Test the connection")}</Button>
        <Button size="small" disabled={off} icon={<AppIcon name="eye" size={13} />} onClick={() => void run(() => hub.sample(trigger, context.projectId, context.prompt, body))}>{t("Show a sample event")}</Button>
        <Button size="small" disabled={off} icon={<AppIcon name="play" size={13} />} onClick={() => void tryIt()}>{t("Try it on this event")}</Button>
      </div>
    </details>
    {check && <Callout className="automation-integration-check" compact intent={check.ok ? "success" : "warning"} role="status">
      {check.detail ? <strong>{check.detail}</strong> : null}
      {check.message && <p>{check.message}</p>}
      {check.replyTo && <p>{t("It would write its answer to {where}.", { where: check.replyTo })}</p>}
      {check.prompt && <pre className="automation-integration-prompt">{check.prompt}</pre>}
    </Callout>}
  </div>;
}
