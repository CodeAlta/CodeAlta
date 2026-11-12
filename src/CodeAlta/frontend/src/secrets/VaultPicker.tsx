import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter, FormGroup, HTMLSelect, InputGroup, NonIdealState, Spinner } from "@blueprintjs/core";
import type { CredentialBrowseResult, CredentialEntryView, CredentialProviderRpcView, CredentialTypeView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { isSecretName, secretNameProblem } from "./secrets";
import { matchesQuery, pickedLabel, suggestedSecretName } from "./credentials";

/**
 * Lists the entries of a vault: the top with a null parent. The signal aborts the call when the picker closes or leaves the level, so
 * that a call still queued for the vault never runs and a vault program still running (a sync of Bitwarden) is stopped.
 */
export type BrowseVault = (provider: string, parent: string | null, signal?: AbortSignal) => Promise<CredentialBrowseResult>;

/** What the dialog binds. */
export type VaultBinding = Readonly<{ name: string; provider: string; reference: string; label: string; replaceLocal: boolean }>;

/**
 * Use a vault item as a secret: the item and field found in the vault (or a reference typed for a vault that lists nothing), then
 * the name the configuration gives the secret, suggested from the item. The value stays in the vault, and the picker shows names
 * only: a value never reaches the page.
 */
export function VaultBindingDialog({ providers, types, names, localNames, initialName, nameEditable = false, initialProvider, busy, browse, onBind, onClose }: {
  providers: readonly CredentialProviderRpcView[]; types: readonly CredentialTypeView[]; names: readonly string[]; localNames: readonly string[];
  initialName?: string; nameEditable?: boolean; initialProvider?: string; busy: boolean;
  browse: BrowseVault;
  onBind: (binding: VaultBinding) => Promise<boolean>; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [name, setName] = useState(initialName ?? "");
  const [nameTyped, setNameTyped] = useState(!!initialName);
  const [provider, setProvider] = useState(initialProvider ?? providers[0]?.id ?? "");
  const [picked, setPicked] = useState<{ reference: string; label: string } | null>(null);
  const [typed, setTyped] = useState("");
  const [replace, setReplace] = useState(false);
  const fixedName = initialName !== undefined && !nameEditable;
  const nameProblem = fixedName || !name.trim() ? null : secretNameProblem(name, names.filter(other => !localNames.includes(other)));
  const local = localNames.some(other => other.toLowerCase() === name.trim().toLowerCase());
  const chosen = providers.find(item => item.id === provider);
  const type = types.find(item => item.type === chosen?.type);
  const reference = picked?.reference ?? typed.trim();
  const ready = isSecretName(name.trim()) && !nameProblem && !!chosen && reference.length > 0 && (!local || replace) && !busy;
  // A name not typed yet follows the item picked: GITHUB_PASSWORD for the password of GitHub.
  const pick = (choice: { reference: string; label: string } | null) => {
    setPicked(choice);
    if (choice && !fixedName && !nameTyped) setName(suggestedSecretName(choice.label));
  };
  const bind = async () => {
    if (!ready) return;
    if (await onBind({ name: name.trim(), provider, reference, label: picked?.label ?? reference, replaceLocal: replace })) onClose();
  };
  return <Dialog isOpen className="vault-binding-dialog" title={t("Use a vault item as a secret")} canOutsideClickClose={!busy} onClose={onClose}>
    <DialogBody>
      <p className="vault-binding-intro">
        {fixedName ? t("The secret {name} takes its value from the item you choose.", { name }) + " " : ""}
        {t("The value stays in the vault: CodeAlta reads it there each time the secret is used, and never writes into the vault.")}
      </p>
      {providers.length > 1 && <FormGroup label={t("Vault")} labelFor="vault-binding-provider">
        <HTMLSelect id="vault-binding-provider" value={provider} disabled={busy} onChange={event => { setProvider(event.currentTarget.value); pick(null); setTyped(""); }}>
          {providers.map(item => <option key={item.id} value={item.id}>{item.displayName}{item.typeName && item.typeName !== item.displayName ? ` (${item.typeName})` : ""}</option>)}
        </HTMLSelect>
      </FormGroup>}
      {chosen && <VaultBrowser key={chosen.id} provider={chosen} hint={type?.referenceHint ?? null} browse={browse} picked={picked} onPick={pick} typed={typed} onType={setTyped}
        autoFocus={fixedName || !nameTyped} />}
      {!fixedName && <FormGroup label={t("Name of the secret")} labelFor="vault-binding-name" intent={nameProblem ? "danger" : "none"}
        helperText={nameProblem ? t(nameProblem) : t("The configuration refers to it as ${secret:NAME}, wherever its value is.")}>
        <InputGroup id="vault-binding-name" value={name} placeholder="ANTHROPIC_API_KEY" disabled={busy}
          intent={nameProblem ? "danger" : "none"} onChange={event => { setName(event.currentTarget.value); setNameTyped(true); }} />
      </FormGroup>}
      {local && <label className="vault-binding-replace">
        <input type="checkbox" checked={replace} disabled={busy} onChange={event => setReplace(event.currentTarget.checked)} />
        <span>{t("{name} has a value on this computer: replace it by the item of the vault. The old value stays masked.", { name: name.trim() })}</span>
      </label>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button text={t("Cancel")} disabled={busy} onClick={onClose} />
      <Button intent="primary" text={t("Use this item")} loading={busy} disabled={!ready} onClick={() => void bind()} />
    </>} />
  </Dialog>;
}

type Step = { id: string; label: string };
// An entry as the list shows it: found in the folder it is in when a search looks into the folders of the top level.
type Shown = Readonly<{ entry: CredentialEntryView; folder: Step | null }>;

/**
 * The entries of a vault, opened one level at a time, with a search: inside an item or a folder it filters what is listed; at the
 * top it also looks into each folder (the vaults of 1Password, the groups of KeePassXC), so that an item is found by its name
 * without knowing where it is. Bitwarden lists its items at the top: their names are all a search needs there. A field is picked by a click; the one hidden field of an item is picked when the item opens.
 */
export function VaultBrowser({ provider, hint, browse, picked, onPick, typed, onType, typedInput = true, autoPick = true, autoFocus = false }: {
  provider: CredentialProviderRpcView; hint: string | null; browse: BrowseVault;
  picked: { reference: string; label: string } | null; onPick: (pick: { reference: string; label: string } | null) => void; typed: string; onType: (text: string) => void;
  typedInput?: boolean; autoPick?: boolean; autoFocus?: boolean;
}) {
  const { t } = useShellLanguage();
  const [trail, setTrail] = useState<Step[]>([]);
  const [page, setPage] = useState<CredentialBrowseResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [query, setQuery] = useState("");
  const [typing, setTyping] = useState(typed.length > 0);
  // What each folder of the top level holds, read once, when a search needs it.
  const [inside, setInside] = useState<ReadonlyMap<string, readonly CredentialEntryView[] | null>>(new Map());
  const parent = trail.at(-1)?.id ?? null;
  useEffect(() => {
    let alive = true;
    const abort = new AbortController();
    setLoading(true);
    browse(provider.id, parent, abort.signal).then(result => { if (alive) setPage(result); })
      .catch(() => { if (alive) setPage({ status: "failed", message: null, path: null, entries: [] }); })
      .finally(() => { if (alive) setLoading(false); });
    // Closed, or another level opened: what this level asked is no longer wanted.
    return () => { alive = false; abort.abort(); };
  }, [provider.id, parent, browse]);
  const searching = query.trim().length > 0;
  // A Bitwarden item is a folder of fields: its name is all a search needs, and opening each one costs a call of bw.
  const searchInside = provider.type !== "bitwarden";
  const folders = useMemo(() => searchInside && parent === null && page?.status === "ok" ? page.entries.filter(entry => entry.kind === "folder") : [],
    [searchInside, parent, page]);
  // The folders asked for once, and whether the browser is still shown: a read that ends later is kept only then, and a read
  // still queued when it closes is aborted.
  const asked = useRef(new Set<string>());
  const shownNow = useRef(true);
  const reading = useRef<AbortController | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    reading.current = abort;
    shownNow.current = true;
    return () => { shownNow.current = false; abort.abort(); };
  }, []);
  // A search at the top reads the folders it has not read yet; a folder that cannot be read is left out.
  useEffect(() => {
    if (!searching) return;
    for (const folder of folders) {
      if (asked.current.has(folder.id)) continue;
      asked.current.add(folder.id);
      setInside(current => new Map(current).set(folder.id, null));
      browse(provider.id, folder.id, reading.current?.signal).then(result => result.status === "ok" ? result.entries : [], () => [] as CredentialEntryView[])
        .then(entries => { if (shownNow.current) setInside(current => new Map(current).set(folder.id, entries)); });
    }
  }, [searching, folders, browse, provider.id]);
  const shown: Shown[] = useMemo(() => {
    if (page?.status !== "ok") return [];
    const here = page.entries.filter(entry => matchesQuery(query, entry.label)).map(entry => ({ entry, folder: null }));
    if (!searching || parent !== null) return here;
    const deeper = page.entries.filter(entry => entry.kind === "folder").flatMap(folder =>
      (inside.get(folder.id) ?? []).filter(entry => matchesQuery(query, entry.label)).map(entry => ({ entry, folder: { id: folder.id, label: folder.label } })));
    return [...here, ...deeper];
  }, [page, query, searching, parent, inside]);
  const stillReading = searching && folders.some(folder => inside.get(folder.id) === null);
  const go = (steps: Step[]) => { setTrail(steps); setQuery(""); onPick(null); };
  const open = ({ entry, folder }: Shown) => {
    const path = folder ? [...trail, folder] : trail;
    if (entry.kind === "folder") { go([...path, { id: entry.id, label: entry.label }]); return; }
    if (entry.reference) onPick({ reference: entry.reference, label: pickedLabel(path.map(step => step.label), entry.label) });
  };
  // An item opened: its one hidden field (the password, the credential) is what is wanted most of the time.
  useEffect(() => {
    if (!autoPick || loading || picked || parent === null || page?.status !== "ok" || page.entries.length === 0) return;
    if (!page.entries.every(entry => entry.kind === "field")) return;
    const hidden = page.entries.filter(entry => entry.detail === "concealed");
    if (hidden.length === 1 && hidden[0].reference) onPick({ reference: hidden[0].reference, label: pickedLabel(trail.map(step => step.label), hidden[0].label) });
  }, [autoPick, loading, picked, parent, page, trail, onPick]);
  const typedOnly = page?.status === "not_supported";
  const inItem = page?.status === "ok" && page.entries.length > 0 && page.entries.every(entry => entry.kind === "field");
  return <div className="vault-browser">
    {!typedOnly && <>
      <div className="vault-browser-trail">
        <Button size="small" variant="minimal" icon={<CredentialIcon icon={provider.icon} size={14} />} text={provider.displayName} onClick={() => go([])} />
        {trail.map((step, index) => <span key={step.id}>›<Button size="small" variant="minimal" text={step.label} onClick={() => go(trail.slice(0, index + 1))} /></span>)}
      </div>
      {!inItem && <InputGroup className="vault-browser-search" type="search" value={query} autoFocus={autoFocus} aria-label={t("Search the vault by name")}
        placeholder={parent === null ? t("Search the vault by name") : t("Search by name")}
        leftElement={<span className="vault-browser-search-icon"><AppIcon name="search" size={14} /></span>}
        onChange={event => setQuery(event.currentTarget.value)}
        onKeyDown={event => { if (event.key === "Enter" && shown.length === 1) { event.preventDefault(); open(shown[0]); } }} />}
      <div className="vault-browser-list" role="listbox" aria-label={inItem ? t("Fields of the item") : t("Items of the vault")}>
        {loading && <div className="vault-browser-loading"><Spinner size={18} /></div>}
        {!loading && page?.status === "ok" && page.entries.length === 0 && <NonIdealState icon={<AppIcon name="folder" size={20} />} description={t("Nothing here.")} />}
        {!loading && page && page.status !== "ok" && <p className="vault-browser-problem" role="alert">{page.message ?? t("The vault cannot be listed now: unlock it, then try again.")}</p>}
        {!loading && page?.status === "ok" && shown.map(item => {
          const { entry, folder } = item;
          const selected = entry.kind === "field" && picked?.reference === entry.reference;
          return <button key={`${folder?.id ?? ""}\n${entry.id}`} type="button" role="option" aria-selected={selected} className={`vault-browser-entry${selected ? " selected" : ""}`}
            data-kind={entry.kind} onClick={() => open(item)}>
            <AppIcon name={entry.kind === "folder" ? "folder" : "secret"} size={14} />
            <span className="vault-browser-label">{folder && <span className="vault-browser-where">{folder.label} › </span>}{entry.label}</span>
            {entry.detail && <span className="vault-browser-detail">{entry.detail}</span>}
            {entry.kind === "folder" && <AppIcon name="chevronRight" size={14} />}
          </button>;
        })}
        {!loading && page?.status === "ok" && searching && shown.length === 0 && !stillReading && <p className="vault-browser-none">{t("Nothing in the vault has this name.")}</p>}
        {!loading && stillReading && <div className="vault-browser-loading vault-browser-reading"><Spinner size={14} />{t("Searching the vault…")}</div>}
      </div>
      {picked && <p className="vault-browser-picked"><AppIcon name="check" size={14} />{picked.label}</p>}
    </>}
    {typedOnly && !typedInput && <p className="vault-browser-problem">{page?.message ?? t("This vault lists nothing: type the reference.")}</p>}
    {/* A reference typed by hand is for a vault that lists nothing, or an item the list does not show: offered, not in the way. */}
    {typedInput && !typedOnly && !typing && <Button className="vault-browser-type" size="small" variant="minimal" icon={<AppIcon name="edit" size={13} />}
      text={t("Type the reference instead…")} onClick={() => setTyping(true)} />}
    {typedInput && (typedOnly || typing) && <FormGroup label={typedOnly ? t("Reference in the vault") : t("Or type the reference")} labelFor="vault-binding-reference" helperText={hint ? t("Such as {example}", { example: hint }) : undefined}>
      <InputGroup id="vault-binding-reference" value={typed} autoFocus={typing} onChange={event => { onType(event.currentTarget.value); if (event.currentTarget.value) onPick(null); }} />
    </FormGroup>}
  </div>;
}
