// The tags the user organizes tasks with: their chips, the filter of the list, the field of a task, and the dialog of a new tag.
import { useState, type CSSProperties } from "react";
import { Button, Dialog, DialogBody, DialogFooter, FormGroup, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext } from "@blueprintjs/core";
import type { WorkItemTagRow } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { tagColor, tagNameOf, type WorkItem } from "./workItems";
import type { WorkItemsHub } from "./workItemsHub";

/** The most tags a task has. */
export const maximumTaskTags = 10;
/** The most characters of the description of a tag. */
export const maximumTagDescription = 200;

const tagStyle = (name: string) => ({ "--tag-color": tagColor(name) }) as CSSProperties;

/** One tag, in the color of its name; with `onRemove`, a button removes it. */
export function TagChip({ name, title, onRemove }: { name: string; title?: string | null; onRemove?: () => void }) {
  const { t } = useShellLanguage();
  return <span className="work-tag" style={tagStyle(name)} title={title ?? undefined}>{name}
    {onRemove && <button type="button" className="work-tag-remove" aria-label={t("Remove the tag {name}", { name })} title={t("Remove the tag {name}", { name })}
      onClick={event => { event.stopPropagation(); onRemove(); }}><AppIcon name="close" size={10} /></button>}</span>;
}

/** The tags of a task in a row of the list: the first ones, then how many more. */
export function TagChips({ tags, max = 3 }: { tags: readonly string[] | null | undefined; max?: number }) {
  if (!tags?.length) return null;
  const more = tags.length - max;
  return <span className="work-tag-chips">{tags.slice(0, max).map(tag => <TagChip key={tag} name={tag} />)}
    {more > 0 && <span className="work-tag-more" title={tags.slice(max).join(", ")}>+{more}</span>}</span>;
}

/** The Tags filter of the list: the tasks that have any of the chosen tags. */
export function TagFilter({ tags, chosen, onChange }: { tags: readonly WorkItemTagRow[]; chosen: ReadonlySet<string>; onChange: (chosen: ReadonlySet<string>) => void }) {
  const { t } = useShellLanguage();
  if (tags.length === 0 && chosen.size === 0) return null;
  const toggle = (name: string) => { const next = new Set(chosen); if (!next.delete(name)) next.add(name); onChange(next); };
  return <PopoverNext placement="bottom-start" content={<Menu className="work-tag-menu" aria-label={t("Tags")}>
    {tags.map(tag => <MenuItem key={tag.name} roleStructure="listoption" selected={chosen.has(tag.name)} shouldDismissPopover={false}
      text={<span className="work-tag-option"><TagChip name={tag.name} />{tag.description && <small>{tag.description}</small>}</span>}
      label={String(tag.openTasks)} onClick={() => toggle(tag.name)} />)}
    {chosen.size > 0 && <><MenuDivider /><MenuItem icon={<AppIcon name="close" size={14} />} text={t("Show every tag")} onClick={() => onChange(new Set())} /></>}
  </Menu>}>
    <Button size="small" variant={chosen.size ? "outlined" : "minimal"} className="work-tag-filter" icon={<AppIcon name="tag" size={14} />}
      endIcon={<AppIcon name="chevronDown" size={12} />} aria-label={t("Filter by tag")} title={t("Show the tasks that have one of the chosen tags")}>
      {chosen.size ? [...chosen].join(", ") : t("Tags")}</Button>
  </PopoverNext>;
}

/**
 * The tags of a task in its detail: each can be removed, and **Add tag** offers the tags of its project, or a new one
 * of the typed name.
 */
export function TaskTagField({ hub, item, tags, disabled }: { hub: WorkItemsHub; item: WorkItem; tags: readonly WorkItemTagRow[]; disabled: boolean }) {
  const { t } = useShellLanguage();
  const [typed, setTyped] = useState("");
  const [open, setOpen] = useState(false);
  const [creating, setCreating] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const own = item.tags ?? [];
  const save = async (next: readonly string[]) => { setFailed(!(await hub.tagTask(item, next)).ok); };
  const add = (name: string) => { setOpen(false); setTyped(""); if (!own.includes(name)) void save([...own, name]); };
  const words = typed.trim().toLowerCase().replace(/^#/, "");
  const offered = tags.filter(tag => !own.includes(tag.name) && (!words || tag.name.includes(words) || (tag.description ?? "").toLowerCase().includes(words)));
  const name = tagNameOf(typed);
  const fresh = name !== null && !tags.some(tag => tag.name === name);
  const full = own.length >= maximumTaskTags;
  return <div className="work-task-tags" role="group" aria-label={t("Tags")}>
    {own.map(tag => <TagChip key={tag} name={tag} title={tags.find(known => known.name === tag)?.description}
      onRemove={disabled ? undefined : () => void save(own.filter(other => other !== tag))} />)}
    {!full && <PopoverNext isOpen={open} onInteraction={setOpen} placement="bottom-start" autoFocus={false} content={<div className="work-tag-picker">
      <InputGroup size="small" autoFocus value={typed} placeholder={t("Find or create a tag")} aria-label={t("Find or create a tag")}
        onChange={event => setTyped(event.target.value)}
        onKeyDown={event => {
          if (event.key !== "Enter") return;
          event.preventDefault();
          if (offered[0] && (!fresh || offered[0].name === name)) add(offered[0].name);
          else if (fresh) { setOpen(false); setCreating(name); }
        }} />
      <Menu aria-label={t("Tags of the project")}>
        {offered.map(tag => <MenuItem key={tag.name} text={<span className="work-tag-option"><TagChip name={tag.name} />{tag.description && <small>{tag.description}</small>}</span>}
          onClick={() => add(tag.name)} />)}
        {fresh && <MenuItem icon={<AppIcon name="plus" size={14} />} text={t("New tag \"{name}\"…", { name: name! })} onClick={() => { setOpen(false); setCreating(name); }} />}
        {!offered.length && !fresh && <MenuItem disabled text={t(tags.length ? "No tag matches. A name is lower case letters, digits and hyphens." : "Type the name of a new tag.")} />}
      </Menu>
    </div>}>
      <Button size="small" variant="minimal" className="work-tag-add" disabled={disabled} icon={<AppIcon name="plus" size={12} />}>{t("Add tag")}</Button>
    </PopoverNext>}
    {failed && <span className="work-tag-failed" role="alert">{t("The change was not made.")}</span>}
    {creating !== null && <NewTagDialog hub={hub} projectId={item.projectId} name={creating} onClose={() => setCreating(null)} onCreated={created => add(created)} />}
  </div>;
}

/** The dialog of a new tag of a project: its name and what it is for, which agents match tasks against. */
export function NewTagDialog({ hub, projectId, name: initialName, description: initialDescription = "", onClose, onCreated }: {
  hub: WorkItemsHub; projectId: string; name: string; description?: string; onClose: () => void; onCreated?: (name: string) => void;
}) {
  const { t } = useShellLanguage();
  const [name, setName] = useState(initialName);
  const [description, setDescription] = useState(initialDescription);
  const [status, setStatus] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const valid = tagNameOf(name);
  const create = async () => {
    if (!valid) return;
    setSaving(true);
    const result = await hub.tag(projectId, "set", valid, description.trim() || null);
    setSaving(false);
    if (result !== "ok") { setStatus(result); return; }
    onCreated?.(valid);
    onClose();
  };
  return <Dialog isOpen className="work-tag-dialog" title={t("New tag")} onClose={onClose}>
    <DialogBody>
      <FormGroup label={t("Name")} labelFor="work-tag-name" helperText={t("Lower case letters, digits and hyphens, such as flaky-tests.")}
        intent={name && !valid ? "danger" : "none"}>
        <InputGroup id="work-tag-name" autoFocus value={name} maxLength={33} intent={name && !valid ? "danger" : "none"} onChange={event => setName(event.target.value)}
          onKeyDown={event => { if (event.key === "Enter") void create(); }} />
      </FormGroup>
      <FormGroup label={t("What it is for")} labelFor="work-tag-description" helperText={t("Optional. Agents give a new task the tags that fit it: by this line, or by the name when it is empty.")}>
        <InputGroup id="work-tag-description" value={description} maxLength={maximumTagDescription}
          onChange={event => setDescription(event.target.value)} onKeyDown={event => { if (event.key === "Enter") void create(); }} />
      </FormGroup>
      {status && <p className="work-tag-failed" role="alert">{t("The change was not made.")}</p>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button onClick={onClose}>{t("Cancel")}</Button>
      <Button intent="primary" disabled={!valid || saving} loading={saving} onClick={() => void create()}>{t("Create tag")}</Button>
    </>} />
  </Dialog>;
}
