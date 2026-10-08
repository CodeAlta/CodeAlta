import { useMemo, useState } from "react";
import { Button, Callout, Checkbox, FormGroup, InputGroup, TextArea } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { brandColor } from "../brands";
import { ProviderColorInput, ProviderIconPicker } from "../ProviderIconFields";
import { useShellLanguage } from "../shellLanguage";
import { SpaceIcon } from "./SpaceViews";
import { maximumSpaceDescription, maximumSpaceName, spaceBrand, spaceColors, spaceNameProblem, spaceTemplates, type Space } from "./spaces";
import type { SpaceOutcome } from "./spacesHub";

/** A project a space can hold, as the dialog and the settings list it. */
export type SpaceProject = Readonly<{ id: string; name: string; path: string; archived: boolean }>;

/** The colors a space is offered, as swatches, with a field for any other color. */
export function SpaceColorChoice({ id, value, disabled, onChange }: { id: string; value: string; disabled?: boolean; onChange: (color: string) => void }) {
  const { t } = useShellLanguage();
  return <div className="space-colors">
    <div className="space-color-swatches" role="group" aria-label={t("Color")}>
      {spaceColors.map(color => <button type="button" key={color} className="space-color" style={{ background: color }} disabled={disabled}
        aria-pressed={value.toLowerCase() === color} aria-label={color} title={color} onClick={() => onChange(color)} />)}
    </div>
    <ProviderColorInput id={id} value={value} disabled={disabled} onChange={onChange} placeholder="#rrggbb" />
  </div>;
}

/**
 * The window that makes a space: its name, which can be taken with an icon and a color from a few usual ones,
 * what it is for, and the projects that start in it. The space is shown once it is made.
 */
export function SpaceDialog({ spaces, projects, selected, create, onCreated, onClose }: {
  spaces: readonly Space[]; projects: readonly SpaceProject[];
  /** The projects that start checked. */
  selected?: readonly string[];
  create: (name: string, description: string | null, icon: string | null, color: string | null, projectIds: readonly string[]) => Promise<SpaceOutcome>;
  onCreated: (space: Space) => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [name, setName] = useState("");
  const [icon, setIcon] = useState("");
  const [color, setColor] = useState("");
  const [description, setDescription] = useState("");
  const [members, setMembers] = useState<ReadonlySet<string>>(() => new Set(selected ?? []));
  const [filter, setFilter] = useState("");
  const [busy, setBusy] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const problem = spaceNameProblem(name, spaces, null);
  const colorValid = !color.trim() || !!brandColor(color);
  const taken = useMemo(() => new Set(spaces.map(space => space.name.toLowerCase())), [spaces]);
  const wanted = filter.trim().toLowerCase();
  const listed = useMemo(() => projects.filter(project => !project.archived && (!wanted || project.name.toLowerCase().includes(wanted) || project.path.toLowerCase().includes(wanted))),
    [projects, wanted]);
  const look = { icon: icon || null, color: brandColor(color) ?? null };
  async function submit() {
    if (busy || problem || !colorValid) return;
    setBusy(true); setFailure(null);
    const outcome = await create(name.trim(), description.trim() || null, icon || null, brandColor(color) ?? null, [...members]);
    setBusy(false);
    if (outcome.ok && outcome.space) { onCreated(outcome.space); return; }
    setFailure(outcome.message ?? t("The space could not be created."));
  }
  return <AppWindow storageKey="codealta.desktop.window.space.v1" className="space-dialog" titleId="space-dialog-title"
    title={<><AppIcon name="space" size={14} /> {t("New space")}</>}
    preferredSize={viewport => ({ width: Math.min(560, viewport.width - 40), height: Math.min(600, viewport.height - 40) })} minimumSize={{ width: 380, height: 360 }}
    onClose={onClose} closeLabel={t("Close")} onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); onClose(); }
      else if (event.key === "Enter" && (event.ctrlKey || event.target instanceof HTMLInputElement && event.target.id === "space-name") && !event.nativeEvent.isComposing) {
        event.preventDefault(); void submit();
      }
    }}
    onOpened={() => document.getElementById("space-name")?.focus()}>
    <div className="space-templates" role="group" aria-label={t("Start from")}>
      {spaceTemplates.filter(template => !taken.has(template.name.toLowerCase())).map(template => <Button key={template.name} size="small" variant="outlined"
        active={name === template.name && icon === template.icon} disabled={busy}
        icon={<SpaceIcon space={template} size={14} />} text={template.name}
        onClick={() => { setName(template.name); setIcon(template.icon); setColor(template.color); }} />)}
    </div>
    <FormGroup label={t("Name")} labelFor="space-name" intent={problem === "taken" || problem === "long" ? "danger" : undefined}
      helperText={problem === "taken" ? t("A space already has this name.") : problem === "long" ? t("A name has at most {count} characters.", { count: maximumSpaceName }) : undefined}>
      <InputGroup id="space-name" value={name} maxLength={maximumSpaceName + 16} disabled={busy} autoComplete="off" spellCheck={false}
        intent={problem === "taken" || problem === "long" ? "danger" : undefined}
        leftElement={<span className="space-name-icon"><SpaceIcon space={look} size={16} /></span>} onChange={event => setName(event.target.value)} />
    </FormGroup>
    <div className="space-look">
      <FormGroup label={t("Icon")} labelFor="space-icon">
        <ProviderIconPicker id="space-icon" value={icon} automatic={{ icon: null }} shown={spaceBrand(look)} disabled={busy} onChange={setIcon} fallback="space" symbolsFirst />
      </FormGroup>
      <FormGroup label={t("Color")} labelFor="space-color"><SpaceColorChoice id="space-color" value={color} disabled={busy} onChange={setColor} /></FormGroup>
    </div>
    <FormGroup label={t("What it is for")} labelFor="space-description">
      <TextArea id="space-description" fill value={description} maxLength={maximumSpaceDescription} disabled={busy} rows={2}
        onChange={event => setDescription(event.target.value)} />
    </FormGroup>
    <FormGroup className="space-dialog-projects" label={members.size ? t("Projects ({count})", { count: members.size }) : t("Projects")} labelFor="space-project-filter">
      {projects.length > 8 && <InputGroup id="space-project-filter" type="search" value={filter} placeholder={t("Search")} aria-label={t("Search")}
        leftIcon={<AppIcon name="search" size={15} className="bp6-icon" />} onChange={event => setFilter(event.target.value)} />}
      <div className="space-project-checks">
        {listed.map(project => <Checkbox key={project.id} checked={members.has(project.id)} disabled={busy} title={project.path}
          onChange={event => setMembers(current => { const next = new Set(current); if (event.currentTarget.checked) next.add(project.id); else next.delete(project.id); return next; })}>
          <span className="space-project-name">{project.name}</span><span className="space-project-path">{project.path}</span></Checkbox>)}
        {listed.length === 0 && <p className="bp6-text-muted">{t(projects.length ? "No project matches." : "No project yet.")}</p>}
      </div>
    </FormGroup>
    {failure && <Callout intent="danger" compact role="alert">{failure}</Callout>}
    <footer><span /><span>
      <Button disabled={busy} onClick={onClose}>{t("Cancel")}</Button>
      <Button intent="primary" loading={busy} disabled={!!problem || !colorValid} onClick={() => void submit()}>{t("Create space")}</Button>
    </span></footer>
  </AppWindow>;
}
