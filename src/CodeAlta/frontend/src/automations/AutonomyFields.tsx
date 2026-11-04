import { useState } from "react";
import { Callout, FormGroup, HTMLSelect, InputGroup, NumericInput, Switch } from "@blueprintjs/core";
import type { AutomationAutonomyItem } from "#neoastra";
import { bypassPermissionMode } from "../permissionModes";
import { useShellLanguage } from "../shellLanguage";
import { describeAllowedTool, describeAutonomy, newAutonomy, toolNames } from "./automations";

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
        {/* What a tool is pinned to and bounded by is written in the file: the form shows it and keeps it while the tool stays allowed. */}
        <span />
        <div className="automation-autonomy-tools">
          {(autonomy.tools ?? []).filter(tool => autonomy.allowedTools.includes(tool.name)).map(tool => {
            const said = describeAllowedTool(tool, t);
            return said && <p key={tool.name}><code>{tool.name}</code> {said}</p>;
          })}
          <p>{t("Pins, bounds and preconditions are written in its configuration file; a try says the pin of each tool it calls.")}</p>
        </div>
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
        <label htmlFor="automation-approvers">{t("Approvals go to")}</label>
        <div className="automation-autonomy-approvers-field">
          <HTMLSelect id="automation-approvers" value={autonomy.approvers} disabled={disabled} onChange={event => set({ approvers: event.target.value })}>
            <option value="owner">{t("Its owner")}</option><option value="oncall">{t("The person on call of its team")}</option>
          </HTMLSelect>
          <span className="automation-autonomy-approvers">{t(autonomy.approvers === "oncall"
            ? "On your server, the team that owns it takes turns on call; without a rotation, its whole team answers."
            : "Its owner: you, or on your server the team that owns it and those granted to approve it.")}</span>
        </div>
      </div>
      <p className="automation-autonomy-summary">{describeAutonomy(autonomy, t)}</p>
      {permissionMode === bypassPermissionMode && <Callout intent="warning" compact role="alert">
        {t("Its runs need a permission mode that asks: one that allows everything would let them do anything without you.")}</Callout>}
    </>}
  </FormGroup>;
}
