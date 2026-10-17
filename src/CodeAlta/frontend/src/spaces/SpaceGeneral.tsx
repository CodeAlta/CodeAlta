import { useEffect, useMemo, useState } from "react";
import { Card, Checkbox, InputGroup, Switch, TextArea } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { ProviderIconPicker } from "../ProviderIconFields";
import { SettingsField as Field } from "../SettingsField";
import { isExcluded, searchEnabled } from "../search/localSearchHub";
import { useLocalSearch } from "../search/localSearchContext";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceProject } from "./SpaceDialog";
import type { SpaceSectionProps } from "./spaceSection";
import { maximumSpaceDescription, maximumSpaceName, spaceBrand, spaceColors, spaceNameProblem } from "./spaces";

/**
 * The section "General" of the page of a space: its name, its icon, its color and what it is for, each saved
 * when it changes.
 */
export function SpaceGeneral({ space, spaces, hub, canEdit, busy, run }: SpaceSectionProps) {
  const { t } = useShellLanguage();
  const [name, setName] = useState(space.name);
  const [description, setDescription] = useState(space.description ?? "");
  useEffect(() => setName(space.name), [space.name]);
  useEffect(() => setDescription(space.description ?? ""), [space.description]);
  const disabled = !canEdit || busy;
  const problem = spaceNameProblem(name, spaces, space.id);
  const save = (edit: Readonly<{ name?: string; description?: string; icon?: string; color?: string }>) => run(() => hub.update(space.id, edit));
  const commitName = () => { if (problem || name.trim() === space.name) setName(space.name); else void save({ name: name.trim() }); };
  const commitDescription = () => { if (description.trim() !== (space.description ?? "")) void save({ description: description.trim() }); };
  const id = `space-${space.id}`;
  // What search keeps of the space: everything, or nothing of its sessions and projects.
  const search = useLocalSearch();
  const [searchSaving, setSearchSaving] = useState(false);
  const searched = !isExcluded(search.state, "space", space.id);
  return <Card className="appearance-settings space-section" aria-labelledby={`${id}-general`}>
    <h2 id={`${id}-general`}>{t("General")}</h2>
    <Field label={t("Name")} htmlFor={`${id}-name`}
      notice={problem && name.trim() !== space.name && <p className="settings-field-help" role="alert">{problem === "taken"
        ? t("A space already has this name.") : problem === "long" ? t("A name has at most {count} characters.", { count: maximumSpaceName }) : null}</p>}>
      <InputGroup id={`${id}-name`} className="space-name" value={name} disabled={disabled} maxLength={maximumSpaceName + 16} spellCheck={false} autoComplete="off"
        intent={problem && name.trim() !== space.name ? "danger" : undefined} onChange={event => setName(event.target.value)} onBlur={commitName}
        onKeyDown={event => { if (event.key === "Enter") event.currentTarget.blur(); else if (event.key === "Escape") { event.stopPropagation(); setName(space.name); } }} />
    </Field>
    <Field label={t("Icon")} htmlFor={`${id}-icon`}>
      <ProviderIconPicker id={`${id}-icon`} value={space.icon ?? ""} automatic={{ icon: null }} shown={spaceBrand(space)} disabled={disabled}
        fallback="space" symbolsFirst onChange={icon => void save({ icon })} />
    </Field>
    <Field label={t("Color")}>
      <span className="space-color-swatches" role="group" aria-label={t("Color")}>
        {spaceColors.map(color => <button type="button" key={color} className="space-color" style={{ background: color }} disabled={disabled}
          aria-pressed={space.color?.toLowerCase() === color} aria-label={color} title={color}
          onClick={() => void save({ color: space.color?.toLowerCase() === color ? "" : color })} />)}
      </span>
    </Field>
    <div className="settings-field">
      <label htmlFor={`${id}-description`}>{t("What it is for")}</label>
      <span />
      <TextArea id={`${id}-description`} className="space-description settings-field-wide" fill value={description} disabled={disabled} rows={2}
        maxLength={maximumSpaceDescription} onChange={event => setDescription(event.target.value)} onBlur={commitDescription} />
      <p className="settings-field-help">{t("The agents of the space read it.")}</p>
    </div>
    {search.hub && searchEnabled(search.state) && <Field label={t("Search this space")} htmlFor={`${id}-search`}
      notice={<p className="settings-field-help">{t("Its sessions, notes, plans and project skills are indexed for the search window and for the agents' alta search. Off keeps all of them out of the index.")}</p>}>
      <Switch id={`${id}-search`} className="settings-checkbox" checked={searched} disabled={searchSaving}
        onChange={event => { const value = event.currentTarget.checked; setSearchSaving(true);
          void search.hub!.setExcluded("space", space.id, !value).finally(() => setSearchSaving(false)); }} />
    </Field>}
  </Card>;
}

/**
 * The section "Projects" of the page of a space: the projects it holds, checked in the list of every project and
 * saved at once. The default space holds every project.
 */
export function SpaceProjects({ space, hub, canEdit, busy, run, projects }: SpaceSectionProps & Readonly<{ projects: readonly SpaceProject[] }>) {
  const { t } = useShellLanguage();
  const [filter, setFilter] = useState("");
  const members = useMemo(() => new Set(space.projectIds), [space.projectIds]);
  const wanted = filter.trim().toLowerCase();
  const listed = useMemo(() => projects.filter(project => (!project.archived || members.has(project.id))
    && (!wanted || project.name.toLowerCase().includes(wanted) || project.path.toLowerCase().includes(wanted))), [projects, members, wanted]);
  const count = space.isDefault ? projects.length : space.projectIds.length;
  const id = `space-${space.id}`;
  return <Card className="appearance-settings space-section" aria-labelledby={`${id}-projects`}>
    <h2 id={`${id}-projects`}>{t("Projects")}<span className="space-section-count">{count}</span></h2>
    {space.isDefault
      ? <p className="settings-field-help">{t("Every project is in this space.")}</p>
      : <>
        {projects.length > 8 && <InputGroup type="search" size="small" value={filter} placeholder={t("Search")} aria-label={t("Search")}
          leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
        <div className="space-project-checks" role="group" aria-label={t("Projects of {name}", { name: space.name })}>
          {listed.map(project => <Checkbox key={project.id} checked={members.has(project.id)} disabled={!canEdit || busy} title={project.path}
            onChange={event => { const join = event.currentTarget.checked; void run(() => hub.assign(project.id, join ? [space.id] : [], join ? [] : [space.id])); }}>
            <span className="space-project-name">{project.name}</span><span className="space-project-path">{project.path}</span></Checkbox>)}
          {listed.length === 0 && <p className="bp6-text-muted">{t(projects.length ? "No project matches." : "No project yet.")}</p>}
        </div>
      </>}
  </Card>;
}
