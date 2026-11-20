import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Button, Checkbox, InputGroup, NonIdealState, PopoverNext, Tag } from "@blueprintjs/core";
import type { SecretsList, SecretView, VariableView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import { DataTable, type DataColumn } from "../fleet/console/DataTable";
import { filterRows, type TableSort } from "../fleet/console/tableRows";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { askDurations, stateLabel } from "./credentials";
import { isSecretName, referenceOf, secretNameProblem } from "./secrets";
import { listedProjects, secretProjectIds, secretProjectNames, secretSearchText, secretSourceText, type SecretProject } from "./secretsTabs";

/** What a change answers: how it went, and why it was refused. */
export type SecretsResult = Promise<{ status: string; message?: string | null }>;

/** What the tables do: each call writes one change and says how it went. */
export type SecretsActions = Readonly<{
  setSecret: (name: string, value: string) => SecretsResult;
  rotateSecret: (name: string, value: string) => SecretsResult;
  deleteSecret: (name: string) => SecretsResult;
  setVariable: (name: string, value: string) => SecretsResult;
  deleteVariable: (name: string) => SecretsResult;
}>;

/** What the rows of the secrets do with the vaults; absent on the page of a machine, which has none. */
export type SecretVaultActions = Readonly<{
  keepInVault: (name: string, provider?: string) => void;
  /** The logo of the first vault, on the button that gives a secret of this computer an item of a vault. */
  icon?: string | null;
  setAskBeforeUse: (name: string, duration: string | null) => SecretsResult;
  verification: { available: boolean; reason: string | null } | null;
}>;

/** Limits a secret to projects of the catalog; absent where the projects of a secret are only shown (the page of a machine). */
export type SecretProjectActions = Readonly<{
  setProjects: (name: string, projectIds: string[]) => SecretsResult;
}>;

type Run = (call: () => SecretsResult) => Promise<boolean>;

/** The reference the configuration writes, such as ${secret:NAME}: a click copies it, and a check says it was copied. `compact` shows the button alone, in a row. */
export function CopyReference({ text, compact = false, copy = value => navigator.clipboard.writeText(value) }: { text: string; compact?: boolean; copy?: (value: string) => Promise<void> }) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const timer = window.setTimeout(() => setCopied(false), 1400);
    return () => window.clearTimeout(timer);
  }, [copied]);
  const label = t("Copy {reference}", { reference: text });
  return <button type="button" className={compact ? "secret-reference-copy secret-reference-copy-compact settings-own-button" : "secret-reference-copy"} title={label} aria-label={label}
    data-copied={copied || undefined} onClick={() => void copy(text).then(() => setCopied(true), () => { /* No clipboard: nothing is copied. */ })}>
    {!compact && <span className="secret-reference">{text}</span>}
    <AppIcon name={copied ? "check" : "copy"} size={12} />
  </button>;
}

/**
 * The secrets of a listing as a table: a line per secret (its name, where it is kept, its state when it cannot be given, its
 * projects, whether it asks before use, when it was set, and Rotate, Use a vault item and Delete), a filter, and Add a
 * secret in a popover. A row opens its details: its reference, who set it, Ask before use, its projects and what names it.
 */
export function SecretsTable({ listing, busy, run, actions, vaults = null, restriction = null, adding, onAdding, toolbar, initialSelected = null }: {
  listing: SecretsList; busy: boolean; run: Run; actions: SecretsActions; vaults?: SecretVaultActions | null; restriction?: SecretProjectActions | null;
  /** The form that adds a secret is open, with the name it starts with; null when it is closed. */
  adding: Readonly<{ name?: string }> | null; onAdding: (adding: Readonly<{ name?: string }> | null) => void;
  /** What the toolbar offers before Add a secret: Add from a vault. */
  toolbar?: ReactNode;
  /** The secret whose details are open at first. */
  initialSelected?: string | null;
}) {
  const { t, locale } = useShellLanguage();
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<TableSort | null>({ column: "name", direction: "asc" });
  const [selected, setSelected] = useState<string | null>(initialSelected);
  const projects = listedProjects(listing);
  const day = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }), [locale]);
  const shown = useMemo(() => filterRows(listing.secrets, [], {}, query, secret => secretSearchText(secret, projects, t)), [listing.secrets, projects, query, t]);
  const limited = listing.secrets.some(secret => secretProjectIds(secret).length > 0);
  const columns: DataColumn<SecretView>[] = [
    { id: "name", label: "Name", sort: secret => secret.name, cell: secret => <span className="secret-cell-name">
      <code title={secret.name}>{secret.name}</code><CopyReference compact text={referenceOf("secret", secret.name)} /></span> },
    { id: "source", label: "Kept in", sort: secret => secretSourceText(secret, t), cell: secret => <span className="secret-source" title={secretSourceText(secret, t)}>
      <CredentialIcon icon={secret.provider ? secret.icon : "computer"} size={14} /><span>{secretSourceText(secret, t)}</span></span> },
    { id: "state", label: "State", width: "6.5rem", sort: secret => secret.state === "ready" ? null : t(stateLabel(secret.state).label),
      cell: secret => secret.state === "ready" ? null : <Tag minimal intent={stateLabel(secret.state).intent} title={secret.stateMessage ?? undefined}>{t(stateLabel(secret.state).label)}</Tag> },
    ...(restriction || limited ? [{ id: "projects", label: "Projects", secondary: true, sort: secret => secretProjectNames(secret, projects).join(", "), cell: secret => {
      const names = secretProjectNames(secret, projects).join(", ");
      return names ? <span title={names}>{names}</span> : <span className="muted-text">{t("All projects")}</span>;
    } } satisfies DataColumn<SecretView>] : []),
    ...(vaults ? [{ id: "ask", label: "Ask before use", hiddenLabel: true, width: "2.75rem", secondary: true, cell: secret => secret.askBeforeUse
      ? <Button size="small" variant="minimal" intent="primary" icon={<AppIcon name="shield" size={14} />} aria-label={t("Ask before using {name}", { name: secret.name })}
        title={t("Asks before use")} onClick={() => setSelected(secret.name)} /> : null } satisfies DataColumn<SecretView>] : []),
    { id: "updated", label: "Updated", width: "7.5rem", secondary: true, sort: secret => Date.parse(secret.setAt),
      cell: secret => <span title={t("Set {date} by {who}", { date: new Date(secret.setAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" }), who: secret.setBy })}>
        {day.format(new Date(secret.setAt))}</span> },
    { id: "actions", label: "Actions", hiddenLabel: true, align: "end", width: "6.75rem", cell: secret => <SecretRowActions secret={secret} busy={busy} run={run} actions={actions} vaults={vaults} /> },
  ];
  const names = listing.secrets.map(secret => secret.name);
  return <section className="fleet-table-section secrets-table" aria-label={t("Secrets")}>
    <div className="secrets-toolbar">
      {listing.secrets.length > 0 && <InputGroup type="search" size="small" className="secrets-filter" value={query} placeholder={t("Filter the secrets")} aria-label={t("Filter the secrets")}
        leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setQuery(event.target.value)} />}
      <span className="secrets-toolbar-actions">
        {toolbar}
        <PopoverNext isOpen={adding !== null} placement="bottom-end" onInteraction={next => { if (!next && !busy) onAdding(null); }}
          content={<div className="secret-new-popover">
            <strong>{t("Add a secret")}</strong>
            <NewValueForm key={adding?.name ?? ""} kind="secret" initialName={adding?.name} taken={names} busy={busy} onCancel={() => onAdding(null)}
              onSave={async (name, value) => { if (await run(() => actions.setSecret(name, value))) onAdding(null); }} />
          </div>}>
          <Button size="small" intent="primary" icon={<AppIcon name="plus" size={14} />} disabled={busy} onClick={() => onAdding({})}>{t("Add a secret…")}</Button>
        </PopoverNext>
      </span>
    </div>
    {listing.secrets.length === 0
      ? <NonIdealState className="secrets-empty" icon={<AppIcon name="secret" size={28} />} title={t("No secret on this machine.")} />
      : <DataTable label={t("Secrets")} rows={shown} columns={columns} rowKey={secret => secret.name} sort={sort} onSort={setSort} selected={selected} onSelect={setSelected}
        rowProps={secret => ({ data: { secret: secret.name, source: secret.provider ?? "this-computer" } })}
        empty={<span className="muted-text">{t("No secret matches.")}</span>}
        details={secret => <SecretDetails secret={secret} projects={projects} busy={busy} run={run} vaults={vaults} restriction={restriction} />} />}
  </section>;
}

/** The actions of a secret at the end of its row: Rotate (a value of this computer), Use a vault item, Delete. */
function SecretRowActions({ secret, busy, run, actions, vaults }: { secret: SecretView; busy: boolean; run: Run; actions: SecretsActions; vaults: SecretVaultActions | null }) {
  const { t } = useShellLanguage();
  const [deleting, setDeleting] = useState(false);
  const [rotating, setRotating] = useState(false);
  const [value, setValue] = useState("");
  const bound = !!secret.provider;
  const rotate = async () => {
    const typed = value;
    setValue("");
    if (await run(() => actions.rotateSecret(secret.name, typed))) setRotating(false);
  };
  return <span className="secret-actions">
    {vaults && <Button size="small" variant="minimal" icon={<CredentialIcon icon={secret.icon ?? vaults.icon ?? "secret"} size={14} />}
      aria-label={t("Use a vault item for {name}", { name: secret.name })} title={t("Use a vault item…")} disabled={busy}
      onClick={() => vaults.keepInVault(secret.name, secret.provider ?? undefined)} />}
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
      <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />} aria-label={t("Rotate {name}", { name: secret.name })} title={t("Rotate")} disabled={busy}
        onClick={() => setRotating(true)} />
    </PopoverNext>}
    <span className="secret-delete">
      <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete {name}", { name: secret.name })} title={t("Delete")}
        disabled={busy} onClick={() => setDeleting(true)} />
      {deleting && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Delete this secret?")} subject={secret.name}
        detail={bound ? t("Nothing changes in the vault. What refers to it stops working until it is set again.") : t("What refers to it stops working until it is set again.")}
        confirmLabel={t("Delete")}
        onConfirm={() => { setDeleting(false); void run(() => actions.deleteSecret(secret.name)); }} onCancel={() => setDeleting(false)} />}
    </span>
  </span>;
}

/** What else is known of a secret, under its row: its reference, who set it and when, Ask before use, its projects, and what names it. */
function SecretDetails({ secret, projects, busy, run, vaults, restriction }: {
  secret: SecretView; projects: readonly SecretProject[]; busy: boolean; run: Run; vaults: SecretVaultActions | null; restriction: SecretProjectActions | null;
}) {
  const { t, locale } = useShellLanguage();
  const names = secretProjectNames(secret, projects);
  const usedBy = secret.usedBy ?? null;
  const kinds = { provider: ["provider", t("Provider")], space: ["space", t("Space")], project: ["folder", t("Project")] } as const;
  return <div className="secret-details" data-secret-details={secret.name}>
    <dl className="secret-details-facts">
      <div><dt>{t("Reference")}</dt><dd><CopyReference text={referenceOf("secret", secret.name)} /></dd></div>
      <div><dt>{t("Kept in")}</dt><dd>{secretSourceText(secret, t)}</dd></div>
      <div><dt>{t("Updated")}</dt><dd>{t("Set {date} by {who}", { date: new Date(secret.setAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" }), who: secret.setBy })}</dd></div>
      {secret.state !== "ready" && secret.stateMessage && <div><dt>{t("State")}</dt><dd>{secret.stateMessage}</dd></div>}
    </dl>
    {vaults && <div className="secret-details-part" role="group" aria-label={t("Ask before use")}>
      <h4>{t("Ask before use")}</h4>
      <AskBeforeUse secret={secret} busy={busy} run={run} vaults={vaults} />
    </div>}
    {(restriction || names.length > 0) && <div className="secret-details-part" role="group" aria-label={t("Projects")}>
      <h4>{t("Projects")}</h4>
      {restriction ? <ProjectRestriction secret={secret} projects={projects} busy={busy} run={run} restriction={restriction} /> : <span>{names.join(", ")}</span>}
    </div>}
    {usedBy && <div className="secret-details-part" role="group" aria-label={t("Used by")}>
      <h4>{t("Used by")}</h4>
      {usedBy.length === 0 ? <span className="muted-text">{t("Nothing names it.")}</span> : <ul className="secret-used-by">
        {usedBy.map(use => {
          const [icon, label] = kinds[use.kind as keyof typeof kinds] ?? kinds.project;
          return <li key={`${use.kind}\n${use.name}`} data-used-by={use.kind}><Tag minimal icon={<AppIcon name={icon} size={12} />} title={label}>{use.name}</Tag></li>;
        })}
      </ul>}
    </div>}
  </div>;
}

/** Ask before use: off, or a confirmation by Windows Hello or Touch ID that lasts a while; off with its reason where the system cannot verify. */
export function AskBeforeUse({ secret, busy, run, vaults }: { secret: SecretView; busy: boolean; run: Run; vaults: SecretVaultActions }) {
  const { t } = useShellLanguage();
  const available = vaults.verification?.available === true;
  const choose = (duration: string | null) => void run(() => vaults.setAskBeforeUse(secret.name, duration));
  return <div className="secret-ask">
    <p>{available ? t("Windows Hello or Touch ID verifies you before CodeAlta gives {name} to what asks for it.", { name: secret.name })
      : vaults.verification?.reason ?? t("This system cannot verify you.")}</p>
    <div className="secret-ask-choices" role="radiogroup" aria-label={t("Ask before use")}>
      <Button size="small" variant={secret.askBeforeUse ? "minimal" : "outlined"} active={!secret.askBeforeUse} text={t("Off")} disabled={busy} onClick={() => choose(null)} />
      {askDurations.map(duration => <Button key={duration.value} size="small" variant={secret.askBeforeUse === duration.value ? "outlined" : "minimal"}
        active={secret.askBeforeUse === duration.value} text={t(duration.label)} disabled={busy || !available} onClick={() => choose(duration.value)} />)}
    </div>
    {available && <p className="secret-ask-note">{t("A confirmation is kept for that secret and what uses it, for the time you choose.")}</p>}
  </div>;
}

/**
 * The projects a secret is given to: every project, or only those checked. A check is saved at once; removing the
 * restriction takes an explicit choice of All projects.
 */
export function ProjectRestriction({ secret, projects, busy, run, restriction }: {
  secret: SecretView; projects: readonly SecretProject[]; busy: boolean; run: Run; restriction: SecretProjectActions;
}) {
  const { t } = useShellLanguage();
  const ids = secretProjectIds(secret);
  // "Only these projects" with none checked yet changes nothing: the secret stays for every project until one is checked.
  const [picking, setPicking] = useState(false);
  const [filter, setFilter] = useState("");
  const limited = ids.length > 0 || picking;
  const wanted = filter.trim().toLowerCase();
  const gone = ids.filter(id => !projects.some(project => project.id === id)).map(id => ({ id, name: id }));
  const listed = [...projects, ...gone].filter(project => !wanted || ids.includes(project.id) || project.name.toLowerCase().includes(wanted));
  const save = (next: string[]) => { if (next.length === 0) setPicking(false); void run(() => restriction.setProjects(secret.name, next)); };
  return <div className="secret-projects" data-limited={limited || undefined}>
    <div className="secret-ask-choices" role="group" aria-label={t("Projects")}>
      <Button size="small" variant={limited ? "minimal" : "outlined"} active={!limited} text={t("All projects")} disabled={busy}
        onClick={() => { setPicking(false); if (ids.length > 0) save([]); }} />
      <Button size="small" variant={limited ? "outlined" : "minimal"} active={limited} text={t("Only these projects")} disabled={busy || projects.length + gone.length === 0}
        onClick={() => setPicking(true)} />
    </div>
    {limited && <>
      {projects.length > 8 && <InputGroup type="search" size="small" className="secret-projects-filter" value={filter} placeholder={t("Search")} aria-label={t("Search")}
        leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
      <div className="secret-project-checks" role="group" aria-label={t("Projects of {name}", { name: secret.name })}>
        {listed.map(project => <Checkbox key={project.id} checked={ids.includes(project.id)} disabled={busy || ids.length === 1 && ids.includes(project.id)} label={project.name} data-project={project.id}
          onChange={event => save(event.currentTarget.checked ? [...ids, project.id] : ids.filter(id => id !== project.id))} />)}
      </div>
    </>}
  </div>;
}

/**
 * The variables of a listing as a table: a line per variable, its value edited in place (Save appears once it changed,
 * Enter saves), Delete, and Add a variable in a popover.
 */
export function VariablesTable({ listing, busy, run, actions, adding, onAdding }: {
  listing: SecretsList; busy: boolean; run: Run; actions: SecretsActions;
  /** Whether the form that adds a variable is open. */
  adding: boolean; onAdding: (adding: boolean) => void;
}) {
  const { t, locale } = useShellLanguage();
  const [sort, setSort] = useState<TableSort | null>({ column: "name", direction: "asc" });
  // What is typed in a row and not saved yet, by the name of its variable.
  const [drafts, setDrafts] = useState<Readonly<Record<string, string>>>({});
  const day = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "medium" }), [locale]);
  const draft = (variable: VariableView) => drafts[variable.name] ?? variable.value;
  const changed = (variable: VariableView) => draft(variable) !== variable.value;
  const forget = (name: string) => setDrafts(current => Object.fromEntries(Object.entries(current).filter(([key]) => key !== name)));
  const save = async (variable: VariableView) => {
    const value = draft(variable);
    if (!value || !changed(variable)) return;
    if (await run(() => actions.setVariable(variable.name, value))) forget(variable.name);
  };
  const columns: DataColumn<VariableView>[] = [
    { id: "name", label: "Name", sort: variable => variable.name, cell: variable => <span className="secret-cell-name">
      <code title={variable.name}>{variable.name}</code><CopyReference compact text={referenceOf("var", variable.name)} /></span> },
    { id: "value", label: "Value", sort: variable => variable.value, cell: variable => <InputGroup className="secret-value" small fill value={draft(variable)} disabled={busy}
      aria-label={t("Value of {name}", { name: variable.name })} onChange={event => { const value = event.target.value; setDrafts(current => ({ ...current, [variable.name]: value })); }}
      onKeyDown={event => { if (event.key === "Enter") void save(variable); else if (event.key === "Escape" && changed(variable)) { event.stopPropagation(); forget(variable.name); } }} /> },
    { id: "updated", label: "Updated", width: "8.5rem", secondary: true, sort: variable => Date.parse(variable.setAt),
      cell: variable => <span title={t("Set {date} by {who}", { date: new Date(variable.setAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" }), who: variable.setBy })}>
        {day.format(new Date(variable.setAt))}</span> },
    { id: "actions", label: "Actions", hiddenLabel: true, align: "end", width: "7.5rem", cell: variable => <span className="secret-actions">
      {changed(variable) && <Button size="small" intent="primary" text={t("Save")} disabled={busy || !draft(variable)} onClick={() => void save(variable)} />}
      <VariableDelete variable={variable} busy={busy} run={run} actions={actions} />
    </span> },
  ];
  return <section className="fleet-table-section secrets-table" aria-label={t("Variables")}>
    <div className="secrets-toolbar">
      <span className="secrets-toolbar-actions">
        <PopoverNext isOpen={adding} placement="bottom-end" onInteraction={next => { if (!next && !busy) onAdding(false); }}
          content={<div className="secret-new-popover">
            <strong>{t("Add a variable")}</strong>
            <NewValueForm kind="var" taken={listing.variables.map(variable => variable.name)} busy={busy} onCancel={() => onAdding(false)}
              onSave={async (name, value) => { if (await run(() => actions.setVariable(name, value))) onAdding(false); }} />
          </div>}>
          <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={busy} onClick={() => onAdding(true)}>{t("Add a variable…")}</Button>
        </PopoverNext>
      </span>
    </div>
    <DataTable label={t("Variables")} rows={listing.variables} columns={columns} rowKey={variable => variable.name} sort={sort} onSort={setSort} selected={null} onSelect={() => { }}
      rowProps={variable => ({ className: "secret-variable-row", data: { variable: variable.name } })}
      empty={<span className="muted-text">{t("No variable on this machine.")}</span>} />
  </section>;
}

/** Delete of a variable, asked beside its row. */
function VariableDelete({ variable, busy, run, actions }: { variable: VariableView; busy: boolean; run: Run; actions: SecretsActions }) {
  const { t } = useShellLanguage();
  const [deleting, setDeleting] = useState(false);
  return <span className="secret-delete">
    <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete {name}", { name: variable.name })} title={t("Delete")}
      disabled={busy} onClick={() => setDeleting(true)} />
    {deleting && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Delete this variable?")} subject={variable.name}
      detail={t("What refers to it stops working until it is set again.")} confirmLabel={t("Delete")}
      onConfirm={() => { setDeleting(false); void run(() => actions.deleteVariable(variable.name)); }} onCancel={() => setDeleting(false)} />}
  </span>;
}

/** The form that adds a secret (its value hidden) or a variable; a secret may start with the name something asked for. */
export function NewValueForm({ kind, taken, busy, onSave, onCancel, initialName = "" }: {
  kind: "secret" | "var"; taken: readonly string[]; busy: boolean; onSave: (name: string, value: string) => Promise<void>; onCancel?: () => void; initialName?: string;
}) {
  const { t } = useShellLanguage();
  const [name, setName] = useState(initialName);
  const [value, setValue] = useState("");
  const problem = secretNameProblem(name, taken);
  const ready = isSecretName(name.trim()) && !problem && value.length > 0 && !busy;
  const save = async () => {
    if (!ready) return;
    const [typedName, typedValue] = [name.trim(), value];
    setValue("");
    await onSave(typedName, typedValue);
  };
  return <form className="secret-new" data-kind={kind} onSubmit={event => { event.preventDefault(); void save(); }}>
    <InputGroup small value={name} placeholder={kind === "secret" ? "GRAFANA_TOKEN" : "GRAFANA_URL"} disabled={busy} intent={problem ? "danger" : "none"}
      aria-label={t(kind === "secret" ? "Name of the secret" : "Name of the variable")} autoFocus={!initialName} onChange={event => setName(event.target.value)} />
    <InputGroup small value={value} type={kind === "secret" ? "password" : "text"} autoComplete="off" disabled={busy} autoFocus={!!initialName}
      aria-label={t(kind === "secret" ? "Value of the secret" : "Value of the variable")} placeholder={t("Value")} onChange={event => setValue(event.target.value)} />
    {problem && <span className="secret-problem" role="alert">{t(problem)}</span>}
    <div className="rename-actions">
      {onCancel && <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onCancel} />}
      <Button type="submit" size="small" intent="primary" text={t("Add")} disabled={!ready} />
    </div>
  </form>;
}
