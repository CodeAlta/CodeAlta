import { useMemo, useState } from "react";
import { Button, InputGroup, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext, Tag } from "@blueprintjs/core";
import type { SecretsList } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import { DataTable, type DataColumn } from "../fleet/console/DataTable";
import type { TableSort } from "../fleet/console/tableRows";
import { Logo } from "../ProviderIcon";
import { useShellLanguage } from "../shellLanguage";
import { spaceBrand } from "../spaces/spaces";
import { isSecretName, referenceOf } from "./secrets";
import type { SecretsResult } from "./SecretsTable";
import { environmentStateText, issueText, projectIssues, projectsWithEnvironment, type EnvironmentIssue, type EnvironmentList, type EnvironmentProject, type EnvironmentVariable } from "./secretsTabs";

/** What the tab writes in the configuration of a project: each call is one change, and says how it went. */
export type EnvironmentActions = Readonly<{
  setVariable: (projectId: string, name: string, value: string) => SecretsResult;
  deleteVariable: (projectId: string, name: string) => SecretsResult;
}>;

type Run = (call: () => SecretsResult) => Promise<boolean>;

/** The icon of a project, as its row shows it elsewhere; a folder for a project without one. */
function ProjectMark({ project, size = 15 }: { project: Readonly<{ icon?: string | null }>; size?: number }) {
  return <span className="environment-project-icon"><Logo brand={spaceBrand({ icon: project.icon ?? null, color: null })} size={size} fallback="folder" /></span>;
}

/**
 * The Environment tab: the projects that give their sessions environment variables (the `[env]` table of their
 * `.alta/config.toml`, and their CLI profiles), each with what is missing on this computer; a row opens the editor of
 * its project. Under them, the spaces whose Identity sets CLI profiles, each with the way to that section.
 */
export function EnvironmentTab({ environment, listing, busy, run, actions, picking, onPicking, onSetValue, onOpenFile, onOpenSpace, initialSelected = null }: {
  environment: EnvironmentList; listing: SecretsList; busy: boolean; run: Run; actions: EnvironmentActions;
  /** Whether the choice of a project to give an environment is open. */
  picking: boolean; onPicking: (picking: boolean) => void;
  /** Opens the form that adds a secret or a variable of this computer, with the name a value refers to. */
  onSetValue: (kind: "secret" | "var", name: string) => void;
  /** Opens the configuration file of a project in the editor; absent where no editor can be opened. */
  onOpenFile?: (projectId: string, path: string) => void;
  /** Opens the page of a space on its Identity; absent where it cannot be opened. */
  onOpenSpace?: (spaceId: string) => void;
  /** The project whose editor is open at first. */
  initialSelected?: string | null;
}) {
  const { t } = useShellLanguage();
  const [sort, setSort] = useState<TableSort | null>({ column: "project", direction: "asc" });
  const [selected, setSelected] = useState<string | null>(initialSelected);
  const [filter, setFilter] = useState("");
  const rows = useMemo(() => projectsWithEnvironment(environment), [environment]);
  const draftProject = environment.projects.find(project => project.id === selected && !rows.includes(project));
  const wanted = filter.trim().toLowerCase();
  const candidates = environment.projects.filter(project => !rows.includes(project) && (!wanted || project.name.toLowerCase().includes(wanted)));
  const offered = environment.projects.length - rows.length;
  const start = (project: EnvironmentProject) => { setSelected(project.id); setFilter(""); onPicking(false); };
  const columns: DataColumn<EnvironmentProject>[] = [
    { id: "project", label: "Project", sort: project => project.name, cell: project => <span className="environment-project-name"><ProjectMark project={project} /><strong>{project.name}</strong></span> },
    { id: "variables", label: "Variables", width: "11rem", sort: project => project.variables.length, cell: project => <span>{project.variables.length}
      {project.profiles.length > 0 && <span className="muted-text">{" · "}{project.profiles.length === 1 ? t("1 CLI profile") : t("{count} CLI profiles", { count: project.profiles.length })}</span>}</span> },
    { id: "secrets", label: "Secrets named", secondary: true, sort: project => project.secrets.join(", "),
      cell: project => project.secrets.length > 0 ? <code title={project.secrets.join(", ")}>{project.secrets.join(", ")}</code> : <span className="muted-text">—</span> },
    { id: "state", label: "On this computer", width: "13rem", sort: project => projectIssues(project).length, cell: project => projectIssues(project).length === 0
      ? <span className="environment-ready"><AppIcon name="check" size={13} />{t("All set")}</span>
      : <Tag minimal intent="warning" title={projectIssues(project).map(issue => issueText(issue, t)).join("\n")}>{environmentStateText(project, t)}</Tag> },
  ];
  const picker = <PopoverNext isOpen={picking} placement="bottom-end" onInteraction={next => { if (!next) onPicking(false); }}
    content={<div className="environment-picker">
      <strong>{t("Set the environment of a project")}</strong>
      {offered > 8 && <InputGroup type="search" size="small" autoFocus value={filter} placeholder={t("Search")} aria-label={t("Search")}
        leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
      <Menu className="environment-picker-list" aria-label={t("Projects")}>
        {candidates.map(project => <MenuItem key={project.id} data-project={project.id} icon={<ProjectMark project={project} size={14} />} text={project.name} onClick={() => start(project)} />)}
        {candidates.length === 0 && <MenuItem disabled text={t(offered === 0 ? "Every project has an environment." : "No project matches.")} />}
      </Menu>
    </div>}>
    <Button size="small" intent="primary" icon={<AppIcon name="plus" size={14} />} disabled={busy} onClick={() => onPicking(true)}>{t("Set the environment of a project…")}</Button>
  </PopoverNext>;
  return <div className="environment-tab">
    <section className="fleet-table-section secrets-table" aria-label={t("Environment variables of projects")}>
      <div className="secrets-toolbar"><span className="secrets-toolbar-actions">{picker}</span></div>
      {rows.length === 0
        ? !draftProject && <NonIdealState className="secrets-empty" icon={<AppIcon name="terminal" size={28} />} title={t("No project sets an environment.")} />
        : <DataTable label={t("Environment variables of projects")} rows={rows} columns={columns} rowKey={project => project.id} sort={sort} onSort={setSort}
          selected={selected} onSelect={setSelected} rowProps={project => ({ data: { project: project.id } })}
          empty={<span className="muted-text">{t("No project matches.")}</span>}
          details={project => <ProjectEnvironment project={project} listing={listing} busy={busy} run={run} actions={actions} onSetValue={onSetValue} onOpenFile={onOpenFile} />} />}
    </section>
    {draftProject && <section className="environment-draft">
      <h3><ProjectMark project={draftProject} /> {draftProject.name}</h3>
      <ProjectEnvironment key={draftProject.id} project={draftProject} listing={listing} busy={busy} run={run} actions={actions}
        onSetValue={onSetValue} onOpenFile={onOpenFile} />
    </section>}
    {environment.spaces.length > 0 && <section className="environment-spaces" aria-labelledby="environment-spaces-title">
      <h3 id="environment-spaces-title">{t("CLI profiles of spaces")}</h3>
      <ul className="environment-space-list">
        {environment.spaces.map(space => <li key={space.id} data-space={space.id}>
          <strong>{space.name}</strong>
          <span className="environment-profiles">{space.profiles.map(profile => <ProfileTag key={profile.name} profile={profile} />)}</span>
          {onOpenSpace && <Button size="small" variant="minimal" endIcon={<AppIcon name="arrowRight" size={13} />} text={t("Identity")}
            aria-label={t("Identity of {name}", { name: space.name })} onClick={() => onOpenSpace(space.id)} />}
        </li>)}
      </ul>
    </section>}
  </div>;
}

/** A CLI profile, read only: its tool, and the names of the variables it sets. */
function ProfileTag({ profile }: { profile: Readonly<{ name: string; variables: readonly string[] }> }) {
  return <Tag minimal title={profile.variables.join(", ")} data-profile={profile.name}><strong>{profile.name}</strong>{profile.variables.length > 0 && <>{" · "}{profile.variables.join(", ")}</>}</Tag>;
}

/**
 * The environment of one project, under its row: its variables (name, value, what is missing on this computer, Delete),
 * the form that adds one, the file they are saved in, and its CLI profiles, read only.
 */
function ProjectEnvironment({ project, listing, busy, run, actions, onSetValue, onOpenFile }: {
  project: EnvironmentProject; listing: SecretsList; busy: boolean; run: Run; actions: EnvironmentActions; onSetValue: (kind: "secret" | "var", name: string) => void;
  onOpenFile?: (projectId: string, path: string) => void;
}) {
  const { t } = useShellLanguage();
  // What the lines of the variables do not already say: the references of the CLI profiles that are not available here.
  const said = new Set(project.variables.flatMap(variable => variable.issues).map(issue => `${issue.kind}\n${issue.name}\n${issue.state}`));
  const others = project.issues.filter(issue => !said.has(`${issue.kind}\n${issue.name}\n${issue.state}`));
  return <div className="environment-editor" data-environment={project.id}>
    {project.variables.length > 0 && <div className="environment-variable-scroll">
      <table className="environment-variable-list" aria-label={t("Environment variables of {name}", { name: project.name })}>
        <thead><tr><th scope="col">{t("Name")}</th><th scope="col">{t("Value")}</th>
          <th scope="col">{t("On this computer")}</th><th scope="col" aria-label={t("Actions")} /></tr></thead>
        <tbody>{project.variables.map(variable => <VariableLine key={variable.name} project={project} variable={variable} busy={busy}
          run={run} actions={actions} onSetValue={onSetValue} />)}</tbody>
      </table>
    </div>}
    <NewEnvironmentVariable project={project} listing={listing} busy={busy} run={run} actions={actions} />
    <p className="environment-file">
      <span className="muted-text">{t("Saved in")}</span><code title={project.path}>{project.path}</code>
      {onOpenFile && <Button size="small" variant="minimal" icon={<AppIcon name="openExternal" size={13} />} text={t("Open")} onClick={() => onOpenFile(project.id, project.path)} />}
    </p>
    {project.profiles.length > 0 && <div className="environment-project-profiles" role="group" aria-label={t("CLI profiles")}>
      <h4>{t("CLI profiles")}</h4>
      <span className="environment-profiles">{project.profiles.map(profile => <ProfileTag key={profile.name} profile={profile} />)}</span>
    </div>}
    <IssueList issues={others} onSetValue={onSetValue} />
  </div>;
}

/** What is missing on this computer for a value: each issue in words, and Set it for a secret that is not set. */
function IssueList({ issues, onSetValue }: { issues: readonly EnvironmentIssue[]; onSetValue: (kind: "secret" | "var", name: string) => void }) {
  const { t } = useShellLanguage();
  if (issues.length === 0) return null;
  return <span className="environment-issues">
    {issues.map(issue => <span key={`${issue.kind}\n${issue.name}\n${issue.state}`} className="environment-issue" data-issue={issue.state} title={issue.message ?? undefined}>
      <AppIcon name="warning" size={12} />{issueText(issue, t)}
      {(issue.kind === "secret" || issue.kind === "var") && issue.state === "not_set" && <Button size="small" variant="minimal" intent="primary" text={t("Set it…")}
        aria-label={t("Set {name}…", { name: issue.name })} onClick={() => onSetValue(issue.kind as "secret" | "var", issue.name)} />}
    </span>)}
  </span>;
}

/** One variable of a project: its name, its value edited in place, what is missing for it on this computer, and Delete for one the project sets itself. */
function VariableLine({ project, variable, busy, run, actions, onSetValue }: {
  project: EnvironmentProject; variable: EnvironmentVariable; busy: boolean; run: Run; actions: EnvironmentActions; onSetValue: (kind: "secret" | "var", name: string) => void;
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState<string | null>(null);
  const [deleting, setDeleting] = useState(false);
  const value = draft ?? variable.value;
  const changed = value !== variable.value;
  const save = async () => {
    if (!busy && changed && value && await run(() => actions.setVariable(project.id, variable.name, value))) setDraft(null);
  };
  return <tr data-environment-variable={variable.name} data-inherited={!variable.local || undefined}>
    <td><span className="environment-variable-name"><code title={variable.name}>{variable.name}</code>
      {!variable.local && <Tag minimal title={t("Set by a parent project. A value saved here replaces it for this project.")}>{t("Inherited")}</Tag>}</span></td>
    <td><InputGroup className="secret-value" small value={value} disabled={busy} aria-label={t("Value of {name}", { name: variable.name })} spellCheck={false}
      onChange={event => setDraft(event.target.value)} onKeyDown={event => {
        if (event.key === "Enter") void save();
        else if (event.key === "Escape" && changed) { event.stopPropagation(); setDraft(null); }
      }} /></td>
    <td>
      {variable.issues.length === 0 ? <span role="img" className="environment-ready" aria-label={t("All set")} title={t("All set")}><AppIcon name="check" size={13} /></span>
        : <IssueList issues={variable.issues} onSetValue={onSetValue} />}
    </td>
    <td><span className="secret-actions">
      {changed && <Button size="small" intent="primary" text={t("Save")} disabled={busy || !value} onClick={() => void save()} />}
      {variable.local && <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete {name}", { name: variable.name })} title={t("Delete")}
          disabled={busy} onClick={() => setDeleting(true)} />
        {deleting && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Delete this variable?")} subject={variable.name}
          detail={t("The sessions of {name} no longer get it.", { name: project.name })} confirmLabel={t("Delete")}
          onConfirm={() => { setDeleting(false); void run(() => actions.deleteVariable(project.id, variable.name)); }} onCancel={() => setDeleting(false)} />}
      </span>}
    </span></td>
  </tr>;
}

/** The form that adds a variable to a project: its name, its value, and a menu that writes the reference of a secret or of a variable of this computer. */
function NewEnvironmentVariable({ project, listing, busy, run, actions }: { project: EnvironmentProject; listing: SecretsList; busy: boolean; run: Run; actions: EnvironmentActions }) {
  const { t } = useShellLanguage();
  const [name, setName] = useState("");
  const [value, setValue] = useState("");
  const [inserting, setInserting] = useState(false);
  const typed = name.trim();
  const problem = typed.length > 0 && !isSecretName(typed) ? "Letters, digits and _, not starting with a digit." as const : null;
  const ready = isSecretName(typed) && value.length > 0 && !busy;
  const save = async () => {
    if (!ready) return;
    if (await run(() => actions.setVariable(project.id, typed, value))) { setName(""); setValue(""); }
  };
  const insert = (reference: string) => { setValue(current => current + reference); setInserting(false); };
  return <form className="environment-new" onSubmit={event => { event.preventDefault(); void save(); }}>
    <InputGroup small value={name} placeholder="GH_TOKEN" disabled={busy} intent={problem ? "danger" : "none"} spellCheck={false}
      aria-label={t("Name of the environment variable")} onChange={event => setName(event.target.value)} />
    <InputGroup small value={value} placeholder={t("Value")} disabled={busy} spellCheck={false} autoComplete="off" aria-label={t("Value of the environment variable")}
      onChange={event => setValue(event.target.value)}
      rightElement={<PopoverNext isOpen={inserting} placement="bottom-end" onInteraction={next => { if (!next) setInserting(false); }}
        content={<Menu className="environment-insert" aria-label={t("Insert a reference")}>
          <MenuDivider title={t("Secrets")} />
          {listing.secrets.map(secret => <MenuItem key={secret.name} data-insert={referenceOf("secret", secret.name)} text={secret.name} onClick={() => insert(referenceOf("secret", secret.name))} />)}
          {listing.secrets.length === 0 && <MenuItem disabled text={t("No secret on this machine.")} />}
          {listing.variables.length > 0 && <MenuDivider title={t("Variables")} />}
          {listing.variables.map(variable => <MenuItem key={variable.name} data-insert={referenceOf("var", variable.name)} text={variable.name} onClick={() => insert(referenceOf("var", variable.name))} />)}
        </Menu>}>
        <Button size="small" variant="minimal" icon={<AppIcon name="secret" size={13} />} aria-label={t("Insert a reference")} title={t("Insert a reference")} disabled={busy}
          onClick={() => setInserting(true)} />
      </PopoverNext>} />
    <Button type="submit" size="small" intent="primary" icon={<AppIcon name="plus" size={14} />} text={t("Add")} disabled={!ready} />
    {problem && <span className="secret-problem" role="alert">{t(problem)}</span>}
  </form>;
}
