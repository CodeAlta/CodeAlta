// The tags the user organizes tasks with: their chips, the filter of the list, the field of a task, and the dialog of a new tag.
import { useRef, useState, type CSSProperties, type KeyboardEvent } from "react";
import { Alert, Button, Callout, Checkbox, Dialog, DialogBody, DialogFooter, FormGroup, HTMLSelect, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext } from "@blueprintjs/core";
import type { WorkItemTagProposalRow, WorkItemTagRow } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { tagColor, tagNameOf, type WorkItem } from "./workItems";
import type { WorkItemsHub, WorkTagAction } from "./workItemsHub";

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
export function TagFilter({ tags, chosen, onChange, onManage }: { tags: readonly WorkItemTagRow[]; chosen: ReadonlySet<string>; onChange: (chosen: ReadonlySet<string>) => void;
  /** Opens the tag manager. */ onManage: () => void }) {
  const { t } = useShellLanguage();
  const toggle = (name: string) => { const next = new Set(chosen); if (!next.delete(name)) next.add(name); onChange(next); };
  return <PopoverNext placement="bottom-start" content={<Menu className="work-tag-menu" aria-label={t("Tags")}>
    {tags.map(tag => <MenuItem key={tag.name} roleStructure="listoption" selected={chosen.has(tag.name)} shouldDismissPopover={false}
      text={<span className="work-tag-option"><TagChip name={tag.name} />{tag.description && <small>{tag.description}</small>}</span>}
      label={String(tag.openTasks)} onClick={() => toggle(tag.name)} />)}
    {chosen.size > 0 && <><MenuDivider /><MenuItem icon={<AppIcon name="close" size={14} />} text={t("Show every tag")} onClick={() => onChange(new Set())} /></>}
    {tags.length > 0 && <MenuDivider />}
    <MenuItem icon={<AppIcon name="settings" size={14} />} text={t("Manage tags…")} onClick={onManage} />
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
  // Each save writes the whole list, so saves run in turn, and the field shows what the last one writes until they are all back.
  const [saving, setSaving] = useState<readonly string[] | null>(null);
  const saves = useRef({ last: Promise.resolve(), running: 0 });
  const own = saving ?? item.tags ?? [];
  const save = (next: readonly string[]) => {
    setSaving(next);
    const queue = saves.current;
    queue.running++;
    queue.last = queue.last.then(async () => {
      setFailed(!(await hub.tagTask(item, next)).ok);
      if (--queue.running === 0) setSaving(null);
    });
  };
  // The picker stays open for the next tag, until the task has all it can have.
  const add = (name: string) => {
    setTyped("");
    if (own.includes(name)) return;
    if (own.length + 1 >= maximumTaskTags) setOpen(false);
    save([...own, name]);
  };
  const words = typed.trim().toLowerCase().replace(/^#/, "");
  const offered = tags.filter(tag => !own.includes(tag.name) && (!words || tag.name.includes(words) || (tag.description ?? "").toLowerCase().includes(words)));
  const name = tagNameOf(typed);
  const fresh = name !== null && !tags.some(tag => tag.name === name);
  const full = own.length >= maximumTaskTags;
  return <div className="work-task-tags" role="group" aria-label={t("Tags")}>
    {own.map(tag => <TagChip key={tag} name={tag} title={tags.find(known => known.name === tag)?.description}
      onRemove={disabled ? undefined : () => save(own.filter(other => other !== tag))} />)}
    {/* The backdrop takes the click that closes the picker, which would otherwise reach what is under it, such as the buttons of the task. */}
    {!full && <PopoverNext isOpen={open} onInteraction={setOpen} placement="bottom-start" autoFocus={false} hasBackdrop content={<div className="work-tag-picker">
      <InputGroup size="small" autoFocus value={typed} placeholder={t("Find or create a tag")} aria-label={t("Find or create a tag")}
        onChange={event => setTyped(event.target.value)}
        onKeyDown={event => {
          if (event.key !== "Enter") return;
          event.preventDefault();
          // The first tag that matches; a new one only when none does.
          if (offered[0]) add(offered[0].name);
          else if (fresh) { setOpen(false); setCreating(name); }
        }} />
      <Menu aria-label={t("Tags of the project")}>
        {offered.map(tag => <MenuItem key={tag.name} text={<span className="work-tag-option"><TagChip name={tag.name} />{tag.description && <small>{tag.description}</small>}</span>}
          shouldDismissPopover={false} onClick={() => add(tag.name)} />)}
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
  // A session reads the open tasks and proposes the ones the tag fits; nothing is tagged before the user applies them.
  const [find, setFind] = useState(true);
  const [status, setStatus] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const valid = tagNameOf(name);
  const create = async () => {
    if (!valid) return;
    setSaving(true);
    const result = await hub.tag(projectId, "set", valid, description.trim() || null);
    setSaving(false);
    if (result !== "ok") { setStatus(result); return; }
    if (find) void hub.findTagTasks(projectId, valid);
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
      <Checkbox checked={find} label={t("Find the tasks it fits")} onChange={event => setFind(event.currentTarget.checked)} />
      <p className="work-tag-hint">{t("A session reads the open tasks of the project and proposes the ones the tag fits. You choose which get it.")}</p>
      {status && <p className="work-tag-failed" role="alert">{t("The change was not made.")}</p>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button onClick={onClose}>{t("Cancel")}</Button>
      <Button intent="primary" disabled={!valid || saving} loading={saving} onClick={() => void create()}>{t("Create tag")}</Button>
    </>} />
  </Dialog>;
}

type TagEdit = Readonly<{ name: string; newName: string; description: string; was: string }>;

/**
 * The tags of one project, to look after: each with what it is for and how many open tasks have it, described,
 * renamed or deleted (on its tasks too); the tags only found on tasks, to describe; the names agents may not
 * propose again, to forget.
 */
export function TagManagerDialog({ hub, projects, projectId: initialProject, onClose }: {
  hub: WorkItemsHub; projects: readonly Readonly<{ id: string; name: string; tags: readonly WorkItemTagRow[]; declined: readonly string[] }>[]; projectId: string | null; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [projectId, setProjectId] = useState(initialProject ?? projects[0]?.id ?? null);
  const [edit, setEdit] = useState<TagEdit | null>(null);
  const [deleting, setDeleting] = useState<WorkItemTagRow | null>(null);
  const [creating, setCreating] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const project = projects.find(candidate => candidate.id === projectId) ?? projects[0] ?? null;
  const change = async (action: WorkTagAction, name: string, value: string | null = null) => {
    if (!project) return false;
    setSaving(true); setStatus(null);
    const result = await hub.tag(project.id, action, name, value);
    setSaving(false);
    if (result !== "ok") setStatus(result);
    return result === "ok";
  };
  // A rename first, on the tasks too, then the description under the new name.
  const finish = async () => {
    const name = edit && tagNameOf(edit.newName);
    if (!edit || !name) return;
    if (name !== edit.name && !await change("rename", edit.name, name)) return;
    const description = edit.description.trim();
    if ((description !== edit.was || !project?.tags.find(tag => tag.name === edit.name)?.listed) && !await change("set", name, description || null)) return;
    setEdit(null);
  };
  const keys = (event: KeyboardEvent) => { if (event.key === "Enter") void finish(); else if (event.key === "Escape") { event.stopPropagation(); setEdit(null); } };
  const editor = (tag: WorkItemTagRow) => edit?.name === tag.name && <span className="work-tag-edit">
    <InputGroup size="small" autoFocus value={edit.newName} maxLength={33} aria-label={t("Name")} intent={tagNameOf(edit.newName) ? "none" : "danger"}
      onChange={event => setEdit({ ...edit, newName: event.target.value })} onKeyDown={keys} />
    <InputGroup size="small" value={edit.description} maxLength={maximumTagDescription} aria-label={t("What it is for")} placeholder={t("What it is for")}
      onChange={event => setEdit({ ...edit, description: event.target.value })} onKeyDown={keys} />
    <Button size="small" intent="primary" disabled={saving || !tagNameOf(edit.newName)} onClick={() => void finish()}>{t("Save")}</Button>
    <Button size="small" variant="minimal" onClick={() => setEdit(null)}>{t("Cancel")}</Button>
  </span>;
  return <Dialog isOpen className="work-tag-manager" title={t("Tags")} onClose={onClose}>
    <DialogBody>
      {projects.length > 1 && <HTMLSelect aria-label={t("Project")} value={project?.id ?? ""} onChange={event => { setProjectId(event.target.value); setEdit(null); }}>
        {projects.map(candidate => <option key={candidate.id} value={candidate.id}>{candidate.name}</option>)}
      </HTMLSelect>}
      <p className="work-tag-manager-intro">{t("Tags are yours: agents give a new task the tags that fit it, and never create one.")}</p>
      {!project?.tags.length && <p className="work-empty">{t("This project has no tags yet.")}</p>}
      <ul className="work-tag-list">
        {project?.tags.map(tag => <li key={tag.name} data-listed={tag.listed || undefined}>
          <TagChip name={tag.name} />
          {editor(tag) || <>
            <span className="work-tag-description">{tag.description ?? <em>{t(tag.listed ? "No description" : "Only on tasks: describe it so that agents use it")}</em>}</span>
            <span className="work-tag-count" title={t("Open tasks")}>{tag.openTasks}</span>
            <span className="work-tag-actions">
              <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={13} />} aria-label={t("Edit the tag {name}", { name: tag.name })} title={t("Edit")}
                onClick={() => setEdit({ name: tag.name, newName: tag.name, description: tag.description ?? "", was: tag.description ?? "" })} />
              <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={13} />} aria-label={t("Delete the tag {name}", { name: tag.name })} title={t("Delete")} onClick={() => setDeleting(tag)} />
            </span>
          </>}
        </li>)}
      </ul>
      {!!project?.declined.length && <section className="work-tag-declined">
        <h4>{t("Declined proposals")}</h4>
        <p>{t("Names you turned down when an agent proposed them: no agent proposes them again.")}</p>
        {project.declined.map(name => <span key={name} className="work-tag-declined-name"><code>{name}</code>
          <Button size="small" variant="minimal" disabled={saving} onClick={() => void change("forget", name)}>{t("Forget")}</Button></span>)}
      </section>}
      {status && <p className="work-tag-failed" role="alert">{t(status === "exists" ? "Another tag has this name." : "The change was not made.")}</p>}
    </DialogBody>
    <DialogFooter actions={<Button onClick={onClose}>{t("Close")}</Button>}>
      <Button icon={<AppIcon name="plus" size={14} />} disabled={!project} onClick={() => setCreating(true)}>{t("New tag")}</Button>
    </DialogFooter>
    <Alert isOpen={!!deleting} intent="danger" icon={null} cancelButtonText={t("Cancel")} confirmButtonText={t("Delete")} canEscapeKeyCancel canOutsideClickCancel
      onCancel={() => setDeleting(null)} onConfirm={() => { const tag = deleting!; setDeleting(null); void change("delete", tag.name); }}>
      <p>{deleting && t(deleting.openTasks === 0 ? "Delete the tag {name}? No open task has it." : deleting.openTasks === 1 ? "Delete the tag {name}? It is removed from the task that has it."
        : "Delete the tag {name}? It is removed from the {count} tasks that have it.", { name: deleting.name, count: deleting.openTasks })}</p>
    </Alert>
    {creating && project && <NewTagDialog hub={hub} projectId={project.id} name="" onClose={() => setCreating(false)} />}
  </Dialog>;
}

/** A tag proposal of a project as the list shows it, with the tasks it names. */
export type ShownTagProposal = Readonly<{ projectId: string; projectName: string | null; proposal: WorkItemTagProposalRow; tasks: readonly WorkItem[] }>;

/** The tag proposals that wait, above the list: the tasks a tag fits, or a new tag an agent proposes. */
export function TagProposalBanners({ proposals, onReview, onDismiss }: { proposals: readonly ShownTagProposal[]; onReview: (proposal: ShownTagProposal) => void;
  onDismiss: (proposal: ShownTagProposal) => void }) {
  const { t } = useShellLanguage();
  if (!proposals.length) return null;
  return <div className="work-tag-proposals">{proposals.map(shown => {
    const { proposal } = shown;
    const count = shown.tasks.length;
    return <Callout key={`${shown.projectId}\n${proposal.name}`} compact intent="primary" icon={<AppIcon name="tag" size={15} />} className="work-tag-proposal">
      <span className="work-tag-proposal-text">{proposal.newTag
        ? t(count === 1 ? "An agent proposes the tag {name}, for one open task." : "An agent proposes the tag {name}, for {count} open tasks.", { name: proposal.name, count })
        : t(count === 1 ? "One task fits the tag {name}." : "{count} tasks fit the tag {name}.", { name: proposal.name, count })}
        {shown.projectName && <small>{shown.projectName}</small>}</span>
      <Button size="small" intent="primary" onClick={() => onReview(shown)}>{t("Review")}</Button>
      <Button size="small" variant="minimal" title={t(proposal.newTag ? "No agent proposes this tag again." : "Tag none of these tasks.")} onClick={() => onDismiss(shown)}>{t("Dismiss")}</Button>
    </Callout>;
  })}</div>;
}

/**
 * Reviews a tag proposal: the tasks it names, ticked, each with why; a new tag with what it would be for. **Apply** tags
 * the ticked tasks (and creates a new tag); **Dismiss** tags none, and declines a new tag.
 */
export function TagProposalDialog({ hub, shown, onClose }: { hub: WorkItemsHub; shown: ShownTagProposal; onClose: () => void }) {
  const { t } = useShellLanguage();
  const { proposal } = shown;
  const [kept, setKept] = useState<ReadonlySet<string>>(() => new Set(shown.tasks.map(task => task.id)));
  const [description, setDescription] = useState(proposal.description ?? "");
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState(false);
  const reasons = new Map(proposal.tasks.map(match => [match.id, match.reason]));
  const decide = async (action: "apply" | "dismiss") => {
    setSaving(true);
    const result = await hub.tag(shown.projectId, action, proposal.name, action === "apply" && proposal.newTag ? description.trim() || null : null, action === "apply" ? [...kept] : null);
    setSaving(false);
    if (result === "ok") onClose(); else setFailed(true);
  };
  const toggle = (id: string) => setKept(current => { const next = new Set(current); if (!next.delete(id)) next.add(id); return next; });
  return <Dialog isOpen className="work-tag-dialog work-tag-review" title={t(proposal.newTag ? "A new tag" : "Tasks for a tag")} onClose={onClose}>
    <DialogBody>
      <p className="work-tag-review-head"><TagChip name={proposal.name} />{proposal.newTag ? t("An agent proposes this tag. It is created only if you apply it.") : t("A session found these open tasks for the tag. Untick the ones it does not fit.")}</p>
      {proposal.newTag && <FormGroup label={t("What it is for")} labelFor="work-tag-review-description">
        <InputGroup id="work-tag-review-description" value={description} maxLength={maximumTagDescription} onChange={event => setDescription(event.target.value)} />
      </FormGroup>}
      <ul className="work-tag-review-tasks">{shown.tasks.map(task => <li key={task.id}>
        <Checkbox checked={kept.has(task.id)} onChange={() => toggle(task.id)} labelElement={<span className="work-tag-review-task">
          {task.shortId && <code className="work-tag-id">{task.shortId}</code>}<strong>{task.title}</strong>
          {reasons.get(task.id) && <small>{reasons.get(task.id)}</small>}</span>} />
      </li>)}</ul>
      {failed && <p className="work-tag-failed" role="alert">{t("The change was not made.")}</p>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={saving} onClick={() => void decide("dismiss")}>{t("Dismiss")}</Button>
      <Button intent="primary" disabled={saving || kept.size === 0} loading={saving} onClick={() => void decide("apply")}>
        {t(kept.size === 1 ? "Tag one task" : "Tag {count} tasks", { count: kept.size })}</Button>
    </>} />
  </Dialog>;
}
