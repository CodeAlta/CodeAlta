import { useState } from "react";
import { Button, Card, InputGroup, NonIdealState, PopoverNext, Tag } from "@blueprintjs/core";
import { secrets, type SecretsList, type SecretView, type VariableView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { isSecretName, referenceOf, secretNameProblem } from "./secrets";

type SecretsApi = Pick<typeof secrets, "list" | "setSecret" | "rotateSecret" | "deleteSecret" | "setVariable" | "deleteVariable">;
type Result = Promise<{ status: string; message?: string | null }>;

/** What the body of the page does: each call writes one change and says how it went. */
export type SecretsActions = Readonly<{
  setSecret: (name: string, value: string) => Result;
  rotateSecret: (name: string, value: string) => Result;
  deleteSecret: (name: string) => Result;
  setVariable: (name: string, value: string) => Result;
  deleteVariable: (name: string) => Result;
}>;

/**
 * Settings > Secrets: the secrets of this machine, listed by name (Add, Rotate, Delete; a value is sent and never shown
 * back), and its variables, which are read and edited.
 */
export function SecretsSettings({ epoch, api = secrets }: { epoch: string | null; api?: SecretsApi }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(options => api.list({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const [adding, setAdding] = useState(false);
  const expectedEpoch = epoch ?? "";
  const actions: SecretsActions = {
    setSecret: (name, value) => api.setSecret({ expectedEpoch, name, value }),
    rotateSecret: (name, value) => api.rotateSecret({ expectedEpoch, name, value }),
    deleteSecret: name => api.deleteSecret({ expectedEpoch, name }),
    setVariable: (name, value) => api.setVariable({ expectedEpoch, name, value }),
    deleteVariable: name => api.deleteVariable({ expectedEpoch, name }),
  };
  const run = (call: () => Result) => mutate(call, "Saved.");
  return <SettingsPage className="secrets-settings" label={t("Secrets")} group="Agent & models" title="Secrets"
    description="Named values of this machine. The configuration refers to them as ${secret:NAME} and ${var:NAME}."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={<Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!epoch || !listing || adding} onClick={() => setAdding(true)}>{t("Add a secret…")}</Button>}>
    {!listing ? <SettingsUnavailable loading={loading} icon="secret" title="Secrets unavailable" />
      : <SecretsBody listing={listing} busy={busy} adding={adding} onAdded={() => setAdding(false)} run={run} actions={actions} />}
  </SettingsPage>;
}

/** The secrets and the variables of a listing, with their forms. */
export function SecretsBody({ listing, busy, adding = false, onAdded = () => { }, run, actions }: {
  listing: SecretsList; busy: boolean; adding?: boolean; onAdded?: () => void;
  run: (call: () => Result) => Promise<boolean>; actions: SecretsActions;
}) {
  const { t, locale } = useShellLanguage();
  const date = (value: string) => new Date(value).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
  const names = listing.secrets.map(secret => secret.name);
  return <div className="settings-grid secrets-body">
    <Card className="appearance-settings" aria-labelledby="secrets-title">
      <h2 id="secrets-title">{t("Secrets")}</h2>
      {adding && <NewValueForm kind="secret" taken={names} busy={busy} onCancel={onAdded}
        onSave={async (name, value) => { if (await run(() => actions.setSecret(name, value))) onAdded(); }} />}
      {listing.secrets.length === 0 && !adding
        ? <NonIdealState className="secrets-empty" icon={<AppIcon name="secret" size={28} />} title={t("No secret on this machine.")} />
        : <ul className="secret-list" aria-label={t("Secrets")}>
          {listing.secrets.map(secret => <SecretRow key={secret.name} secret={secret} busy={busy} date={date} run={run} actions={actions} />)}
        </ul>}
    </Card>
    <Card className="appearance-settings" aria-labelledby="secret-variables-title">
      <h2 id="secret-variables-title">{t("Variables")}</h2>
      {listing.variables.length > 0 && <ul className="secret-list" aria-label={t("Variables")}>
        {listing.variables.map(variable => <VariableRow key={variable.name} variable={variable} busy={busy} run={run} actions={actions} />)}
      </ul>}
      <NewValueForm kind="var" taken={listing.variables.map(variable => variable.name)} busy={busy}
        onSave={(name, value) => run(() => actions.setVariable(name, value)).then(() => { })} />
    </Card>
  </div>;
}

/** One secret: its name and reference, its version, when and by whom it was set; Rotate and Delete. */
function SecretRow({ secret, busy, date, run, actions }: {
  secret: SecretView; busy: boolean; date: (value: string) => string; run: (call: () => Result) => Promise<boolean>; actions: SecretsActions;
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
  return <li className="secret-row" data-secret={secret.name}>
    <span className="secret-name"><code>{secret.name}</code><span className="secret-reference">{referenceOf("secret", secret.name)}</span></span>
    <Tag minimal>{t("Version {version}", { version: String(secret.version) })}</Tag>
    <span className="secret-meta">{t("Set {date} by {who}", { date: date(secret.setAt), who: secret.setBy })}</span>
    <span className="secret-actions">
      <PopoverNext isOpen={rotating} placement="bottom-end" onInteraction={next => { if (!next && !busy) { setRotating(false); setValue(""); } }}
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
      </PopoverNext>
      <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete {name}", { name: secret.name })}
          disabled={busy} onClick={() => setDeleting(true)} />
        {deleting && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Delete this secret?")} subject={secret.name}
          detail={t("What refers to it stops working until it is set again.")} confirmLabel={t("Delete")}
          onConfirm={() => { setDeleting(false); void run(() => actions.deleteSecret(secret.name)); }} onCancel={() => setDeleting(false)} />}
      </span>
    </span>
  </li>;
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
    <span className="secret-name"><code>{variable.name}</code><span className="secret-reference">{referenceOf("var", variable.name)}</span></span>
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
