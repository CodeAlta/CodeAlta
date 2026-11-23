import { useEffect, useRef, useState } from "react";
import { Button, Checkbox, InputGroup, PopoverNext } from "@blueprintjs/core";
import { useShellLanguage } from "../shellLanguage";
import { labelAncestors, labelDepth, labelNameProblem, labelPath, maximumLabelName, type Label } from "./labels";
import type { LabelOutcome, SessionLabelsOutcome } from "./labelsHub";
import { LabelBadge, LabelIcon } from "./LabelViews";

/** How many badges a row of the plain list shows before it says how many more. */
const shownBadges = 2;

/** The labels of a project or of a session as small badges after its name: the first ones, then how many more. */
export function LabelBadges({ labels, all, className = "project-label-badges" }: { labels: readonly Label[]; all: readonly Label[]; className?: string }) {
  const { t } = useShellLanguage();
  if (!labels.length) return null;
  const more = labels.length - shownBadges;
  const paths = labels.map(label => labelPath(label, all)).join("\n");
  return <span className={className} title={paths} aria-label={t("Labels: {names}", { names: labels.map(label => label.name).join(", ") })}>
    {labels.slice(0, shownBadges).map(label => <LabelBadge key={label.id} label={label} small />)}
    {more > 0 && <span className="project-label-more">+{more}</span>}
  </span>;
}

/** How a change made from the popover ended. */
type Outcome = Readonly<{ ok: boolean; message: string | null }>;

/**
 * The labels of a project or of a session in a popover beside its row: each label of the space with a check box, in the
 * order of their tree, and a field that makes a new one and gives it at once.
 */
function LabelsPopover({ heading, labels, has, toggle, create, onClose }: {
  heading: string;
  /** The labels of the space it can have, in the order of their tree. */
  labels: readonly Label[];
  has: (label: Label) => boolean;
  toggle: (label: Label, add: boolean) => Promise<Outcome>;
  /** Makes a label at the top and gives it. */
  create: (name: string) => Promise<Outcome>;
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [name, setName] = useState("");
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => { const timer = setTimeout(() => (labels.length ? null : input.current)?.focus()); return () => clearTimeout(timer); }, []);
  const problem = name.trim() ? labelNameProblem(name, labels, null, null) : null;
  async function run(call: () => Promise<Outcome>) {
    setBusy(true); setError(null);
    const outcome = await call();
    setBusy(false);
    if (!outcome.ok) setError(outcome.message ?? t("The labels could not be changed."));
    return outcome.ok;
  }
  const content = <div className="project-labels-form" role="dialog" aria-label={heading}
    onKeyDown={event => { if (event.key === "Escape" && !busy) { event.preventDefault(); event.stopPropagation(); onClose(); } }}>
    <strong className="label-form-heading">{heading}</strong>
    <div className="project-labels-checks">
      {labels.map(label => <Checkbox key={label.id} className="project-labels-check" checked={has(label)} disabled={busy}
        style={{ paddingInlineStart: 26 + Math.min(labelDepth(label, labels) - 1, 1) * 12 }} title={labelPath(label, labels)}
        onChange={event => { const add = event.currentTarget.checked; void run(() => toggle(label, add)); }}>
        <LabelBadge label={label} small path={labelDepth(label, labels) > 2 ? labelAncestors(label, labels).slice(-1) : []} />
      </Checkbox>)}
      {labels.length === 0 && <p className="bp6-text-muted">{t("No label yet.")}</p>}
    </div>
    <form className="project-labels-new" onSubmit={event => {
      event.preventDefault();
      if (busy || !name.trim() || problem) return;
      void run(() => create(name.trim())).then(ok => { if (ok) setName(""); });
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

/** The labels of a project in a popover beside its row: the labels for projects of its space. */
export function ProjectLabelsPopover({ projectId, projectName, labels, assign, create, onClose }: {
  projectId: string; projectName: string;
  /** The labels for projects of the space, in the order of their tree. */
  labels: readonly Label[];
  assign: (projectId: string, add: readonly string[], remove: readonly string[]) => Promise<LabelOutcome>;
  /** Makes a label at the top and gives it to the projects named. */
  create: (name: string, projectIds: readonly string[]) => Promise<LabelOutcome>;
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  return <LabelsPopover heading={t("Labels of {name}", { name: projectName })} labels={labels} has={label => label.projectIds.includes(projectId)}
    toggle={(label, add) => assign(projectId, add ? [label.id] : [], add ? [] : [label.id])} create={name => create(name, [projectId])} onClose={onClose} />;
}

/**
 * The labels of a session in a popover beside its row: the labels for sessions of its space. The window shows a change with
 * the next list of the sessions; the check boxes follow what the host answered meanwhile.
 */
export function SessionLabelsPopover({ sessionId, sessionTitle, current, labels, assign, create, onClose }: {
  sessionId: string; sessionTitle: string;
  /** The labels the session has, as the list of the sessions says. */
  current: readonly string[];
  /** The labels for sessions of the space, in the order of their tree. */
  labels: readonly Label[];
  assign: (sessionId: string, add: readonly string[], remove: readonly string[]) => Promise<SessionLabelsOutcome>;
  /** Makes a label for sessions at the top. */
  create: (name: string) => Promise<LabelOutcome>;
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [own, setOwn] = useState<readonly string[]>(current);
  const give = async (add: readonly string[], remove: readonly string[]) => {
    const outcome = await assign(sessionId, add, remove);
    if (outcome.ok) setOwn(outcome.labels);
    return outcome;
  };
  return <LabelsPopover heading={t("Labels of {name}", { name: sessionTitle })} labels={labels} has={label => own.includes(label.id)}
    toggle={(label, add) => give(add ? [label.id] : [], add ? [] : [label.id])}
    create={async name => { const made = await create(name); return made.ok && made.label ? give([made.label.id], []) : made; }} onClose={onClose} />;
}
