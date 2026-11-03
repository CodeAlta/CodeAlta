import { useEffect, useRef, useState } from "react";
import { Button, Callout, Collapse, Dialog, DialogBody, DialogFooter, InputGroup, NumericInput, Radio, RadioGroup, Tag } from "@blueprintjs/core";
import { fleetServer, machines, type MachineSetupEvent, type MachineView } from "#neoastra";
import { AppIcon, type IconName } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { keyWords } from "./fleetServer";
import { managesMachine } from "../machines/machineManagement";
import { sshTargetOf } from "../machines/addMachine";
import { localServerSteps, organizationUrlOf, serverSetupProblem, serverStepTitle, sshServerSteps } from "./createServer";

/** The calls of the dialog on the servers. */
export type CreateServerApi = Pick<typeof fleetServer, "createServer">;

/** The calls of the dialog on the machines, to add them to the new server. */
export type CreateServerMachinesApi = Pick<typeof machines, "list" | "joinServer">;

type StepState = { state: "waiting" | "running" | "done" | "failed"; detail?: string | null };
type Where = "ssh" | "local";

/**
 * Create my server: on a machine reached over SSH, the usual place of a server (CodeAlta installs CodeAlta Server
 * there with its service, checks from here that it answers where the machines will reach it, reads its setup code over
 * SSH and claims it), on this computer, or a server that runs already. Then the machines this computer manages join it,
 * each approved at once.
 */
export function CreateServerDialog({ epoch, api = fleetServer, machinesApi = machines, onExisting, onClose }: {
  epoch: string; api?: CreateServerApi; machinesApi?: CreateServerMachinesApi; onExisting: () => void; onClose: (created: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [where, setWhere] = useState<Where | null>(null);
  const [target, setTarget] = useState("");
  const [reach, setReach] = useState<"network" | "address">("network");
  const [address, setAddress] = useState("");
  const [owner, setOwner] = useState("");
  const [organization, setOrganization] = useState("");
  const [advanced, setAdvanced] = useState(false);
  const [port, setPort] = useState(2586);
  const [dataRoot, setDataRoot] = useState("");
  const [toolPath, setToolPath] = useState("");
  const [packages, setPackages] = useState("");
  const [steps, setSteps] = useState<Record<string, StepState> | null>(null);
  const [failed, setFailed] = useState<{ step: string; code: string; detail?: string | null } | null>(null);
  const [linkId, setLinkId] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  const order = where === "local" ? localServerSteps : sshServerSteps;
  const computer = sshTargetOf(target);
  const url = organizationUrlOf(address);
  const ready = !!owner.trim() && (where === "local" || !!computer && (reach === "network" || !!url));

  async function create(acceptNewHostKey: boolean) {
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    setRunning(true);
    setFailed(null);
    setSteps(Object.fromEntries(order.map(step => [step, { state: "waiting" }])));
    const common = { expectedEpoch: epoch, ownerName: owner.trim(), organizationName: organization.trim() || null, port, dataRoot: dataRoot.trim() || null,
      toolPath: toolPath.trim() || null, packageFolder: packages.trim() || null };
    try {
      const events = await api.createServer(where === "local" ? { ...common, target: null, address: null, acceptNewHostKey: false }
        : { ...common, target: computer, address: reach === "address" ? url : null, acceptNewHostKey }, { signal: controller.signal });
      for await (const event of events) {
        if (controller.signal.aborted) return;
        apply(event);
      }
    } catch {
      if (!controller.signal.aborted) setFailed(value => value ?? { step: order[0], code: "unavailable" });
    } finally {
      if (!controller.signal.aborted) setRunning(false);
    }
  }

  function apply(event: MachineSetupEvent) {
    if (event.step === "finished") { setLinkId(event.machineId ?? null); return; }
    if (!order.includes(event.step)) return;
    setSteps(current => current && { ...current, [event.step]: { state: event.state === "done" ? "done" : event.state === "failed" ? "failed" : "running",
      detail: event.detail ?? current[event.step]?.detail } });
    if (event.state === "failed") setFailed({ step: event.step, code: event.code ?? "unavailable", detail: event.detail });
  }

  const shownHost = computer.replace(/^[^@]*@/u, "").replace(/:\d+$/u, "");
  return <Dialog isOpen className="create-server-dialog" title={t("Create my server")} canOutsideClickClose={!running && where === null} canEscapeKeyClose={!running}
    onClose={() => { if (!running) onClose(!!linkId); }}>
    {where === null && <DialogBody>
      <p className="muted-text">{t("Where does your server run?")}</p>
      <div className="add-machine-choices" role="list">
        <Choice icon="server" title="On a machine over SSH" recommended
          description="An always-on machine you reach with ssh: CodeAlta installs CodeAlta Server there with its service, checks that it answers from here and makes you its owner."
          onClick={() => setWhere("ssh")} />
        <Choice icon="computer" title="On this computer" description="CodeAlta installs CodeAlta Server here, starts it and makes you its owner. Nothing to copy."
          onClick={() => setWhere("local")} />
        <Choice icon="remote" title="It runs already" description="A server you started yourself (altaserver run): give its address and its setup code." onClick={onExisting} />
      </div>
    </DialogBody>}
    {where !== null && !steps && <>
      <DialogBody>
        {where === "ssh" && <>
          <label className="add-machine-field"><span>{t("Computer")}</span>
            <InputGroup autoFocus fill value={target} placeholder="user@host" aria-label={t("Computer")} onChange={event => setTarget(event.currentTarget.value)} />
            <small className="muted-text">{t("As you would type it after ssh: user@host, user@host:port, or a host of your SSH configuration. Your SSH keys and agent sign in.")}</small>
          </label>
          <div className="add-machine-field"><span>{t("How your machines reach it")}</span>
            <RadioGroup selectedValue={reach} onChange={event => setReach(event.currentTarget.value as "network" | "address")} className="create-server-reach">
              <Radio value="network" labelElement={<span>{t("The local network")} <code className="muted-text">{`http://${shownHost || "host"}:${port}`}</code></span>} />
              <Radio value="address" label={t("An address I put in front of it (a domain, Tailscale Serve)")} />
            </RadioGroup>
            {reach === "address" && <InputGroup fill value={address} placeholder="https://fleet.example.com" aria-label={t("Address of the server")}
              onChange={event => setAddress(event.currentTarget.value)} />}
          </div>
        </>}
        <label className="add-machine-field"><span>{t("Your name")}</span>
          <InputGroup autoFocus={where === "local"} fill value={owner} aria-label={t("Your name")} onChange={event => setOwner(event.currentTarget.value)} /></label>
        <label className="add-machine-field"><span>{t("Organization")}</span>
          <InputGroup fill value={organization} placeholder={t("Home")} aria-label={t("Organization")} onChange={event => setOrganization(event.currentTarget.value)} /></label>
        <Button variant="minimal" size="small" icon={<AppIcon name={advanced ? "chevronDown" : "chevronRight"} size={14} />} text={t("Advanced")} onClick={() => setAdvanced(value => !value)} />
        <Collapse isOpen={advanced}>
          <div className="add-machine-advanced">
            <label className="add-machine-field"><span>{t("Port")}</span>
              <NumericInput min={1} max={65535} value={port} onValueChange={value => setPort(Number.isFinite(value) ? value : 2586)} aria-label={t("Port")} /></label>
            <label className="add-machine-field"><span>{t("Data folder")}</span>
              <InputGroup fill value={dataRoot} placeholder="~/.altaserver" aria-label={t("Data folder")} onChange={event => setDataRoot(event.currentTarget.value)} /></label>
            <label className="add-machine-field"><span>{t("Install folder")}</span>
              <InputGroup fill value={toolPath} placeholder="~/.dotnet/tools" aria-label={t("Install folder")} onChange={event => setToolPath(event.currentTarget.value)} /></label>
            <label className="add-machine-field"><span>{t("Packages from a folder of this computer")}</span>
              <InputGroup fill value={packages} placeholder="nuget.org" aria-label={t("Packages from a folder of this computer")} onChange={event => setPackages(event.currentTarget.value)} /></label>
          </div>
        </Collapse>
      </DialogBody>
      <DialogFooter actions={<>
        <Button text={t("Back")} onClick={() => setWhere(null)} />
        <Button intent="primary" disabled={!ready} text={t("Create my server")} onClick={() => void create(false)} />
      </>} />
    </>}
    {steps && <>
      <DialogBody>
        <ol className="add-machine-steps" aria-label={t("Setup")}>
          {order.map(step => <li key={step} data-state={steps[step]?.state ?? "waiting"}>
            <span className="add-machine-step-icon">{steps[step]?.state === "running" ? <ActivitySpinner size={14} />
              : <AppIcon name={steps[step]?.state === "done" ? "checked" : steps[step]?.state === "failed" ? "error" : "minus"} size={15} />}</span>
            <span className="add-machine-step-main"><strong>{t(serverStepTitle(step))}</strong>
              {steps[step]?.detail && <small className="muted-text">{steps[step].detail}</small>}</span>
          </li>)}
        </ol>
        {failed && <Callout compact intent="warning" role="alert" className="add-machine-failure">{t(serverSetupProblem(failed.code), { host: shownHost || computer, port })}
          {failed.detail && <pre className="add-machine-detail">{failed.detail}</pre>}</Callout>}
        {linkId && <>
          <Callout compact intent="success" role="status">{t(where === "local" ? "Your server runs on this computer, and you are its owner." : "Your server runs on {host}, and you are its owner.",
            { host: shownHost })}</Callout>
          <AddMachines epoch={epoch} linkId={linkId} api={machinesApi} />
        </>}
      </DialogBody>
      <DialogFooter actions={<>
        {!running && failed && <Button text={t("Back")} onClick={() => { setSteps(null); setFailed(null); }} />}
        {!running && failed?.code === "host_key_unknown" && <Button intent="primary" text={t("Trust it and try again")} onClick={() => void create(true)} />}
        {!running && failed && failed.code !== "host_key_unknown" && <Button intent="primary" icon={<AppIcon name="refresh" size={14} />} text={t("Try again")} onClick={() => void create(false)} />}
        {linkId && <Button intent="primary" text={t("Done")} onClick={() => onClose(true)} />}
      </>} />
    </>}
  </Dialog>;
}

function Choice({ icon, title, description, recommended, onClick }: { icon: IconName; title: MessageKey; description: MessageKey; recommended?: boolean; onClick: () => void }) {
  const { t } = useShellLanguage();
  return <button type="button" role="listitem" className="add-machine-choice" onClick={onClick}>
    <span className="add-machine-choice-icon"><AppIcon name={icon} size={20} /></span>
    <span className="add-machine-choice-main"><strong>{t(title)}{recommended && <Tag minimal round intent="primary">{t("Recommended")}</Tag>}</strong>
      <span className="muted-text">{t(description)}</span></span>
    <AppIcon name="chevronRight" size={16} />
  </button>;
}

type Joined = { state: "joining" | "done" | "failed"; words?: string | null; problem?: string | null };

/** The machines this computer manages, each with Add: it joins the server from itself and is approved here. */
export function AddMachines({ epoch, linkId, api }: { epoch: string; linkId: string; api: CreateServerMachinesApi }) {
  const { t } = useShellLanguage();
  const [list, setList] = useState<MachineView[] | null>(null);
  const [joined, setJoined] = useState<Record<string, Joined>>({});
  useEffect(() => {
    const abort = new AbortController();
    api.list({ expectedEpoch: epoch }, { signal: abort.signal }).then(value => {
      if (!abort.signal.aborted) setList(value.machines.filter(machine => machine.paired && managesMachine(machine)));
    }, () => { if (!abort.signal.aborted) setList([]); });
    return () => abort.abort();
  }, [api, epoch]);
  async function add(machine: MachineView) {
    setJoined(current => ({ ...current, [machine.id]: { state: "joining" } }));
    try {
      const result = await api.joinServer({ expectedEpoch: epoch, machineId: machine.id, linkId }, { timeoutMilliseconds: 60_000 });
      setJoined(current => ({ ...current, [machine.id]: result.status === "ok" ? { state: "done", words: result.words } : { state: "failed", problem: result.message ?? result.status } }));
    } catch {
      setJoined(current => ({ ...current, [machine.id]: { state: "failed", problem: null } }));
    }
  }
  if (!list || list.length === 0) return null;
  return <section className="create-server-machines" aria-label={t("Add your machines")}>
    <h3>{t("Add your machines")}</h3>
    <p className="muted-text">{t("The machines this computer manages join the server from themselves, and are approved here.")}</p>
    <ul>{list.map(machine => {
      const state = joined[machine.id];
      return <li key={machine.id}>
        <AppIcon name="remote" size={15} /><strong>{machine.name}</strong>
        {state?.state === "done" ? <span className="create-server-joined"><Tag minimal round intent="success">{t("Joined")}</Tag>
          {keyWords(state.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</span>
          : state?.state === "failed" ? <span className="create-server-joined"><Tag minimal round intent="warning">{t("Not added")}</Tag><small className="muted-text">{state.problem}</small></span>
          : <Button size="small" disabled={state?.state === "joining" || machine.state !== "online"} icon={state?.state === "joining" ? <ActivitySpinner size={12} /> : <AppIcon name="plus" size={13} />}
            text={t(machine.state === "online" ? "Add" : "Offline")} onClick={() => void add(machine)} />}
      </li>;
    })}</ul>
  </section>;
}
