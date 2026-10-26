import { useEffect, useId, useRef, useState, type CSSProperties } from "react";
import { createPortal } from "react-dom";
import { Button, FormGroup, InputGroup, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { brandColor } from "../brands";
import { Logo } from "../ProviderIcon";
import { ProviderIconPicker } from "../ProviderIconFields";
import { useShellLanguage } from "../shellLanguage";
import { spaceColors } from "../spaces/spaces";
import type { RailDragView } from "./labelDrag";
import { labelBrand, labelNameProblem, maximumLabelName, type Label } from "./labels";

/** The icon of a label, in its color. */
export function LabelIcon({ label, size = 12 }: { label: Readonly<{ icon: string | null; color: string | null }>; size?: number }) {
  return <span className="label-icon"><Logo brand={labelBrand(label)} size={size} fallback="tag" /></span>;
}

/** The color a badge is tinted with, for its style. */
export const labelTint = (label: Readonly<{ color: string | null }>): CSSProperties | undefined =>
  label.color ? { "--label-color": label.color } as CSSProperties : undefined;

/**
 * A label as a badge: its icon and its name on a tint of its color. With `path`, the names of the labels above it
 * come first, dimmed.
 */
export function LabelBadge({ label, path = [], small = false, title }: { label: Label; path?: readonly Label[]; small?: boolean; title?: string }) {
  return <span className="label-badge" data-small={small ? "true" : undefined} data-colored={label.color ? "true" : undefined} style={labelTint(label)} title={title}>
    <LabelIcon label={label} size={small ? 10 : 12} />
    {path.length > 0 && <span className="label-badge-path">{path.map(above => above.name).join(" › ")} › </span>}
    <span className="label-badge-name">{label.name}</span>
  </span>;
}

/** What the form of a label gives: its name, its icon and its color, empty for none. */
export type LabelLook = Readonly<{ name: string; icon: string; color: string }>;

/**
 * The form of a label in a popover beside the row it is opened from: its name, its icon and its color. It makes a
 * label, or changes one. Enter saves; Escape, Cancel or a click elsewhere leaves everything as it is.
 */
export function LabelFormPopover({ heading, submitLabel, initial, labels, parent, ownId, busy, error, onSubmit, onCancel }: {
  heading: string; submitLabel: string; initial: LabelLook;
  /** The labels, to tell a name already given beside it. */
  labels: readonly Label[];
  /** The label it is (or goes) under; null at the top. */
  parent: string | null;
  /** The label changed; null for a new one. */
  ownId: string | null;
  busy: boolean; error: string | null;
  onSubmit: (look: LabelLook) => void; onCancel: () => void;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const input = useRef<HTMLInputElement>(null);
  const [look, setLook] = useState(initial);
  useEffect(() => { const timer = setTimeout(() => { input.current?.focus(); input.current?.select(); }); return () => clearTimeout(timer); }, []);
  const problem = labelNameProblem(look.name, labels, parent, ownId);
  const shown = { icon: look.icon || null, color: brandColor(look.color) ?? null };
  const help = problem === "taken" ? t("A label beside it already has this name.") : problem === "long" ? t("A name has at most {count} characters.", { count: maximumLabelName }) : error;
  const form = <form className="label-form" aria-label={heading} onSubmit={event => {
    event.preventDefault();
    if (!busy && !problem) onSubmit({ ...look, name: look.name.trim() });
  }} onKeyDown={event => { if (event.key === "Escape" && !busy) { event.preventDefault(); event.stopPropagation(); onCancel(); } }}>
    <strong className="label-form-heading">{heading}</strong>
    <FormGroup label={t("Name")} labelFor={`${id}-name`} intent={help ? "danger" : "none"} helperText={help ? <span role="alert">{help}</span> : undefined}>
      <InputGroup id={`${id}-name`} inputRef={input} value={look.name} maxLength={maximumLabelName + 16} disabled={busy} autoComplete="off" spellCheck={false}
        intent={help ? "danger" : "none"} leftElement={<span className="label-form-icon"><LabelIcon label={shown} size={14} /></span>}
        onChange={event => setLook(current => ({ ...current, name: event.target.value }))} />
    </FormGroup>
    <FormGroup label={t("Icon")} labelFor={`${id}-icon`}>
      <ProviderIconPicker id={`${id}-icon`} value={look.icon} automatic={{ icon: null }} shown={labelBrand(shown)} disabled={busy} fallback="tag" symbolsFirst
        onChange={icon => setLook(current => ({ ...current, icon }))} />
    </FormGroup>
    <FormGroup label={t("Color")}>
      <span className="space-color-swatches label-form-colors" role="group" aria-label={t("Color")}>
        {spaceColors.map(color => <button type="button" key={color} className="space-color" style={{ background: color }} disabled={busy}
          aria-pressed={look.color.toLowerCase() === color} aria-label={color} title={color}
          onClick={() => setLook(current => ({ ...current, color: current.color.toLowerCase() === color ? "" : color }))} />)}
      </span>
    </FormGroup>
    <div className="rename-actions">
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onCancel} />
      <Button type="submit" intent="primary" size="small" text={submitLabel} loading={busy} disabled={!!problem} />
    </div>
  </form>;
  return <PopoverNext isOpen content={form} placement="right-start" popoverClassName="rename-popover label-popover" className="rename-anchor" targetTagName="span"
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!busy}
    onInteraction={(next, event) => {
      // The list of the icons opens in a popover of its own: a press there is not a press elsewhere.
      if (!next && !busy && !(event?.target instanceof Element && event.target.closest(".bp6-popover, .bp6-portal"))) onCancel();
    }}>
    <span aria-hidden="true" />
  </PopoverNext>;
}

/** The name of what is dragged, by the pointer: a label, or a project with `+` when it keeps the label it comes from. */
export function LabelDragGhost({ view }: { view: RailDragView | null }) {
  if (!view) return null;
  return createPortal(<div className="space-drag-ghost" role="presentation" data-over={view.over ? "true" : undefined} data-copy={view.copy ? "true" : undefined}
    style={{ left: view.x + 14, top: view.y + 10 }}><AppIcon name={view.copy ? "plus" : "grip"} size={13} /><span>{view.name}</span></div>, view.layer);
}
