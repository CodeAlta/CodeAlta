import { useRef, useState, type PointerEvent, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import type { RailLabelGroup } from "../explorer/projectRail";
import { SessionTabMenu, type SessionMenuEntry } from "../SessionTabMenu";
import { useShellLanguage } from "../shellLanguage";
import { labelDepth, labelPath, maximumLabelDepth, type Label } from "./labels";
import type { LabelEdit, LabelOutcome } from "./labelsHub";
import { LabelFormPopover, LabelIcon, labelTint, type LabelLook } from "./LabelViews";

/** What the title of a label can change: the label, its place, and the labels under it. Absent where nothing can be changed. */
export type LabelRowEditing = Readonly<{
  /** Every label of the catalog, the labels of other spaces included: a place among siblings is counted among all of them. */
  catalog: readonly Label[];
  update: (id: string, edit: LabelEdit) => Promise<LabelOutcome>;
  create: (name: string, look: Readonly<{ parent: string | null; icon: string | null; color: string | null }>) => Promise<LabelOutcome>;
  move: (id: string, parent: string | null, index: number) => Promise<LabelOutcome>;
  remove: (id: string) => Promise<LabelOutcome>;
}>;

type Form = "edit" | "child" | "delete" | null;

/**
 * The title of a label in the Explorer, the head of its branch: its icon in its color, its name, how many projects it
 * lists, what runs or waits in them, and a chevron that folds them, with the labels under it. On hover, a star keeps it on top and `⋯` opens what
 * changes it: its look, a label under it, its place, its deletion. Its row is dragged to put it before, after or under
 * another label, and a project dropped on it gets it.
 */
export function LabelRailRow({ group, labels, folded, onFold, editing, drop, drag, activity }: {
  group: RailLabelGroup;
  /** The labels of the space. */
  labels: readonly Label[];
  folded: boolean;
  onFold: (folded: boolean) => void;
  editing?: LabelRowEditing;
  /** Where a drag under way would drop on this row. */
  drop?: "before" | "after" | "into";
  /** What starts a drag of this row. */
  drag?: Readonly<{ onPointerDown?: (event: PointerEvent<HTMLElement>) => void }>;
  /** What goes on in its projects and in those of the labels under it: the sessions that run or wait. */
  activity?: ReactNode;
}) {
  const { t } = useShellLanguage();
  const { label } = group;
  const trigger = useRef<HTMLButtonElement>(null);
  const [menu, setMenu] = useState(false);
  const [form, setForm] = useState<Form>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const path = labelPath(label, labels);
  const close = () => { setForm(null); setError(null); };
  async function run(call: () => Promise<LabelOutcome>, failure: string) {
    setBusy(true); setError(null);
    const outcome = await call();
    setBusy(false);
    if (outcome.ok) { close(); return; }
    setError(outcome.message ?? failure);
  }
  // The siblings shown in this space, and the place among all of them of the catalog.
  const siblings = labels.filter(value => value.parent === label.parent);
  const at = siblings.findIndex(value => value.id === label.id);
  function moveBeside(other: Label | undefined, after: boolean) {
    if (!editing || !other) return;
    const all = editing.catalog.filter(value => value.parent === label.parent && value.id !== label.id);
    void run(() => editing.move(label.id, label.parent, all.findIndex(value => value.id === other.id) + (after ? 1 : 0)), t("The label could not be moved."));
  }
  const items: SessionMenuEntry[] = editing ? [
    { key: "edit", label: t("Edit label…"), icon: "edit", onSelect: () => setForm("edit") },
    { key: "child", label: t("New label under it…"), icon: "plus", disabled: labelDepth(label, labels) >= maximumLabelDepth, onSelect: () => setForm("child") },
    { key: "favorite", label: t(label.favorite ? "Remove from favorites" : "Add to favorites"), icon: "star",
      onSelect: () => void run(() => editing.update(label.id, { favorite: !label.favorite }), t("The label could not be changed.")) },
    { key: "place", divider: true },
    { key: "up", label: t("Move up"), icon: "arrowUp", disabled: at <= 0, onSelect: () => moveBeside(siblings[at - 1], false) },
    { key: "down", label: t("Move down"), icon: "arrowDown", disabled: at < 0 || at >= siblings.length - 1, onSelect: () => moveBeside(siblings[at + 1], true) },
    ...(label.parent ? [{ key: "out", label: t("Move to the top"), icon: "arrowLeft" as const,
      onSelect: () => void run(() => editing.move(label.id, null, -1), t("The label could not be moved.")) }] : []),
    { key: "remove", divider: true },
    { key: "delete", label: t("Delete label…"), icon: "trash", danger: true, onSelect: () => setForm("delete") },
  ] : [];
  const save = (look: LabelLook) => editing && run(() => editing.update(label.id, { name: look.name, icon: look.icon, color: look.color }), t("The label could not be changed."));
  const child = (look: LabelLook) => editing && run(() => editing.create(look.name, { parent: label.id, icon: look.icon || null, color: look.color || null }),
    t("The label could not be created."));
  const count = group.projects.length;
  return <li className="project-label-row" data-depth={group.depth} data-label-drop={label.id} data-drop={drop} data-movable={drag?.onPointerDown ? "true" : undefined}
    data-colored={label.color ? "true" : undefined} style={labelTint(label)} onPointerDown={drag?.onPointerDown}
    onContextMenu={event => { if (editing && !(event.target as HTMLElement).closest("input, textarea, [role=menu], dialog")) { event.preventDefault(); setMenu(true); } }}>
    <button type="button" className="project-label-toggle" data-label={label.id} aria-expanded={!folded}
      title={label.description ? `${path}\n${label.description}` : path} onClick={() => onFold(!folded)}>
      <span className="tree-twist" aria-hidden="true"><AppIcon name="chevronDown" size={12} className={folded ? "tree-chevron" : "tree-chevron expanded"} /></span>
      <LabelIcon label={label} size={15} />
      <strong className="project-label-name">{label.name}</strong>
      {count > 0 && <span className="project-label-count" aria-label={t(count === 1 ? "{count} project" : "{count} projects", { count })}>{count}</span>}
      {activity}
      {group.path.length > 0 && <span className="project-label-path">{group.path.map(above => above.name).join(" › ")}</span>}
    </button>
    {editing && <button type="button" className="icon-button label-row-action label-favorite-trigger" data-on={label.favorite}
      aria-label={t(label.favorite ? "Remove {name} from favorites" : "Add {name} to favorites", { name: label.name })}
      title={t(label.favorite ? "Remove from favorites" : "Add to favorites")}
      onClick={() => void run(() => editing.update(label.id, { favorite: !label.favorite }), t("The label could not be changed."))}>
      <AppIcon name="star" size={13} /></button>}
    {editing && <button ref={trigger} type="button" className="icon-button label-row-action label-actions-trigger"
      aria-label={t("Actions for {title}", { title: label.name })} aria-haspopup="menu" aria-expanded={menu} onClick={() => setMenu(value => !value)}>
      <AppIcon name="ellipsis" size={15} /></button>}
    {menu && trigger.current && <SessionTabMenu anchor={trigger.current} title={t("Label actions")} container={document.body} current={() => true}
      onClose={() => queueMicrotask(() => setMenu(false))} items={items} />}
    {form === "edit" && editing && <LabelFormPopover heading={t("Edit label")} submitLabel={t("Save")} labels={editing.catalog} parent={label.parent} ownId={label.id}
      initial={{ name: label.name, icon: label.icon ?? "", color: label.color ?? "" }} busy={busy} error={error} onSubmit={save} onCancel={close} />}
    {form === "child" && editing && <LabelFormPopover heading={t("New label under {name}", { name: label.name })} submitLabel={t("Create")} labels={editing.catalog}
      parent={label.id} ownId={null} initial={{ name: "", icon: "", color: label.color ?? "" }} busy={busy} error={error} onSubmit={child} onCancel={close} />}
    {form === "delete" && editing && <ConfirmPopover always title={t("Delete this label?")} subject={path} intent="danger" confirmLabel={t("Delete")}
      detail={t("Its projects lose it and stay. The labels under it take its place.")} busy={busy} error={error}
      onConfirm={() => void run(() => editing.remove(label.id), t("The label could not be deleted."))} onCancel={close} />}
  </li>;
}
