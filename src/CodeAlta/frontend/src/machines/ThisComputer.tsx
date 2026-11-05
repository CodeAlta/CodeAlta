import { useCallback, useEffect, useRef, useState } from "react";
import { Button, Callout, Dialog, DialogBody, DialogFooter, Switch, Tag, TextArea } from "@blueprintjs/core";
import { machines, type MachineDeviceView, type ThisComputerPairingLink, type ThisComputerView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { DeviceTable } from "./MachinePage";
import { pairingWords } from "./machines";
import type { DevicePermission } from "./machineManagement";

/** The calls of the This computer section. */
export type ThisComputerApi = Pick<typeof machines, "thisComputer" | "setVisible" | "setReachableThroughServers" | "createPairingLink" | "waitPairing"
  | "setThisComputerDevicePermission" | "revokeThisComputerDevice">;

/** What a computer paired with this one may be given here: what a desktop serves. */
export const thisComputerPermissions: readonly DevicePermission[] = ["view", "drive", "start", "move"];

/**
 * This computer as a machine of the person's other computers, at the top of Settings > Fleet > Machines: a switch lets
 * them pair with it, Pair another computer… shows the link and the six words to give the other computer, and the
 * computers paired with it are listed with what each may do here. Once it is linked to a server, another switch lets the
 * person's other devices of that organization reach it through the server, without pairing.
 */
export function ThisComputerSection({ epoch, api = machines, generation }: { epoch: string; api?: ThisComputerApi; generation: number }) {
  const [view, setView] = useState<ThisComputerView | null>(null);
  const [busy, setBusy] = useState(false);
  const [pairing, setPairing] = useState(false);
  const [problem, setProblem] = useState<MessageKey | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    api.thisComputer({ expectedEpoch: epoch }, { signal: abort.signal }).then(value => { if (!abort.signal.aborted) setView(value); }, () => { });
    return () => abort.abort();
  }, [api, epoch, generation]);
  const change = useCallback(async (call: () => Promise<ThisComputerView>) => {
    setBusy(true);
    setProblem(null);
    try {
      const value = await call();
      if (value.status === "ok") setView(value);
      else { setProblem(value.status === "write_failed" ? "The setting could not be saved." : "This computer could not do it."); if (value.name) setView(value); }
    } catch {
      setProblem("This computer could not do it.");
    } finally { setBusy(false); }
  }, []);
  if (!view || view.status === "unavailable") return null;
  return <>
    <ThisComputerBody view={view} busy={busy} problem={problem}
      onVisible={visible => void change(() => api.setVisible({ expectedEpoch: epoch, visible }, { timeoutMilliseconds: 30_000 }))}
      onReachable={reachable => void change(() => api.setReachableThroughServers({ expectedEpoch: epoch, reachable }, { timeoutMilliseconds: 30_000 }))}
      onPair={() => setPairing(true)}
      onPermission={(device, permission, allowed) => void change(() => api.setThisComputerDevicePermission({ expectedEpoch: epoch, deviceId: device.id, permission, allowed }))}
      onRevoke={device => void change(() => api.revokeThisComputerDevice({ expectedEpoch: epoch, deviceId: device.id }))} />
    {pairing && <PairThisComputerDialog epoch={epoch} api={api} name={view.name ?? ""} onClose={() => {
      setPairing(false);
      void api.thisComputer({ expectedEpoch: epoch }).then(setView, () => { });
    }} />}
  </>;
}

/** What the This computer section shows, from what the host said: the switch, where it is reached, and its paired computers. */
export function ThisComputerBody({ view, busy, problem, onVisible, onReachable, onPair, onPermission, onRevoke }: {
  view: ThisComputerView; busy: boolean; problem?: MessageKey | null; onVisible: (visible: boolean) => void; onReachable: (reachable: boolean) => void; onPair: () => void;
  onPermission: (device: MachineDeviceView, permission: DevicePermission, allowed: boolean) => void; onRevoke: (device: MachineDeviceView) => void;
}) {
  const { t } = useShellLanguage();
  return <section className="machine-section this-computer" aria-label={t("This computer")}>
    <div className="machine-section-head">
      <h2><AppIcon name="computer" size={16} />{t("This computer")}{view.name && <span className="muted-text">{view.name}</span>}</h2>
      {view.visible && view.listening && <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={busy} onClick={onPair}>{t("Pair another computer…")}</Button>}
    </div>
    <Switch checked={view.visible} disabled={busy} label={t("Let my other computers pair with this one")} onChange={event => onVisible(event.currentTarget.checked)} />
    <p className="muted-text">{t("Another CodeAlta Desktop of yours pairs with this computer as with a machine: it sees, drives and starts its sessions while CodeAlta runs here, encrypted from end to end. Nothing is reachable until you turn it on.")}</p>
    {view.visible && view.listening && <div className="this-computer-reach muted-text">
      {view.tailnet && <div><Tag minimal round intent="success" icon={<AppIcon name="remote" size={11} />}>{view.network ?? "Tailscale"}</Tag> {t("On your private network as {name}: reached away from home too.", { name: view.tailnet })}</div>}
      <div>{t("Reached at {addresses}", { addresses: view.addresses.join(", ") })}</div>
    </div>}
    {view.visible && !view.listening && <Callout compact intent="warning" role="alert">{t("This computer cannot listen for your other computers.")}
      {view.problem && <div className="muted-text">{view.problem}</div>}</Callout>}
    {(view.servers.length > 0 || view.reachable) && <div className="this-computer-servers">
      <Switch checked={view.reachable} disabled={busy} label={t("Let my other computers reach this one through my server")} onChange={event => onReachable(event.currentTarget.checked)} />
      <p className="muted-text">{view.servers.length > 0
        ? t("Your other computers linked to {servers} list this computer with their machines, and reach it from anywhere through the server, encrypted from end to end, while CodeAlta runs here. No port opens here.", { servers: view.servers.join(", ") })
        : t("This computer is linked to no server now: nothing reaches it this way.")}</p>
    </div>}
    {problem && <Callout compact intent="warning" role="alert">{t(problem)}</Callout>}
    {view.devices.length > 0 ? <>
      <h3>{t("Computers paired with this one")}</h3>
      <DeviceTable devices={view.devices} permissions={thisComputerPermissions} busy={busy} subject={view.name ?? ""} here onPermission={onPermission} onRevoke={onRevoke} />
    </> : view.visible && <p className="muted-text">{t("No other computer is paired with this one yet.")}</p>}
  </section>;
}

/** The outcome of a pairing link, as the dialog says it. */
type Outcome = { kind: "waiting" } | { kind: "paired"; name: string | null } | { kind: "expired" } | { kind: "failed" };

/**
 * Pair another computer: a link for the other computer, made when the dialog opens, with the six words of this computer,
 * which the other one shows before it pairs; the dialog waits until it is used, and says by which computer.
 */
export function PairThisComputerDialog({ epoch, api, name, onClose }: { epoch: string; api: Pick<ThisComputerApi, "createPairingLink" | "waitPairing">; name: string; onClose: () => void }) {
  const { t, locale } = useShellLanguage();
  const [link, setLink] = useState<ThisComputerPairingLink | null>(null);
  const [outcome, setOutcome] = useState<Outcome>({ kind: "waiting" });
  const [copied, setCopied] = useState(false);
  const [generation, setGeneration] = useState(0);
  const open = useRef(true);
  useEffect(() => () => { open.current = false; }, []);
  useEffect(() => {
    const abort = new AbortController();
    setLink(null);
    setCopied(false);
    setOutcome({ kind: "waiting" });
    void (async () => {
      try {
        const made = await api.createPairingLink({ expectedEpoch: epoch }, { signal: abort.signal });
        if (abort.signal.aborted) return;
        setLink(made);
        if (made.status !== "ok") { setOutcome({ kind: "failed" }); return; }
        // The link lives ten minutes: the wait ends with it, or once the other computer used it.
        const result = await api.waitPairing({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 11 * 60_000 });
        if (abort.signal.aborted || !open.current) return;
        setOutcome(result.status === "redeemed" ? { kind: "paired", name: result.deviceName ?? null } : result.status === "expired" || result.status === "replaced" ? { kind: "expired" } : { kind: "failed" });
      } catch {
        if (!abort.signal.aborted) setOutcome({ kind: "failed" });
      }
    })();
    return () => abort.abort();
  }, [api, epoch, generation]);
  const words = pairingWords(link?.words);
  const expires = link?.expiresAt ? new Intl.DateTimeFormat(locale, { timeStyle: "short" }).format(new Date(link.expiresAt)) : null;
  return <Dialog isOpen className="pair-machine-dialog pair-this-computer-dialog" title={t("Pair another computer")} canOutsideClickClose={false} onClose={onClose}>
    <DialogBody>
      {outcome.kind === "paired" ? <Callout intent="success" icon={<AppIcon name="check" size={16} />}>
        {t("{name} is paired with this computer.", { name: outcome.name ?? t("The other computer") })}
        <div className="muted-text">{t("It lists {computer} with its machines, and sees its sessions while CodeAlta runs here.", { computer: name })}</div>
      </Callout> : <>
        <ol className="pair-this-computer-steps">
          <li>{t("On the other computer, in CodeAlta Desktop, open Settings > Fleet > Machines > Add a machine… > With a pairing link.")}</li>
          <li>{t("Paste this link there:")}</li>
        </ol>
        <div className="pair-machine-field">
          <TextArea readOnly fill rows={3} value={link?.link ?? ""} aria-label={t("Pairing link")} onFocus={event => event.currentTarget.select()} />
          <div className="pair-this-computer-copy">
            <Button icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} disabled={!link?.link}
              onClick={() => { if (link?.link) void navigator.clipboard.writeText(link.link).then(() => setCopied(true), () => { /* No clipboard: the link is selected to copy by hand. */ }); }}>
              {t(copied ? "Copied" : "Copy link")}</Button>
            {expires && <small className="muted-text">{t("The link works once, until {time}.", { time: expires })}</small>}
          </div>
        </div>
        {words.length > 0 && <section className="pair-machine-preview">
          <p>{t("The other computer shows these words before it pairs:")}</p>
          <div className="pair-machine-words">{words.map((word, index) => <Tag key={`${index}:${word}`} large minimal>{word}</Tag>)}</div>
        </section>}
        {outcome.kind === "waiting" && link?.status === "ok" && <p className="pair-this-computer-wait muted-text"><ActivitySpinner size={14} /> {t("Waiting for the other computer…")}</p>}
        {outcome.kind === "expired" && <Callout compact intent="warning" role="alert">{t("The link expired: make a new one.")}</Callout>}
        {outcome.kind === "failed" && <Callout compact intent="warning" role="alert">{t(link && link.status === "not_listening" ? "This computer cannot listen for your other computers." : "The link could not be made.")}</Callout>}
      </>}
    </DialogBody>
    <DialogFooter actions={<>
      {(outcome.kind === "expired" || outcome.kind === "failed") && <Button icon={<AppIcon name="refresh" size={14} />} text={t("New link")} onClick={() => setGeneration(value => value + 1)} />}
      <Button intent={outcome.kind === "paired" ? "primary" : "none"} text={t("Close")} onClick={onClose} />
    </>} />
  </Dialog>;
}
