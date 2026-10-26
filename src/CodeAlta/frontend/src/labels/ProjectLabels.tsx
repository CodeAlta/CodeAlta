import { useEffect, useRef, useState } from "react";
import { Button, Checkbox, InputGroup, PopoverNext } from "@blueprintjs/core";
import { useShellLanguage } from "../shellLanguage";
import { labelAncestors, labelDepth, labelNameProblem, labelPath, maximumLabelName, type Label } from "./labels";
import type { LabelOutcome } from "./labelsHub";
import { LabelBadge, LabelIcon } from "./LabelViews";

/** How many badges a row of the plain list shows before it says how many more. */
const shownBadges = 2;

/** The labels of a project as small badges after its name, in the plain list: the first ones, then how many more. */
export function ProjectLabelBadges({ labels, all }: { labels: readonly Label[]; all: readonly Label[] }) {
  const { t } = useShellLanguage();
  if (!labels.length) return null;
  const more = labels.length - shownBadges;
  const paths = labels.map(label => labelPath(label, all)).join("\n");
  return <span className="project-label-badges" title={paths} aria-label={t("Labels: {names}", { names: labels.map(label => label.name).join(", ") })}>
    {labels.slice(0, shownBadges).map(label => <LabelBadge key={label.id} label={label} small />)}
    {more > 0 && <span className="project-label-more">+{more}</span>}
  </span>;
}

/**
 * The labels of a project in a popover beside its row: each label of the space with a check box, in the order of
 * their tree, and a field that makes a new one and gives it to the project at once.
 */
export function ProjectLabelsPopover({ projectId, projectName, labels, assign, create, onClose }: {
  projectId: string; projectName: string;
  /** The labels of the space, in the order of their tree. */
  labels: readonly Label[];
  assign: (projectId: string, add: readonly string[], remove: readonly string[]) => Promise<LabelOutcome>;
  /** Makes a label at the top and gives it to the projects named. */
  create: (name: string, projectIds: readonly string[]) => Promise<LabelOutcome>;
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => { const timer = setTimeout(() => (labels.length ? null : input.current)?.focus()); return () => clearTimeout(timer); }, []);
  const problem = name.trim() ? labelNameProblem(name, labels, null, null) : null;
  async function run(call: () => Promise<LabelOutcome>) {
    setBusy(true); setError(null);
    const outcome = await call();
    setBusy(false);
    if (!outcome.ok) setError(outcome.message ?? t("The labels could not be changed."));
    return outcome.ok;
  }
  const content = <div className="project-labels-form" role="dialog" aria-label={t("Labels of {name}", { name: projectName })}
    onKeyDown={event => { if (event.key === "Escape" && !busy) { event.preventDefault(); event.stopPropagation(); onClose(); } }}>
    <strong className="label-form-heading">{t("Labels of {name}", { name: projectName })}</strong>
    <div className="project-labels-checks">
      {labels.map(label => <Checkbox key={label.id} className="project-labels-check" checked={label.projectIds.includes(projectId)} disabled={busy}
        style={{ paddingInlineStart: 26 + Math.min(labelDepth(label, labels) - 1, 1) * 12 }} title={labelPath(label, labels)}
        onChange={event => { const add = event.currentTarget.checked; void run(() => assign(projectId, add ? [label.id] : [], add ? [] : [label.id])); }}>
        <LabelBadge label={label} small path={labelDepth(label, labels) > 2 ? labelAncestors(label, labels).slice(-1) : []} />
      </Checkbox>)}
      {labels.length === 0 && <p className="bp6-text-muted">{t("No label yet.")}</p>}
    </div>
    <form className="project-labels-new" onSubmit={event => {
      event.preventDefault();
      if (busy || !name.trim() || problem) return;
      void run(() => create(name.trim(), [projectId])).then(ok => { if (ok) setName(""); });
    }}>
      <InputGroup inputRef={input} size="small" value={name} placeholder={t("New label")} aria-label={t("New label")} disabled={busy} maxLength={maximumLabelName + 16}
        leftElement={<span className="label-form-icon"><LabelIcon label={{ icon: null, color: null }} size={12} /></span>}
        intent={problem ? "danger" : "none"} autoComplete="off" spellCheck={false} onChange={event => setName(event.target.value)} />
      <Button type="submit" size="small" text={t("Add")} disabled={busy || !name.trim() || !!problem} />
    </form>
    {(error || problem === "taken") && <p className="settings-field-help error-text" role="alert">{problem === "taken" ? t("A label beside it already has this name.") : error}</p>}
    <div className="rename-actions"><Button variant="minimal" size="small" text={t("Done")} disabled={busy} onClick={onClose} /></div>
  </div>;
  return <PopoverNext isOpen content={content} placement="right-start" popoverClassName="rename-popover label-popover" className="rename-anchor" targetTagName="span"
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false} canEscapeKeyClose={!busy}
    onInteraction={next => { if (!next && !busy) onClose(); }}>
    <span aria-hidden="true" />
  </PopoverNext>;
}
