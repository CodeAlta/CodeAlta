import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Button, Card, Tag } from "@blueprintjs/core";
import { secrets, type CredentialProviderRpcView, type SecretsList } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { DataTable, type DataColumn } from "../fleet/console/DataTable";
import type { TableSort } from "../fleet/console/tableRows";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { CredentialProviders } from "./CredentialProviders";
import { EnvironmentTab } from "./EnvironmentTab";
import { ProviderDialog } from "./ProviderDialog";
import { SecretsOverview } from "./SecretsOverview";
import { SecretsTable, VariablesTable, type SecretsActions, type SecretsResult, type SecretVaultActions } from "./SecretsTable";
import { keepLastSecretsTab, offeredSecretsTabs, readLastSecretsTab, secretProjectIds, secretsTabSetting, secretsTabStatus, secretsTabTitles, type EnvironmentList, type SecretsTab } from "./secretsTabs";
import { SignInsTable } from "./SignIns";
import { VaultBindingDialog } from "./VaultPicker";

export { CopyReference, type SecretsActions, type SecretVaultActions } from "./SecretsTable";

type SecretsApi = Pick<typeof secrets, "list" | "setSecret" | "rotateSecret" | "deleteSecret" | "setVariable" | "deleteVariable" | "saveProvider" | "removeProvider"
  | "unlockProvider" | "lockProvider" | "testProvider" | "browse" | "bindSecret" | "setAskBeforeUse" | "confirmUse" | "declineUse" | "setDeveloperUnlock" | "saveSignIn" | "removeSignIn" | "revokeSiteGrant"
  | "setSecretProjects" | "listEnvironment" | "setProjectEnvironment" | "deleteProjectEnvironment">;

/** The tab the page is asked to open on; each request is a new ticket, so that the same tab can be asked for twice. */
export type SecretsTabFocus = Readonly<{ tab: SecretsTab; ticket: number }>;

/**
 * Settings > Secrets and environment: the uses that wait for a confirmation, then tabs. Overview (a card per concept),
 * Secrets (named values of this computer, kept here or read from a vault, never shown back, each for every project or
 * limited to some), Variables (named values that are read and edited), Environment (what the sessions of each project get
 * as environment variables), Vaults, Sign-ins of websites, and Activity (the last uses of the secrets).
 */
export function SecretsSettings({ epoch, api = secrets, runInTerminal, focus = null, onOpenFile, onOpenSpace }: {
  epoch: string | null; api?: SecretsApi; runInTerminal?: (command: string) => void;
  /** The tab a link or `alta settings open` asks for; without one, the page opens on the tab it was left on. */
  focus?: SecretsTabFocus | null;
  /** Opens the configuration file of a project in the editor. */
  onOpenFile?: (projectId: string, path: string) => void;
  /** Opens the page of a space on its Identity. */
  onOpenSpace?: (spaceId: string) => void;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, refresh, mutate } = useSettingsEditor(options => api.list({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const [tab, setTab] = useState<SecretsTab>(() => focus?.tab ?? readLastSecretsTab());
  const [environment, setEnvironment] = useState<EnvironmentList | null>(null);
  const [environmentGeneration, setEnvironmentGeneration] = useState(0);
  // What each tab is adding: opened from its own toolbar, or from the card of the overview.
  const [addingSecret, setAddingSecret] = useState<Readonly<{ name?: string }> | null>(null);
  const [addingVariable, setAddingVariable] = useState<Readonly<{ name?: string }> | null>(null);
  const [addingSignIn, setAddingSignIn] = useState(false);
  const [pickingProject, setPickingProject] = useState(false);
  const [providerDialog, setProviderDialog] = useState<{ editing: CredentialProviderRpcView | null } | null>(null);
  const [binding, setBinding] = useState<{ name?: string; provider?: string } | null>(null);
  const tabs = useRef<HTMLDivElement>(null);
  const expectedEpoch = epoch ?? "";
  useEffect(() => { if (focus) setTab(focus.tab); }, [focus?.tab, focus?.ticket]);
  useEffect(() => keepLastSecretsTab(tab), [tab]);
  // A vault unlocked elsewhere, a use that waits: the page reads again by itself while it is shown.
  useEffect(() => {
    if (!epoch) return;
    const timer = window.setInterval(() => { if (document.visibilityState === "visible") refresh(); }, 4000);
    return () => window.clearInterval(timer);
  }, [epoch, refresh]);
  // The environment of the projects is read again when a secret or a variable it may name changed, not at each look at the vaults.
  const named = useMemo(() => listing ? [...listing.secrets.map(secret => `${secret.name}:${secret.state}:${secretProjectIds(secret).join(",")}`),
    ...listing.variables.map(variable => variable.name)].join("|") : "", [listing]);
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    api.listEnvironment({ expectedEpoch: epoch }, { signal: abort.signal })
      .then(value => { if (!abort.signal.aborted) setEnvironment(value.status === "ok" ? value : null); }, () => { /* What was read stays shown. */ });
    return () => abort.abort();
  }, [api, epoch, environmentGeneration, named]);
  const run = useCallback(async (call: () => SecretsResult) => {
    const done = await mutate(call, "Saved.");
    setEnvironmentGeneration(value => value + 1);
    return done;
  }, [mutate]);
  const actions: SecretsActions = {
    setSecret: (name, value) => api.setSecret({ expectedEpoch, name, value }),
    rotateSecret: (name, value) => api.rotateSecret({ expectedEpoch, name, value }),
    deleteSecret: name => api.deleteSecret({ expectedEpoch, name }),
    setVariable: (name, value) => api.setVariable({ expectedEpoch, name, value }),
    deleteVariable: name => api.deleteVariable({ expectedEpoch, name }),
  };
  const browse = useCallback((provider: string, parent: string | null, signal?: AbortSignal) => api.browse({ expectedEpoch, id: provider, parent }, { signal }),
    [api, expectedEpoch]);
  // A host with a credential broker offers the vaults; without a provider yet, using a vault item adds one first.
  const vaults: SecretVaultActions | null = listing && listing.types.length > 0 ? {
    keepInVault: (name, provider) => { if (listing.providers.length > 0) setBinding({ name, provider }); else setProviderDialog({ editing: null }); },
    icon: listing.providers[0]?.icon ?? null,
    setAskBeforeUse: (name, duration) => api.setAskBeforeUse({ expectedEpoch, name, duration }),
    verification: listing.verification,
  } : null;
  const offered = listing ? offeredSecretsTabs(listing, environment) : [];
  const current = offered.includes(tab) ? tab : "overview";
  // Left and Right move between the tabs, as in a list of tabs.
  function moveTab(event: KeyboardEvent<HTMLDivElement>) {
    const at = offered.indexOf(current);
    const next = event.key === "ArrowRight" ? at + 1 : event.key === "ArrowLeft" ? at - 1 : event.key === "Home" ? 0 : event.key === "End" ? offered.length - 1 : null;
    if (next === null) return;
    event.preventDefault();
    const target = offered[(next + offered.length) % offered.length];
    setTab(target);
    tabs.current?.querySelector<HTMLElement>(`[data-secrets-tab="${target}"]`)?.focus();
  }
  const addSecret = (name?: string) => { setTab("secrets"); setAddingSecret(name ? { name } : {}); };
  const addVariable = (name?: string) => { setTab("variables"); setAddingVariable(name ? { name } : {}); };
  return <SettingsPage className="secrets-settings" label={t("Secrets and environment")} group="Agent & models" title="Secrets and environment"
    notice={notice} loading={loading} busy={busy} onReload={() => { reload(); setEnvironmentGeneration(value => value + 1); }}>
    {!listing ? <SettingsUnavailable loading={loading} icon="secret" title="Secrets unavailable" />
      : <>
        {listing.pending.length > 0 && <PendingUses listing={listing} busy={busy}
          onConfirm={(name, use) => run(() => api.confirmUse({ expectedEpoch, name, use, names: null }))} onDecline={(name, use) => run(() => api.declineUse({ expectedEpoch, name, use, names: null }))} />}
        <div className="secrets-tabs-frame">
          <div className="space-section-nav secrets-tabs" role="tablist" aria-label={t("Secrets and environment")} ref={tabs} onKeyDown={moveTab}>
            {offered.map(item => {
              const status = secretsTabStatus(item, listing, environment, t);
              return <Button key={item} variant="minimal" className="space-section-tab" role="tab" id={`secrets-tab-${item}`} data-secrets-tab={item}
                aria-selected={item === current} aria-controls="secrets-panel" tabIndex={item === current ? 0 : -1} active={item === current}
                title={status ? `${t(secretsTabTitles[item])}: ${status}` : undefined} onClick={() => setTab(item)}>
                <span className="space-section-tab-title">{t(secretsTabTitles[item])}</span>
                {status && <span className="space-section-tab-status">{status}</span>}
              </Button>;
            })}
          </div>
        </div>
        <div className="secrets-panel" role="tabpanel" id="secrets-panel" aria-labelledby={`secrets-tab-${current}`} data-secrets-panel={current}
          data-setting={secretsTabSetting(current) ?? undefined}>
          {current === "overview" && <SecretsOverview listing={listing} environment={environment} offered={offered} busy={!epoch || busy} onOpen={setTab}
            onAddSecret={() => addSecret()} onAddVariable={() => addVariable()}
            onSetEnvironment={() => { setTab("environment"); setPickingProject(true); }}
            onAddVault={() => { setTab("vaults"); setProviderDialog({ editing: null }); }} onAddSignIn={() => { setTab("signIns"); setAddingSignIn(true); }} />}
          {current === "secrets" && <SecretsTable listing={listing} busy={!epoch || busy} run={run} actions={actions} vaults={vaults} adding={addingSecret} onAdding={setAddingSecret}
            restriction={{ setProjects: (name, projectIds) => api.setSecretProjects({ expectedEpoch, name, projectIds }) }}
            toolbar={vaults && <Button size="small" icon={<CredentialIcon icon={listing.providers[0]?.icon ?? "secret"} size={14} />} disabled={!epoch || busy}
              onClick={() => listing.providers.length > 0 ? setBinding({}) : setProviderDialog({ editing: null })}>{t("Add from a vault…")}</Button>} />}
          {current === "variables" && <VariablesTable listing={listing} busy={!epoch || busy} run={run} actions={actions} adding={addingVariable} onAdding={setAddingVariable} />}
          {current === "environment" && environment && <EnvironmentTab environment={environment} listing={listing} busy={!epoch || busy} run={run}
            actions={{ setVariable: (projectId, name, value) => api.setProjectEnvironment({ expectedEpoch, projectId, name, value }),
              deleteVariable: (projectId, name) => api.deleteProjectEnvironment({ expectedEpoch, projectId, name }) }}
            picking={pickingProject} onPicking={setPickingProject} onSetValue={(kind, name) => kind === "secret" ? addSecret(name) : addVariable(name)} onOpenFile={onOpenFile} onOpenSpace={onOpenSpace} />}
          {current === "vaults" && <CredentialProviders listing={listing} busy={busy} run={run} onAdd={() => setProviderDialog({ editing: null })} actions={{
            unlock: (id, password) => api.unlockProvider({ expectedEpoch, id, password }),
            lock: id => api.lockProvider({ expectedEpoch, id }),
            test: id => api.testProvider({ expectedEpoch, id }),
            remove: id => api.removeProvider({ expectedEpoch, id }),
            edit: provider => setProviderDialog({ editing: provider }),
            runInTerminal,
            setDeveloperUnlock: enabled => api.setDeveloperUnlock({ expectedEpoch, enabled }),
          }} />}
          {current === "signIns" && <SignInsTable signIns={listing.signIns} providers={listing.providers} types={listing.types} busy={busy} run={run}
            adding={addingSignIn} onAdding={setAddingSignIn} actions={{
              save: draft => api.saveSignIn({ expectedEpoch, id: draft.id, label: draft.label, provider: draft.provider, sites: [...draft.sites],
                username: draft.username, password: draft.password, totp: draft.totp.trim() || null }),
              remove: id => api.removeSignIn({ expectedEpoch, id }),
              revoke: (id, origin) => api.revokeSiteGrant({ expectedEpoch, id, origin }),
              browse,
            }} />}
          {current === "activity" && <RecentUses listing={listing} />}
        </div>
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
        <span className="secret-name"><code title={pending.name}>{pending.name}</code>
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

type SecretUse = SecretsList["uses"][number];

/** The Activity tab: the last uses of the secrets as a table, which, for what, when and how it went. Never a value. */
export function RecentUses({ listing }: { listing: SecretsList }) {
  const { t, locale } = useShellLanguage();
  const [sort, setSort] = useState<TableSort | null>({ column: "when", direction: "desc" });
  const outcome = (use: SecretUse) => use.outcome === "given" ? t("given") : use.outcome === "waits" ? t("waits for you") : use.outcome === "confirmed" ? t("confirmed")
    : use.outcome === "restricted" ? t("limited to other projects")
    : use.outcome === "refused" ? t("declined") : use.outcome === "expired" ? t("not answered in time") : t("not available");
  const rows = useMemo(() => listing.uses.map((use, index) => ({ ...use, key: `${use.at}-${index}` })), [listing.uses]);
  const columns: DataColumn<SecretUse & { key: string }>[] = [
    { id: "secret", label: "Secret", sort: use => use.name, cell: use => <code title={use.name}>{use.name}</code> },
    { id: "use", label: "For", sort: use => use.use, cell: use => use.use ? <span title={use.use}>{use.use}</span> : <span className="muted-text">—</span> },
    { id: "when", label: "When", width: "11rem", sort: use => Date.parse(use.at), cell: use => new Date(use.at).toLocaleString(locale, { timeStyle: "short", dateStyle: "short" }) },
    { id: "outcome", label: "Result", width: "11rem", sort: outcome,
      cell: use => <Tag minimal intent={use.outcome === "given" || use.outcome === "confirmed" ? "success"
        : use.outcome === "waits" || use.outcome === "restricted" ? "warning" : "none"}>{outcome(use)}</Tag> },
  ];
  return <section className="fleet-table-section secrets-table secret-uses" aria-label={t("Recent uses")}>
    <DataTable label={t("Recent uses")} rows={rows} columns={columns} rowKey={use => use.key} sort={sort} onSort={setSort} selected={null} onSelect={() => { }}
      rowProps={use => ({ data: { outcome: use.outcome } })} empty={<span className="muted-text">{t("No secret was used yet.")}</span>} />
  </section>;
}

/**
 * The secrets and the variables of a listing as two tables, one under the other, in a section of another page (the page of
 * a machine): the same tables as Settings > Secrets and environment, without what only this computer has.
 */
export function SecretsBody({ listing, busy, run, actions, vaults = null, initialSelected = null }: {
  listing: SecretsList; busy: boolean; run: (call: () => SecretsResult) => Promise<boolean>; actions: SecretsActions; vaults?: SecretVaultActions | null;
  /** The secret whose details are open at first. */
  initialSelected?: string | null;
}) {
  const { t } = useShellLanguage();
  const [addingSecret, setAddingSecret] = useState<Readonly<{ name?: string }> | null>(null);
  const [addingVariable, setAddingVariable] = useState<Readonly<{ name?: string }> | null>(null);
  return <div className="secrets-body">
    <SecretsTable listing={listing} busy={busy} run={run} actions={actions} vaults={vaults} adding={addingSecret} onAdding={setAddingSecret} initialSelected={initialSelected} />
    <div className="secrets-part" role="group" aria-label={t("Variables")}>
      <h3>{t("Variables")}</h3>
      <VariablesTable listing={listing} busy={busy} run={run} actions={actions} adding={addingVariable} onAdding={setAddingVariable} />
    </div>
  </div>;
}
