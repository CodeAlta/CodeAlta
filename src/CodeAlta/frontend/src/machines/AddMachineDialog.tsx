import { useEffect, useRef, useState } from "react";
import { Button, Callout, Checkbox, Collapse, Dialog, DialogBody, DialogFooter, InputGroup, NumericInput, Tag } from "@blueprintjs/core";
import { fleetServer, machines, type FleetDeviceView, type FleetServerView, type MachineSetupEvent } from "#neoastra";
import { AppIcon, type IconName } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { joinCommand, keyWords } from "../fleet/fleetServer";
import { asksSudoPassword, hostKeyOf, setupProblem, setupSteps, shownSetupSteps, sshTargetOf, stepTitle, type SetupStep } from "./addMachine";

/** What a failed step of a setup over SSH said: the SSH key of a computer ssh does not know yet, else the words of the step. */
export function SetupFailureDetail({ code, detail }: { code: string; detail: string | null | undefined }) {
  const { t } = useShellLanguage();
  const key = hostKeyOf(code, detail);
  if (key) return <div className="add-machine-host-key"><span className="muted-text">{t("Its SSH key:")}</span> <code>{key}</code></div>;
  return detail ? <pre className="add-machine-detail">{detail}</pre> : null;
}

/** The calls of the assistant. */
export type AddMachineApi = Pick<typeof machines, "addOverSsh">;

/** The calls of the assistant on the servers of this computer. */
export type AddMachineServerApi = Pick<typeof fleetServer, "status" | "createToken" | "devices" | "approve">;

type Choice = "ssh" | "link" | "server";

/**
 * Add a machine…: how the machine is reached, then a checklist that shows what each step checks and changes. Over SSH
 * the desktop installs and pairs by itself; a machine that runs CodeAlta Node already pairs with its link; through a
 * server, the machine runs one command and the person approves it here once it shows the same words.
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
          description="CodeAlta Desktop or CodeAlta Node runs there: paste the link of its Pair another computer…, or of altanode pair." onClick={() => { onPairLink(); }} />
        <ChoiceCard icon="server" title={server ? "Through my server" : "Through my server"} disabled={!server}
          description={server ? "The machine joins your server with one command; you approve it here when it shows the same words." : "Create your server first, in Settings > Fleet > Server."}
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
type Steps = Partial<Record<SetupStep, StepState>>;

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
  // Managing the machine from here (its devices, secrets, updates) is asked, never implied by being the first.
  const [manage, setManage] = useState(false);
  // Always on asks sudo there; its password is asked only when the check says so, and forgotten once sent.
  const [alwaysOn, setAlwaysOn] = useState(false);
  const [sudoPassword, setSudoPassword] = useState("");
  const [steps, setSteps] = useState<Steps | null>(null);
  const [outcome, setOutcome] = useState<Outcome>(null);
  // The machine is paired whatever its server step says: a failure there is told beside the outcome, not as the setup's.
  const [serverProblem, setServerProblem] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const abort = useRef<AbortController | null>(null);
  useEffect(() => () => abort.current?.abort(), []);
  useEffect(() => onBusy(running), [onBusy, running]);
  const parsed = sshTargetOf(target);

  // "Trust it" trusts the SSH key shown, by its fingerprint: every connection of the setup requires that key.
  async function start(trustedHostKey: string | null) {
    if (!parsed) return;
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    setRunning(true);
    setOutcome(null);
    setServerProblem(null);
    setSteps(Object.fromEntries(setupSteps.map(step => [step, { state: "waiting" }])) as Steps);
    try {
      const password = alwaysOn && sudoPassword ? sudoPassword : null;
      setSudoPassword("");
      const events = await api.addOverSsh({ expectedEpoch: epoch, target: parsed, name: name.trim() || null, port, dataRoot: dataRoot.trim() || null,
        toolPath: toolPath.trim() || null, packageFolder: packages.trim() || null, trustedHostKey, manage, alwaysOn, sudoPassword: password }, { signal: controller.signal });
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
    if (!setupSteps.includes(step) && step !== "server") return;
    setSteps(current => current && { ...current, [step]: { state: event.state === "done" ? "done" : event.state === "failed" ? "failed" : "running", detail: event.detail ?? current[step]?.detail } });
    if (event.state === "failed" && step === "server") setServerProblem(event.detail ?? event.code ?? "");
    else if (event.state === "failed") setOutcome({ failed: { step, code: event.code ?? "unavailable", detail: event.detail } });
  }

  const failure = outcome?.failed;

  // The SSH key shown of a computer ssh does not know yet, which "Trust it" trusts.

  const shownKey = failure ? hostKeyOf(failure.code, failure.detail) : null;
  const problem = failure ? setupProblem(failure.code) : null;
  if (!steps) return <>
    <DialogBody>
      <label className="add-machine-field"><span>{t("Computer")}</span>
        <InputGroup autoFocus fill value={target} placeholder="user@host" aria-label={t("Computer")} onChange={event => setTarget(event.currentTarget.value)}
          onKeyDown={event => { if (event.key === "Enter" && parsed) void start(null); }} />
        <small className="muted-text">{t("As you would type it after ssh: user@host, user@host:port, or a host of your SSH configuration. Your SSH keys and agent sign in.")}</small>
      </label>
      {target.trim() && !parsed && <Callout compact intent="warning">{t("Give the computer as user@host, or a host of your SSH configuration.")}</Callout>}
      <label className="add-machine-field"><span>{t("Name")}</span>
        <InputGroup fill value={name} placeholder={t("The name of the computer")} aria-label={t("Name")} onChange={event => setName(event.currentTarget.value)} />
      </label>
      <Checkbox className="add-machine-manage" checked={manage} onChange={event => setManage(event.currentTarget.checked)}
        labelElement={<span>{t("Manage it from this computer")} <span className="muted-text">{t("its devices, projects, secrets and updates")}</span></span>} />
      <Checkbox className="add-machine-always-on" checked={alwaysOn} onChange={event => setAlwaysOn(event.currentTarget.checked)}
        labelElement={<span>{t("Always on")} <span className="muted-text">{t("it starts with the machine, with nobody logged in (needs sudo there)")}</span></span>} />
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
      <Button intent="primary" disabled={!parsed} text={t("Add the machine")} onClick={() => void start(null)} />
    </>} />
  </>;

  return <>
    <DialogBody>
      <ol className="add-machine-steps" aria-label={t("Setup")}>
        {shownSetupSteps(steps).map(step => {
          const state = steps[step] ?? { state: "waiting" };
          return <li key={step} data-state={state.state}>
            <span className="add-machine-step-icon">{state.state === "running" ? <ActivitySpinner size={14} />
              : <AppIcon name={state.state === "done" ? "checked" : state.state === "failed" ? "error" : "minus"} size={15} />}</span>
            <span className="add-machine-step-main"><strong>{t(stepTitle(step))}</strong>
              {state.detail && state.state !== "failed" && <small className="muted-text">{state.detail}</small>}</span>
          </li>;
        })}
      </ol>
      {failure && problem && <Callout compact intent="warning" role="alert" className="add-machine-failure">{t(problem, { host: parsed ?? target })}
        <SetupFailureDetail code={failure.code} detail={failure.detail} />
        {!running && asksSudoPassword(failure.code) && <label className="add-machine-field add-machine-sudo"><span>{t("Password on the machine")}</span>
          <InputGroup type="password" autoFocus fill value={sudoPassword} autoComplete="off" aria-label={t("Password on the machine")}
            onChange={event => setSudoPassword(event.currentTarget.value)} onKeyDown={event => { if (event.key === "Enter" && sudoPassword) void start(null); }} /></label>}</Callout>}
      {outcome?.machineId && <Callout compact intent="success" role="status">
        {t(manage ? "{name} is paired with this computer and managed from here." : "{name} is paired with this computer.", { name: outcome.name ?? parsed ?? target })}
        {outcome.words && <div className="pair-machine-words add-machine-words">{keyWords(outcome.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</div>}
      </Callout>}
      {outcome?.machineId && serverProblem !== null && <Callout compact intent="warning" role="alert" className="add-machine-failure">
        {t("It did not join your server: join it from its page, or with Through my server.")}
        {serverProblem && <pre className="add-machine-detail">{serverProblem}</pre>}
      </Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      {running && <Button text={t("Cancel")} onClick={() => { abort.current?.abort(); setRunning(false); setSteps(null); }} />}
      {!running && failure && <Button text={t("Back")} onClick={() => { setSteps(null); setOutcome(null); }} />}
      {!running && shownKey && <Button intent="primary" text={t("Trust it and start over")} title={t("Every step runs again from the first one.")}
        onClick={() => void start(shownKey)} />}
      {!running && failure && !shownKey && <Button intent="primary" icon={<AppIcon name="refresh" size={14} />} text={t("Start over")}
        disabled={asksSudoPassword(failure.code) && !sudoPassword}
        title={t("Every step runs again from the first one.")} onClick={() => void start(null)} />}
      {outcome?.machineId && <>
        <Button text={t("Done")} onClick={() => onClose(true)} />
        <Button intent="primary" text={t("Open its page")} onClick={() => onOpen(outcome.machineId!)} />
      </>}
    </>} />
  </>;
}

/**
 * Through my server: a one-time code made here, the command the machine runs, then the nodes that come after the code,
 * each with the six words of its key. The server lists them, so any of them may be of its own making: the person approves
 * the one that shows the words `altanode join` printed on the machine, and the host signs that key only, as a node.
 */
function ServerJoin({ epoch, server, api, onBusy, onBack, onClose }: {
  epoch: string; server: FleetServerView; api: AddMachineServerApi; onBusy: (busy: boolean) => void; onBack: () => void; onClose: (added: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [token, setToken] = useState<string | null>(null);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [joined, setJoined] = useState<FleetDeviceView | null>(null);
  const [pending, setPending] = useState<FleetDeviceView[]>([]);
  const [approving, setApproving] = useState<string | null>(null);
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
      void api.devices({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 15_000 }).then(value => {
        if (abort.signal.aborted || value.status !== "ok") return;
        // Nothing is approved by itself, not even a node alone: it waits for the person to compare its words with the machine's.
        setPending(value.devices.filter(device => device.status === "pending" && device.kind === "node" && !known.current?.has(device.id)));
      }, () => { });
    }, 2000);
    return () => { abort.abort(); clearInterval(timer); };
  }, [api, epoch, joined, server.id, token]);
  async function approve(device: FleetDeviceView) {
    setApproving(device.id);
    setProblem(null);
    try {
      // The words the person confirmed, as a node: the host signs the key of those words only, never a controller.
      const approved = await api.approve({ expectedEpoch: epoch, linkId: server.id, deviceId: device.id, words: device.words, kind: "node", confirmWith: null, handle: null }, { timeoutMilliseconds: 30_000 });
      if (approved.status === "ok") setJoined(approved.device ?? device);
      else setProblem({ key: "The server refused the approval.", detail: approved.message });
    } catch { setProblem({ key: "The server does not answer." }); }
    finally { setApproving(null); }
  }
  const command = token ? joinCommand(server.shareUrl ?? server.url, token) : "";
  return <>
    <DialogBody>
      {!token && !problem && <ActivitySpinner size={16} />}
      {token && !joined && <>
        <p>{t("Run this on the machine, where CodeAlta Node is installed:")}</p>
        <InputGroup readOnly fill value={command} aria-label={t("Command")} onFocus={event => event.currentTarget.select()}
          rightElement={<Button variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} aria-label={t("Copy")} title={t("Copy")}
            onClick={() => void navigator.clipboard?.writeText(command).then(() => setCopied(true), () => setCopied(false))} />} />
        <p className="muted-text add-machine-waiting"><ActivitySpinner size={12} /> {t("Waiting for the machine to join {server}. The command prints the words of its key there: approve the machine here when it shows the same words.", { server: server.organization ?? server.url })}</p>
        {pending.length > 0 && <ul className="add-machine-joining" aria-label={t("Machines that wait")}>
          {pending.map(device => <li key={device.id}>
            <span className="add-machine-joining-main"><strong>{device.name}</strong>
              <span className="pair-machine-words add-machine-words">{keyWords(device.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</span></span>
            <Button size="small" intent="primary" loading={approving === device.id} disabled={approving !== null} text={t("It shows the same words")} onClick={() => void approve(device)} />
          </li>)}
        </ul>}
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
