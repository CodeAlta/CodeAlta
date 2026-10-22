import { useEffect, useMemo, useState } from "react";
import { Button, Callout, FormGroup, HTMLSelect, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, Switch } from "@blueprintjs/core";
import type { issues as issuesService, IssueFieldInfo, IssueFilterField, IssueViewsResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { SettingsFileLocations } from "../SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "../settingsFiles";
import { RemoveButton } from "../SettingsPage";
import type { SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import { filterValueText, joinValues, movePicks, pickKey, trackerText } from "./issues";
import { MultiChoice } from "./MultiChoice";
import { addableFilters, changeEntry, configuredText, configuredValue, entriesOf, entryFor, fieldTypeLabel, formSelections, freeViewName, isActive, sameEntries, savedEntries,
  switchEntry, valueOf, valuesOf, viewKey, viewSourceLabel, viewSources, type FilterEntry, type ViewSource } from "./issueFilterSettings";

/** The part of the host the window uses; a test gives its own. */
export type IssueViewsApi = Pick<typeof issuesService, "filters" | "fields" | "views" | "saveView" | "renameView" | "removeView">;

// The choice of every item in a list of values, and a value the view names that the tracker no longer offers.
const allChoice = "\u0000all";
const unknownChoice = "\u0000unknown";
const shortName = (id: string) => id.startsWith("field:") ? id.slice("field:".length) : id;

/**
 * The window of the Issues tab that manages the views of the tracker of the project the tab shows: the user's, in
 * `.alta/local/issues.toml`, and the team's, in `.alta/issues.toml`. A view is chosen, made, renamed or removed, and for
 * the view chosen there is one row per filter: its name, its field in the tracker, the type of that field, what the tab
 * does with it and its value. Fields of the tracker are added as filters.
 */
export function IssueViewsWindow({ api, epoch, project, source, onClose, onChanged, onOpenedFile, filesApi }: {
  api: IssueViewsApi; epoch: string;
  project: Readonly<{ id: string; name: string }>;
  /** The tracker of the project whose views are managed. */
  source: Readonly<{ service: string; name: string }>;
  onClose: () => void;
  /** Called after a view was written: the tab reads its filters again. */
  onChanged: () => void;
  /** Called once the code editor was asked to show a file of views. */
  onOpenedFile?: () => void;
  filesApi?: SettingsFilesApi;
}) {
  const { t } = useShellLanguage();
  const projectId = project.id;
  const [views, setViews] = useState<IssueViewsResponse | null>(null);
  const [chosen, setChosen] = useState<string | null>(null);
  const [entries, setEntries] = useState<readonly FilterEntry[]>([]);
  const [filters, setFilters] = useState<{ value: readonly IssueFilterField[]; problem: string | null } | null>(null);
  const [fields, setFields] = useState<readonly IssueFieldInfo[] | null>(null);
  const [search, setSearch] = useState("");
  const [newName, setNewName] = useState("");
  const [newSource, setNewSource] = useState<ViewSource>("personal");
  const [newEmpty, setNewEmpty] = useState(false);
  const [rename, setRename] = useState("");
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [generation, setGeneration] = useState(0);
  const files = useSettingsFiles({ page: "issues", epoch, projectId, revision: views, onOpened: onOpenedFile, setNotice, api: filesApi });
  const [titleId] = useState(() => `issue-views-${Math.random().toString(36).slice(2)}`);

  useEffect(() => {
    setViews(null); setFields(null);
    const controller = new AbortController();
    void api.views({ expectedEpoch: epoch, projectId, service: source.service }, { signal: controller.signal, timeoutMilliseconds: 20_000 }).then(
      reply => { if (!controller.signal.aborted) setViews(reply.status === "ok" ? reply : { status: reply.status, views: [], current: null, paths: [], problem: null }); },
      () => { if (!controller.signal.aborted) { setViews({ status: "failed", views: [], current: null, paths: [], problem: null }); setNotice({ key: "The operation did not complete.", intent: "danger" }); } });
    return () => controller.abort();
  }, [api, epoch, projectId, source.service, generation]);
  // The view the window shows: the one chosen in it, else the one the tab shows, else the first.
  const view = views?.views.find(candidate => viewKey(candidate) === chosen) ?? (views?.current ? views.views.find(candidate => viewKey(candidate) === viewKey(views.current!)) : null)
    ?? views?.views[0] ?? null;
  const saved = useMemo(() => entriesOf(view), [view]);
  useEffect(() => { setEntries(saved); setRename(view?.name ?? ""); }, [saved]);

  // The values of the filters, for the team the view being edited says: the sprints are those of that team. The current view of the tab does not
  // take part, whatever it fixes. The fields the view names are described too.
  const selections = useMemo(() => formSelections(filters?.value ?? [], entries), [filters?.value, JSON.stringify(entries)]);
  const include = entries.map(entry => entry.id).filter(id => id.includes(":"));
  const askKey = JSON.stringify([selections, include]);
  useEffect(() => {
    const controller = new AbortController();
    void api.filters({ expectedEpoch: epoch, projectId, service: source.service, selections, include, withoutView: true }, { signal: controller.signal, timeoutMilliseconds: 60_000 }).then(
      reply => { if (!controller.signal.aborted) setFilters({ value: reply.status === "ok" ? reply.filters : [], problem: reply.problem }); },
      () => { if (!controller.signal.aborted) setFilters({ value: [], problem: null }); });
    return () => controller.abort();
  }, [api, epoch, projectId, source.service, askKey, generation]);

  const known = filters?.value ?? [];
  // The rows are the filters the view names, in its order.
  const rows = view ? entries.map(entry => entry.id) : [];
  const changed = !!view && !sameEntries(entries, saved);
  async function run(call: () => Promise<{ status: string }>, success: "Saved." | "Removed.", after?: (status: string) => void) {
    if (busy) return;
    setBusy(true); setNotice(null);
    try {
      const reply = await call();
      if (reply.status === "ok") { setNotice({ key: success, intent: "success" }); after?.(reply.status); setGeneration(value => value + 1); onChanged(); }
      else setNotice({ key: reply.status === "name_taken" ? "A view already has this name." : reply.status === "config_invalid" ? "The file of the views could not be read: fix it, then save again."
        : reply.status === "too_many_views" ? "This file already holds the most views that are read: remove one first."
        : "The operation did not complete.", intent: "danger" });
    } catch { setNotice({ key: "The operation did not complete.", intent: "danger" }); }
    finally { setBusy(false); }
  }
  const base = () => ({ expectedEpoch: epoch, projectId, service: source.service });
  const save = () => view && void run(() => api.saveView({ ...base(), source: view.source, name: view.name, filters: savedEntries(entries), select: false }), "Saved.");
  const create = () => {
    const name = newName.trim();
    if (!name) return;
    void run(() => api.saveView({ ...base(), source: newSource, name, filters: newEmpty ? [] : savedEntries(entries), select: true }), "Saved.", () => {
      setChosen(viewKey({ name, source: newSource })); setNewName("");
    });
  };
  const renameView = () => view && rename.trim() && rename.trim() !== view.name && void run(() => api.renameView({ ...base(), source: view.source, name: view.name, newName: rename.trim() }),
    "Saved.", () => {
      // The values chosen in the tab for the view follow its name.
      movePicks(pickKey(projectId, source.service, view), pickKey(projectId, source.service, { name: rename.trim(), source: view.source }));
      setChosen(viewKey({ name: rename.trim(), source: view.source }));
    });
  const removeView = () => view && void run(() => api.removeView({ ...base(), source: view.source, name: view.name }), "Removed.", () => setChosen(null));
  const change = (id: string, part: Partial<Omit<FilterEntry, "id">>) => setEntries(value => changeEntry(value, id, part));
  const openFields = () => {
    if (fields !== null) return;
    void api.fields({ expectedEpoch: epoch, projectId, service: source.service }, { timeoutMilliseconds: 60_000 }).then(
      reply => setFields(reply.status === "ok" ? reply.fields : []), () => setFields([]));
  };
  const words = search.trim().toLowerCase();
  const matches = (label: string, id: string) => !words || label.toLowerCase().includes(words) || id.toLowerCase().includes(words);
  const addable = addableFilters(known, fields ?? [], entries);
  const addableOwn = addable.own.map(id => known.find(filter => filter.id === id)!).filter(filter => matches(trackerText(filter.label, t), filter.id));
  const addableFields = addable.fields.map(id => fields!.find(field => field.id === id)!).filter(field => matches(field.label, field.id));
  const allText = (filter: IssueFilterField | undefined) => filter?.allLabel ? trackerText(filter.allLabel, t) : t("All");
  const valueSelect = (id: string, filter: IssueFilterField | undefined, own: FilterEntry, name: string, disabled: boolean) => {
    if (filter?.kind === "text") return <InputGroup size="small" aria-label={t("Value of {filter}", { filter: name })} value={own.value ?? ""} disabled={disabled}
      onChange={event => change(id, { value: event.target.value === "" ? null : event.target.value })} />;
    if (filter?.multiple) return <MultiChoice label={name} values={filter.values} disabled={disabled} chosen={valuesOf(filter, own.value).map(value => value.id)} allText={allText(filter)}
      text={value => trackerText(value.label, t)} onChange={ids => change(id, { value: configuredValue(filter, joinValues(ids)) || null })} />;
    const picked = filter ? valueOf(filter, own.value) : null;
    const unknown = !!own.value && !picked;
    return <HTMLSelect aria-label={t("Value of {filter}", { filter: name })} disabled={disabled} value={picked?.id ?? (unknown ? unknownChoice : allChoice)}
      onChange={event => { const value = filter?.values.find(candidate => candidate.id === event.target.value); change(id, { value: value ? configuredText(value) : null }); }}>
      <option value={allChoice}>{allText(filter)}</option>
      {unknown && <option value={unknownChoice} disabled>{own.value}</option>}
      {filter?.values.map(value => <option key={value.id} value={value.id}>{filterValueText({ label: trackerText(value.label, t), detail: value.detail && trackerText(value.detail, t) })}</option>)}
    </HTMLSelect>;
  };

  // A window with changes not saved stays open when Escape is pressed or the press is beside it, and keeps the view it edits.
  const close = () => { if (!changed) onClose(); };
  const keep = changed ? t("Save or discard the changes first.") : undefined;
  return <AppWindow storageKey="codealta.desktop.issue-views-window.v1" title={t("Views of {project}", { project: project.name })} titleId={titleId}
    className="issue-views-window" preferredSize={viewport => ({ width: Math.min(1100, viewport.width - 80), height: Math.min(760, viewport.height - 60) })}
    onClose={onClose} closeLabel={t("Close")} keepOnOutsidePress={changed} onCancel={event => { event.preventDefault(); close(); }}>
    <div className="issue-views-body" aria-busy={busy || views === null}>
      <p className="issue-views-intro">{t("Each view lists the items of {tracker} that match its active filters.", { tracker: source.name })}</p>
      {notice && <Callout intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>{t(notice.key)}</Callout>}
      <div className="issue-views-choices">
        {view && <FormGroup label={t("View")} labelFor="issue-views-view" inline>
          <HTMLSelect id="issue-views-view" value={viewKey(view)} disabled={busy || changed} title={keep} onChange={event => setChosen(event.target.value)}>
            {viewSources.map(group => views!.views.some(candidate => candidate.source === group) && <optgroup key={group} label={t(viewSourceLabel(group))}>
              {views!.views.filter(candidate => candidate.source === group).map(candidate => <option key={viewKey(candidate)} value={viewKey(candidate)}>{candidate.name}</option>)}
            </optgroup>)}
          </HTMLSelect>
        </FormGroup>}
        <div className="issue-views-actions">
          <PopoverNext placement="bottom-start" content={<div className="issue-views-popover">
            <FormGroup label={t("Name")} labelFor="issue-views-new-name">
              <InputGroup id="issue-views-new-name" size="small" autoFocus maxLength={64} value={newName} onChange={event => setNewName(event.target.value)}
                onKeyDown={event => { if (event.key === "Enter") create(); }} />
            </FormGroup>
            <FormGroup label={t("Kept for")} labelFor="issue-views-new-source">
              <HTMLSelect id="issue-views-new-source" value={newSource} onChange={event => setNewSource(event.target.value as ViewSource)}>
                {viewSources.map(candidate => <option key={candidate} value={candidate}>{t(viewSourceLabel(candidate))}</option>)}
              </HTMLSelect>
            </FormGroup>
            {newSource === "team" && <p className="issue-views-note">{t("Written in .alta/issues.toml: commit it to share it with your team.")}</p>}
            {view && <HTMLSelect aria-label={t("Starts with")} value={newEmpty ? "empty" : "copy"} onChange={event => setNewEmpty(event.target.value === "empty")}>
              <option value="copy">{t("The filters of “{view}”", { view: view.name })}</option>
              <option value="empty">{t("No filter")}</option>
            </HTMLSelect>}
            <Button intent="primary" className="bp6-popover-dismiss" disabled={busy || !newName.trim()} onClick={create}>{t("Create")}</Button>
          </div>}>
            <Button icon={<AppIcon name="plus" size={15} />} disabled={busy || changed} title={keep}
              onClick={() => setNewName(view ? "" : freeViewName(views?.views ?? [], t("Mine")))}>{t("New view")}</Button>
          </PopoverNext>
          {view && <PopoverNext placement="bottom-start" content={<div className="issue-views-popover">
            <InputGroup size="small" autoFocus maxLength={64} aria-label={t("Name")} value={rename} onChange={event => setRename(event.target.value)}
              onKeyDown={event => { if (event.key === "Enter") renameView(); }} />
            <Button intent="primary" className="bp6-popover-dismiss" disabled={busy || !rename.trim() || rename.trim() === view.name} onClick={renameView}>{t("Rename")}</Button>
          </div>}>
            <Button variant="minimal" icon={<AppIcon name="edit" size={15} />} disabled={busy || changed} title={keep}>{t("Rename")}</Button>
          </PopoverNext>}
          {view && <RemoveButton text name={view.name} disabled={busy || changed} onRemove={removeView} />}
        </div>
      </div>
      <SettingsFileLocations files={files} disabled={busy} />
      {views === null ? <p className="issue-views-empty"><ActivitySpinner size={14} /> {t("Loading…")}</p>
        : !view ? <Callout compact icon={null}>{t("This project has no view yet: the Issues tab lists every item. A new view says which filters it shows.")}</Callout>
        : <section aria-label={view.name}>
          <h2 className="issue-views-heading">{view.name} · {t(viewSourceLabel(view.source))}
            <small>{view.source === "team" ? t("Written in .alta/issues.toml: commit it to share it with your team.") : t("Written in .alta/local/issues.toml, which git ignores.")}</small></h2>
          {filters?.problem && <Callout compact intent="warning">{filters.problem}</Callout>}
          {filters === null ? <p className="issue-views-empty"><ActivitySpinner size={14} /> {t("Loading…")}</p>
            : rows.length === 0 ? <p className="issue-views-empty">{t("This view has no filter yet: it lists every item. Add one below.")}</p>
            : <table className="issue-views-table">
              <thead><tr>
                <th>{t("Active")}</th><th>{t("Name")}</th><th>{t("Field")}</th><th>{t("Field type")}</th><th>{t("Value")}</th>
                <th title={t("The tab offers to change the value for a while, in the line under its toolbar; the view keeps its value.")}>{t("In the tab")}</th>
                <th><span className="sr-only">{t("Remove from this view")}</span></th>
              </tr></thead>
              <tbody>{rows.map(id => {
                const filter = known.find(candidate => candidate.id === id);
                const own = entryFor(entries, id);
                const on = isActive(entries, id);
                const name = trackerText(filter?.trackerLabel || shortName(id), t);
                return <tr key={id} data-off={!on || undefined}>
                  <td><Switch className="issue-views-active" checked={on} disabled={busy} aria-label={t("List by {filter}", { filter: name })}
                    onChange={event => { const checked = event.currentTarget.checked; setEntries(value => switchEntry(value, id, checked)); }} /></td>
                  <td>
                    <InputGroup size="small" aria-label={t("Name of {filter}", { filter: name })} value={own.label ?? ""} placeholder={name} disabled={busy} maxLength={80}
                      onChange={event => change(id, { label: event.target.value.trim() ? event.target.value : null })} />
                    {filter?.problem && <small className="issue-views-problem">{filter.problem}</small>}
                    {!filter && filters !== null && <small className="issue-views-problem">{t("The tracker does not know this field.")}</small>}
                  </td>
                  <td><code className="issue-views-field" title={id}>{shortName(id)}</code></td>
                  <td className="issue-views-type">{filter ? t(fieldTypeLabel(filter.fieldType)) : "…"}</td>
                  <td>{valueSelect(id, filter, own, name, busy || !on)}</td>
                  <td><Switch className="issue-views-pick" checked={own.pick} disabled={busy || !on} aria-label={t("Change {filter} in the tab", { filter: name })}
                    onChange={event => { const checked = event.currentTarget.checked; change(id, { pick: checked }); }} /></td>
                  <td><Button variant="minimal" size="small" icon={<AppIcon name="close" size={14} />} disabled={busy}
                    aria-label={t("Remove {filter} from this view", { filter: name })} title={t("Remove from this view")}
                    onClick={() => setEntries(value => value.filter(entry => entry.id !== id))} /></td>
                </tr>;
              })}</tbody>
            </table>}
          <div className="issue-views-buttons">
            <PopoverNext placement="bottom-start" onOpening={openFields} content={<div className="issue-views-fields">
              <InputGroup size="small" autoFocus leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} placeholder={t("Search the filters")} aria-label={t("Search the filters")}
                value={search} onChange={event => setSearch(event.target.value)} />
              {addableOwn.length + addableFields.length === 0 && fields !== null ? <p className="issue-views-empty">{t("No filter matches.")}</p>
                : <Menu className="issue-views-field-list">
                  {addableOwn.map(filter => <MenuItem key={filter.id} className="bp6-popover-dismiss" text={trackerText(filter.label, t)} label={t("Built in")}
                    onClick={() => change(filter.id, { mode: "on" })} />)}
                  {addableOwn.length > 0 && <MenuDivider title={t("Fields")} />}
                  {fields === null ? <MenuItem disabled text={t("Loading…")} />
                    : addableFields.slice(0, 200).map(field => <MenuItem key={field.id} className="bp6-popover-dismiss" text={field.label}
                      label={`${field.name ?? shortName(field.id)} · ${t(fieldTypeLabel(field.type))}`} onClick={() => change(field.id, { mode: "on" })} />)}
                </Menu>}
            </div>}>
              <Button icon={<AppIcon name="plus" size={15} />} disabled={busy || filters === null}>{t("Add a filter")}</Button>
            </PopoverNext>
            <span className="issue-views-spacer" />
            <Button intent="primary" disabled={busy || !changed} onClick={save}>{t("Save")}</Button>
            <Button disabled={busy || !changed} onClick={() => setEntries(saved)}>{t("Discard")}</Button>
          </div>
        </section>}
    </div>
  </AppWindow>;
}
