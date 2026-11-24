import { useEffect, useState } from "react";
import { Button, Callout, HTMLSelect, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import type { machines, MachineAgentsView } from "#neoastra";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { agentSandboxChoices, agentsAllowed, confirmsBypass, managementProblem } from "./machineManagement";

/** The calls that read and set the policy of a node or this computer. */
export type MachineAgentsApi = Pick<typeof machines, "agents" | "setAgentSpace" | "setAgentSandbox" | "setAgentBypass">;

/**
 * The spaces that let agents of other machines start sessions and send prompts, the minimum sandbox of their
 * sessions, and whether those may bypass permissions. Off until a space is turned on; turning on bypass asks first.
 */
export function AgentsSection({ epoch, machineId, name, api, refreshGeneration = 0 }: {
  epoch: string; machineId: string | null; name: string | null; api: MachineAgentsApi; refreshGeneration?: number;
}) {
  const { t } = useShellLanguage();
  const [view, setView] = useState<MachineAgentsView | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [generation, setGeneration] = useState(0);
  const noAnswer = machineId === null ? "This computer could not do it." : "The machine does not answer: it is offline.";
  useEffect(() => {
    const abort = new AbortController();
    api.agents({ expectedEpoch: epoch, machineId }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message }); else setView(value);
    }, () => { if (!abort.signal.aborted) setProblem({ key: noAnswer }); });
    return () => abort.abort();
  }, [api, epoch, machineId, generation, refreshGeneration, noAnswer]);
  // One change of the policy on the machine, then the policy read again; what went wrong stays above it.
  const run = async (call: () => Promise<{ status: string; message?: string | null }>) => {
    setBusy(true);
    setProblem(null);
    try {
      const result = await call();
      const failed = managementProblem(result.status);
      if (failed) setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: noAnswer });
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  };
  const target = { expectedEpoch: epoch, machineId };
  const allowed = view ? agentsAllowed(view) : false;
  const sandboxes = view ? agentSandboxChoices(view) : [];
  const Heading = machineId === null ? "h3" : "h2";
  return <section className="machine-section machine-agents" aria-label={t("Agents of other machines")}>
    <div className="machine-section-head">
      <Heading>{t("Agents of other machines")}</Heading>
      {view && <Tag minimal round intent={allowed ? "success" : "none"}>{t(allowed ? "On" : "Off")}</Tag>}
    </div>
    <p className="muted-text">{t("Agents of other computers may start sessions on {name} and send prompts to its sessions, in the spaces turned on here.", { name: name ?? t("This computer") })}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {view && <div className="machine-agents-settings">
      <div className="machine-agent-row" role="group" aria-label={t("Spaces")}>
        <span className="machine-agent-label">{t("Spaces")}</span>
        <span className="machine-agent-spaces">{view.spaces.map(space => <Switch key={space.id} className="machine-permission" inline label={space.name}
          checked={space.allowed} disabled={busy} onChange={event => {
            const on = event.currentTarget.checked;
            void run(() => api.setAgentSpace({ ...target, space: space.id, allowed: on }));
          }} />)}</span>
      </div>
      <label className="machine-agent-row">
        <span className="machine-agent-label">{t("Sandbox")}</span>
        <HTMLSelect value={sandboxes.find(value => value.toLowerCase() === (view.sandbox ?? "").toLowerCase()) ?? ""} disabled={busy} aria-label={t("Sandbox")}
          options={sandboxes.map(value => ({ value, label: value || t("As their parent") }))}
          onChange={event => {
            const sandbox = event.currentTarget.value || null;
            void run(() => api.setAgentSandbox({ ...target, sandbox }));
          }} />
      </label>
      <div className="machine-agent-row">
        <span className="machine-agent-label">{t("Bypass permissions")}</span>
        <BypassSwitch allowed={view.allowBypass} disabled={busy} onChange={value => void run(() => api.setAgentBypass({ ...target, allowed: value }))} />
      </div>
    </div>}
  </section>;
}

/** Whether the sessions of agents may bypass permissions: turned off at once, turned on from a small confirmation beside it. */
function BypassSwitch({ allowed, disabled, onChange }: { allowed: boolean; disabled: boolean; onChange: (allowed: boolean) => void }) {
  const { t } = useShellLanguage();
  const label = t("Bypass permissions");
  if (confirmsBypass(!allowed)) {
    return <PopoverNext placement="right" content={<div className="machine-confirm machine-bypass-confirm">
      <p>{t("Let their sessions bypass permissions when their parent does?")}</p>
      <Button className="bp6-popover-dismiss" intent="danger" disabled={disabled} onClick={() => onChange(true)}>{t("Allow")}</Button></div>}>
      <Switch className="machine-permission" checked={false} aria-label={label} disabled={disabled} onChange={() => { }} />
    </PopoverNext>;
  }

  return <Switch className="machine-permission" checked aria-label={label} disabled={disabled} onChange={() => onChange(false)} />;
}
