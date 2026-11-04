import { useState } from "react";
import { Callout, FormGroup, HTMLSelect, InputGroup, NumericInput, Switch } from "@blueprintjs/core";
import type { AutomationAutonomyItem } from "#neoastra";
import { bypassPermissionMode } from "../permissionModes";
import { useShellLanguage } from "../shellLanguage";
import { describeAutonomy, newAutonomy, toolNames } from "./automations";

/**
 * What the runs of an automation do without a person: off, its runs work as any session of its permission mode; on,
 * they read freely, call the tools it names by themselves within a rate, and wait for its owner for anything else.
 */
export function AutonomyFields({ autonomy, permissionMode, disabled, onChange }: {
  autonomy: AutomationAutonomyItem | null;
  /** The permission mode its sessions run in: one that allows everything is refused for a run with autonomy. */
  permissionMode: string | null;
  disabled: boolean;
  onChange: (autonomy: AutomationAutonomyItem | null) => void;
}) {
  const { t } = useShellLanguage();
  // The tools are written as text and read as names: a comma being typed is not lost.
  const [tools, setTools] = useState(() => autonomy?.allowedTools.join(", ") ?? "");
  const set = (change: Partial<AutomationAutonomyItem>) => autonomy && onChange({ ...autonomy, ...change });
  return <FormGroup label={t("Autonomy")} className="automation-autonomy">
    <Switch checked={!!autonomy} disabled={disabled} label={t("Decide what its runs do without a person")}
      onChange={event => { const on = event.currentTarget.checked; setTools(""); onChange(on ? newAutonomy() : null); }} />
    {autonomy && <>
      <p className="automation-autonomy-help">{t("Its runs read without asking. A command, an edit, or a tool it may not call by itself waits for your approval, in its session.")}</p>
      <div className="automation-autonomy-grid">
        <label htmlFor="automation-allowed-tools">{t("Tools it calls by itself")}</label>
        <InputGroup id="automation-allowed-tools" value={tools} disabled={disabled} spellCheck={false} placeholder="jira_comment, mcp__github__create_issue"
          onChange={event => { setTools(event.target.value); set({ allowedTools: toolNames(event.target.value) }); }} />
        <label htmlFor="automation-max-calls">{t("Calls in an hour, at most")}</label>
        <NumericInput id="automation-max-calls" min={1} max={10000} value={autonomy.maxCallsPerHour ?? ""} disabled={disabled} buttonPosition="none" placeholder="—"
          onValueChange={value => set({ maxCallsPerHour: Number.isFinite(value) && value > 0 ? Math.round(value) : null })} />
        <label htmlFor="automation-approval-timeout">{t("Minutes an approval waits")}</label>
        <div className="automation-autonomy-timeout">
          <NumericInput id="automation-approval-timeout" min={1} max={10080} value={autonomy.approvalTimeout ?? ""} disabled={disabled} buttonPosition="none" placeholder="∞"
            onValueChange={value => set({ approvalTimeout: Number.isFinite(value) && value > 0 ? Math.round(value) : null })} />
          <HTMLSelect value={autonomy.onTimeout} disabled={disabled} aria-label={t("Minutes an approval waits")} onChange={event => set({ onTimeout: event.target.value })}>
            <option value="stop">{t("then stop the run")}</option><option value="wait">{t("then keep waiting")}</option>
          </HTMLSelect>
        </div>
        {/* The approvals follow the owner of the automation: on a server, a team that owns it and who is granted to approve it. */}
        <span className="automation-autonomy-label">{t("Approvals go to")}</span>
        <span className="automation-autonomy-approvers">{t("Its owner: you, or on your server the team that owns it and those granted to approve it.")}</span>
      </div>
      <p className="automation-autonomy-summary">{describeAutonomy(autonomy, t)}</p>
      {permissionMode === bypassPermissionMode && <Callout intent="warning" compact role="alert">
        {t("Its runs need a permission mode that asks: one that allows everything would let them do anything without you.")}</Callout>}
    </>}
  </FormGroup>;
}
