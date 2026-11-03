import { useEffect, useRef, useState } from "react";
import { Button, Callout, Dialog, DialogBody, DialogFooter, NonIdealState, PopoverNext, Switch, Tag, TextArea } from "@blueprintjs/core";
import { machines, type MachinePairPreview, type MachineView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { machineIntent, machineState, machineStateLabel, orderedMachines, pairingLinkOf, pairingProblem, pairingWords } from "./machines";
import { MachinePage, type MachinePageApi } from "./MachinePage";
import { managesMachine } from "./machineManagement";
import { AddMachineDialog, type AddMachineApi } from "./AddMachineDialog";

type MachinesApi = Pick<typeof machines, "list" | "previewPairing" | "pair" | "forget" | "watch" | "moveSettings" | "setReceiveMoves"> & Partial<MachinePageApi> & Partial<AddMachineApi>;

/**
 * Settings > Fleet > Machines: the machines this computer is paired with and their state, pairing with another one
 * from the link `altanode pair` shows there, and forgetting one.
 */
export function MachinesSettings({ epoch, api = machines }: { epoch: string | null; api?: MachinesApi }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(options => api.list({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const [pairing, setPairing] = useState(false);
  // Add a machine…: how it is reached, then its setup.
  const [adding, setAdding] = useState(false);
  // The machine whose page is open, by its id; its row opens it.
  const [opened, setOpened] = useState<string | null>(null);
  // Whether this computer receives the sessions its machines move here: off until the person turns it on.
  const [receive, setReceive] = useState<boolean | null>(null);
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    api.moveSettings({ expectedEpoch: epoch }, { signal: abort.signal }).then(value => { if (value.status === "ok") setReceive(value.receiveMoves); }, () => { });
    return () => abort.abort();
  }, [api, epoch, listing]);
  // The host says when a machine changes (its state, its sessions, a pairing elsewhere): the list is read again.
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    void (async () => {
      try { for await (const _ of await api.watch({ expectedEpoch: epoch }, { signal: abort.signal })) if (!abort.signal.aborted) reload(); }
      catch { /* The bridge is gone: Reload still reads the list. */ }
    })();
    return () => abort.abort();
  }, [api, epoch, reload]);
  const openedMachine = opened ? listing?.machines.find(machine => machine.id === opened && machine.paired) : undefined;
  if (openedMachine && epoch) return <MachinePage epoch={epoch} machine={openedMachine} api={{ ...machines, ...api } as MachinePageApi} onBack={() => setOpened(null)} />;
  return <SettingsPage className="machines-settings" label={t("Machines")} group="Fleet" title="Paired machines"
    description="The computers this one is paired with: their sessions are listed and driven from here."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={<Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!epoch || !listing} onClick={() => setAdding(true)}>{t("Add a machine…")}</Button>}>
    {!listing ? <SettingsUnavailable loading={loading} icon="remote" title="Machines unavailable" />
      // The machines of a server reached through its relay are its own business: only the paired ones are here.
      : !listing.machines.some(machine => machine.paired) ? <NonIdealState className="machines-empty" icon={<AppIcon name="remote" size={36} />} title={t("No paired machine.")}
        description={t("Another computer of yours can run sessions for you: a Mac mini that stays on, a build machine, a cloud server. CodeAlta installs CodeAlta Node there over SSH, or pairs with one that runs already.")}
        action={<Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!epoch} onClick={() => setAdding(true)}>{t("Add a machine…")}</Button>} />
      : <><ul className="machine-list" aria-label={t("Machines")}>
        {orderedMachines(listing.machines.filter(machine => machine.paired)).map(machine => <MachineRow key={machine.id} machine={machine} busy={busy}
          onOpen={() => setOpened(machine.id)} onForget={() => void mutate(async () => api.forget({ expectedEpoch: epoch ?? "", machineId: machine.id }, { timeoutMilliseconds: 30_000 }))} />)}
      </ul>
      {receive !== null && <div className="machine-receive">
        <Switch checked={receive} disabled={busy} label={t("Receive moved sessions on this computer")}
          onChange={event => { const enabled = event.currentTarget.checked; void api.setReceiveMoves({ expectedEpoch: epoch ?? "", enabled })
            .then(value => { if (value.status === "ok") setReceive(value.receiveMoves); }, () => { }); }} />
        <small className="muted-text">{t("Sessions your paired machines move here then run on this computer.")}</small>
      </div>}</>}
    {pairing && epoch && <PairMachineDialog epoch={epoch} api={api} guide onClose={paired => { setPairing(false); if (paired) reload(); }} />}
    {adding && epoch && <AddMachineDialog epoch={epoch} api={{ ...machines, ...api } as AddMachineApi} onPairLink={() => { setAdding(false); setPairing(true); }}
      onClose={added => { setAdding(false); if (added) reload(); }} onOpen={machineId => { setAdding(false); reload(); setOpened(machineId); }} />}
  </SettingsPage>;
}

/** One paired machine: its name, state and what it runs, the start of its key, its page, and Forget. */
export function MachineRow({ machine, busy, onOpen, onForget }: { machine: MachineView; busy: boolean; onOpen?: () => void; onForget: () => void }) {
  const { t } = useShellLanguage();
  const state = machineState(machine);
  const details = [machine.os, machine.version && `CodeAlta ${machine.version}`, machine.address,
    t(machine.sessions === 1 ? "{count} session" : "{count} sessions", { count: machine.sessions })].filter(Boolean).join(" · ");
  return <li className="machine-row" data-state={state}>
    <span className="machine-icon"><AppIcon name="remote" size={18} /></span>
    <div className="machine-main">
      <div className="machine-title">{onOpen ? <button type="button" className="machine-open" onClick={onOpen} title={t("Open the page of {name}", { name: machine.name })}><strong>{machine.name}</strong></button>
        : <strong>{machine.name}</strong>}
        <Tag minimal round intent={machineIntent(state)} className="machine-state">{t(machineStateLabel(state))}</Tag>
        {managesMachine(machine) && <Tag minimal round icon={<AppIcon name="shield" size={11} />} className="machine-manages">{t("Managed from here")}</Tag>}</div>
      <div className="machine-details muted-text">{details}</div>
      {machine.problem && state !== "online" && <div className="machine-problem muted-text" title={machine.problem}>{machine.problem}</div>}
    </div>
    <code className="machine-fingerprint" title={machine.words}>{machine.fingerprint}</code>
    {onOpen && <Button variant="minimal" size="small" icon={<AppIcon name="chevronRight" size={15} />} aria-label={t("Open the page of {name}", { name: machine.name })}
      title={t("Open the page of {name}", { name: machine.name })} onClick={onOpen} />}
    <PopoverNext placement="left" content={<div className="machine-forget">
      <p>{t("Forget {name}?", { name: machine.name })}</p>
      <p className="muted-text">{t("Its sessions are no longer shown here; they go on on the machine.")}</p>
      <Button className="bp6-popover-dismiss" intent="danger" disabled={busy} onClick={onForget}>{t("Forget")}</Button></div>}>
      <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={busy}
        aria-label={`${t("Forget")} ${machine.name}`} title={t("Forget")} />
    </PopoverNext>
  </li>;
}

/**
 * Pairing from a link: the link is read as it is pasted, and the name of the machine and the six words of its key are
 * shown before anything connects. Pair connects, pinned to that key.
 */
export function PairMachineDialog({ epoch, api, guide = false, onClose }: { epoch: string; api: Pick<MachinesApi, "previewPairing" | "pair">; guide?: boolean; onClose: (paired: boolean) => void }) {
  const { t } = useShellLanguage();
  const [text, setText] = useState("");
  const [preview, setPreview] = useState<MachinePairPreview | null>(null);
  // What went wrong: words of the window, and what the machine said after them.
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [pairing, setPairing] = useState(false);
  const read = useRef(0);
  const link = pairingLinkOf(text);
  useEffect(() => {
    const ticket = ++read.current;
    setPreview(null);
    setProblem(null);
    if (!text.trim()) return;
    if (!link) { setProblem({ key: "This is not a pairing link." }); return; }
    const abort = new AbortController();
    api.previewPairing({ expectedEpoch: epoch, link }, { signal: abort.signal, timeoutMilliseconds: 10_000 }).then(value => {
      if (ticket !== read.current) return;
      const failed = pairingProblem(value.status);
      if (failed) setProblem({ key: failed }); else setPreview(value);
    }, () => { if (ticket === read.current && !abort.signal.aborted) setProblem({ key: "Machines unavailable" }); });
    return () => abort.abort();
  }, [api, epoch, link, text]);
  async function pair() {
    if (!preview || !link) return;
    setPairing(true);
    setProblem(null);
    try {
      const result = await api.pair({ expectedEpoch: epoch, link }, { timeoutMilliseconds: 60_000 });
      const failed = pairingProblem(result.status);
      if (!failed) { onClose(true); return; }
      setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: "The machine does not answer." });
    } finally { setPairing(false); }
  }
  const words = pairingWords(preview?.words);
  return <Dialog isOpen className="pair-machine-dialog" title={t("Pair a machine")} canOutsideClickClose={false} onClose={() => { if (!pairing) onClose(false); }}>
    <DialogBody>
      {guide && !preview && <div className="pair-machine-guide">
        <p className="muted-text">{t("On the machine, with .NET 10 installed, run these once:")}</p>
        <ol>
          <li><code>dotnet tool install -g CodeAlta.Node</code></li>
          <li><code>altanode install --listen-all</code> <span className="muted-text">{t("starts it as a service")}</span></li>
          <li><code>altanode pair</code> <span className="muted-text">{t("shows the link and six words")}</span></li>
        </ol>
      </div>}
      <label className="pair-machine-field"><span>{t("Pairing link")}</span>
        <TextArea autoFocus fill rows={3} value={text} disabled={pairing} placeholder="codealta-pair://…" aria-label={t("Pairing link")}
          onChange={event => setText(event.currentTarget.value)} />
        <small className="muted-text">{t("Paste the link that altanode pair shows on the machine.")}</small>
      </label>
      {preview && <section className="pair-machine-preview" aria-label={preview.name ?? undefined}>
        <div className="machine-title"><AppIcon name="remote" size={16} /><strong>{preview.name}</strong>
          <span className="muted-text">{preview.addresses.join(", ")}</span></div>
        <p>{t("The machine shows the same words:")}</p>
        <div className="pair-machine-words">{words.map((word, index) => <Tag key={`${index}:${word}`} large minimal>{word}</Tag>)}</div>
        {preview.alreadyPaired && <Callout compact intent="primary">{t("This computer is paired with it already: pairing again replaces it.")}</Callout>}
      </section>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={pairing} text={t("Cancel")} onClick={() => onClose(false)} />
      <Button intent="primary" disabled={!preview || pairing} icon={pairing ? <ActivitySpinner size={14} /> : undefined} text={t("Pair")} onClick={() => void pair()} />
    </>} />
  </Dialog>;
}
