import { useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter, FormGroup, HTMLSelect, InputGroup, NonIdealState, Tag, TextArea } from "@blueprintjs/core";
import type { CredentialProviderRpcView, CredentialTypeView, SignInView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import { DataTable, type DataColumn } from "../fleet/console/DataTable";
import type { TableSort } from "../fleet/console/tableRows";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { signInDraftProblem, siteList, type SignInDraft } from "./credentials";
import { VaultBrowser, type BrowseVault } from "./VaultPicker";

type Result = Promise<{ status: string; message?: string | null }>;

/** What the table of the sign-ins does: each call is one change, and says how it went. */
export type SignInActions = Readonly<{
  save: (draft: SignInDraft) => Result;
  remove: (id: string) => Result;
  revoke: (id: string, origin: string) => Result;
  browse: BrowseVault;
}>;

/**
 * The sign-ins of websites as a table: the vault items the browser of a session types on their sites, once the user allowed
 * it, with the sites the user allowed for 30 days. A value is never shown, nor sent to an agent.
 */
export function SignInsTable({ signIns, providers, types, busy, run, actions, adding, onAdding }: {
  signIns: readonly SignInView[]; providers: readonly CredentialProviderRpcView[]; types: readonly CredentialTypeView[];
  busy: boolean; run: (call: () => Result) => Promise<boolean>; actions: SignInActions;
  /** Whether the dialog that adds a sign-in is open. */
  adding: boolean; onAdding: (adding: boolean) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [editing, setEditing] = useState<SignInView | null>(null);
  const [sort, setSort] = useState<TableSort | null>({ column: "name", direction: "asc" });
  const date = (value: string) => new Date(value).toLocaleDateString(locale, { dateStyle: "medium" });
  const columns: DataColumn<SignInView>[] = [
    { id: "name", label: "Sign-in", sort: signIn => signIn.label, cell: signIn => <span className="credential-provider-cell">
      <span className="credential-provider-icon"><CredentialIcon icon={signIn.icon} size={16} /></span>
      <span className="credential-provider-name"><strong title={signIn.id}>{signIn.label}</strong></span></span> },
    { id: "vault", label: "Vault", secondary: true, sort: signIn => signIn.providerName ?? signIn.provider, cell: signIn => signIn.providerName ?? signIn.provider },
    { id: "sites", label: "Sites", sort: signIn => signIn.sites.join(", "), cell: signIn => <span className="sign-in-sites">
      <span title={signIn.sites.join(", ")}>{signIn.sites.join(", ")}</span>
      {signIn.grants.length > 0 && <span className="sign-in-grants">
        {signIn.grants.map(grant => <Tag key={grant.origin} minimal intent="success" icon={<AppIcon name="check" size={12} />}
          onRemove={busy ? undefined : () => void run(() => actions.revoke(signIn.id, grant.origin))}
          title={t("Typed there without asking you until {date}. Remove it to be asked again.", { date: date(grant.until) })}>
          {t("Allowed on {site} until {date}", { site: grant.origin.replace(/^https:\/\//, ""), date: date(grant.until) })}
        </Tag>)}
      </span>}
    </span> },
    { id: "totp", label: "One-time code", width: "9rem", secondary: true, sort: signIn => signIn.totp ? 0 : 1, cell: signIn => signIn.totp ? <Tag minimal>{t("One-time code")}</Tag> : null },
    { id: "actions", label: "Actions", hiddenLabel: true, align: "end", width: "5.5rem", cell: signIn => <SignInRowActions signIn={signIn} busy={busy} run={run} actions={actions} onEdit={() => setEditing(signIn)} /> },
  ];
  return <section className="fleet-table-section secrets-table sign-ins" aria-label={t("Sign-ins of websites")}>
    <div className="secrets-toolbar"><span className="secrets-toolbar-actions">
      <Button size="small" intent="primary" icon={<AppIcon name="plus" size={14} />} text={t("Add a sign-in…")} disabled={busy || providers.length === 0} onClick={() => onAdding(true)} />
    </span></div>
    {signIns.length > 0
      ? <DataTable label={t("Sign-ins of websites")} rows={signIns} columns={columns} rowKey={signIn => signIn.id} sort={sort} onSort={setSort} selected={null} onSelect={() => { }}
        rowProps={signIn => ({ className: "sign-in-row", data: { "sign-in": signIn.id } })} empty={null} />
      : <NonIdealState className="credential-providers-empty" icon={<AppIcon name="globe" size={24} />}
        description={providers.length === 0 ? t("Add a vault first: a sign-in is an item of a vault.") : t("Add the user name and password of a site, kept in a vault.")} />}
    {(editing || adding) && providers.length > 0 && <SignInDialog editing={editing} providers={providers} types={types} busy={busy} browse={actions.browse}
      onClose={() => { setEditing(null); onAdding(false); }} onSave={draft => run(() => actions.save(draft))} />}
  </section>;
}

/** The actions of a sign-in at the end of its row: Edit and Remove. */
function SignInRowActions({ signIn, busy, run, actions, onEdit }: {
  signIn: SignInView; busy: boolean; run: (call: () => Result) => Promise<boolean>; actions: SignInActions; onEdit: () => void;
}) {
  const { t } = useShellLanguage();
  const [removing, setRemoving] = useState(false);
  return <span className="credential-provider-actions">
    <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={14} />} aria-label={t("Edit {name}", { name: signIn.label })} disabled={busy} onClick={onEdit} />
    <span className="secret-delete">
      <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove {name}", { name: signIn.label })}
        disabled={busy} onClick={() => setRemoving(true)} />
      {removing && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Remove this sign-in?")} subject={signIn.label}
        detail={t("Nothing changes in the vault.")} confirmLabel={t("Remove")}
        onConfirm={() => { setRemoving(false); void run(() => actions.remove(signIn.id)); }} onCancel={() => setRemoving(false)} />}
    </span>
  </span>;
}

type Slot = "username" | "password" | "totp";

/** Adds or changes a sign-in: its name, its vault, its sites, and the items of the vault picked for each field. */
function SignInDialog({ editing, providers, types, busy, browse, onSave, onClose }: {
  editing: SignInView | null; providers: readonly CredentialProviderRpcView[]; types: readonly CredentialTypeView[]; busy: boolean;
  browse: BrowseVault; onSave: (draft: SignInDraft) => Promise<boolean>; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [label, setLabel] = useState(editing?.label ?? "");
  const [provider, setProvider] = useState(editing?.provider ?? providers[0]?.id ?? "");
  const [sites, setSites] = useState(editing?.sites.join("\n") ?? "");
  const [refs, setRefs] = useState<Record<Slot, string>>({ username: editing?.username ?? "", password: editing?.password ?? "", totp: editing?.totp ?? "" });
  const [labels, setLabels] = useState<Record<Slot, string | null>>({ username: null, password: null, totp: null });
  const [choosing, setChoosing] = useState<Slot | null>(null);
  const chosen = providers.find(item => item.id === provider);
  const hint = types.find(item => item.type === chosen?.type)?.referenceHint ?? null;
  const draft: SignInDraft = { id: editing?.id ?? null, label, provider, sites: siteList(sites), username: refs.username, password: refs.password, totp: refs.totp };
  const problem = signInDraftProblem(draft);
  const save = async () => { if (!problem && !busy && await onSave(draft)) onClose(); };
  const slot = (key: Slot, title: string, optional: boolean) => <FormGroup key={key} label={title} labelFor={`sign-in-${key}`} labelInfo={optional ? t("(optional)") : t("(required)")}
    helperText={labels[key] ?? undefined}>
    <div className="sign-in-slot">
      <InputGroup id={`sign-in-${key}`} value={refs[key]} placeholder={hint ?? undefined} disabled={busy}
        onChange={event => { const value = event.currentTarget.value; setRefs(current => ({ ...current, [key]: value })); setLabels(current => ({ ...current, [key]: null })); }} />
      <Button icon={<AppIcon name="folder" size={14} />} text={t("Choose…")} disabled={busy || !chosen} active={choosing === key}
        onClick={() => setChoosing(choosing === key ? null : key)} />
    </div>
    {choosing === key && chosen && <VaultBrowser key={`${chosen.id}-${key}`} provider={chosen} hint={hint} browse={browse} picked={null} typedInput={false} autoPick={false}
      onPick={pick => { if (!pick) return; setRefs(current => ({ ...current, [key]: pick.reference })); setLabels(current => ({ ...current, [key]: pick.label })); setChoosing(null); }}
      typed="" onType={() => { }} />}
  </FormGroup>;
  return <Dialog isOpen className="vault-binding-dialog sign-in-dialog" title={editing ? t("Edit {name}", { name: editing.label }) : t("Add a sign-in")}
    canOutsideClickClose={!busy} onClose={onClose}>
    <DialogBody>
      <FormGroup label={t("Name")} labelFor="sign-in-label">
        <InputGroup id="sign-in-label" value={label} placeholder="GitHub" autoFocus disabled={busy} onChange={event => setLabel(event.currentTarget.value)} />
      </FormGroup>
      <FormGroup label={t("Sites")} labelFor="sign-in-sites" helperText={t("One a line, such as https://github.com. It is typed there only: not on another address, nor a subdomain.")}>
        <TextArea id="sign-in-sites" fill rows={2} value={sites} placeholder="https://github.com" disabled={busy} onChange={event => setSites(event.currentTarget.value)} />
      </FormGroup>
      <FormGroup label={t("Vault")} labelFor="sign-in-provider">
        <HTMLSelect id="sign-in-provider" value={provider} disabled={busy} onChange={event => {
          setProvider(event.currentTarget.value); setRefs({ username: "", password: "", totp: "" }); setLabels({ username: null, password: null, totp: null }); setChoosing(null);
        }}>
          {providers.map(item => <option key={item.id} value={item.id}>{item.displayName}{item.typeName && item.typeName !== item.displayName ? ` (${item.typeName})` : ""}</option>)}
        </HTMLSelect>
      </FormGroup>
      {slot("username", t("User name"), false)}
      {slot("password", t("Password"), false)}
      {slot("totp", t("One-time code"), true)}
    </DialogBody>
    <DialogFooter actions={<>
      <Button text={t("Cancel")} disabled={busy} onClick={onClose} />
      <Button intent="primary" text={t("Save")} loading={busy} disabled={!!problem || busy} title={problem ? t(problem) : undefined} onClick={() => void save()} />
    </>} />
  </Dialog>;
}
