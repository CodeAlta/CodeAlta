import { useEffect, useMemo, useState } from "react";
import { Button, Callout, Collapse, Switch, Tag } from "@blueprintjs/core";
import { machines, type MachineMobileView, type MachineView } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import { createMobileApi } from "../mobile/canvas/api";
import { MobileCanvas } from "../mobile/canvas/MobileCanvas";
import { useMobileCanvas } from "../mobile/canvas/useMobileCanvas";
import { MobileSettings } from "../mobile/settings/MobileSettings";
import { createMobileSettingsApi } from "../mobile/settings/settings";
import { createMachineMobileRpc, mobileProblem, type MachineMobileApi } from "./machineMobileRpc";

/**
 * Connect a phone, on the page of a machine this computer manages by pairing: the machine serves the page of the phones itself (its Mobile
 * plugin, which it turns on and off), and this section draws the Mobile tab of that plugin: the QR code of a code that connects one phone,
 * and what keeps the phone out. The machine makes the code when it is asked for; it never leaves this window.
 */
export function MachineMobile({ epoch, machine, api = machines }: { epoch: string; machine: MachineView; api?: MachineMobileApi }) {
  const { t } = useShellLanguage();
  const [state, setState] = useState<MachineMobileView | null>(null);
  const [busy, setBusy] = useState(false);
  const [generation, setGeneration] = useState(0);
  // The machine's own refusal, kept until the next read of its state.
  const [problem, setProblem] = useState<{ status: string; message?: string | null } | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    api.mobile({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal }).then(value => {
      if (abort.signal.aborted) return;
      setState(value.status === "ok" ? value : null);
      setProblem(value.status === "ok" ? null : value);
    }, () => { if (!abort.signal.aborted) { setState(null); setProblem({ status: "offline" }); } });
    return () => abort.abort();
  }, [api, epoch, machine.id, machine.state, generation]);

  async function enable(enabled: boolean) {
    setBusy(true);
    try {
      const value = await api.mobileEnable({ expectedEpoch: epoch, machineId: machine.id, enabled });
      if (value.status === "ok") { setState(value); setProblem(null); } else setProblem(value);
    } catch {
      setProblem({ status: "offline" });
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  }

  // A machine that does not take the calls (an older version) offers nothing here: the section says to update it.
  const words = problem ? mobileProblem(problem.status) ?? "The machine could not do it." : null;
  const refusal = state?.refusal;
  const reasons = !state ? null : !state.available ? t("This machine cannot serve the page of the phones.") : null;
  return <section className="machine-section machine-mobile" aria-label={t("Connect a phone")}>
    <div className="machine-section-head">
      <h2>{t("Connect a phone")}</h2>
      {state?.running && <Tag minimal round intent="success">{t("On")}</Tag>}
    </div>
    <p className="muted-text">{t("Serve the sessions of {name} to a phone: scan its code with the phone, within a few minutes. {name} serves the page itself, through its own private network.", { name: machine.name })}</p>
    {words && <Callout compact intent="warning" role="alert">{t(words)}{problem?.message && problem.status !== "mobile.unsupported" && <div className="muted-text">{problem.message}</div>}</Callout>}
    {!state && !problem && <ActivitySpinner size={14} />}
    {state && <>
      {reasons && <Callout compact intent="warning" role="alert">{reasons}</Callout>}
      {refusal && !reasons && <Callout compact intent="warning" role="alert">{refusal}</Callout>}
      {state.available && <Switch className="machine-mobile-switch" label={t("Serve the phone page")} checked={state.enabled} disabled={busy || !state.enabled && !!refusal}
        onChange={event => void enable(event.currentTarget.checked)} />}
      {state.running && <MachineMobileTab key={machine.id} epoch={epoch} machine={machine} api={api} />}
      {state.enabled && !state.running && !refusal && <p className="muted-text" role="status">{t("The phone page is starting.")}</p>}
    </>}
  </section>;
}

// The Mobile tab over the machine: its status followed by asking, its code asked for when the person wants one.
function MachineMobileTab({ epoch, machine, api }: { epoch: string; machine: MachineView; api: MachineMobileApi }) {
  const { t } = useShellLanguage();
  const [settingsOpen, setSettingsOpen] = useState(false);
  const rpc = useMemo(() => createMachineMobileRpc(api, { epoch, machineId: machine.id }, status => {
    const key = mobileProblem(status);
    return key && t(key);
  }), [api, epoch, machine.id, t]);
  const mobile = useMemo(() => createMobileApi(rpc), [rpc]);
  const settings = useMemo(() => createMobileSettingsApi(rpc), [rpc]);
  const host = useMemo(() => ({
    openLink: (url: string) => { void api.openSignInPage?.({ expectedEpoch: epoch, url }); },
    copyFailed: () => { },
  }), [api, epoch]);
  const { status, failure, actions } = useMobileCanvas(mobile, host, true, true);
  return <>
    <MobileCanvas status={status} failure={failure} actions={actions} />
    <Button variant="minimal" text={t("Phone settings")} onClick={() => setSettingsOpen(value => !value)} aria-expanded={settingsOpen} />
    <Collapse isOpen={settingsOpen}><MobileSettings api={settings} /></Collapse>
  </>;
}
