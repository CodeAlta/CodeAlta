import { useEffect, useRef, useState } from "react";
import { Button, Callout, Checkbox, Collapse, Dialog, DialogBody, DialogFooter, InputGroup, NumericInput, Radio, RadioGroup, Tag } from "@blueprintjs/core";
import { fleetServer, machines, type MachineSetupEvent, type MachineView } from "#neoastra";
import { AppIcon, type IconName } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { keyWords } from "./fleetServer";
import { managesMachine } from "../machines/machineManagement";
import { sshTargetOf } from "../machines/addMachine";
import { SetupFailureDetail } from "../machines/AddMachineDialog";
import { hostKeyOf } from "../machines/addMachine";
import { localServerSteps, offersNetworkPublish, offersTailscaleServe, organizationUrlOf, serverAsksSudoPassword, serverSetupProblem, serverStepTitle, sshServerSteps } from "./createServer";
import { ServerSpaces, type ServerSpacesApi, type ServerSpacesListApi } from "./ServerSpaces";

/** The calls of the dialog on the servers. */
export type CreateServerApi = Pick<typeof fleetServer, "createServer" | "status">;

/** The calls of the dialog on the machines, to add them to the new server. */
export type CreateServerMachinesApi = Pick<typeof machines, "list" | "joinServer">;

type StepState = { state: "waiting" | "running" | "done" | "failed"; detail?: string | null };
type Where = "ssh" | "local";
type Reach = "address" | "encrypted" | "network";
// A server just created has no space linked yet.
const noSpaces: readonly string[] = [];

/**
 * Create my server: on a machine reached over SSH, the usual place of a server (CodeAlta installs CodeAlta Server
 * there with its service, checks from here that it answers where the machines will reach it, reads its setup code over
 * SSH and claims it), on this computer, or a server that runs already. Then the machines this computer manages join it,
 * each approved at once. The owner's recovery key is made with the server: its words go to `onClose`, for the page to show
 * them once.
 */
export function CreateServerDialog({ epoch, api = fleetServer, machinesApi = machines, spacesApi, onExisting, onClose }: {
  epoch: string; api?: CreateServerApi; machinesApi?: CreateServerMachinesApi; spacesApi?: { link: ServerSpacesApi; list: ServerSpacesListApi };
  onExisting: () => void; onClose: (created: boolean, recoveryWords?: string | null) => void;
}) {
  const { t } = useShellLanguage();
  const [where, setWhere] = useState<Where | null>(null);
  const [target, setTarget] = useState("");
  // An address put in front of the server (TLS) stays the default; on the local network, the server's own certificate
  // (pinned by every device) comes first, and plain HTTP is chosen knowingly.
  const [reach, setReach] = useState<Reach>("address");
  const [address, setAddress] = useState("");
  const [owner, setOwner] = useState("");
  const [organization, setOrganization] = useState("");
  const [advanced, setAdvanced] = useState(false);
  const [port, setPort] = useState(2586);
  const [dataRoot, setDataRoot] = useState("");
  const [toolPath, setToolPath] = useState("");
  const [packages, setPackages] = useState("");
  // The machines depend on the server: over SSH it starts with its machine by default (sudo there), its password asked only when needed.
  const [alwaysOn, setAlwaysOn] = useState(true);
  const [sudoPassword, setSudoPassword] = useState("");
  // Behind a name of a tailnet that Tailscale Serve does not publish yet, the person may ask CodeAlta to publish it there: kept for the next tries.
  const [tailscaleServe, setTailscaleServe] = useState(false);
  const [steps, setSteps] = useState<Record<string, StepState> | null>(null);
  const [failed, setFailed] = useState<{ step: string; code: string; detail?: string | null } | null>(null);
  const [linkId, setLinkId] = useState<string | null>(null);
  // The words of the owner's recovery key, made with the server: kept until the dialog closes, then shown once.
  const [recoveryWords, setRecoveryWords] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  // The owner's name starts as the full name of the person signed in to this computer: changed at will, kept once typed.
  useEffect(() => {
    const reading = new AbortController();
    api.status({ expectedEpoch: epoch }, { signal: reading.signal, timeoutMilliseconds: 10_000 })
      .then(value => { if (!reading.signal.aborted && value.personName) setOwner(current => current || value.personName!); }, () => { });
    return () => reading.abort();
  }, [api, epoch]);
  const order = where === "local" ? localServerSteps : sshServerSteps;
  const computer = sshTargetOf(target);
  const url = organizationUrlOf(address);
  const ready = !!owner.trim() && (where === "local" || !!computer && (reach !== "address" || !!url));
  // The SSH key shown of a computer ssh does not know yet, which "Trust it" trusts.
  const shownKey = failed ? hostKeyOf(failed.code, failed.detail) : null;

  // "Trust it" trusts the SSH key shown, by its fingerprint: every connection of the setup requires that key.
  async function create(trustedHostKey: string | null, publish = tailscaleServe) {
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    setRunning(true);
    setFailed(null);
    setSteps(Object.fromEntries(order.map(step => [step, { state: "waiting" }])));
    const common = { expectedEpoch: epoch, ownerName: owner.trim(), organizationName: organization.trim() || null, port, dataRoot: dataRoot.trim() || null,
      toolPath: toolPath.trim() || null, packageFolder: packages.trim() || null };
    const serve = where === "ssh" && reach === "address" && publish;
    const password = where === "ssh" && (alwaysOn || serve) && sudoPassword ? sudoPassword : null;
    setSudoPassword("");
    setTailscaleServe(serve);
    try {
      const events = await api.createServer(where === "local"
        ? { ...common, target: null, address: null, trustedHostKey: null, ownCertificate: false, alwaysOn: false, sudoPassword: null, tailscaleServe: false, publishNetwork: false }
        : { ...common, target: computer, address: reach === "address" ? url : null, trustedHostKey, ownCertificate: reach === "encrypted", alwaysOn, sudoPassword: password, tailscaleServe: serve, publishNetwork: serve },
        { signal: controller.signal });
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
    if (event.step === "finished") { setLinkId(event.machineId ?? null); setRecoveryWords(event.recoveryWords ?? null); return; }
    if (!order.includes(event.step)) return;
    setSteps(current => current && { ...current, [event.step]: { state: event.state === "done" ? "done" : event.state === "failed" ? "failed" : "running",
      detail: event.detail ?? current[event.step]?.detail } });
    if (event.state === "failed") setFailed({ step: event.step, code: event.code ?? "unavailable", detail: event.detail });
  }

  const shownHost = computer.replace(/^[^@]*@/u, "").replace(/:\d+$/u, "");
  return <Dialog isOpen className="create-server-dialog" title={t("Create my server")} canOutsideClickClose={!running && where === null} canEscapeKeyClose={!running}
    onClose={() => { if (!running) onClose(!!linkId, recoveryWords); }}>
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
            <RadioGroup selectedValue={reach} onChange={event => setReach(event.currentTarget.value as Reach)} className="create-server-reach">
              <Radio value="address" label={t("An address I put in front of it (a domain, Tailscale Serve)")} />
              <Radio value="encrypted" labelElement={<span>{t("The local network, encrypted with the server's own certificate")} <code className="muted-text">{`https://${shownHost || "host"}:${port}`}</code></span>} />
              <Radio value="network" labelElement={<span>{t("The local network, not encrypted")} <code className="muted-text">{`http://${shownHost || "host"}:${port}`}</code></span>} />
            </RadioGroup>
            {reach === "address" && <InputGroup fill value={address} placeholder="https://fleet.example.com" aria-label={t("Address of the server")}
              onChange={event => setAddress(event.currentTarget.value)} />}
            {reach === "encrypted" && <small className="muted-text create-server-reach-note">{t("The server makes a certificate of its own. No browser trusts it, but CodeAlta reads its fingerprint over SSH and every device checks it at each connection: nothing travels in clear, and no other machine of the network can pass for your server.")}</small>}
            {reach === "network" && <small className="muted-text create-server-reach-note">{t("Anyone on the network can read what your devices send the server. Only on a network you trust.")}</small>}
          </div>
          <Checkbox className="add-machine-always-on" checked={alwaysOn} onChange={event => setAlwaysOn(event.currentTarget.checked)}
            labelElement={<span>{t("Always on")} <span className="muted-text">{t("it starts with the machine, with nobody logged in (needs sudo there)")}</span></span>} />
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
        <Button intent="primary" disabled={!ready} text={t("Create my server")} onClick={() => void create(null)} />
      </>} />
    </>}
    {steps && <>
      <DialogBody>
        <ol className="add-machine-steps" aria-label={t("Setup")}>
          {order.map(step => <li key={step} data-state={steps[step]?.state ?? "waiting"}>
            <span className="add-machine-step-icon">{steps[step]?.state === "running" ? <ActivitySpinner size={14} />
              : <AppIcon name={steps[step]?.state === "done" ? "checked" : steps[step]?.state === "failed" ? "error" : "minus"} size={15} />}</span>
            <span className="add-machine-step-main"><strong>{t(serverStepTitle(step))}</strong>
              {steps[step]?.detail && steps[step].state !== "failed" && <small className="muted-text">{steps[step].detail}</small>}</span>
          </li>)}
        </ol>
        {failed && <Callout compact intent="warning" role="alert" className="add-machine-failure">{t(serverSetupProblem(failed.code), { host: shownHost || computer, port })}
          <SetupFailureDetail code={failed.code} detail={failed.detail} />
          {!running && serverAsksSudoPassword(failed.code) && <label className="add-machine-field add-machine-sudo"><span>{t("Password on the machine")}</span>
            <InputGroup type="password" autoFocus fill value={sudoPassword} autoComplete="off" aria-label={t("Password on the machine")}
              onChange={event => setSudoPassword(event.currentTarget.value)} onKeyDown={event => { if (event.key === "Enter" && sudoPassword) void create(null, offersNetworkPublish(failed.code) || undefined); }} /></label>}</Callout>}
        {linkId && <>
          <Callout compact intent="success" role="status">{t(where === "local" ? "Your server runs on this computer, and you are its owner." : "Your server runs on {host}, and you are its owner.",
            { host: shownHost })}</Callout>
          {recoveryWords && <p className="muted-text create-server-recovery-note">{t("Done shows your recovery key next, once.")}</p>}
          {/* The owner's own sessions show on the server only from a linked space: offered here, all of them in one click. */}
          <ServerSpaces epoch={epoch} linkId={linkId} linked={noSpaces} api={spacesApi?.link} spaces={spacesApi?.list} />
          <AddMachines epoch={epoch} linkId={linkId} api={machinesApi} />
        </>}
      </DialogBody>
      <DialogFooter actions={<>
        {!running && failed && <Button text={t("Back")} onClick={() => { setSteps(null); setFailed(null); setTailscaleServe(false); }} />}
        {!running && shownKey && <Button intent="primary" text={t("Trust it and start over")} title={t("Every step runs again from the first one.")}
          onClick={() => void create(shownKey)} />}
        {!running && failed && !shownKey && <Button intent={offersNetworkPublish(failed.code) ? "none" : "primary"} icon={<AppIcon name="refresh" size={14} />} text={t("Start over")}
          disabled={serverAsksSudoPassword(failed.code) && !offersNetworkPublish(failed.code) && !sudoPassword} title={t("Every step runs again from the first one.")}
          onClick={() => void create(null)} />}
        {!running && failed && offersNetworkPublish(failed.code) && <Button intent="primary" text={t(offersTailscaleServe(failed.code) ? "Publish it with Tailscale Serve" : "Publish with its private network")} disabled={serverAsksSudoPassword(failed.code) && !sudoPassword}
          title={t("Every step runs again from the first one, and CodeAlta runs this command on the machine.")} onClick={() => void create(null, true)} />}
        {linkId && <Button intent="primary" text={t("Done")} onClick={() => onClose(true, recoveryWords)} />}
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
