import { useEffect, useState } from "react";
import { Button, FormGroup, HTMLSelect, TagInput } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { machineState } from "../machines/machines";
import type { MachineView } from "#neoastra";
import { settingsNavigation } from "../settingsNavigation";
import { useShellLanguage } from "../shellLanguage";
import { isMachineTag, maximumPoolTags, poolTakes, triggerLabel, type AutomationForm } from "./automations";
import type { AutomationsHub, PoolMachines } from "./automationsHub";

/** The value of the choice of Runs on that runs it on the machines with some tags. */
export const poolChoice = "@tags";

/**
 * Runs on: this computer, a machine, or the machines with some tags (a pool), with the tags the machines have and the
 * machines that have them all now. Always shown: alone with Add a machine… while no machine is reachable, and disabled for
 * an automation kept in its project, which runs on the machines that have the project.
 */
export function RunsOnField({ hub, form, reachable, disabled, edit }: {
  hub: AutomationsHub; form: AutomationForm; reachable: readonly MachineView[]; disabled: boolean;
  edit: (change: Partial<AutomationForm>) => void;
}) {
  const { t } = useShellLanguage();
  const tags = form.runsOnTags ?? null;
  const pooled = !form.storeInProject && tags !== null;
  const [pool, setPool] = useState<PoolMachines | null>(null);
  const key = pooled ? JSON.stringify([tags, form.publishTo ?? null]) : null;
  useEffect(() => {
    if (key === null) { setPool(null); return; }
    const abort = new AbortController();
    // The members are looked for a moment after the last change of the tags.
    const timer = setTimeout(() => {
      void hub.poolMachines(tags ?? [], form.publishTo ?? null, abort.signal).then(value => { if (!abort.signal.aborted) setPool(value); });
    }, 250);
    return () => { clearTimeout(timer); abort.abort(); };
  }, [hub, key]);
  const value = form.storeInProject ? "" : pooled ? poolChoice : form.runsOn ?? "";
  const offered = reachable.length > 0 || !!form.runsOn || pooled;
  const choose = (next: string) => next === poolChoice
    ? edit({ runsOn: null, runsOnTags: tags ?? [], publishTo: null })
    : edit({ runsOn: next || null, runsOnTags: null, publishTo: null });
  const setTags = (next: readonly string[]) => {
    const kept = [...new Set(next.map(tag => tag.trim()).filter(isMachineTag))].slice(0, maximumPoolTags);
    edit({ runsOnTags: kept });
  };
  const suggested = (pool?.tags ?? []).filter(tag => !(tags ?? []).some(held => held.toLowerCase() === tag.toLowerCase()));
  const refused = pooled ? [...new Set(form.triggers.filter(trigger => !poolTakes(trigger.type)).map(trigger => trigger.type))] : [];
  return <>
    <FormGroup label={t("Runs on")} labelFor="automation-machine"
      helperText={form.storeInProject ? t("An automation of the project runs on the machines that have the project.") : undefined}>
      <div className="automation-runs-on">
        <HTMLSelect id="automation-machine" fill value={value} disabled={disabled || form.storeInProject} onChange={event => choose(event.target.value)}>
          <option value="">{t("This computer")}</option>
          {form.runsOn && !reachable.some(machine => machine.id === form.runsOn) && <option value={form.runsOn}>{t("Unavailable machine")}</option>}
          {reachable.map(machine => <option key={machine.id} value={machine.id}>
            {machineState(machine) === "online" ? machine.name : t("{machine} (offline)", { machine: machine.name })}</option>)}
          {offered && <option value={poolChoice}>{t("Machines with tags…")}</option>}
        </HTMLSelect>
        {/* No machine yet: the way to add one, beside the only choice. */}
        {!offered && !form.storeInProject && <Button size="small" variant="minimal" icon={<AppIcon name="plus" size={14} />} text={t("Add a machine…")}
          onClick={() => settingsNavigation.open("machines")} />}
      </div>
    </FormGroup>
    {pooled && <FormGroup label={t("Tags")} labelFor="automation-pool-tags" className="automation-pool"
      helperText={t("The machines that have all these tags run it: each run goes to one of them.")}>
      <TagInput inputProps={{ id: "automation-pool-tags" }} values={[...(tags ?? [])]} disabled={disabled} addOnBlur placeholder={t("Add a tag")} fill
        onChange={values => setTags(values.map(String))} />
      {suggested.length > 0 && <div className="automation-pool-suggested" aria-label={t("Tags of your machines")}>
        {suggested.map(tag => <Button key={tag} size="small" variant="minimal" icon={<AppIcon name="plus" size={12} />} text={tag} disabled={disabled}
          onClick={() => setTags([...(tags ?? []), tag])} />)}
      </div>}
      {(tags ?? []).length > 0 && pool && <p className="automation-pool-members" role="status">
        {pool.members.length > 0
          ? t("Runs on now: {machines}", { machines: pool.members.map(member => member.online ? member.name : t("{machine} (offline)", { machine: member.name })).join(", ") })
          : pool.problem ?? t("No machine of yours has these tags.")}
      </p>}
      {refused.length > 0 && <p className="automation-pool-refused" role="alert">
        {t("Machines with tags do not take these triggers: {triggers}.", { triggers: refused.map(type => t(triggerLabel(type))).join(", ") })}
      </p>}
    </FormGroup>}
  </>;
}
