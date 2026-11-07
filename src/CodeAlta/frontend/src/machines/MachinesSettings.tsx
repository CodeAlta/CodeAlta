import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Callout, Checkbox, Dialog, DialogBody, DialogFooter, NonIdealState, Switch, Tag, TextArea } from "@blueprintjs/core";
import { machines, type MachinePairPreview } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { pairingLinkOf, pairingProblem, pairingWords } from "./machines";
import { MachineTable, machineTableView } from "./MachineTable";
import type { TableView } from "../fleet/console/tableRows";
import { FleetHelp } from "../fleet/console/FleetHelp";
import { MachinePage, type MachinePageApi } from "./MachinePage";
import { AddMachineDialog, type AddMachineApi } from "./AddMachineDialog";
import { ThisComputerSection, type ThisComputerApi } from "./ThisComputer";

type MachinesApi = Pick<typeof machines, "list" | "previewPairing" | "pair" | "forget" | "watch" | "moveSettings" | "setReceiveMoves"> & Partial<MachinePageApi> & Partial<AddMachineApi>
  & Partial<ThisComputerApi>;

/**
 * Settings > Fleet > Machines: this computer, which the person's other computers may pair with; the machines this
 * computer is paired with and their state, pairing with another one from the link it shows (Pair another computer… in
 * CodeAlta Desktop, `altanode pair` on a node), and forgetting one.
 */
export function MachinesSettings({ epoch, api = machines, initialMachine = null }: { epoch: string | null; api?: MachinesApi; initialMachine?: string | null }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(options => api.list({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const [pairing, setPairing] = useState(false);
  // Add a machine…: how it is reached, then its setup.
  const [adding, setAdding] = useState(false);
  // The machine whose page is open, by its id; its row opens it, and so does its name in the Fleet canvas.
  const [opened, setOpened] = useState<string | null>(initialMachine);
  // The table as the person left it to open a machine, and that machine: Back finds both.
  const [tableView, setTableView] = useState<TableView>(machineTableView);
  const [returnedFrom, setReturnedFrom] = useState<string | null>(null);
  // Whether this computer receives the sessions its machines move here: off until the person turns it on.
  const [receive, setReceive] = useState<boolean | null>(null);
  // Counts the changes the host said, so that the This computer section reads itself again with the list.
  const [changes, setChanges] = useState(0);
  // One object for the life of the page: the pairing link of the section is made once per opening, not at each render.
  const thisComputerApi = useMemo(() => ({ ...machines, ...api }) as ThisComputerApi, [api]);
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
      try { for await (const _ of await api.watch({ expectedEpoch: epoch }, { signal: abort.signal })) if (!abort.signal.aborted) { reload(); setChanges(value => value + 1); } }
      catch { /* The bridge is gone: Reload still reads the list. */ }
    })();
    return () => abort.abort();
  }, [api, epoch, reload]);
  // One list of the person's machines whatever the path: those paired with this computer and those it reaches through its server.
  const openedMachine = opened ? listing?.machines.find(machine => machine.id === opened) : undefined;
  if (openedMachine && epoch) return <MachinePage epoch={epoch} machine={openedMachine} api={{ ...machines, ...api } as MachinePageApi} onBack={() => { setReturnedFrom(opened); setOpened(null); }} />;
  return <SettingsPage className="machines-settings" label={t("Machines")} group="Fleet" title="Machines"
    description="The computers that run sessions for this one."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={<>
      <FleetHelp text="A machine is another computer of yours that runs sessions for this one: paired with it from a link, set up over SSH, or reached through your server." page="fleet/machines.md" />
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!epoch || !listing} onClick={() => setAdding(true)}>{t("Add a machine…")}</Button>
    </>}>
    {!listing ? <SettingsUnavailable loading={loading} icon="remote" title="Machines unavailable" />
      : listing.machines.length === 0 ? <NonIdealState className="machines-empty" icon={<AppIcon name="remote" size={36} />} title={t("No machine yet.")}
        description={t("Another computer of yours can run sessions for you: another CodeAlta Desktop, a Mac mini that stays on, a build machine, a cloud server. Pair with a CodeAlta Desktop or a CodeAlta Node from its link, or let CodeAlta install CodeAlta Node over SSH.")}
        action={<Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!epoch} onClick={() => setAdding(true)}>{t("Add a machine…")}</Button>} />
      : <><MachineTable machines={listing.machines} busy={busy} onOpen={id => { setReturnedFrom(null); setOpened(id); }} view={tableView} onView={setTableView} returnTo={returnedFrom}
        onForget={machine => void mutate(async () => api.forget({ expectedEpoch: epoch ?? "", machineId: machine.id }, { timeoutMilliseconds: 30_000 }))} />
      {receive !== null && <div className="machine-receive">
        <Switch checked={receive} disabled={busy} label={t("Receive moved sessions on this computer")}
          onChange={event => { const enabled = event.currentTarget.checked; void api.setReceiveMoves({ expectedEpoch: epoch ?? "", enabled })
            .then(value => { if (value.status === "ok") setReceive(value.receiveMoves); }, () => { }); }} />
        <small className="muted-text">{t("Sessions your paired machines move here then run on this computer.")}</small>
      </div>}</>}
    {/* What this computer lets the others do comes after the machines it reaches: it is set once. */}
    {epoch && <ThisComputerSection epoch={epoch} api={thisComputerApi} generation={changes} />}
    {pairing && epoch && <PairMachineDialog epoch={epoch} api={api} guide onClose={paired => { setPairing(false); if (paired) reload(); }} />}
    {adding && epoch && <AddMachineDialog epoch={epoch} api={{ ...machines, ...api } as AddMachineApi} onPairLink={() => { setAdding(false); setPairing(true); }}
      onClose={added => { setAdding(false); if (added) reload(); }} onOpen={machineId => { setAdding(false); reload(); setOpened(machineId); }} />}
  </SettingsPage>;
}

/**
 * Pairing from a link: the link is read as it is pasted, and the name of the machine and the six words of its key are
 * shown before anything connects. Pair connects, pinned to that key. A link of another CodeAlta Desktop offers to pair both
 * ways, on unless the person unticks it: that computer then lists this one with its machines too, after the same words.
 */
export function PairMachineDialog({ epoch, api, guide = false, onClose }: { epoch: string; api: Pick<MachinesApi, "previewPairing" | "pair">; guide?: boolean; onClose: (paired: boolean) => void }) {
  const { t } = useShellLanguage();
  const [text, setText] = useState("");
  const [preview, setPreview] = useState<MachinePairPreview | null>(null);
  // What went wrong: words of the window, and what the machine said after them.
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [pairing, setPairing] = useState(false);
  const [bothWays, setBothWays] = useState(true);
  // Paired, but one way only though both were asked: the dialog says so before it closes.
  const [oneWay, setOneWay] = useState(false);
  const read = useRef(0);
  const link = pairingLinkOf(text);
  useEffect(() => {
    const ticket = ++read.current;
    setPreview(null);
    setProblem(null);
    if (!text.trim()) return;
    if (!link) { setProblem({ key: "This is not a pairing link." }); return; }
    const abort = new AbortController();
    api.previewPairing({ expectedEpoch: epoch, link, bothWays: false }, { signal: abort.signal, timeoutMilliseconds: 10_000 }).then(value => {
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
      const asked = preview.bothWays && bothWays;
      const result = await api.pair({ expectedEpoch: epoch, link, bothWays: asked }, { timeoutMilliseconds: 60_000 });
      const failed = pairingProblem(result.status);
      if (!failed && asked && !result.bothWays) { setOneWay(true); return; }
      if (!failed) { onClose(true); return; }
      setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: "The machine does not answer." });
    } finally { setPairing(false); }
  }
  const words = pairingWords(preview?.words);
  if (oneWay) return <Dialog isOpen className="pair-machine-dialog" title={t("Pair a machine")} canOutsideClickClose={false} onClose={() => onClose(true)}>
    <DialogBody>
      <Callout intent="success" icon={<AppIcon name="check" size={16} />}>{t("{name} is paired with this computer.", { name: preview?.name ?? "" })}
        <div className="muted-text">{t("It does not reach this computer: it pairs one way only.")}</div></Callout>
    </DialogBody>
    <DialogFooter actions={<Button intent="primary" text={t("Close")} onClick={() => onClose(true)} />} />
  </Dialog>;
  return <Dialog isOpen className="pair-machine-dialog" title={t("Pair a machine")} canOutsideClickClose={false} onClose={() => { if (!pairing) onClose(false); }}>
    <DialogBody>
      {guide && !preview && <div className="pair-machine-guide">
        <p className="muted-text">{t("In CodeAlta Desktop on the other computer: Settings > Fleet > Machines, turn on Let my other computers pair with this one, then Pair another computer….")}</p>
        <p className="muted-text">{t("Or on a machine with CodeAlta Node and .NET 10, run these once:")}</p>
        <ol>
          <li><code>dotnet tool install -g CodeAlta.Node</code></li>
          <li><code>altanode install --listen-all</code> <span className="muted-text">{t("starts it as a service")}</span></li>
          <li><code>altanode pair</code> <span className="muted-text">{t("shows the link and six words")}</span></li>
        </ol>
      </div>}
      <label className="pair-machine-field"><span>{t("Pairing link")}</span>
        <TextArea autoFocus fill rows={3} value={text} disabled={pairing} placeholder="codealta-pair://…" aria-label={t("Pairing link")}
          onChange={event => setText(event.currentTarget.value)} />
        <small className="muted-text">{t("Paste the link that Pair another computer… shows in CodeAlta Desktop there, or that altanode pair shows on a node.")}</small>
      </label>
      {preview && <section className="pair-machine-preview" aria-label={preview.name ?? undefined}>
        <div className="machine-title"><AppIcon name="remote" size={16} /><strong>{preview.name}</strong>
          <span className="muted-text">{preview.addresses.join(", ")}</span></div>
        <p>{t("The machine shows the same words:")}</p>
        <div className="pair-machine-words">{words.map((word, index) => <Tag key={`${index}:${word}`} large minimal>{word}</Tag>)}</div>
        {preview.alreadyPaired && <Callout compact intent="primary">{t("This computer is paired with it already: pairing again replaces it.")}</Callout>}
        {preview.bothWays && <div className="pair-machine-both-ways">
          <Checkbox checked={bothWays} disabled={pairing} label={t("Let {name} reach this computer too", { name: preview.name ?? "" })}
            onChange={event => setBothWays(event.currentTarget.checked)} />
          <small className="muted-text">{t(preview.listening
            ? "It lists this computer with its machines, and sees, drives and starts its sessions. Change it or revoke it under Computers paired with this one."
            : "This computer then lets your other computers pair with it, so that the other one reaches it: it lists this computer with its machines, and sees, drives and starts its sessions.")}</small>
        </div>}
      </section>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={pairing} text={t("Cancel")} onClick={() => onClose(false)} />
      <Button intent="primary" disabled={!preview || pairing} icon={pairing ? <ActivitySpinner size={14} /> : undefined} text={t("Pair")} onClick={() => void pair()} />
    </>} />
  </Dialog>;
}
