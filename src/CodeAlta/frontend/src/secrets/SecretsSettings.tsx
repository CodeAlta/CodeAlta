import { useCallback, useEffect, useState } from "react";
import { Button, Card, InputGroup, NonIdealState, PopoverNext, Tag } from "@blueprintjs/core";
import { secrets, type CredentialProviderRpcView, type SecretsList, type SecretView, type VariableView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { CredentialProviders } from "./CredentialProviders";
import { askDurations, stateLabel } from "./credentials";
import { ProviderDialog } from "./ProviderDialog";
import { SignInsCard } from "./SignIns";
import { VaultBindingDialog } from "./VaultPicker";
import { isSecretName, referenceOf, secretNameProblem } from "./secrets";

type SecretsApi = Pick<typeof secrets, "list" | "setSecret" | "rotateSecret" | "deleteSecret" | "setVariable" | "deleteVariable" | "saveProvider" | "removeProvider"
  | "unlockProvider" | "lockProvider" | "testProvider" | "browse" | "bindSecret" | "setAskBeforeUse" | "confirmUse" | "declineUse" | "saveSignIn" | "removeSignIn" | "revokeSiteGrant">;
type Result = Promise<{ status: string; message?: string | null }>;

/** What the body of the page does: each call writes one change and says how it went. */
export type SecretsActions = Readonly<{
  setSecret: (name: string, value: string) => Result;
  rotateSecret: (name: string, value: string) => Result;
  deleteSecret: (name: string) => Result;
  setVariable: (name: string, value: string) => Result;
  deleteVariable: (name: string) => Result;
}>;

/** What the rows of the secrets do with the vaults; absent on the page of a machine, which has none. */
export type SecretVaultActions = Readonly<{
  keepInVault: (name: string) => void;
  setAskBeforeUse: (name: string, duration: string | null) => Result;
  verification: { available: boolean; reason: string | null } | null;
}>;

/**
 * Settings > Secrets: the credential providers (this computer and the vaults), the secrets of this machine listed by name
 * with where each is kept (Add, Rotate, Delete, Keep in a vault, Ask before use; a value is sent and never shown back), the
 * uses that wait for a confirmation, and the variables, which are read and edited.
 */
export function SecretsSettings({ epoch, api = secrets, runInTerminal }: { epoch: string | null; api?: SecretsApi; runInTerminal?: (command: string) => void }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, refresh, mutate } = useSettingsEditor(options => api.list({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const [adding, setAdding] = useState(false);
  const [providerDialog, setProviderDialog] = useState<{ editing: CredentialProviderRpcView | null } | null>(null);
  const [binding, setBinding] = useState<{ name?: string; provider?: string } | null>(null);
  const expectedEpoch = epoch ?? "";
  // A vault unlocked elsewhere, a use that waits: the page reads again by itself while it is shown.
  useEffect(() => {
    if (!epoch) return;
    const timer = window.setInterval(() => { if (document.visibilityState === "visible") refresh(); }, 4000);
    return () => window.clearInterval(timer);
  }, [epoch, refresh]);
  const actions: SecretsActions = {
    setSecret: (name, value) => api.setSecret({ expectedEpoch, name, value }),
    rotateSecret: (name, value) => api.rotateSecret({ expectedEpoch, name, value }),
    deleteSecret: name => api.deleteSecret({ expectedEpoch, name }),
    setVariable: (name, value) => api.setVariable({ expectedEpoch, name, value }),
    deleteVariable: name => api.deleteVariable({ expectedEpoch, name }),
  };
  const run = (call: () => Result) => mutate(call, "Saved.");
  const browse = useCallback((provider: string, parent: string | null) => api.browse({ expectedEpoch, id: provider, parent }), [api, expectedEpoch]);
  // A host with a credential broker offers the vaults; without a provider yet, Keep in a vault adds one first.
  const vaults: SecretVaultActions | null = listing && listing.types.length > 0 ? {
    keepInVault: name => { if (listing.providers.length > 0) setBinding({ name }); else setProviderDialog({ editing: null }); },
    setAskBeforeUse: (name, duration) => api.setAskBeforeUse({ expectedEpoch, name, duration }),
    verification: listing.verification,
  } : null;
  return <SettingsPage className="secrets-settings" label={t("Secrets")} group="Agent & models" title="Secrets"
    description="Named values of this machine. The configuration refers to them as ${secret:NAME} and ${var:NAME}."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={<>
      {listing && listing.providers.length > 0 && <Button icon={<AppIcon name="secret" size={15} />} disabled={!epoch || busy} onClick={() => setBinding({})}>{t("Keep in a vault…")}</Button>}
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!epoch || !listing || adding} onClick={() => setAdding(true)}>{t("Add a secret…")}</Button>
    </>}>
    {!listing ? <SettingsUnavailable loading={loading} icon="secret" title="Secrets unavailable" />
      : <>
        {listing.pending.length > 0 && <PendingUses listing={listing} busy={busy}
          onConfirm={(name, use) => run(() => api.confirmUse({ expectedEpoch, name, use }))} onDecline={(name, use) => run(() => api.declineUse({ expectedEpoch, name, use }))} />}
        <CredentialProviders listing={listing} busy={busy} run={run} onAdd={() => setProviderDialog({ editing: null })} actions={{
          unlock: (id, password) => api.unlockProvider({ expectedEpoch, id, password }),
          lock: id => api.lockProvider({ expectedEpoch, id }),
          test: id => api.testProvider({ expectedEpoch, id }),
          remove: id => api.removeProvider({ expectedEpoch, id }),
          edit: provider => setProviderDialog({ editing: provider }),
          runInTerminal,
        }} />
        <SecretsBody listing={listing} busy={busy} adding={adding} onAdded={() => setAdding(false)} run={run} actions={actions} vaults={vaults} />
        {listing.types.length > 0 && <SignInsCard signIns={listing.signIns} providers={listing.providers} types={listing.types} busy={busy} run={run} actions={{
          save: draft => api.saveSignIn({ expectedEpoch, id: draft.id, label: draft.label, provider: draft.provider, sites: [...draft.sites],
            username: draft.username, password: draft.password, totp: draft.totp.trim() || null }),
          remove: id => api.removeSignIn({ expectedEpoch, id }),
          revoke: (id, origin) => api.revokeSiteGrant({ expectedEpoch, id, origin }),
          browse,
        }} />}
        {listing.uses.length > 0 && <RecentUses listing={listing} />}
      </>}
    {providerDialog && listing && <ProviderDialog types={listing.types} providers={listing.providers} editing={providerDialog.editing} busy={busy}
      onClose={() => setProviderDialog(null)}
      onSave={draft => run(() => api.saveProvider({ expectedEpoch, id: draft.id, type: draft.type, displayName: draft.displayName, cache: draft.cache,
        unlockAtStart: draft.unlockAtStart, settings: draft.settings }))} />}
    {binding && listing && listing.providers.length > 0 && <VaultBindingDialog providers={listing.providers} types={listing.types}
      names={listing.secrets.map(secret => secret.name)} localNames={listing.secrets.filter(secret => !secret.provider).map(secret => secret.name)}
      initialName={binding.name} initialProvider={binding.provider} busy={busy} browse={browse} onClose={() => setBinding(null)}
      onBind={value => run(() => api.bindSecret({ expectedEpoch, name: value.name, provider: value.provider, reference: value.reference, label: value.label, replaceLocal: value.replaceLocal }))} />}
  </SettingsPage>;
}

/** The uses of secrets that ask before use, waiting for the user: Confirm asks the system to verify the user. */
function PendingUses({ listing, busy, onConfirm, onDecline }: {
  listing: SecretsList; busy: boolean; onConfirm: (name: string, use: string | null) => Promise<boolean>; onDecline: (name: string, use: string | null) => Promise<boolean>;
}) {
  const { t } = useShellLanguage();
  return <Card className="appearance-settings secret-pending" aria-labelledby="secret-pending-title">
    <h2 id="secret-pending-title"><AppIcon name="warning" size={16} />{t("Waits for your confirmation")}</h2>
    <ul className="secret-list">
      {listing.pending.map(pending => <li key={`${pending.name}\n${pending.use ?? ""}`} className="secret-row secret-pending-row">
        <span className="secret-name"><code>{pending.name}</code>
          <span className="secret-reference">{pending.use ? t("for the {use}", { use: pending.use }) : t("for a use nobody named")}</span></span>
        <span className="secret-meta">{new Date(pending.at).toLocaleTimeString()}</span>
        <span className="secret-actions">
          <Button size="small" intent="primary" icon={<AppIcon name="shield" size={14} />} text={t("Confirm…")} disabled={busy || !listing.verification?.available}
            title={listing.verification?.available ? t("Windows Hello or Touch ID verifies you first.") : listing.verification?.reason ?? undefined}
            onClick={() => void onConfirm(pending.name, pending.use)} />
          <Button size="small" variant="minimal" text={t("Decline")} disabled={busy} onClick={() => void onDecline(pending.name, pending.use)} />
        </span>
      </li>)}
    </ul>
  </Card>;
}

/** The last uses of the secrets: which, by what, when and how it went. Never a value. */
function RecentUses({ listing }: { listing: SecretsList }) {
  const { t, locale } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const outcome = (value: string) => value === "given" ? t("given") : value === "waits" ? t("waits for you") : value === "confirmed" ? t("confirmed")
    : value === "refused" ? t("declined") : t("not available");
  const uses = open ? listing.uses : listing.uses.slice(0, 5);
  return <Card className="appearance-settings secret-uses" aria-labelledby="secret-uses-title">
    <h2 id="secret-uses-title">{t("Recent uses")}</h2>
    <ul className="secret-use-list">
      {uses.map((use, index) => <li key={`${use.at}-${index}`} data-outcome={use.outcome}>
        <code>{use.name}</code>
        <span>{use.use ? t("for the {use}", { use: use.use }) : "—"}</span>
        <span className="secret-meta">{new Date(use.at).toLocaleString(locale, { timeStyle: "short", dateStyle: "short" })}</span>
        <Tag minimal intent={use.outcome === "given" || use.outcome === "confirmed" ? "success" : use.outcome === "waits" ? "warning" : "none"}>{outcome(use.outcome)}</Tag>
      </li>)}
    </ul>
    {listing.uses.length > 5 && <Button size="small" variant="minimal" text={open ? t("Fewer") : t("All {count}", { count: String(listing.uses.length) })} onClick={() => setOpen(!open)} />}
  </Card>;
}

/**
 * The secrets and the variables of a listing, with their forms: two cards of a settings page, or, `flat`, two parts of a
 * section of another page (the page of a machine).
 */
export function SecretsBody({ listing, busy, adding = false, onAdded = () => { }, run, actions, flat = false, vaults = null }: {
  listing: SecretsList; busy: boolean; adding?: boolean; onAdded?: () => void;
  run: (call: () => Result) => Promise<boolean>; actions: SecretsActions; flat?: boolean; vaults?: SecretVaultActions | null;
}) {
  const { t, locale } = useShellLanguage();
  const date = (value: string) => new Date(value).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
  const names = listing.secrets.map(secret => secret.name);
  const secretsPart = <>
    {adding && <NewValueForm kind="secret" taken={names} busy={busy} onCancel={onAdded}
      onSave={async (name, value) => { if (await run(() => actions.setSecret(name, value))) onAdded(); }} />}
    {listing.secrets.length === 0 && !adding
      ? <NonIdealState className="secrets-empty" icon={<AppIcon name="secret" size={28} />} title={t("No secret on this machine.")} />
      : <ul className="secret-list" aria-label={t("Secrets")}>
        {listing.secrets.map(secret => <SecretRow key={secret.name} secret={secret} busy={busy} date={date} run={run} actions={actions} vaults={vaults} />)}
      </ul>}
  </>;
  const variablesPart = <>
    {listing.variables.length > 0 && <ul className="secret-list" aria-label={t("Variables")}>
      {listing.variables.map(variable => <VariableRow key={variable.name} variable={variable} busy={busy} run={run} actions={actions} />)}
    </ul>}
    <NewValueForm kind="var" taken={listing.variables.map(variable => variable.name)} busy={busy}
      onSave={(name, value) => run(() => actions.setVariable(name, value)).then(() => { })} />
  </>;
  if (flat) {
    return <div className="secrets-body secrets-body-flat">
      <div className="secrets-part">{secretsPart}</div>
      <div className="secrets-part" role="group" aria-label={t("Variables")}><h3>{t("Variables")}</h3>{variablesPart}</div>
    </div>;
  }

  return <div className="settings-grid secrets-body">
    <Card className="appearance-settings" aria-labelledby="secrets-title">
      <h2 id="secrets-title">{t("Secrets")}</h2>
      {secretsPart}
    </Card>
    <Card className="appearance-settings" aria-labelledby="secret-variables-title">
      <h2 id="secret-variables-title">{t("Variables")}</h2>
      {variablesPart}
    </Card>
  </div>;
}

/**
 * One secret: its name and reference, where it is kept (this computer, with its version, or an item of a vault), its state when
 * it cannot be given now, Ask before use, and Rotate (a value of this computer) and Delete.
 */
function SecretRow({ secret, busy, date, run, actions, vaults }: {
  secret: SecretView; busy: boolean; date: (value: string) => string; run: (call: () => Result) => Promise<boolean>; actions: SecretsActions; vaults: SecretVaultActions | null;
}) {
  const { t } = useShellLanguage();
  const [deleting, setDeleting] = useState(false);
  const [rotating, setRotating] = useState(false);
  const [value, setValue] = useState("");
  const rotate = async () => {
    const typed = value;
    setValue("");
    if (await run(() => actions.rotateSecret(secret.name, typed))) setRotating(false);
  };
  const state = stateLabel(secret.state);
  const bound = !!secret.provider;
  return <li className="secret-row" data-secret={secret.name} data-source={bound ? secret.provider ?? undefined : "this-computer"}>
    <span className="secret-name"><code title={secret.name}>{secret.name}</code>
      <span className="secret-reference" title={referenceOf("secret", secret.name)}>{referenceOf("secret", secret.name)}</span></span>
    {bound
      ? <span className="secret-source" title={secret.label ?? undefined}><CredentialIcon icon={secret.icon} size={14} />
        <span>{secret.providerName ?? secret.provider}{secret.label ? ` › ${secret.label}` : ""}</span></span>
      : <Tag minimal>{t("Version {version}", { version: String(secret.version) })}</Tag>}
    <span className="secret-meta">
      {secret.state !== "ready" && <Tag minimal intent={state.intent} title={secret.stateMessage ?? undefined}>{t(state.label)}</Tag>}
      {secret.askBeforeUse && <Tag minimal icon={<AppIcon name="shield" size={12} />}>{t("Asks before use")}</Tag>}
      {!bound && t("Set {date} by {who}", { date: date(secret.setAt), who: secret.setBy })}
    </span>
    <span className="secret-actions">
      {vaults && <AskBeforeUse secret={secret} busy={busy} run={run} vaults={vaults} />}
      {!bound && vaults && <Button size="small" variant="minimal" icon={<AppIcon name="openExternal" size={14} />} aria-label={t("Keep {name} in a vault", { name: secret.name })}
        title={t("Keep in a vault…")} disabled={busy} onClick={() => vaults.keepInVault(secret.name)} />}
      {!bound && <PopoverNext isOpen={rotating} placement="bottom-end" onInteraction={next => { if (!next && !busy) { setRotating(false); setValue(""); } }}
        content={<form className="secret-rotate" onSubmit={event => { event.preventDefault(); if (value) void rotate(); }}>
          <strong>{t("New value of {name}", { name: secret.name })}</strong>
          <InputGroup type="password" autoComplete="off" autoFocus small value={value} disabled={busy} aria-label={t("New value of {name}", { name: secret.name })}
            onChange={event => setValue(event.target.value)} />
          <div className="rename-actions">
            <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={() => { setRotating(false); setValue(""); }} />
            <Button type="submit" intent="primary" size="small" text={t("Rotate")} disabled={busy || !value} />
          </div>
        </form>}>
        <Button size="small" icon={<AppIcon name="refresh" size={14} />} text={t("Rotate")} disabled={busy} onClick={() => setRotating(true)} />
      </PopoverNext>}
      <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete {name}", { name: secret.name })}
          disabled={busy} onClick={() => setDeleting(true)} />
        {deleting && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Delete this secret?")} subject={secret.name}
          detail={bound ? t("Nothing changes in the vault. What refers to it stops working until it is set again.") : t("What refers to it stops working until it is set again.")}
          confirmLabel={t("Delete")}
          onConfirm={() => { setDeleting(false); void run(() => actions.deleteSecret(secret.name)); }} onCancel={() => setDeleting(false)} />}
      </span>
    </span>
  </li>;
}

/** Ask before use: off, or a confirmation by Windows Hello or Touch ID that lasts a while; off with its reason where the system cannot verify. */
function AskBeforeUse({ secret, busy, run, vaults }: { secret: SecretView; busy: boolean; run: (call: () => Result) => Promise<boolean>; vaults: SecretVaultActions }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const available = vaults.verification?.available === true;
  const choose = async (duration: string | null) => { if (await run(() => vaults.setAskBeforeUse(secret.name, duration))) setOpen(false); };
  return <PopoverNext isOpen={open} placement="bottom-end" onInteraction={next => { if (!next) setOpen(false); }}
    content={<div className="secret-ask">
      <strong>{t("Ask before use")}</strong>
      <p>{available ? t("Windows Hello or Touch ID verifies you before CodeAlta gives {name} to what asks for it.", { name: secret.name })
        : vaults.verification?.reason ?? t("This system cannot verify you.")}</p>
      <div className="secret-ask-choices" role="radiogroup" aria-label={t("Ask before use")}>
        <Button size="small" variant={secret.askBeforeUse ? "minimal" : "outlined"} active={!secret.askBeforeUse} text={t("Off")} disabled={busy} onClick={() => void choose(null)} />
        {askDurations.map(duration => <Button key={duration.value} size="small" variant={secret.askBeforeUse === duration.value ? "outlined" : "minimal"}
          active={secret.askBeforeUse === duration.value} text={t(duration.label)} disabled={busy || !available} onClick={() => void choose(duration.value)} />)}
      </div>
      {available && <p className="secret-ask-note">{t("A confirmation is kept for that secret and what uses it, for the time you choose.")}</p>}
    </div>}>
    <Button size="small" variant="minimal" intent={secret.askBeforeUse ? "primary" : "none"} icon={<AppIcon name="shield" size={14} />}
      aria-label={t("Ask before using {name}", { name: secret.name })} title={t("Ask before use")} disabled={busy} onClick={() => setOpen(true)} />
  </PopoverNext>;
}

/** One variable: its name and reference, its value, editable; Delete. */
function VariableRow({ variable, busy, run, actions }: {
  variable: VariableView; busy: boolean; run: (call: () => Result) => Promise<boolean>; actions: SecretsActions;
}) {
  const { t } = useShellLanguage();
  const [value, setValue] = useState(variable.value);
  const [deleting, setDeleting] = useState(false);
  const changed = value !== variable.value;
  return <li className="secret-row" data-variable={variable.name}>
    <span className="secret-name"><code title={variable.name}>{variable.name}</code>
      <span className="secret-reference" title={referenceOf("var", variable.name)}>{referenceOf("var", variable.name)}</span></span>
    <InputGroup className="secret-value" small value={value} disabled={busy} aria-label={t("Value of {name}", { name: variable.name })}
      onChange={event => setValue(event.target.value)} onKeyDown={event => { if (event.key === "Enter" && changed && value) void run(() => actions.setVariable(variable.name, value)); }} />
    <span className="secret-actions">
      {changed && <Button size="small" intent="primary" text={t("Save")} disabled={busy || !value} onClick={() => void run(() => actions.setVariable(variable.name, value))} />}
      <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete {name}", { name: variable.name })}
          disabled={busy} onClick={() => setDeleting(true)} />
        {deleting && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Delete this variable?")} subject={variable.name}
          detail={t("What refers to it stops working until it is set again.")} confirmLabel={t("Delete")}
          onConfirm={() => { setDeleting(false); void run(() => actions.deleteVariable(variable.name)); }} onCancel={() => setDeleting(false)} />}
      </span>
    </span>
  </li>;
}

/** The form that adds a secret (its value hidden) or a variable. */
function NewValueForm({ kind, taken, busy, onSave, onCancel }: {
  kind: "secret" | "var"; taken: readonly string[]; busy: boolean; onSave: (name: string, value: string) => Promise<void>; onCancel?: () => void;
}) {
  const { t } = useShellLanguage();
  const [name, setName] = useState("");
  const [value, setValue] = useState("");
  const problem = secretNameProblem(name, taken);
  const ready = isSecretName(name.trim()) && !problem && value.length > 0 && !busy;
  const save = async () => {
    if (!ready) return;
    const [typedName, typedValue] = [name.trim(), value];
    setValue("");
    await onSave(typedName, typedValue);
    setName("");
  };
  return <form className="secret-new" data-kind={kind} onSubmit={event => { event.preventDefault(); void save(); }}>
    <InputGroup small value={name} placeholder={kind === "secret" ? "GRAFANA_TOKEN" : "GRAFANA_URL"} disabled={busy} intent={problem ? "danger" : "none"}
      aria-label={t(kind === "secret" ? "Name of the secret" : "Name of the variable")} autoFocus={kind === "secret"} onChange={event => setName(event.target.value)} />
    <InputGroup small value={value} type={kind === "secret" ? "password" : "text"} autoComplete="off" disabled={busy}
      aria-label={t(kind === "secret" ? "Value of the secret" : "Value of the variable")} placeholder={t("Value")} onChange={event => setValue(event.target.value)} />
    {onCancel && <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onCancel} />}
    <Button type="submit" size="small" intent={kind === "secret" ? "primary" : "none"} text={t("Add")} disabled={!ready} />
    {problem && <span className="secret-problem" role="alert">{t(problem)}</span>}
  </form>;
}
