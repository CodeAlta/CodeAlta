import { useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import { Button, Card, Checkbox, HTMLSelect, InputGroup, TextArea } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import { ProviderIconPicker } from "../ProviderIconFields";
import { SettingsField as Field } from "../SettingsField";
import { SettingsPage } from "../SettingsPage";
import { settingsFailure, type SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceProject } from "../spaces/SpaceDialog";
import { SpaceIcon } from "../spaces/SpaceViews";
import { spaceColors, type Space } from "../spaces/spaces";
import { canMoveLabel, labelBrand, labelDepth, labelNameProblem, labelPath, maximumLabelDepth, maximumLabelDescription, maximumLabelName, type Label } from "./labels";
import { labelMoveTarget, useRailDrag, type RailDrag, type RailDrop } from "./labelDrag";
import type { LabelOutcome, LabelsHub } from "./labelsHub";
import { LabelBadge, LabelDragGhost, LabelFormPopover, type LabelLook } from "./LabelViews";

/** The place of a label once moved one step: up or down among its siblings, in under the one above it, or out beside its parent. Null when it cannot. */
export function steppedPlace(labels: readonly Label[], id: string, step: "up" | "down" | "in" | "out"): Readonly<{ parent: string | null; index: number }> | null {
  const label = labels.find(value => value.id === id);
  if (!label) return null;
  const siblings = labels.filter(value => value.parent === label.parent);
  const at = siblings.indexOf(label);
  if (step === "up") return at > 0 ? { parent: label.parent, index: at - 1 } : null;
  if (step === "down") return at < siblings.length - 1 ? { parent: label.parent, index: at + 1 } : null;
  if (step === "in") {
    const above = siblings[at - 1];
    return above && canMoveLabel(id, above.id, labels) ? { parent: above.id, index: -1 } : null;
  }
  if (!label.parent) return null;
  const parent = labels.find(value => value.id === label.parent);
  if (!parent) return null;
  const outer = labels.filter(value => value.parent === parent.parent);
  return { parent: parent.parent, index: outer.indexOf(parent) + 1 };
}

/**
 * The page of Settings that organizes the labels: their tree, where a label is dragged before, after or under
 * another one (or moved with the buttons of its row), and the form of the label selected: its name, icon, color,
 * space, description and projects.
 */
export function LabelSettings({ hub, spaces, projects, shownId, canEdit }: {
  hub: LabelsHub; spaces: readonly Space[];
  /** Every project of the catalog. */
  projects: readonly SpaceProject[];
  /** The space the window shows: a new label goes there, or in every space from the default one. */
  shownId: string;
  canEdit: boolean;
}) {
  const { t } = useShellLanguage();
  const state = useSyncExternalStore(hub.subscribe, hub.getSnapshot, hub.getSnapshot);
  const labels = state.labels;
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [spaceFilter, setSpaceFilter] = useState("");
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const label = labels.find(value => value.id === selected) ?? null;
  // The labels listed: every one, or those a space shows (its own and those of every space).
  const listed = spaceFilter ? labels.filter(value => value.space === null || value.space === spaceFilter) : labels;

  async function run(change: () => Promise<LabelOutcome>) {
    setBusy(true); setNotice(null);
    const outcome = await change();
    if (!alive.current) return outcome;
    setBusy(false);
    setNotice(outcome.ok ? null : settingsFailure(outcome.status, outcome.message) ?? { key: "The operation did not complete.", intent: "danger" });
    return outcome;
  }
  const move = (id: string, place: Readonly<{ parent: string | null; index: number }> | null) => { if (place) void run(() => hub.move(id, place.parent, place.index)); };

  const drag = useRailDrag((dragged: RailDrag, drop: RailDrop) => {
    if (!canEdit || busy || dragged.kind !== "label") return false;
    const target = labelMoveTarget(labels, dragged.labelId, drop);
    return !!target && canMoveLabel(dragged.labelId, target.parent, labels);
  }, (dragged, drop) => { if (dragged.kind === "label") move(dragged.labelId, labelMoveTarget(labels, dragged.labelId, drop)); });

  async function create(look: LabelLook) {
    const outcome = await run(() => hub.create(look.name, { icon: look.icon || null, color: look.color || null,
      space: spaceFilter || (shownId !== "default" ? shownId : null) }));
    if (outcome.ok) { setCreating(false); if (outcome.label) setSelected(outcome.label.id); }
  }
  const spaceOf = (id: string | null) => id === null ? null : spaces.find(space => space.id === id) ?? null;
  return <SettingsPage className="label-settings" label={t("Labels")} group="Spaces" title="Labels"
    description="Give projects labels to find them together: the Explorer lists the projects of a space under their labels."
    notice={notice} loading={!state.loaded} busy={busy} onReload={() => void hub.refresh()}
    actions={<span className="label-new-anchor">
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!canEdit || !state.available} onClick={() => setCreating(true)}>{t("New label")}</Button>
      {creating && <LabelFormPopover heading={t("New label")} submitLabel={t("Create")} labels={labels} parent={null} ownId={null}
        initial={{ name: "", icon: "", color: "" }} busy={busy} error={null} onSubmit={look => void create(look)} onCancel={() => setCreating(false)} />}
    </span>}>
    {state.loaded && !state.available ? <p className="bp6-text-muted">{t("Labels need CodeAlta Desktop with its own catalog.")}</p> : <div className="label-board">
      <Card className="label-tree-card">
        {spaces.length > 1 && <label className="label-space-filter"><span>{t("Space")}</span>
          <HTMLSelect value={spaceFilter} onChange={event => setSpaceFilter(event.currentTarget.value)}>
            <option value="">{t("Every label")}</option>
            {spaces.map(space => <option key={space.id} value={space.id}>{space.name}</option>)}
          </HTMLSelect></label>}
        <ul className="label-tree" aria-label={t("Labels")}>
          {listed.map(item => {
            const depth = labelDepth(item, labels);
            const space = spaceOf(item.space);
            const over = drag.view?.over?.labelId === item.id ? drag.view.over.place : undefined;
            return <li key={item.id} className="label-tree-row" data-label-drop={item.id} data-drop={over} aria-current={item.id === selected ? "true" : undefined}
              style={{ paddingInlineStart: 6 + (depth - 1) * 18 }} {...drag.source(canEdit ? { kind: "label", labelId: item.id } : null, item.name)}
              onClick={event => { if (!(event.target as HTMLElement).closest("button")) setSelected(item.id); }}>
              {canEdit && <AppIcon name="grip" size={13} className="label-grip" />}
              <button type="button" className="label-tree-open" aria-pressed={item.id === selected} title={labelPath(item, labels)} onClick={() => setSelected(item.id)}>
                <LabelBadge label={item} />
              </button>
              {item.favorite && <AppIcon name="star" size={12} className="label-tree-favorite" aria-label={t("Favorite")} />}
              <span className="label-tree-space" title={space ? space.name : undefined}>{item.parent === null && space && <SpaceIcon space={space} size={13} />}</span>
              <span className="label-tree-count">{item.projectIds.length}</span>
              <span className="label-tree-tools">
                <Button variant="minimal" size="small" icon={<AppIcon name="arrowUp" size={13} />} disabled={!canEdit || busy || !steppedPlace(labels, item.id, "up")}
                  aria-label={t("Move {name} up", { name: item.name })} title={t("Move up")} onClick={() => move(item.id, steppedPlace(labels, item.id, "up"))} />
                <Button variant="minimal" size="small" icon={<AppIcon name="arrowDown" size={13} />} disabled={!canEdit || busy || !steppedPlace(labels, item.id, "down")}
                  aria-label={t("Move {name} down", { name: item.name })} title={t("Move down")} onClick={() => move(item.id, steppedPlace(labels, item.id, "down"))} />
                <Button variant="minimal" size="small" icon={<AppIcon name="arrowRight" size={13} />} disabled={!canEdit || busy || !steppedPlace(labels, item.id, "in")}
                  aria-label={t("Move {name} under the label above", { name: item.name })} title={t("Under the label above")} onClick={() => move(item.id, steppedPlace(labels, item.id, "in"))} />
                <Button variant="minimal" size="small" icon={<AppIcon name="arrowLeft" size={13} />} disabled={!canEdit || busy || !steppedPlace(labels, item.id, "out")}
                  aria-label={t("Move {name} out of its parent", { name: item.name })} title={t("Out of its parent")} onClick={() => move(item.id, steppedPlace(labels, item.id, "out"))} />
              </span>
            </li>;
          })}
          {listed.length === 0 && <li className="label-tree-empty">{t("No label yet.")}</li>}
        </ul>
        <p className="settings-field-help">{t("Drag a label onto another one to put it under it, or above or below it to move it.")}</p>
      </Card>
      {label ? <LabelForm key={label.id} label={label} labels={labels} spaces={spaces} projects={projects} canEdit={canEdit} busy={busy} run={run}
        hub={hub} onDeleted={() => setSelected(null)} /> : <Card className="label-form-card label-form-empty"><p className="bp6-text-muted">{t("Select a label to change it.")}</p></Card>}
      <LabelDragGhost view={drag.view} />
    </div>}
  </SettingsPage>;
}

// The form of a label: each value is saved when it changes.
function LabelForm({ label, labels, spaces, projects, canEdit, busy, run, hub, onDeleted }: {
  label: Label; labels: readonly Label[]; spaces: readonly Space[]; projects: readonly SpaceProject[]; canEdit: boolean; busy: boolean;
  run: (change: () => Promise<LabelOutcome>) => Promise<LabelOutcome>; hub: LabelsHub; onDeleted: () => void;
}) {
  const { t } = useShellLanguage();
  const [name, setName] = useState(label.name);
  const [description, setDescription] = useState(label.description ?? "");
  const [filter, setFilter] = useState("");
  const [deleting, setDeleting] = useState(false);
  useEffect(() => setName(label.name), [label.name]);
  useEffect(() => setDescription(label.description ?? ""), [label.description]);
  const disabled = !canEdit || busy;
  const problem = labelNameProblem(name, labels, label.parent, label.id);
  const id = `label-${label.id}`;
  const members = useMemo(() => new Set(label.projectIds), [label.projectIds]);
  const wanted = filter.trim().toLowerCase();
  const listed = projects.filter(project => (!project.archived || members.has(project.id))
    && (!wanted || project.name.toLowerCase().includes(wanted) || project.path.toLowerCase().includes(wanted)));
  const save = (edit: Parameters<LabelsHub["update"]>[1]) => run(() => hub.update(label.id, edit));
  const commitName = () => { if (problem || name.trim() === label.name) setName(label.name); else void save({ name: name.trim() }); };
  return <Card className="label-form-card" aria-labelledby={`${id}-title`}>
    <h2 id={`${id}-title`}><LabelBadge label={label} />{label.parent && <span className="label-form-path">{labelPath(label, labels)}</span>}</h2>
    <Field label={t("Name")} htmlFor={`${id}-name`}
      notice={problem && name.trim() !== label.name && <p className="settings-field-help" role="alert">{problem === "taken"
        ? t("A label beside it already has this name.") : problem === "long" ? t("A name has at most {count} characters.", { count: maximumLabelName }) : null}</p>}>
      <InputGroup id={`${id}-name`} value={name} disabled={disabled} maxLength={maximumLabelName + 16} spellCheck={false} autoComplete="off"
        intent={problem && name.trim() !== label.name ? "danger" : undefined} onChange={event => setName(event.target.value)} onBlur={commitName}
        onKeyDown={event => { if (event.key === "Enter") event.currentTarget.blur(); else if (event.key === "Escape") { event.stopPropagation(); setName(label.name); } }} />
    </Field>
    <Field label={t("Icon")} htmlFor={`${id}-icon`}>
      <ProviderIconPicker id={`${id}-icon`} value={label.icon ?? ""} automatic={{ icon: null }} shown={labelBrand(label)} disabled={disabled}
        fallback="tag" symbolsFirst onChange={icon => void save({ icon })} />
    </Field>
    <Field label={t("Color")}>
      <span className="space-color-swatches" role="group" aria-label={t("Color")}>
        {spaceColors.map(color => <button type="button" key={color} className="space-color" style={{ background: color }} disabled={disabled}
          aria-pressed={label.color?.toLowerCase() === color} aria-label={color} title={color}
          onClick={() => void save({ color: label.color?.toLowerCase() === color ? "" : color })} />)}
      </span>
    </Field>
    <Field label={t("Favorite")} htmlFor={`${id}-favorite`}>
      <Checkbox id={`${id}-favorite`} className="settings-checkbox" checked={label.favorite} disabled={disabled} label={t("Keep it on top in the Explorer")}
        onChange={event => void save({ favorite: event.currentTarget.checked })} />
    </Field>
    <Field label={t("Space")} htmlFor={`${id}-space`}>
      {label.parent === null
        ? <HTMLSelect id={`${id}-space`} value={label.space ?? ""} disabled={disabled} onChange={event => void save({ space: event.currentTarget.value })}>
          <option value="">{t("Every space")}</option>
          {spaces.map(space => <option key={space.id} value={space.id}>{space.name}</option>)}
        </HTMLSelect>
        : <span className="bp6-text-muted">{t("The space of the label above it")}</span>}
    </Field>
    <div className="settings-field">
      <label htmlFor={`${id}-description`}>{t("What it stands for")}</label>
      <span />
      <TextArea id={`${id}-description`} className="settings-field-wide" fill value={description} disabled={disabled} rows={2} maxLength={maximumLabelDescription}
        onChange={event => setDescription(event.target.value)} onBlur={() => { if (description.trim() !== (label.description ?? "")) void save({ description: description.trim() }); }} />
      <p className="settings-field-help">{t("The agents read it.")}</p>
    </div>
    <h3 className="label-form-projects">{t("Projects")}<span className="space-section-count">{label.projectIds.length}</span></h3>
    {projects.length > 8 && <InputGroup type="search" size="small" value={filter} placeholder={t("Search")} aria-label={t("Search")}
      leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
    <div className="space-project-checks" role="group" aria-label={t("Projects of {name}", { name: label.name })}>
      {listed.map(project => <Checkbox key={project.id} checked={members.has(project.id)} disabled={disabled} title={project.path}
        onChange={event => { const add = event.currentTarget.checked; void run(() => hub.assign(project.id, add ? [label.id] : [], add ? [] : [label.id])); }}>
        <span className="space-project-name">{project.name}</span><span className="space-project-path">{project.path}</span></Checkbox>)}
      {listed.length === 0 && <p className="bp6-text-muted">{t(projects.length ? "No project matches." : "No project yet.")}</p>}
    </div>
    <footer className="label-form-footer"><span className="label-delete-anchor">
      <Button intent="danger" variant="outlined" icon={<AppIcon name="trash" size={14} />} disabled={disabled} onClick={() => setDeleting(true)}>{t("Delete label…")}</Button>
      {deleting && <ConfirmPopover always placement="top-start" title={t("Delete this label?")} subject={labelPath(label, labels)} intent="danger" confirmLabel={t("Delete")}
        detail={t("Its projects lose it and stay. The labels under it take its place.")} busy={busy}
        onConfirm={() => void run(() => hub.remove(label.id)).then(outcome => { setDeleting(false); if (outcome.ok) onDeleted(); })} onCancel={() => setDeleting(false)} />}
    </span>
    {labelDepth(label, labels) >= maximumLabelDepth && <span className="bp6-text-muted">{t("Labels go {count} levels deep at most.", { count: maximumLabelDepth })}</span>}
    </footer>
  </Card>;
}
