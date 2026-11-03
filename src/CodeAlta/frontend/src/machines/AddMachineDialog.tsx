import { useEffect, useRef, useState } from "react";
import { Button, Callout, Collapse, Dialog, DialogBody, DialogFooter, InputGroup, NumericInput, Tag } from "@blueprintjs/core";
import { fleetServer, machines, type FleetDeviceView, type FleetServerView, type MachineSetupEvent } from "#neoastra";
import { AppIcon, type IconName } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { joinCommand, keyWords } from "../fleet/fleetServer";
import { setupProblem, setupSteps, sshTargetOf, stepTitle, type SetupStep } from "./addMachine";

/** The calls of the assistant. */
export type AddMachineApi = Pick<typeof machines, "addOverSsh">;

/** The calls of the assistant on the servers of this computer. */
export type AddMachineServerApi = Pick<typeof fleetServer, "status" | "createToken" | "devices" | "approve">;

type Choice = "ssh" | "link" | "server";

/**
 * Add a machine…: how the machine is reached, then a checklist that shows what each step checks and changes. Over SSH
 * the desktop installs and pairs by itself; a machine that runs CodeAlta Node already pairs with its link; through a
 * server, the machine runs one command and this computer approves it.
 */
export function AddMachineDialog({ epoch, api = machines, serverApi = fleetServer, onPairLink, onClose, onOpen }: {
  epoch: string; api?: AddMachineApi; serverApi?: AddMachineServerApi; onPairLink: () => void; onClose: (added: boolean) => void; onOpen: (machineId: string) => void;
}) {
  const { t } = useShellLanguage();
  const [choice, setChoice] = useState<Choice | null>(null);
  const [server, setServer] = useState<FleetServerView | null | undefined>(undefined);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    serverApi.status({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 10_000 }).then(value => {
      if (abort.signal.aborted) return;
      // The server this computer is an owner's controller of, linked and approved.
      setServer(value.servers.find(item => item.controller && item.link === "approved") ?? null);
    }, () => { if (!abort.signal.aborted) setServer(null); });
    return () => abort.abort();
  }, [epoch, serverApi]);
  return <Dialog isOpen className="add-machine-dialog" title={t("Add a machine")} canOutsideClickClose={!busy && choice === null} canEscapeKeyClose={!busy}
    onClose={() => { if (!busy) onClose(false); }}>
    {choice === null && <DialogBody>
      <p className="muted-text">{t("How does this computer reach the machine?")}</p>
      <div className="add-machine-choices" role="list">
        <ChoiceCard icon="terminal" title="Over SSH" recommended
          description="CodeAlta installs CodeAlta Node there, starts it as a service and pairs it with this computer. Nothing to type on the machine." onClick={() => setChoice("ssh")} />
        <ChoiceCard icon="remote" title="With a pairing link"
          description="CodeAlta Node runs there already: paste the link its altanode pair command shows." onClick={() => { onPairLink(); }} />
        <ChoiceCard icon="server" title={server ? "Through my server" : "Through my server"} disabled={!server}
          description={server ? "The machine joins your server with one command; this computer approves it." : "Create your server first, in Settings > Fleet > Server."}
          detail={server?.organization ?? undefined} onClick={() => setChoice("server")} />
      </div>
    </DialogBody>}
    {choice === "ssh" && <SshSetup epoch={epoch} api={api} onBusy={setBusy} onBack={() => setChoice(null)} onClose={onClose} onOpen={onOpen} />}
    {choice === "server" && server && <ServerJoin epoch={epoch} server={server} api={serverApi} onBusy={setBusy} onBack={() => setChoice(null)} onClose={onClose} />}
  </Dialog>;
}

function ChoiceCard({ icon, title, description, detail, recommended, disabled, onClick }: {
  icon: IconName; title: MessageKey; description: MessageKey; detail?: string; recommended?: boolean; disabled?: boolean; onClick: () => void;
}) {
  const { t } = useShellLanguage();
  return <button type="button" role="listitem" className="add-machine-choice" disabled={disabled} onClick={onClick}>
    <span className="add-machine-choice-icon"><AppIcon name={icon} size={20} /></span>
    <span className="add-machine-choice-main">
      <strong>{t(title)}{recommended && <Tag minimal round intent="primary">{t("Recommended")}</Tag>}{detail && <span className="muted-text"> · {detail}</span>}</strong>
      <span className="muted-text">{t(description)}</span>
    </span>
    <AppIcon name="chevronRight" size={16} />
  </button>;
}

type StepState = { state: "waiting" | "running" | "done" | "failed"; detail?: string | null };
type Outcome = { failed?: { step: SetupStep; code: string; detail?: string | null }; machineId?: string; name?: string | null; words?: string | null } | null;

/** The form of a setup over SSH, then its checklist as the host reports it. */
function SshSetup({ epoch, api, onBusy, onBack, onClose, onOpen }: {
  epoch: string; api: AddMachineApi; onBusy: (busy: boolean) => void; onBack: () => void; onClose: (added: boolean) => void; onOpen: (machineId: string) => void;
}) {
  const { t } = useShellLanguage();
  const [target, setTarget] = useState("");
  const [name, setName] = useState("");
  const [advanced, setAdvanced] = useState(false);
  const [port, setPort] = useState(2585);
  const [dataRoot, setDataRoot] = useState("");
  const [toolPath, setToolPath] = useState("");
  const [packages, setPackages] = useState("");
  const [steps, setSteps] = useState<Record<SetupStep, StepState> | null>(null);
  const [outcome, setOutcome] = useState<Outcome>(null);
  const [running, setRunning] = useState(false);
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  useEffect(() => onBusy(running), [onBusy, running]);
  const parsed = sshTargetOf(target);

  async function start(acceptNewHostKey: boolean) {
    if (!parsed) return;
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    setRunning(true);
    setOutcome(null);
    setSteps(Object.fromEntries(setupSteps.map(step => [step, { state: "waiting" }])) as Record<SetupStep, StepState>);
    try {
      const events = await api.addOverSsh({ expectedEpoch: epoch, target: parsed, name: name.trim() || null, port, dataRoot: dataRoot.trim() || null,
        toolPath: toolPath.trim() || null, packageFolder: packages.trim() || null, acceptNewHostKey }, { signal: controller.signal });
      for await (const event of events) {
        if (controller.signal.aborted) return;
        apply(event);
      }
    } catch {
      if (!controller.signal.aborted) setOutcome(value => value ?? { failed: { step: "connect", code: "unavailable" } });
    } finally {
      if (!controller.signal.aborted) setRunning(false);
    }
  }

  function apply(event: MachineSetupEvent) {
    if (event.step === "finished") { setOutcome({ machineId: event.machineId ?? undefined, name: event.machineName, words: event.words }); return; }
    const step = event.step as SetupStep;
    if (!setupSteps.includes(step)) return;
    setSteps(current => current && { ...current, [step]: { state: event.state === "done" ? "done" : event.state === "failed" ? "failed" : "running", detail: event.detail ?? current[step]?.detail } });
    if (event.state === "failed") setOutcome({ failed: { step, code: event.code ?? "unavailable", detail: event.detail } });
  }

  const failure = outcome?.failed;
  const problem = failure ? setupProblem(failure.code) : null;
  if (!steps) return <>
    <DialogBody>
      <label className="add-machine-field"><span>{t("Computer")}</span>
        <InputGroup autoFocus fill value={target} placeholder="user@host" aria-label={t("Computer")} onChange={event => setTarget(event.currentTarget.value)}
          onKeyDown={event => { if (event.key === "Enter" && parsed) void start(false); }} />
        <small className="muted-text">{t("As you would type it after ssh: user@host, user@host:port, or a host of your SSH configuration. Your SSH keys and agent sign in.")}</small>
      </label>
      {target.trim() && !parsed && <Callout compact intent="warning">{t("Give the computer as user@host, or a host of your SSH configuration.")}</Callout>}
      <label className="add-machine-field"><span>{t("Name")}</span>
        <InputGroup fill value={name} placeholder={t("The name of the computer")} aria-label={t("Name")} onChange={event => setName(event.currentTarget.value)} />
      </label>
      <Button variant="minimal" size="small" icon={<AppIcon name={advanced ? "chevronDown" : "chevronRight"} size={14} />} text={t("Advanced")} onClick={() => setAdvanced(value => !value)} />
      <Collapse isOpen={advanced}>
        <div className="add-machine-advanced">
          <label className="add-machine-field"><span>{t("Port of the node")}</span>
            <NumericInput min={1} max={65535} value={port} onValueChange={value => setPort(Number.isFinite(value) ? value : 2585)} aria-label={t("Port of the node")} /></label>
          <label className="add-machine-field"><span>{t("Profile on the machine")}</span>
            <InputGroup fill value={dataRoot} placeholder="~/.alta" aria-label={t("Profile on the machine")} onChange={event => setDataRoot(event.currentTarget.value)} /></label>
          <label className="add-machine-field"><span>{t("Install folder on the machine")}</span>
            <InputGroup fill value={toolPath} placeholder="~/.dotnet/tools" aria-label={t("Install folder on the machine")} onChange={event => setToolPath(event.currentTarget.value)} /></label>
          <label className="add-machine-field"><span>{t("Packages from a folder of this computer")}</span>
            <InputGroup fill value={packages} placeholder="nuget.org" aria-label={t("Packages from a folder of this computer")} onChange={event => setPackages(event.currentTarget.value)} /></label>
        </div>
      </Collapse>
    </DialogBody>
    <DialogFooter actions={<>
      <Button text={t("Back")} onClick={onBack} />
      <Button intent="primary" disabled={!parsed} text={t("Add the machine")} onClick={() => void start(false)} />
    </>} />
  </>;

  return <>
    <DialogBody>
      <ol className="add-machine-steps" aria-label={t("Setup")}>
        {setupSteps.map(step => <li key={step} data-state={steps[step].state}>
          <span className="add-machine-step-icon">{steps[step].state === "running" ? <ActivitySpinner size={14} />
            : <AppIcon name={steps[step].state === "done" ? "checked" : steps[step].state === "failed" ? "error" : "minus"} size={15} />}</span>
          <span className="add-machine-step-main"><strong>{t(stepTitle(step))}</strong>
            {steps[step].detail && <small className="muted-text">{steps[step].detail}</small>}</span>
        </li>)}
      </ol>
      {failure && problem && <Callout compact intent="warning" role="alert" className="add-machine-failure">{t(problem, { host: parsed ?? target })}
        {failure.detail && <pre className="add-machine-detail">{failure.detail}</pre>}</Callout>}
      {outcome?.machineId && <Callout compact intent="success" role="status">
        {t("{name} is paired with this computer and managed from here.", { name: outcome.name ?? parsed ?? target })}
        {outcome.words && <div className="pair-machine-words add-machine-words">{keyWords(outcome.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</div>}
      </Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      {running && <Button text={t("Cancel")} onClick={() => { abort.current?.abort(); setRunning(false); setSteps(null); }} />}
      {!running && failure && <Button text={t("Back")} onClick={() => { setSteps(null); setOutcome(null); }} />}
      {!running && failure?.code === "host_key_unknown" && <Button intent="primary" text={t("Trust it and try again")} onClick={() => void start(true)} />}
      {!running && failure && failure.code !== "host_key_unknown" && <Button intent="primary" icon={<AppIcon name="refresh" size={14} />} text={t("Try again")} onClick={() => void start(false)} />}
      {outcome?.machineId && <>
        <Button text={t("Done")} onClick={() => onClose(true)} />
        <Button intent="primary" text={t("Open its page")} onClick={() => onOpen(outcome.machineId!)} />
      </>}
    </>} />
  </>;
}

/**
 * Through my server: a one-time code made here, the command the machine runs, then the machine as it comes: this
 * computer made the code a moment ago, so the one machine that joins with it is approved here at once, its six words shown.
 */
function ServerJoin({ epoch, server, api, onBusy, onBack, onClose }: {
  epoch: string; server: FleetServerView; api: AddMachineServerApi; onBusy: (busy: boolean) => void; onBack: () => void; onClose: (added: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [token, setToken] = useState<string | null>(null);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [joined, setJoined] = useState<FleetDeviceView | null>(null);
  const [several, setSeveral] = useState<FleetDeviceView[]>([]);
  const [copied, setCopied] = useState(false);
  const known = useRef<Set<string> | null>(null);
  useEffect(() => { onBusy(false); }, [onBusy]);
  useEffect(() => {
    const abort = new AbortController();
    void (async () => {
      try {
        // The devices before the code: a machine that comes after it is the one the code brings.
        const before = await api.devices({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 20_000 });
        // A code is made once: not for an effect undone meanwhile (it would wait on the server for an hour, unused).
        if (abort.signal.aborted) return;
        known.current = new Set(before.devices.map(device => device.id));
        const made = await api.createToken({ expectedEpoch: epoch, linkId: server.id, hours: 1, label: null }, { signal: abort.signal, timeoutMilliseconds: 30_000 });
        if (abort.signal.aborted) return;
        if (made.status !== "ok" || !made.token) { setProblem({ key: "The server could not make a code.", detail: made.message }); return; }
        setToken(made.token);
      } catch { if (!abort.signal.aborted) setProblem({ key: "The server does not answer." }); }
    })();
    return () => abort.abort();
  }, [api, epoch, server.id]);
  useEffect(() => {
    if (!token || joined) return;
    const abort = new AbortController();
    const timer = setInterval(() => {
      void api.devices({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 15_000 }).then(async value => {
        if (abort.signal.aborted || value.status !== "ok") return;
        const fresh = value.devices.filter(device => device.status === "pending" && device.kind === "node" && !known.current?.has(device.id));
        if (fresh.length === 1) {
          const approved = await api.approve({ expectedEpoch: epoch, linkId: server.id, deviceId: fresh[0].id, words: null }, { timeoutMilliseconds: 30_000 });
          if (approved.status === "ok") setJoined(approved.device ?? fresh[0]);
          else setProblem({ key: "The server refused the approval.", detail: approved.message });
        } else if (fresh.length > 1) setSeveral(fresh);
      }, () => { });
    }, 2000);
    return () => { abort.abort(); clearInterval(timer); };
  }, [api, epoch, joined, server.id, token]);
  const command = token ? joinCommand(server.url, token) : "";
  return <>
    <DialogBody>
      {!token && !problem && <ActivitySpinner size={16} />}
      {token && !joined && <>
        <p>{t("Run this on the machine, where CodeAlta Node is installed:")}</p>
        <InputGroup readOnly fill value={command} aria-label={t("Command")} onFocus={event => event.currentTarget.select()}
          rightElement={<Button variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} aria-label={t("Copy")} title={t("Copy")}
            onClick={() => void navigator.clipboard?.writeText(command).then(() => setCopied(true), () => setCopied(false))} />} />
        <p className="muted-text add-machine-waiting"><ActivitySpinner size={12} /> {t("Waiting for the machine to join {server}. This computer made the code: it approves the machine as soon as it comes.", { server: server.organization ?? server.url })}</p>
        {several.length > 0 && <Callout compact intent="warning">{t("Several machines came at once: approve the right one in Settings > Fleet > Server.")}</Callout>}
      </>}
      {joined && <Callout compact intent="success" role="status">{t("{name} joined {server} and is approved. It shows these words:", { name: joined.name, server: server.organization ?? server.url })}
        <div className="pair-machine-words add-machine-words">{keyWords(joined.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</div></Callout>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      {!joined && <Button text={t("Back")} onClick={onBack} />}
      <Button intent={joined ? "primary" : "none"} text={t(joined ? "Done" : "Close")} onClick={() => onClose(!!joined)} />
    </>} />
  </>;
}
