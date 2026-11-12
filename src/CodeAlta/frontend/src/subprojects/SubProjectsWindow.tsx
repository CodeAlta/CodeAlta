import { useCallback, useEffect, useId, useMemo, useRef, useState, type ReactNode } from "react";
import { Button, Callout, Checkbox, FormGroup, HTMLSelect, InputGroup, NonIdealState, Switch, TextArea } from "@blueprintjs/core";
import type { subProjects as subProjectsService, SubProjectCandidateView, SubProjectEntryView, SubProjectLabelView, SubProjectsMapView } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { ConfirmPopover } from "../ConfirmPopover";
import { brandColor } from "../brands";
import { ProviderIconPicker } from "../ProviderIconFields";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { spaceColors } from "../spaces/spaces";
import { labelBrand } from "../labels/labels";
import { LabelIcon, labelTint } from "../labels/LabelViews";
import { addableCandidates, entryChanges, entryCounts, entryDraft, entryName, entryState, filterEntries, keyFrom, mapLabelAncestors, mapLabelPath, normalizeFolder,
  parentChoices, validKey, type EntryDraft, type EntryState } from "./subProjects";

export type SubProjectsApi = Pick<typeof subProjectsService, "map" | "scan" | "show" | "add" | "update" | "remove" | "label">;

type Tab = "projects" | "labels" | "add";
type Selection = Readonly<{ kind: "entry"; key: string }> | Readonly<{ kind: "label"; key: string | null }> | null;
type Change = Readonly<{ status: string; message?: string | null; key?: string | null }>;

const stateText: Readonly<Record<EntryState, "Shown" | "Hidden" | "Optional" | "Folder missing" | "Leads outside" | "In another repository">> = {
  shown: "Shown", hidden: "Hidden", optional: "Optional", missing: "Folder missing", outside: "Leads outside", elsewhere: "In another repository",
};

/**
 * The sub-projects of a repository: the entries of its project map, each shown or hidden for the user, the labels of
 * the map, and the folders that can be added. What changes the map writes `.alta/projects.toml`, a file of the
 * repository that the user reviews and commits; showing or hiding writes only the user's files.
 */
export function SubProjectsWindow({ epoch, project, api, onClose, onOpenMap, onSetUp, onOpenProject, onChanged }: {
  epoch: string;
  /** The repository project. */
  project: Readonly<{ id: string; name: string }>;
  api: SubProjectsApi;
  onClose: () => void;
  /** Opens the file of the map in the code editor. */
  onOpenMap: (path: string) => void;
  /** Starts a session of the repository that sets it up with an agent. */
  onSetUp?: () => void;
  /** Shows a sub-project in the Explorer. */
  onOpenProject: (projectId: string) => void;
  /** The map or what is shown changed: the window reads the projects again. */
  onChanged: () => void;
}) {
  const { t } = useShellLanguage();
  const titleId = useId();
  const [map, setMap] = useState<SubProjectsMapView | null>(null);
  const [status, setStatus] = useState<string>("loading");
  const [tab, setTab] = useState<Tab>("projects");
  const [filter, setFilter] = useState("");
  const [selection, setSelection] = useState<Selection>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);
  const alive = useRef(true);
  // Set again on each mount: StrictMode runs the cleanup once before the window is used.
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  const read = useCallback(async () => {
    try {
      const reply = await api.map({ expectedEpoch: epoch, projectId: project.id }, { timeoutMilliseconds: 30_000 });
      if (!alive.current) return;
      setStatus(reply.status);
      setMap(reply.status === "ok" ? reply.map ?? null : null);
    } catch { if (alive.current) setStatus("failed"); }
  }, [api, epoch, project.id]);
  useEffect(() => { void read(); }, [read]);

  // A change of the map, then the map read again and the projects of the window.
  async function change(call: () => Promise<Change>, failure: MessageKey): Promise<boolean> {
    setBusy(true); setError(null);
    try {
      const reply = await call();
      if (!alive.current) return false;
      if (reply.status !== "ok") { setError(reply.message ?? t(failure)); return false; }
      await read();
      onChanged();
      setDirty(false);
      return true;
    } catch { if (alive.current) setError(t(failure)); return false; }
    finally { if (alive.current) setBusy(false); }
  }

  const entries = map?.entries ?? [];
  const labels = map?.labels ?? [];
  const shown = useMemo(() => filterEntries(entries, labels, filter), [entries, labels, filter]);
  const counts = entryCounts(entries);
  const selectedEntry = selection?.kind === "entry" ? entries.find(entry => entry.key === selection.key) ?? null : null;
  const selectedLabel = selection?.kind === "label" ? selection.key === null ? "new" : labels.find(label => label.key === selection.key) ?? null : null;
  const choose = (next: Selection) => { if (!busy) { setSelection(next); setError(null); setDirty(false); } };

  const toggle = (entry: SubProjectEntryView, value: boolean) => void change(() => api.show({ expectedEpoch: epoch, projectId: project.id, key: entry.key, shown: value },
    { timeoutMilliseconds: 30_000 }), "The sub-project could not be changed.");

  const bar = <header className="subprojects-bar">
    <span className="subprojects-tabs" role="tablist" aria-label={t("Sub-projects")}>
      {(["projects", "labels", "add"] as const).map(value => <button key={value} type="button" role="tab" aria-selected={tab === value} className="subprojects-tab"
        onClick={() => { if (!busy) { setTab(value); setSelection(null); setError(null); setDirty(false); } }}>
        {value === "projects" ? <>{t("Projects")}<span className="subprojects-tab-count">{entries.length}</span></>
          : value === "labels" ? <>{t("Labels")}<span className="subprojects-tab-count">{labels.length}</span></>
          : <><AppIcon name="plus" size={13} />{t("Add folders")}</>}
      </button>)}
    </span>
    <span className="subprojects-spacer" />
    {map?.exists && <Button variant="minimal" size="small" icon={<AppIcon name="fileCode" size={14} />} text={t("Edit the project map")} title={map.manifestPath}
      onClick={() => onOpenMap(map.manifestPath)} />}
    {onSetUp && <Button variant="minimal" size="small" icon={<AppIcon name="assistant" size={14} />} text={t("Set up with an agent…")} onClick={onSetUp} />}
    <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={14} />} aria-label={t("Refresh")} title={t("Refresh")} disabled={busy} onClick={() => void read()} />
  </header>;

  const body = status === "loading" ? <NonIdealState className="subprojects-empty" icon={<ActivitySpinner size={28} />} title={t("Loading…")} />
    : !map ? <NonIdealState className="subprojects-empty" icon={<AppIcon name="folder" size={36} />} title={t("The project map cannot be read")}
      action={<Button icon={<AppIcon name="refresh" size={15} />} text={t("Refresh")} onClick={() => void read()} />} />
    : map.claimedBy ? <NonIdealState className="subprojects-empty" icon={<AppIcon name="folder" size={36} />} title={t("Its project map is not read")}
      description={t("The problem above says why. Declare its sub-projects in the map of the repository above it.")}
      action={<Button text={t("Open that repository")} onClick={() => onOpenProject(map.claimedBy!)} />} />
    : tab === "projects" ? <ProjectsPane map={map} entries={shown} total={entries.length} filter={filter} onFilter={setFilter} selected={selectedEntry}
      onSelect={entry => choose({ kind: "entry", key: entry.key })} onToggle={toggle} busy={busy} onAdd={() => { setTab("add"); setSelection(null); }} onSetUp={onSetUp}>
      {selectedEntry && <EntryForm key={selectedEntry.key} entry={selectedEntry} labels={labels} busy={busy} error={error} onDirty={setDirty}
        onOpen={selectedEntry.projectId && !selectedEntry.hidden ? () => onOpenProject(selectedEntry.projectId!) : undefined}
        onSave={changes => void change(() => api.update({ expectedEpoch: epoch, projectId: project.id, key: selectedEntry.key, path: changes.path, name: changes.name,
          description: changes.description, labels: changes.labels, optional: changes.optional, write: changes.write }, { timeoutMilliseconds: 30_000 }),
          "The sub-project could not be changed.")}
        onRemove={() => void change(() => api.remove({ expectedEpoch: epoch, projectId: project.id, key: selectedEntry.key, path: null, name: null, description: null,
          labels: null, optional: null, write: null }, { timeoutMilliseconds: 30_000 }), "The sub-project could not be removed.").then(done => { if (done) setSelection(null); })} />}
    </ProjectsPane>
    : tab === "labels" ? <LabelsPane labels={labels} entries={entries} selected={selectedLabel === "new" ? null : selectedLabel} creating={selectedLabel === "new"}
      onSelect={label => choose({ kind: "label", key: label?.key ?? null })}>
      {selectedLabel && <LabelForm key={selectedLabel === "new" ? "new" : selectedLabel.key} label={selectedLabel === "new" ? null : selectedLabel} labels={labels}
        busy={busy} error={error} onDirty={setDirty}
        onSave={(key, edit) => void change(() => api.label({ expectedEpoch: epoch, projectId: project.id, key, name: edit.name, parent: edit.parent, icon: edit.icon,
          color: edit.color, description: edit.description, remove: false }, { timeoutMilliseconds: 30_000 }), "The label could not be changed.")
          .then(done => { if (done) setSelection({ kind: "label", key }); })}
        onRemove={key => void change(() => api.label({ expectedEpoch: epoch, projectId: project.id, key, name: null, parent: null, icon: null, color: null,
          description: null, remove: true }, { timeoutMilliseconds: 30_000 }), "The label could not be removed.").then(done => { if (done) setSelection(null); })} />}
    </LabelsPane>
    : <AddPane api={api} epoch={epoch} projectId={project.id} map={map} busy={busy} error={error}
      onAdd={async folders => {
        let added = 0;
        for (const folder of folders) {
          const done = await change(() => api.add({ expectedEpoch: epoch, projectId: project.id, key: null, path: folder, name: null, description: null, labels: null,
            optional: null, write: null }, { timeoutMilliseconds: 30_000 }), "The folder could not be added.");
          if (!done) break;
          added++;
        }
        if (added === folders.length && alive.current) setTab("projects");
      }} />;

  return <AppWindow storageKey="codealta.desktop.window.subProjects.v1" className="subprojects-dialog" titleId={titleId}
    title={<>{t("Sub-projects")}<span className="subprojects-repository">
      {map?.parentName && <span className="subprojects-parent">{map.parentName} › </span>}{project.name}</span></>}
    preferredSize={viewport => ({ width: Math.min(1080, viewport.width - 40), height: Math.min(720, viewport.height - 40) })} minimumSize={{ width: 520, height: 380 }}
    onClose={() => { if (!busy) onClose(); }} closeLabel={t("Close")} keepOnOutsidePress={dirty || busy}>
    <div className="subprojects" data-tab={tab}>
      {bar}
      {map && map.problems.length > 0 && tab !== "add" && <Problems map={map} />}
      {map && !map.claimedBy && <p className="subprojects-summary" role="status">{entries.length === 0 ? t("This repository declares no sub-project yet.")
        : t("{shown} shown, {hidden} hidden, {optional} optional, {unavailable} unavailable.", { shown: counts.shown, hidden: counts.hidden, optional: counts.optional,
          unavailable: counts.missing + counts.outside + counts.elsewhere })}</p>}
      {body}
    </div>
  </AppWindow>;
}

/** The problems of the map: what it leaves out, and why. */
function Problems({ map }: { map: SubProjectsMapView }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  return <Callout intent="warning" compact className="subprojects-problems" role="alert">
    <button type="button" className="subprojects-problems-toggle" aria-expanded={open} onClick={() => setOpen(value => !value)}>
      <AppIcon name="chevronDown" size={12} className={open ? "tree-chevron expanded" : "tree-chevron"} />
      {t(map.problems.length === 1 ? "The map has {count} problem: what it says there is left out." : "The map has {count} problems: what they say is left out.",
        { count: map.problems.length })}
    </button>
    {open && <ul>{map.problems.map((problem, index) => <li key={index}>{problem.key && <code>{problem.key}</code>} {problem.message}</li>)}</ul>}
  </Callout>;
}

function ProjectsPane({ map, entries, total, filter, onFilter, selected, onSelect, onToggle, busy, onAdd, onSetUp, children }: {
  map: SubProjectsMapView; entries: readonly SubProjectEntryView[]; total: number; filter: string; onFilter: (value: string) => void;
  selected: SubProjectEntryView | null; onSelect: (entry: SubProjectEntryView) => void; onToggle: (entry: SubProjectEntryView, shown: boolean) => void;
  busy: boolean; onAdd: () => void; onSetUp?: () => void; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  if (total === 0) return <NonIdealState className="subprojects-empty" icon={<AppIcon name="folder" size={36} />} title={t("No sub-project yet")}
    description={t("A sub-project is a folder of the repository that is a project of its own, with its own sessions. The repository declares them in .alta/projects.toml, committed with it, so that everyone who adds it gets them.")}
    action={<span className="subprojects-empty-actions">
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} text={t("Add folders")} onClick={onAdd} />
      {onSetUp && <Button icon={<AppIcon name="assistant" size={15} />} text={t("Set up with an agent…")} onClick={onSetUp} />}
    </span>} />;
  return <div className="subprojects-body">
    <div className="subprojects-list-pane">
      <InputGroup className="subprojects-filter" size="small" leftElement={<span className="subprojects-filter-icon"><AppIcon name="filter" size={13} /></span>} placeholder={t("Filter by name, folder or label")}
        value={filter} onChange={event => onFilter(event.target.value)} aria-label={t("Filter by name, folder or label")} />
      <ul className="subprojects-list" aria-label={t("Projects")}>
        {entries.map(entry => {
          const state = entryState(entry);
          const usable = state !== "missing" && state !== "outside" && state !== "elsewhere";
          return <li key={entry.key} className="subprojects-row" aria-current={selected?.key === entry.key ? "true" : undefined} data-state={state}>
            <Switch className="subprojects-switch" checked={state === "shown"} disabled={busy || !usable}
              aria-label={t(state === "shown" ? "Hide {name}" : "Show {name}", { name: entryName(entry) })}
              onChange={event => onToggle(entry, event.currentTarget.checked)} />
            <button type="button" className="subprojects-row-main" onClick={() => onSelect(entry)} title={entry.path}>
              <span className="project-icon" data-file-tone={usable ? "gold" : "muted"}><AppIcon name={entry.submodule ? "branch" : "folder"} size={15} /></span>
              <span className="subprojects-row-text">
                <strong>{entryName(entry)}</strong>
                <code>{entry.path}</code>
              </span>
              <span className="subprojects-row-tags">
                {entry.labels.map(key => <MapLabelBadge key={key} labelKey={key} labels={map.labels} />)}
                {entry.subProjects > 0 && <span className="subprojects-chip" data-nested="true" title={t("It has a project map of its own: its sub-projects are grouped under it.")}>
                  {t(entry.subProjects === 1 ? "{count} sub-project" : "{count} sub-projects", { count: entry.subProjects })}</span>}
                {entry.submodule && <span className="subprojects-chip" title={t("Its folder is a git submodule: a repository of its own.")}>{t("Submodule")}</span>}
                {entry.write === "folder" && <span className="subprojects-chip" title={t("Its sessions write only its folder.")}>{t("Its folder only")}</span>}
                {state !== "shown" && <span className="subprojects-chip" data-state={state}>{t(stateText[state])}</span>}
              </span>
            </button>
          </li>;
        })}
        {entries.length === 0 && <li className="subprojects-none">{t("No sub-project matches the filter.")}</li>}
      </ul>
    </div>
    <section className="subprojects-detail" aria-label={t("Details")}>
      {children ?? <p className="subprojects-hint">{t("Select a sub-project to change its name, its folder or its labels. Its switch shows or hides it in your Explorer only: the map stays as it is.")}</p>}
    </section>
  </div>;
}

/** A label of the map as a badge: its icon, its path and its name, in its color. */
function MapLabelBadge({ labelKey, labels }: { labelKey: string; labels: readonly SubProjectLabelView[] }) {
  const label = labels.find(value => value.key === labelKey);
  if (!label) return null;
  const look = { icon: label.icon ?? null, color: brandColor(label.color) ?? null };
  const above = mapLabelAncestors(labelKey, labels);
  return <span className="label-badge" data-small="true" data-colored={look.color ? "true" : undefined} style={labelTint(look)} title={mapLabelPath(labelKey, labels)}>
    <LabelIcon label={look} size={10} />
    {above.length > 0 && <span className="label-badge-path">{above.map(value => value.name).join(" › ")} › </span>}
    <span className="label-badge-name">{label.name}</span>
  </span>;
}

function EntryForm({ entry, labels, busy, error, onDirty, onSave, onRemove, onOpen }: {
  entry: SubProjectEntryView; labels: readonly SubProjectLabelView[]; busy: boolean; error: string | null; onDirty: (dirty: boolean) => void;
  onSave: (changes: NonNullable<ReturnType<typeof entryChanges>>) => void; onRemove: () => void; onOpen?: () => void;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const [draft, setDraft] = useState<EntryDraft>(() => entryDraft(entry));
  const [asking, setAsking] = useState(false);
  const changes = entryChanges(entry, draft);
  useEffect(() => onDirty(!!changes), [!!changes]); // eslint-disable-line react-hooks/exhaustive-deps
  const update = (value: Partial<EntryDraft>) => setDraft(current => ({ ...current, ...value }));
  return <form className="subprojects-form" aria-label={entryName(entry)} onSubmit={event => { event.preventDefault(); if (changes && !busy) onSave(changes); }}>
    <header className="subprojects-form-head">
      <strong>{entryName(entry)}</strong><code>{entry.key}</code>
      {onOpen && <Button variant="minimal" size="small" icon={<AppIcon name="locate" size={14} />} text={t("Show in the Explorer")} onClick={onOpen} />}
    </header>
    <FormGroup label={t("Name")} labelFor={`${id}-name`} helperText={t("Empty for the name of its folder.")}>
      <InputGroup id={`${id}-name`} value={draft.name} placeholder={entry.path.split("/").at(-1)} disabled={busy} autoComplete="off" spellCheck={false}
        onChange={event => update({ name: event.target.value })} />
    </FormGroup>
    <FormGroup label={t("Folder")} labelFor={`${id}-path`} helperText={t("Relative to the root of the repository. A folder that moves keeps its project and its sessions.")}>
      <InputGroup id={`${id}-path`} value={draft.path} disabled={busy} autoComplete="off" spellCheck={false} onChange={event => update({ path: event.target.value })} />
    </FormGroup>
    <FormGroup label={t("Description")} labelFor={`${id}-description`}>
      <TextArea id={`${id}-description`} value={draft.description} disabled={busy} rows={2} fill onChange={event => update({ description: event.target.value })} />
    </FormGroup>
    <FormGroup label={t("Labels of the map")}>
      {labels.length === 0 ? <p className="subprojects-hint">{t("The map has no label yet: create them in its Labels tab.")}</p>
        : <div className="subprojects-label-choices">{labels.map(label => <Checkbox key={label.key} disabled={busy} checked={draft.labels.includes(label.key)}
          labelElement={<MapLabelBadge labelKey={label.key} labels={labels} />}
          onChange={event => update({ labels: event.currentTarget.checked ? [...draft.labels, label.key] : draft.labels.filter(key => key !== label.key) })} />)}</div>}
    </FormGroup>
    <Checkbox checked={draft.optional} disabled={busy} label={t("Optional: listed only for those who add it")} onChange={event => update({ optional: event.currentTarget.checked })} />
    <Checkbox checked={draft.folderOnly} disabled={busy} label={t("Its sessions write only its folder")} onChange={event => update({ folderOnly: event.currentTarget.checked })} />
    {error && <Callout intent="danger" compact role="alert" className="subprojects-error">{error}</Callout>}
    <footer className="subprojects-form-actions">
      <span>
        <Button intent="danger" variant="minimal" icon={<AppIcon name="trash" size={14} />} text={t("Remove from the map…")} disabled={busy} onClick={() => setAsking(true)} />
        {asking && <ConfirmPopover always title={t("Remove this sub-project from the map?")} subject={entryName(entry)} intent="danger" confirmLabel={t("Remove")}
          detail={t("Its folder stays. Once it has sessions, it stays a project of its own; otherwise it leaves the Explorer.")} busy={busy} error={null}
          placement="top-start" onConfirm={() => { setAsking(false); onRemove(); }} onCancel={() => setAsking(false)} />}
      </span>
      <span>
        <Button variant="minimal" text={t("Reset")} disabled={busy || !changes} onClick={() => setDraft(entryDraft(entry))} />
        <Button type="submit" intent="primary" text={t("Save")} loading={busy} disabled={!changes || !normalizeFolder(draft.path)} />
      </span>
    </footer>
  </form>;
}

function LabelsPane({ labels, entries, selected, creating, onSelect, children }: {
  labels: readonly SubProjectLabelView[]; entries: readonly SubProjectEntryView[]; selected: SubProjectLabelView | null; creating: boolean;
  onSelect: (label: SubProjectLabelView | null) => void; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  return <div className="subprojects-body">
    <div className="subprojects-list-pane">
      <Button className="subprojects-new-label" variant="minimal" size="small" icon={<AppIcon name="plus" size={14} />} text={t("New label")} active={creating}
        onClick={() => onSelect(null)} />
      <ul className="subprojects-list" aria-label={t("Labels")}>
        {labels.map(label => {
          const count = entries.filter(entry => entry.labels.includes(label.key)).length;
          const depth = mapLabelAncestors(label.key, labels).length;
          return <li key={label.key} className="subprojects-row" aria-current={selected?.key === label.key ? "true" : undefined}>
            <button type="button" className="subprojects-row-main" style={{ paddingLeft: 10 + depth * 16 }} onClick={() => onSelect(label)}>
              <LabelIcon label={{ icon: label.icon ?? null, color: brandColor(label.color) ?? null }} size={14} />
              <span className="subprojects-row-text"><strong>{label.name}</strong><code>{label.key}</code></span>
              <span className="project-label-count" aria-label={t(count === 1 ? "{count} project" : "{count} projects", { count })}>{count}</span>
            </button>
          </li>;
        })}
        {labels.length === 0 && <li className="subprojects-none">{t("The map has no label yet.")}</li>}
      </ul>
    </div>
    <section className="subprojects-detail" aria-label={t("Details")}>
      {children ?? <p className="subprojects-hint">{t("The labels of the map are the team's: they group the sub-projects under the repository in the Explorer of everyone. Your own labels stay yours.")}</p>}
    </section>
  </div>;
}

type LabelDraft = Readonly<{ key: string; name: string; parent: string; icon: string; color: string; description: string }>;

function LabelForm({ label, labels, busy, error, onDirty, onSave, onRemove }: {
  label: SubProjectLabelView | null; labels: readonly SubProjectLabelView[]; busy: boolean; error: string | null; onDirty: (dirty: boolean) => void;
  onSave: (key: string, edit: Readonly<{ name: string | null; parent: string | null; icon: string | null; color: string | null; description: string | null }>) => void;
  onRemove: (key: string) => void;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const initial: LabelDraft = { key: label?.key ?? "", name: label?.name ?? "", parent: label?.parent ?? "", icon: label?.icon ?? "", color: label?.color ?? "",
    description: label?.description ?? "" };
  const [draft, setDraft] = useState(initial);
  const [keyEdited, setKeyEdited] = useState(false);
  const [asking, setAsking] = useState(false);
  const update = (value: Partial<LabelDraft>) => setDraft(current => ({ ...current, ...value }));
  const key = label ? label.key : keyEdited ? draft.key : keyFrom(draft.name);
  const changed = (Object.keys(initial) as (keyof LabelDraft)[]).some(field => draft[field] !== initial[field]);
  useEffect(() => onDirty(changed), [changed]); // eslint-disable-line react-hooks/exhaustive-deps
  const taken = !label && labels.some(value => value.key === key);
  const problem = !draft.name.trim() ? t("A label has a name.") : !validKey(key) ? t("A key is lower-case letters, digits, - and _.")
    : taken ? t("Another label of the map has this key.") : null;
  const parents = parentChoices(label?.key ?? null, labels).filter(value => value.key !== label?.key);
  const shown = { icon: draft.icon || null, color: brandColor(draft.color) ?? null };
  const edit = () => ({
    name: draft.name.trim() !== initial.name ? draft.name.trim() : null,
    parent: draft.parent !== initial.parent ? draft.parent : null,
    icon: draft.icon !== initial.icon ? draft.icon : null,
    color: draft.color !== initial.color ? draft.color : null,
    description: draft.description.trim() !== initial.description ? draft.description.trim() : null,
  });
  return <form className="subprojects-form" aria-label={label?.name ?? t("New label")} onSubmit={event => { event.preventDefault(); if (!busy && !problem && changed) onSave(key, label ? edit() : { ...edit(), name: draft.name.trim() }); }}>
    <header className="subprojects-form-head"><strong>{label ? label.name : t("New label")}</strong>{label && <code>{label.key}</code>}</header>
    <FormGroup label={t("Name")} labelFor={`${id}-name`} intent={problem && changed ? "danger" : "none"} helperText={problem && changed ? <span role="alert">{problem}</span> : undefined}>
      <InputGroup id={`${id}-name`} value={draft.name} disabled={busy} autoComplete="off" spellCheck={false} maxLength={80}
        leftElement={<span className="label-form-icon"><LabelIcon label={shown} size={14} /></span>} onChange={event => update({ name: event.target.value })} />
    </FormGroup>
    {!label && <FormGroup label={t("Key")} labelFor={`${id}-key`} helperText={t("Its identity in the map, kept when it is renamed.")}>
      <InputGroup id={`${id}-key`} value={key} disabled={busy} autoComplete="off" spellCheck={false} maxLength={48}
        onChange={event => { setKeyEdited(true); update({ key: event.target.value }); }} />
    </FormGroup>}
    <FormGroup label={t("Under")} labelFor={`${id}-parent`}>
      <HTMLSelect id={`${id}-parent`} value={draft.parent} disabled={busy} onChange={event => update({ parent: event.target.value })}
        options={[{ value: "", label: t("The top level") }, ...parents.map(value => ({ value: value.key, label: mapLabelPath(value.key, labels) }))]} />
    </FormGroup>
    <FormGroup label={t("Icon")} labelFor={`${id}-icon`}>
      <ProviderIconPicker id={`${id}-icon`} value={draft.icon} automatic={{ icon: null }} shown={labelBrand(shown)} disabled={busy} fallback="tag" symbolsFirst
        onChange={icon => update({ icon })} />
    </FormGroup>
    <FormGroup label={t("Color")}>
      <span className="space-color-swatches label-form-colors" role="group" aria-label={t("Color")}>
        {spaceColors.map(color => <button type="button" key={color} className="space-color" style={{ background: color }} disabled={busy}
          aria-pressed={draft.color.toLowerCase() === color} aria-label={color} title={color}
          onClick={() => update({ color: draft.color.toLowerCase() === color ? "" : color })} />)}
      </span>
    </FormGroup>
    <FormGroup label={t("Description")} labelFor={`${id}-description`}>
      <TextArea id={`${id}-description`} value={draft.description} disabled={busy} rows={2} fill onChange={event => update({ description: event.target.value })} />
    </FormGroup>
    {error && <Callout intent="danger" compact role="alert" className="subprojects-error">{error}</Callout>}
    <footer className="subprojects-form-actions">
      <span>{label && <>
        <Button intent="danger" variant="minimal" icon={<AppIcon name="trash" size={14} />} text={t("Remove the label…")} disabled={busy} onClick={() => setAsking(true)} />
        {asking && <ConfirmPopover always title={t("Remove this label from the map?")} subject={label.name} intent="danger" confirmLabel={t("Remove")}
          detail={t("Its projects lose it and stay. The labels under it take its place.")} busy={busy} error={null} placement="top-start"
          onConfirm={() => { setAsking(false); onRemove(label.key); }} onCancel={() => setAsking(false)} />}
      </>}</span>
      <span>
        <Button variant="minimal" text={t("Reset")} disabled={busy || !changed} onClick={() => { setDraft(initial); setKeyEdited(false); }} />
        <Button type="submit" intent="primary" text={label ? t("Save") : t("Create")} loading={busy} disabled={!changed || !!problem} />
      </span>
    </footer>
  </form>;
}

function AddPane({ api, epoch, projectId, map, busy, error, onAdd }: {
  api: SubProjectsApi; epoch: string; projectId: string; map: SubProjectsMapView; busy: boolean; error: string | null; onAdd: (folders: readonly string[]) => Promise<void>;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const [glob, setGlob] = useState("");
  const [scanning, setScanning] = useState(false);
  const [candidates, setCandidates] = useState<readonly SubProjectCandidateView[] | null>(null);
  const [truncated, setTruncated] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [chosen, setChosen] = useState<ReadonlySet<string>>(new Set());
  const [folder, setFolder] = useState("");
  const turn = useRef(0);
  const scan = useCallback(async (pattern: string) => {
    const mine = ++turn.current;
    setScanning(true); setProblem(null);
    try {
      const reply = await api.scan({ expectedEpoch: epoch, projectId, glob: pattern.trim() || null }, { timeoutMilliseconds: 60_000 });
      if (mine !== turn.current) return;
      if (reply.status !== "ok") { setProblem(reply.message ?? t("The folders could not be listed.")); setCandidates([]); return; }
      setCandidates(reply.candidates); setTruncated(reply.truncated); setChosen(new Set());
    } catch { if (mine === turn.current) { setProblem(t("The folders could not be listed.")); setCandidates([]); } }
    finally { if (mine === turn.current) setScanning(false); }
  }, [api, epoch, projectId, t]);
  useEffect(() => { void scan(""); }, [scan]);
  const addable = candidates ? addableCandidates(candidates, map) : [];
  return <div className="subprojects-add">
    <form className="subprojects-add-bar" onSubmit={event => { event.preventDefault(); void scan(glob); }}>
      <FormGroup label={t("Folders that match")} labelFor={`${id}-glob`} inline>
        <InputGroup id={`${id}-glob`} size="small" value={glob} placeholder="services/*" spellCheck={false} autoComplete="off" onChange={event => setGlob(event.target.value)} />
      </FormGroup>
      <Button type="submit" size="small" icon={scanning ? <ActivitySpinner size={13} /> : <AppIcon name="search" size={13} />} text={t("List")} disabled={scanning} />
      <span className="subprojects-hint">{t("Without a pattern: the folders three levels down that hold a project (package.json, a .csproj, Cargo.toml, go.mod…) or a git submodule.")}</span>
    </form>
    {problem && <Callout intent="warning" compact role="alert" className="subprojects-error">{problem}</Callout>}
    {error && <Callout intent="danger" compact role="alert" className="subprojects-error">{error}</Callout>}
    <ul className="subprojects-candidates" aria-label={t("Folders")}>
      {candidates === null ? <li className="subprojects-none"><ActivitySpinner size={14} /> {t("Loading…")}</li>
        : addable.length === 0 ? <li className="subprojects-none">{t("No folder to add: every folder found is declared already.")}</li>
        : addable.map(candidate => <li key={candidate.path}>
          <Checkbox checked={chosen.has(candidate.path)} disabled={busy} onChange={event => {
            // Read now: the event is gone when the update runs.
            const checked = event.currentTarget.checked;
            setChosen(current => {
              const next = new Set(current);
              if (checked) next.add(candidate.path); else next.delete(candidate.path);
              return next;
            });
          }} labelElement={<span className="subprojects-candidate">
            <AppIcon name={candidate.submodule ? "branch" : "folder"} size={14} /><code>{candidate.path}</code>
            {candidate.submodule && <span className="subprojects-chip">{t("Submodule")}</span>}
            {candidate.markers.length > 0 && <small>{candidate.markers.join(", ")}</small>}
          </span>} />
        </li>)}
      {truncated && <li className="subprojects-none">{t("More folders were found than are listed: narrow them with a pattern.")}</li>}
    </ul>
    <footer className="subprojects-form-actions">
      <form className="subprojects-add-folder" onSubmit={event => { event.preventDefault(); if (normalizeFolder(folder)) void onAdd([normalizeFolder(folder)]).then(() => setFolder("")); }}>
        <InputGroup size="small" value={folder} placeholder={t("Another folder, such as apps/admin")} aria-label={t("Another folder, such as apps/admin")}
          spellCheck={false} autoComplete="off" disabled={busy} onChange={event => setFolder(event.target.value)} />
        <Button type="submit" size="small" text={t("Add")} disabled={busy || !normalizeFolder(folder)} />
      </form>
      <span>
        {addable.length > 0 && <Button variant="minimal" text={chosen.size === addable.length ? t("Clear selection") : t("Select all")} disabled={busy}
          onClick={() => setChosen(chosen.size === addable.length ? new Set() : new Set(addable.map(candidate => candidate.path)))} />}
        <Button intent="primary" icon={<AppIcon name="plus" size={14} />} loading={busy} disabled={chosen.size === 0}
          text={chosen.size === 0 ? t("Add folders") : t(chosen.size === 1 ? "Add the folder" : "Add {count} folders", { count: chosen.size })} onClick={() => void onAdd([...chosen])} />
      </span>
    </footer>
  </div>;
}
