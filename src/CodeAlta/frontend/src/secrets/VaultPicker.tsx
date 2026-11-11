import { useEffect, useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter, FormGroup, HTMLSelect, InputGroup, NonIdealState, Spinner } from "@blueprintjs/core";
import type { CredentialBrowseResult, CredentialEntryView, CredentialProviderRpcView, CredentialTypeView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { isSecretName, secretNameProblem } from "./secrets";
import { pickedLabel } from "./credentials";

/** What the dialog binds. */
export type VaultBinding = Readonly<{ name: string; provider: string; reference: string; label: string; replaceLocal: boolean }>;

/**
 * Keep a secret in a vault: its name, the vault, and the item and field picked in the vault, or a reference typed for a vault
 * that lists nothing. The picker shows names only: a value never reaches the page.
 */
export function VaultBindingDialog({ providers, types, names, localNames, initialName, nameEditable = false, initialProvider, busy, browse, onBind, onClose }: {
  providers: readonly CredentialProviderRpcView[]; types: readonly CredentialTypeView[]; names: readonly string[]; localNames: readonly string[];
  initialName?: string; nameEditable?: boolean; initialProvider?: string; busy: boolean;
  browse: (provider: string, parent: string | null) => Promise<CredentialBrowseResult>;
  onBind: (binding: VaultBinding) => Promise<boolean>; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [name, setName] = useState(initialName ?? "");
  const [provider, setProvider] = useState(initialProvider ?? providers[0]?.id ?? "");
  const [picked, setPicked] = useState<{ reference: string; label: string } | null>(null);
  const [typed, setTyped] = useState("");
  const [replace, setReplace] = useState(false);
  const fixedName = initialName !== undefined && !nameEditable;
  const nameProblem = fixedName ? null : secretNameProblem(name, names.filter(other => !localNames.includes(other)));
  const local = localNames.some(other => other.toLowerCase() === name.trim().toLowerCase());
  const chosen = providers.find(item => item.id === provider);
  const type = types.find(item => item.type === chosen?.type);
  const reference = picked?.reference ?? typed.trim();
  const ready = isSecretName(name.trim()) && !nameProblem && !!chosen && reference.length > 0 && (!local || replace) && !busy;
  const bind = async () => {
    if (!ready) return;
    if (await onBind({ name: name.trim(), provider, reference, label: picked?.label ?? reference, replaceLocal: replace })) onClose();
  };
  return <Dialog isOpen className="vault-binding-dialog" title={t("Keep a secret in a vault")} canOutsideClickClose={!busy} onClose={onClose}>
    <DialogBody>
      <FormGroup label={t("Name of the secret")} labelFor="vault-binding-name" intent={nameProblem ? "danger" : "none"}
        helperText={nameProblem ? t(nameProblem) : t("The configuration refers to it as ${secret:NAME}, wherever its value is.")}>
        <InputGroup id="vault-binding-name" value={name} placeholder="ANTHROPIC_API_KEY" autoFocus={!fixedName} disabled={fixedName || busy}
          intent={nameProblem ? "danger" : "none"} onChange={event => setName(event.currentTarget.value)} />
      </FormGroup>
      <FormGroup label={t("Vault")} labelFor="vault-binding-provider">
        <HTMLSelect id="vault-binding-provider" value={provider} disabled={busy} onChange={event => { setProvider(event.currentTarget.value); setPicked(null); setTyped(""); }}>
          {providers.map(item => <option key={item.id} value={item.id}>{item.displayName}{item.typeName && item.typeName !== item.displayName ? ` (${item.typeName})` : ""}</option>)}
        </HTMLSelect>
      </FormGroup>
      {chosen && <VaultBrowser key={chosen.id} provider={chosen} hint={type?.referenceHint ?? null} browse={browse} picked={picked} onPick={setPicked} typed={typed} onType={setTyped} />}
      {local && <label className="vault-binding-replace">
        <input type="checkbox" checked={replace} disabled={busy} onChange={event => setReplace(event.currentTarget.checked)} />
        <span>{t("{name} has a value on this computer: replace it by the item of the vault. The old value stays masked.", { name: name.trim() })}</span>
      </label>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button text={t("Cancel")} disabled={busy} onClick={onClose} />
      <Button intent="primary" text={t("Keep in the vault")} loading={busy} disabled={!ready} onClick={() => void bind()} />
    </>} />
  </Dialog>;
}

/** The entries of a vault, opened one level at a time; a field is picked by a click. */
function VaultBrowser({ provider, hint, browse, picked, onPick, typed, onType }: {
  provider: CredentialProviderRpcView; hint: string | null; browse: (provider: string, parent: string | null) => Promise<CredentialBrowseResult>;
  picked: { reference: string; label: string } | null; onPick: (pick: { reference: string; label: string } | null) => void; typed: string; onType: (text: string) => void;
}) {
  const { t } = useShellLanguage();
  const [trail, setTrail] = useState<{ id: string; label: string }[]>([]);
  const [page, setPage] = useState<CredentialBrowseResult | null>(null);
  const [loading, setLoading] = useState(false);
  const parent = trail.at(-1)?.id ?? null;
  useEffect(() => {
    let alive = true;
    setLoading(true);
    browse(provider.id, parent).then(result => { if (alive) setPage(result); })
      .catch(() => { if (alive) setPage({ status: "failed", message: null, path: null, entries: [] }); })
      .finally(() => { if (alive) setLoading(false); });
    return () => { alive = false; };
  }, [provider.id, parent, browse]);
  const typedOnly = page?.status === "not_supported";
  const open = (entry: CredentialEntryView) => {
    if (entry.kind === "folder") { setTrail([...trail, { id: entry.id, label: entry.label }]); return; }
    if (entry.reference) onPick({ reference: entry.reference, label: pickedLabel(trail.map(step => step.label), entry.label) });
  };
  return <div className="vault-browser">
    {!typedOnly && <>
      <div className="vault-browser-trail">
        <Button size="small" variant="minimal" icon={<CredentialIcon icon={provider.icon} size={14} />} text={provider.displayName} onClick={() => { setTrail([]); onPick(null); }} />
        {trail.map((step, index) => <span key={step.id}>›<Button size="small" variant="minimal" text={step.label} onClick={() => { setTrail(trail.slice(0, index + 1)); onPick(null); }} /></span>)}
      </div>
      <div className="vault-browser-list" role="listbox" aria-label={t("Items of the vault")}>
        {loading && <div className="vault-browser-loading"><Spinner size={18} /></div>}
        {!loading && page?.status === "ok" && page.entries.length === 0 && <NonIdealState icon={<AppIcon name="folder" size={20} />} description={t("Nothing here.")} />}
        {!loading && page && page.status !== "ok" && <p className="vault-browser-problem" role="alert">{page.message ?? t("The vault cannot be listed now: unlock it, then try again.")}</p>}
        {!loading && page?.status === "ok" && page.entries.map(entry => {
          const selected = entry.kind === "field" && picked?.reference === entry.reference;
          return <button key={entry.id} type="button" role="option" aria-selected={selected} className={`vault-browser-entry${selected ? " selected" : ""}`} data-kind={entry.kind}
            onClick={() => open(entry)}>
            <AppIcon name={entry.kind === "folder" ? "folder" : "secret"} size={14} />
            <span className="vault-browser-label">{entry.label}</span>
            {entry.detail && <span className="vault-browser-detail">{entry.detail}</span>}
            {entry.kind === "folder" && <AppIcon name="chevronRight" size={14} />}
          </button>;
        })}
      </div>
      {picked && <p className="vault-browser-picked"><AppIcon name="check" size={14} />{picked.label}</p>}
    </>}
    <FormGroup label={typedOnly ? t("Reference in the vault") : t("Or type the reference")} labelFor="vault-binding-reference" helperText={hint ? t("Such as {example}", { example: hint }) : undefined}>
      <InputGroup id="vault-binding-reference" value={typed} placeholder={hint ?? undefined} onChange={event => { onType(event.currentTarget.value); if (event.currentTarget.value) onPick(null); }} />
    </FormGroup>
  </div>;
}
