import { useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter, FormGroup, HTMLSelect, InputGroup, Switch, TextArea } from "@blueprintjs/core";
import type { CredentialProviderRpcView, CredentialSettingInput, CredentialTypeView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { cacheDurations, draftsOf, isProviderId, isSecretReference, missingSetting, settingsOf, suggestedProviderId, type SettingDrafts } from "./credentials";

/** What the dialog saves. */
export type ProviderDraft = Readonly<{ id: string; type: string; displayName: string | null; cache: string; unlockAtStart: boolean; settings: CredentialSettingInput[] }>;

/**
 * Add a provider: the types with their logo and what each reads, then the form of the one chosen. Edit opens the form of a
 * provider directly. A setting that is a token names a secret of this computer, never holds it.
 */
export function ProviderDialog({ types, providers, editing, busy, onSave, onClose }: {
  types: readonly CredentialTypeView[]; providers: readonly CredentialProviderRpcView[]; editing: CredentialProviderRpcView | null; busy: boolean;
  onSave: (draft: ProviderDraft) => Promise<boolean>; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [chosen, setChosen] = useState<CredentialTypeView | null>(() => editing ? types.find(type => type.type === editing.type) ?? null : null);
  const title = editing ? t("Edit {name}", { name: editing.displayName }) : chosen ? t("Add {name}", { name: chosen.displayName }) : t("Add a credential provider");
  return <Dialog isOpen className="credential-provider-dialog" title={title} canOutsideClickClose={!busy} onClose={onClose}>
    {chosen
      ? <ProviderForm type={chosen} providers={providers} editing={editing} busy={busy} onSave={onSave} onBack={editing ? null : () => setChosen(null)} onClose={onClose} />
      : <DialogBody>
        <p className="credential-gallery-intro">{t("Choose where the values are kept. CodeAlta reads them through the program of the vault, and never writes into it.")}</p>
        <ul className="credential-gallery" aria-label={t("Types of credential providers")}>
          {types.map(type => <li key={type.type}>
            <button type="button" className="credential-gallery-item" data-type={type.type} onClick={() => setChosen(type)}>
              <span className="credential-gallery-icon"><CredentialIcon icon={type.icon} size={22} /></span>
              <span className="credential-gallery-text"><strong>{type.displayName}</strong>{type.description && <span>{type.description}</span>}</span>
            </button>
          </li>)}
        </ul>
      </DialogBody>}
  </Dialog>;
}

function ProviderForm({ type, providers, editing, busy, onSave, onBack, onClose }: {
  type: CredentialTypeView; providers: readonly CredentialProviderRpcView[]; editing: CredentialProviderRpcView | null; busy: boolean;
  onSave: (draft: ProviderDraft) => Promise<boolean>; onBack: (() => void) | null; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [id, setId] = useState(() => editing?.id ?? suggestedProviderId(type.type, providers.map(provider => provider.id)));
  const [name, setName] = useState(editing && editing.displayName !== (editing.typeName ?? editing.type) ? editing.displayName : "");
  const [cache, setCache] = useState(editing?.cache ?? "session");
  const [unlockAtStart, setUnlockAtStart] = useState(editing?.unlockAtStart ?? true);
  const [drafts, setDrafts] = useState<SettingDrafts>(() => draftsOf(type, editing?.settings ?? null));
  const taken = !editing && providers.some(provider => provider.id === id);
  const idProblem = !isProviderId(id) ? t("Lowercase letters, digits, - and _, starting with a letter.") : taken ? t("This id is already used.") : null;
  const missing = missingSetting(type, drafts);
  const secretProblem = type.settings.find(setting => setting.kind === "secret" && typeof drafts[setting.key] === "string"
    && (drafts[setting.key] as string).trim().length > 0 && !isSecretReference(drafts[setting.key] as string));
  const ready = !idProblem && !missing && !secretProblem && !busy;
  const save = async () => {
    if (!ready) return;
    if (await onSave({ id, type: type.type, displayName: name.trim() || null, cache, unlockAtStart, settings: settingsOf(type, drafts) })) onClose();
  };
  return <>
    <DialogBody className="credential-provider-form">
      <div className="credential-form-heading">
        <CredentialIcon icon={type.icon} size={28} />
        <div><strong>{type.displayName}</strong>{type.description && <p>{type.description}</p>}
          {type.website && <a href={type.website} target="_blank" rel="noreferrer">{t("Its documentation")}</a>}</div>
      </div>
      <FormGroup label={t("Id")} labelFor="credential-provider-id" helperText={idProblem ?? t("The secrets kept in this vault name it.")} intent={idProblem ? "danger" : "none"}>
        <InputGroup id="credential-provider-id" value={id} disabled={!!editing || busy} intent={idProblem ? "danger" : "none"} onChange={event => setId(event.currentTarget.value.trim())} />
      </FormGroup>
      <FormGroup label={t("Name")} labelFor="credential-provider-name" helperText={t("Optional: how the list shows it.")}>
        <InputGroup id="credential-provider-name" value={name} placeholder={type.displayName} disabled={busy} onChange={event => setName(event.currentTarget.value)} />
      </FormGroup>
      {type.settings.map(setting => <FormGroup key={setting.key} label={setting.label} labelFor={`credential-setting-${setting.key}`}
        labelInfo={setting.required ? t("(required)") : undefined}
        helperText={secretProblem?.key === setting.key ? t("A reference to a secret of this computer, such as ${secret:NAME}.") : setting.description ?? undefined}
        intent={secretProblem?.key === setting.key ? "danger" : "none"}>
        {setting.kind === "boolean"
          ? <Switch id={`credential-setting-${setting.key}`} checked={drafts[setting.key] === true} disabled={busy}
            onChange={event => setDrafts({ ...drafts, [setting.key]: event.currentTarget.checked })} />
          : setting.kind === "list"
            ? <TextArea id={`credential-setting-${setting.key}`} fill rows={3} value={drafts[setting.key] as string} placeholder={setting.placeholder?.split(" ").join("\n") ?? undefined}
              disabled={busy} className="credential-setting-list" onChange={event => setDrafts({ ...drafts, [setting.key]: event.currentTarget.value })} />
            : <InputGroup id={`credential-setting-${setting.key}`} value={drafts[setting.key] as string} placeholder={setting.placeholder ?? undefined} disabled={busy}
              leftElement={setting.kind === "secret" ? <span className="credential-setting-icon"><AppIcon name="secret" size={14} /></span> : undefined}
              onChange={event => setDrafts({ ...drafts, [setting.key]: event.currentTarget.value })} />}
      </FormGroup>)}
      {type.settings.some(setting => setting.kind === "list") && <p className="credential-form-note">{t("One argument a line. The program is found on the PATH, and run without a shell.")}</p>}
      <FormGroup label={t("Its values stay in memory")} labelFor="credential-provider-cache">
        <HTMLSelect id="credential-provider-cache" value={cache} disabled={busy} onChange={event => setCache(event.currentTarget.value)}>
          {cacheDurations.map(duration => <option key={duration.value} value={duration.value}>{t(duration.label)}</option>)}
        </HTMLSelect>
      </FormGroup>
      {!type.unlockAsksPassword && <Switch checked={unlockAtStart} disabled={busy} label={t("Unlock when CodeAlta starts")}
        onChange={event => setUnlockAtStart(event.currentTarget.checked)} />}
    </DialogBody>
    <DialogFooter minimal={false} actions={<>
      {onBack && <Button variant="minimal" icon={<AppIcon name="arrowLeft" size={14} />} text={t("Other types")} disabled={busy} onClick={onBack} />}
      <Button text={t("Cancel")} disabled={busy} onClick={onClose} />
      <Button intent="primary" text={editing ? t("Save") : t("Add")} loading={busy} disabled={!ready} onClick={() => void save()} />
    </>} />
  </>;
}
