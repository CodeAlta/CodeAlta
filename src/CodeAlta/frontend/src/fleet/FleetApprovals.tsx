import { useCallback, useEffect, useRef, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { automations, fleetServer, type FleetBudgetHoldView, type FleetDeviceView, type FleetNotificationView, type FleetServerView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { dismissToast, showToast } from "../appToaster";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { keyWords, waitingRuns } from "./fleetServer";
import { continueHeldRun, heldRunToastKey, waitingApprovals, type HeldRunOutcome, type WaitingApproval } from "./approvals";
import { approvalRun, newNotifications, notificationIntent, notificationPhrase, waitingHolds } from "./budgets";

/** The calls of the Fleet button. */
export type FleetApprovalsApi = Pick<typeof fleetServer, "status" | "devices" | "approve" | "revoke" | "watch" | "budgets" | "continueRun" | "stopRun" | "readNotification" | "directory">;

// How often the devices of the servers are read again, beside the changes the host says.
const ReadInterval = 20_000;

/** A run that waits for a reached budget of a server. */
type WaitingHold = { server: FleetServerView; hold: FleetBudgetHoldView };

/** A notification of a server not dismissed yet. */
type NewNotification = { server: FleetServerView; notification: FleetNotificationView };

/**
 * The Fleet button of the title bar, for a computer linked to a server: it opens the Fleet canvas. On a server it owns, a mark counts what waits for the person (devices to
 * approve, runs held by a budget, notifications of budgets and of the health of machines), and each new one comes as a toast
 * wherever the person is: Approve and Refuse for a device, Continue anyway and Stop for a run, Dismiss for a notification.
 */
export function FleetApprovals({ epoch, api = fleetServer, active = false, onOpen }: { epoch: string | null; api?: FleetApprovalsApi; active?: boolean; onOpen: () => void }) {
  const { t } = useShellLanguage();
  const [servers, setServers] = useState<FleetServerView[]>([]);
  const [waiting, setWaiting] = useState<WaitingApproval[]>([]);
  const [holds, setHolds] = useState<WaitingHold[]>([]);
  const [notes, setNotes] = useState<NewNotification[]>([]);
  // The sessions of other machines that wait for the person: a question, a command or a file change to allow.
  const [runs, setRuns] = useState(0);
  // What a toast was shown for: each comes once.
  const announced = useRef(new Set<string>());
  // The toasts this window takes away itself, whose closing is not the person's: the others closed with their ✕ are dismissed.
  const closing = useRef(new Set<string>());
  // The notifications marked read, so that a toast closed twice marks it once.
  const marked = useRef(new Set<string>());
  const close = useCallback((key: string) => { closing.current.add(key); dismissToast(key); }, []);
  const read = useCallback(async (signal: AbortSignal) => {
    if (!epoch) return;
    const status = await api.status({ expectedEpoch: epoch }, { signal, timeoutMilliseconds: 15_000 });
    // The button shows for any server this computer is approved on; what waits for an owner is read on the servers it owns.
    const linked = status.servers.filter(server => server.link === "approved");
    const owned = linked.filter(server => server.controller);
    const lists: { server: FleetServerView; devices: readonly FleetDeviceView[] }[] = [];
    const held: WaitingHold[] = [];
    const fresh: NewNotification[] = [];
    for (const server of owned) {
      const devices = await api.devices({ expectedEpoch: epoch, linkId: server.id }, { signal, timeoutMilliseconds: 15_000 }).catch(() => null);
      if (devices?.status === "ok") lists.push({ server, devices: devices.devices });
      const budgets = await api.budgets({ expectedEpoch: epoch, linkId: server.id }, { signal, timeoutMilliseconds: 15_000 }).catch(() => null);
      if (budgets?.status === "ok") {
        held.push(...waitingHolds(budgets).map(hold => ({ server, hold })));
        fresh.push(...newNotifications(budgets).map(notification => ({ server, notification })));
      }
    }
    // The directory of the server, or of the machines paired with this computer without one.
    const directory = await api.directory({ expectedEpoch: epoch, linkId: null }, { signal, timeoutMilliseconds: 15_000 }).catch(() => null);
    if (signal.aborted) return;
    setRuns(directory ? waitingRuns(directory.machines, directory.sessions) : 0);
    setServers(linked);
    setWaiting(waitingApprovals(lists));
    setHolds(held);
    setNotes(fresh);
  }, [api, epoch]);
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    const again = () => void read(abort.signal).catch(() => { });
    again();
    const timer = setInterval(again, ReadInterval);
    void (async () => {
      try { for await (const _ of await api.watch({ expectedEpoch: epoch }, { signal: abort.signal })) if (!abort.signal.aborted) again(); }
      catch { /* The interval still reads them. */ }
    })();
    return () => { abort.abort(); clearInterval(timer); };
  }, [api, epoch, read]);

  const act = useCallback(async (approval: WaitingApproval, approve: boolean) => {
    if (!epoch) return;
    close(toastKey(approval));
    // An approval names the words the person compared in the toast and the class it said: the host signs the key of those
    // words only, as that class only.
    const request = { expectedEpoch: epoch, linkId: approval.server.id, deviceId: approval.device.id, words: approve ? approval.device.words : null,
      kind: approve ? approval.device.kind : null };
    await (approve ? api.approve(request, { timeoutMilliseconds: 30_000 }) : api.revoke(request, { timeoutMilliseconds: 30_000 })).catch(() => null);
    setWaiting(current => current.filter(item => item.device.id !== approval.device.id || item.server.id !== approval.server.id));
  }, [api, close, epoch]);

  const decide = useCallback(async (item: WaitingHold, carryOn: boolean) => {
    if (!epoch) return;
    close(holdKey(item));
    // Continue anyway is signed by this computer for this run only; Stop needs no signature.
    const request = { expectedEpoch: epoch, linkId: item.server.id, id: item.hold.id };
    await (carryOn ? api.continueRun(request, { timeoutMilliseconds: 30_000 }) : api.stopRun(request, { timeoutMilliseconds: 30_000 })).catch(() => null);
    setHolds(current => current.filter(other => other.hold.id !== item.hold.id || other.server.id !== item.server.id));
  }, [api, close, epoch]);

  // A notification dismissed here, with Dismiss or the ✕ of its toast, is read for the person on the server: no computer of theirs
  // shows it again, and only a change of what it is about (a budget reached after 80%, a new period, a new problem) makes another.
  const markRead = useCallback(async (item: NewNotification) => {
    const key = noteKey(item);
    if (!epoch || marked.current.has(key)) return;
    marked.current.add(key);
    await api.readNotification({ expectedEpoch: epoch, linkId: item.server.id, id: item.notification.id }, { timeoutMilliseconds: 30_000 }).catch(() => null);
    setNotes(current => current.filter(other => other.notification.id !== item.notification.id || other.server.id !== item.server.id));
  }, [api, epoch]);

  const dismiss = useCallback(async (item: NewNotification) => {
    close(noteKey(item));
    await markRead(item);
  }, [close, markRead]);

  // A run that waits for the person is taken over from its message, signed by this computer: the server and its machine check
  // that the person answers the approvals of its automation (its owner, the team that owns it, a grant).
  const takeOver = useCallback(async (item: NewNotification) => {
    const run = approvalRun(item.notification);
    if (!epoch || !run) return;
    const reply = await automations.takeOverOnMachine({ expectedEpoch: epoch, id: run.automationId, machineId: run.machineId, runId: run.runId }, { timeoutMilliseconds: 30_000 })
      .catch(() => null);
    if (reply?.status === "ok") {
      await dismiss(item);
      showToast({ intent: "success", icon: <AppIcon name="check" size={16} />, message: t("You took the run over: answer it in its session on {machine}.", { machine: item.notification.machine ?? "" }) });
    } else {
      showToast({ intent: "danger", icon: <AppIcon name="error" size={16} />, message: reply?.message ?? t("The run could not be taken over.") });
    }
  }, [dismiss, epoch, t]);

  useEffect(() => {
    // What was decided or dismissed elsewhere (the server's page, another computer, the Budgets page) no longer waits here.
    const keys = new Set([...waiting.map(toastKey), ...holds.map(holdKey), ...notes.map(noteKey)]);
    for (const key of announced.current) if (!keys.has(key)) close(key);
    for (const approval of waiting) {
      const key = toastKey(approval);
      if (announced.current.has(key)) continue;
      announced.current.add(key);
      showToast({
        intent: "primary", icon: <AppIcon name="shield" size={16} />, timeout: 0,
        message: <div className="fleet-approval-toast">
          <strong>{t(approval.device.kind === "node" ? "{name} asks to join {server} as a machine." : "{name} asks to join {server}.", {
            name: approval.device.name, server: approval.server.organization ?? approval.server.url })}</strong>
          <span className="muted-text">{t("Approve it when it shows the same words:")}</span>
          <span className="fleet-approval-words">{keyWords(approval.device.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</span>
          <span className="fleet-approval-actions">
            <Button size="small" intent="primary" text={t("Approve")} onClick={() => void act(approval, true)} />
            <Button size="small" variant="minimal" text={t("Refuse")} onClick={() => void act(approval, false)} />
          </span>
        </div>,
      }, key);
    }
    for (const item of holds) {
      const key = holdKey(item);
      if (announced.current.has(key)) continue;
      announced.current.add(key);
      // It takes the place of what the start of its session said here before the server listed it.
      close(heldRunToastKey(item.hold.runId));
      showToast({
        intent: "warning", icon: <AppIcon name="usage" size={16} />, timeout: 0,
        message: <div className="fleet-approval-toast fleet-hold-toast">
          <strong>{t("A run on {machine} waits for {budget}.", { machine: item.hold.machine, budget: item.hold.budgetName })}</strong>
          {item.hold.title && <span>{item.hold.title}</span>}
          <span className="fleet-approval-actions">
            <Button size="small" intent="warning" text={t("Continue anyway")} onClick={() => void decide(item, true)} />
            <Button size="small" variant="minimal" text={t("Stop")} onClick={() => void decide(item, false)} />
          </span>
        </div>,
      }, key);
    }
    for (const item of notes) {
      const key = noteKey(item);
      if (announced.current.has(key)) continue;
      announced.current.add(key);
      const phrase = notificationPhrase(item.notification);
      const run = approvalRun(item.notification);
      showToast({
        intent: notificationIntent(item.notification.severity),
        icon: <AppIcon name={item.notification.kind === "health" ? "remote" : item.notification.kind === "approval" ? "shield" : "usage"} size={16} />, timeout: 0,
        onDismiss: () => { if (!closing.current.delete(key)) void markRead(item); },
        message: <div className="fleet-approval-toast fleet-notification-toast" data-notification={item.notification.kind}>
          <strong>{t(phrase.key, phrase.values)}</strong>
          <span className="fleet-approval-actions">
            {run && <Button size="small" intent="warning" text={t("Take over")} onClick={() => void takeOver(item)} />}
            <Button size="small" variant="minimal" text={t("Dismiss")} onClick={() => void dismiss(item)} />
          </span>
        </div>,
      }, key);
    }
  }, [act, close, decide, dismiss, holds, markRead, notes, t, takeOver, waiting]);

  if (!epoch || (servers.length === 0 && runs === 0)) return null;
  const count = waiting.length + holds.length + notes.length + runs;
  const label = count === 0 ? t("Fleet")
    : count === waiting.length ? t(waiting.length === 1 ? "Fleet: {count} device waits for your approval" : "Fleet: {count} devices wait for your approval", { count: waiting.length })
    : t(count === 1 ? "Fleet: 1 thing needs you" : "Fleet: {count} things need you", { count });
  return <Button variant="minimal" size="small" className="fleet-approvals-button" data-waiting={count > 0 ? "true" : undefined} active={active}
    icon={<span className="fleet-approvals-icon"><AppIcon name="server" size={16} />{count > 0 && <span className="fleet-approvals-mark">{count}</span>}</span>}
    aria-label={label} title={label} onClick={onOpen} />;
}

/**
 * Says that the first prompt of a session started on a machine waits for a reached budget there, with Continue anyway beside it,
 * until the server lists its hold: the toast of the hold, with Stop, then takes its place.
 */
export function showHeldStartToast({ epoch, runId, message, say, api = fleetServer }: {
  epoch: string; runId: string; message: string;
  say: (key: MessageKey, values?: Readonly<Record<string, string | number>>) => string;
  api?: Pick<typeof fleetServer, "status" | "budgets" | "continueRun">;
}): void {
  const key = heldRunToastKey(runId);
  const show = (problem: string | null) => showToast({
    intent: "warning", icon: <AppIcon name="usage" size={16} />, timeout: 0,
    message: <div className="fleet-approval-toast fleet-held-start-toast" data-run={runId}>
      <strong>{message}</strong>
      {problem && <span className="muted-text">{problem}</span>}
      <span className="fleet-approval-actions">
        <Button size="small" intent="warning" text={say("Continue anyway")} onClick={() => void press()} />
      </span>
    </div>,
  }, key);
  const press = async () => {
    const outcome: HeldRunOutcome = await continueHeldRun(api, epoch, runId);
    if (outcome === "continued") dismissToast(key);
    else show(say(outcome === "not_found" ? "Your server does not list this run yet: try again in a moment." : "Continue anyway was not accepted."));
  };
  show(null);
}

const toastKey = (approval: WaitingApproval) => `fleet-approval:${approval.server.id}:${approval.device.id}`;
const holdKey = (item: WaitingHold) => `fleet-hold:${item.server.id}:${item.hold.id}`;
const noteKey = (item: NewNotification) => `fleet-note:${item.server.id}:${item.notification.id}`;
