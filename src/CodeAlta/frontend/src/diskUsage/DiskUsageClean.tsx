import { useEffect, useId, useRef, useState } from "react";
import { Button, Callout, Checkbox, NonIdealState } from "@blueprintjs/core";
import type { DiskUsageCheckout, DiskUsageCleanResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { useShellLanguage } from "../shellLanguage";
import { storageBytes, storageSize } from "../storage/storage";
import { cleanBlock, cleanBlockText, cleanedBytes, cleanStatusText, outcomeText, type CleanBlock, type UsageNode } from "./diskUsage";
import type { DiskUsageHub } from "./diskUsageHub";

/** A checkout chosen for a clean, with the project that carries it. */
type Chosen = Readonly<{ node: UsageNode; projectId: string; checkout: DiskUsageCheckout }>;
type Answer = Readonly<{ chosen: Chosen; result: DiskUsageCleanResponse }>;
type Phase =
  | Readonly<{ kind: "confirm" }>
  | Readonly<{ kind: "cleaning"; index: number; count: number; done: number; total: number; freed: number }>
  | Readonly<{ kind: "results"; answers: readonly Answer[]; stopped: number }>;

/**
 * The clean of the build output of one or several checkouts: the build folders each would lose, listed with their size,
 * their files and their last write, all ticked; then the checkouts one after the other, with how far each is; then
 * what was freed and, per folder, what stayed and why. A checkout that cannot be cleaned is named with the reason.
 * The clean itself is the hub's: closing this window, or the view that opened it, does not keep the other views from
 * seeing its end.
 */
export function DiskUsageClean({ hub, nodes, current, label, onClose }: {
  hub: DiskUsageHub;
  /** The checkouts the user chose. */
  nodes: readonly UsageNode[];
  /** Whether the measurement shown was made in this run. */
  current: boolean;
  label: (node: UsageNode) => string;
  onClose: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const titleId = useId();
  const chosen: Chosen[] = [], blocked: { node: UsageNode; block: CleanBlock }[] = [];
  for (const node of nodes) {
    if (!node.checkout || !node.project) continue;
    const block = cleanBlock(node.checkout, current);
    if (block) blocked.push({ node, block });
    else chosen.push({ node, projectId: node.project.id, checkout: node.checkout });
  }
  // Every listed folder is ticked at first: the user unticks what stays.
  const [ticked, setTicked] = useState<ReadonlyMap<string, ReadonlySet<string>>>(() => new Map(chosen.map(item => [item.checkout.path, new Set(item.checkout.planned.map(entry => entry.path))])));
  const [phase, setPhase] = useState<Phase>({ kind: "confirm" });
  const alive = useRef(true), stop = useRef(false);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const toggle = (checkout: string, path: string) => setTicked(current => {
    const next = new Map(current), paths = new Set(next.get(checkout) ?? []);
    if (!paths.delete(path)) paths.add(path);
    next.set(checkout, paths);
    return next;
  });
  const selected = chosen.filter(item => (ticked.get(item.checkout.path)?.size ?? 0) > 0);
  const selectedBytes = selected.reduce((total, item) => total + item.checkout.planned.filter(entry => ticked.get(item.checkout.path)!.has(entry.path))
    .reduce((sum, entry) => sum + storageBytes(entry.bytes), 0), 0);

  async function clean() {
    stop.current = false;
    const items = selected.map(item => ({ projectId: item.projectId, checkout: item.checkout,
      paths: item.checkout.planned.map(entry => entry.path).filter(path => ticked.get(item.checkout.path)!.has(path)) }));
    setPhase({ kind: "cleaning", index: 0, count: items.length, done: 0, total: items[0]?.paths.length ?? 0, freed: 0 });
    const answers = await hub.clean(items, {
      stopped: () => stop.current,
      progress: at => { if (alive.current) setPhase({ kind: "cleaning", index: at.index, count: items.length, done: at.done, total: at.total, freed: at.freed }); },
    }).then(done => done.map((answer, index) => ({ chosen: selected[index], result: answer.result })));
    if (alive.current) setPhase({ kind: "results", answers, stopped: items.length - answers.length });
  }

  const working = phase.kind === "cleaning";
  const date = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
  const count = (value: number) => new Intl.NumberFormat(locale).format(value);
  return <AppWindow storageKey="codealta.desktop.window.diskUsageClean.v1" className="disk-usage-dialog" titleId={titleId} title={t("Clean build output")}
    preferredSize={viewport => ({ width: Math.min(820, viewport.width - 40), height: Math.min(640, viewport.height - 40) })} minimumSize={{ width: 360, height: 300 }}
    onClose={() => { if (!working) onClose(); }} closeLabel={t("Close")} keepOnOutsidePress
    onCancel={event => { event.preventDefault(); if (!working) onClose(); }}
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape" && !event.nativeEvent.isComposing) { event.preventDefault(); if (!working) onClose(); } }}>
    <div className="disk-usage-clean" data-phase={phase.kind}>
      {phase.kind === "confirm" && <>
        <div className="disk-usage-clean-body">
          <p>{t("Git deletes the ticked folders. They are build output: a build brings them back.")}</p>
          {chosen.map(item => {
            const paths = ticked.get(item.checkout.path)!;
            return <section key={item.checkout.path} className="disk-usage-clean-checkout" data-path={item.checkout.path} aria-label={label(item.node)}>
              <div className="disk-usage-clean-heading"><AppIcon name={item.checkout.project ? "folder" : "worktree"} size={15} /><strong>{label(item.node)}</strong><code title={item.checkout.path}>{item.checkout.path}</code></div>
              <ul className="disk-usage-clean-folders">{item.checkout.planned.map(entry => <li key={entry.path} data-folder={entry.path}>
                <Checkbox checked={paths.has(entry.path)} onChange={() => toggle(item.checkout.path, entry.path)}
                  aria-label={t("Clean {folder}", { folder: entry.path })}>
                  <code>{entry.path}</code>
                  <span className="disk-usage-clean-figures">{storageSize(storageBytes(entry.bytes), locale)} · {t("{count} files", { count: count(storageBytes(entry.files)) })}
                    {entry.lastWrite && <> · {t("last written {time}", { time: date.format(new Date(entry.lastWrite)) })}</>}</span>
                </Checkbox></li>)}</ul>
              {item.checkout.plannedTruncated && <p className="disk-usage-hint">{t("More build folders exist than are listed here: they stay.")}</p>}
            </section>;
          })}
          {blocked.length > 0 && <section className="disk-usage-clean-blocked" aria-label={t("Not cleaned")}>
            <div className="disk-usage-clean-heading"><strong>{t("Not cleaned")}</strong></div>
            <ul>{blocked.map(({ node, block }) => <li key={node.id} data-path={node.checkout!.path}><strong>{label(node)}</strong><span>{cleanBlockText(block, t)}</span></li>)}</ul>
          </section>}
        </div>
        <footer className="disk-usage-clean-footer"><Button text={t("Cancel")} onClick={onClose} autoFocus />
          <Button intent="danger" icon={<AppIcon name="trash" size={14} />} disabled={selected.length === 0}
            text={selected.length === 0 ? t("Clean") : t("Clean {size}", { size: storageSize(selectedBytes, locale) })} onClick={() => void clean()} /></footer>
      </>}
      {phase.kind === "cleaning" && <NonIdealState className="disk-usage-clean-progress" icon={<ActivitySpinner size={28} />}
        title={t("Cleaning {name}… ({index} of {count})", { name: label(selected[phase.index].node), index: phase.index + 1, count: phase.count })}
        description={<span role="status">{t("{done} of {total} folders done", { done: phase.done, total: phase.total })}
          {phase.freed > 0 && <><br />{t("{size} freed so far", { size: storageSize(phase.freed, locale) })}</>}</span>}
        action={phase.count > 1 ? <Button text={t("Stop")} title={t("The checkout being cleaned is finished first.")} onClick={() => { stop.current = true; }} /> : undefined} />}
      {phase.kind === "results" && <>
        <div className="disk-usage-clean-body">
          <p className="disk-usage-clean-total" role="status">{t("{size} freed.", { size: storageSize(cleanedBytes(phase.answers.map(answer => answer.result)), locale) })}</p>
          {phase.stopped > 0 && <p>{t("{count} checkouts were not started.", { count: phase.stopped })}</p>}
          <ul className="disk-usage-clean-results">{phase.answers.map(({ chosen: item, result }) => {
            const problem = cleanStatusText(result.status, result.message, t);
            const kept = result.entries.filter(entry => entry.outcome !== "removed");
            return <li key={item.checkout.path} data-path={item.checkout.path} data-status={result.status}>
              <span className="disk-usage-result-mark"><AppIcon name={problem ? "error" : kept.length ? "warning" : "check"} size={15} /></span>
              <span className="disk-usage-result-text">
                <strong>{label(item.node)}</strong>
                {problem ? <span>{problem}</span> : <span>{t("{size} freed.", { size: storageSize(storageBytes(result.bytesFreed), locale) })}</span>}
                {kept.length > 0 && <ul className="disk-usage-result-folders">{kept.map(entry => <li key={entry.path} data-folder={entry.path} data-outcome={entry.outcome}>
                  <code>{entry.path}</code><span>{outcomeText(entry.outcome, t)}</span>
                  {storageBytes(entry.bytesFreed) > 0 && <span>{t("{size} freed.", { size: storageSize(storageBytes(entry.bytesFreed), locale) })}</span>}</li>)}</ul>}
                {result.moreRemain && <span>{t("Build folders that were not listed remain.")}</span>}
              </span>
            </li>;
          })}</ul>
          {phase.answers.some(answer => answer.result.status === "unconfirmed") && <Callout intent="warning" compact>{t("Measure again to see what is left.")}</Callout>}
        </div>
        <footer className="disk-usage-clean-footer"><span /><Button intent="primary" text={t("Done")} onClick={onClose} autoFocus /></footer>
      </>}
    </div>
  </AppWindow>;
}
