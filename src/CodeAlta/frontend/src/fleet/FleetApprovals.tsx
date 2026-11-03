import { useCallback, useEffect, useRef, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetDeviceView, type FleetServerView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { dismissToast, showToast } from "../appToaster";
import { useShellLanguage } from "../shellLanguage";
import { keyWords } from "./fleetServer";
import { waitingApprovals, type WaitingApproval } from "./approvals";

/** The calls of the Fleet button. */
export type FleetApprovalsApi = Pick<typeof fleetServer, "status" | "devices" | "approve" | "revoke" | "watch">;

// How often the devices of the servers are read again, beside the changes the host says.
const ReadInterval = 20_000;

/**
 * The Fleet button of the title bar, for a computer that owns a server: a mark counts the devices that wait for an
 * approval, and each new one comes as a toast with its six words, Approve and Refuse, wherever the person is.
 */
export function FleetApprovals({ epoch, api = fleetServer, onOpen }: { epoch: string | null; api?: FleetApprovalsApi; onOpen: () => void }) {
  const { t } = useShellLanguage();
  const [servers, setServers] = useState<FleetServerView[]>([]);
  const [waiting, setWaiting] = useState<WaitingApproval[]>([]);
  // The devices a toast was shown for: each comes once.
  const announced = useRef(new Set<string>());
  const read = useCallback(async (signal: AbortSignal) => {
    if (!epoch) return;
    const status = await api.status({ expectedEpoch: epoch }, { signal, timeoutMilliseconds: 15_000 });
    const owned = status.servers.filter(server => server.controller && server.link === "approved");
    const lists: { server: FleetServerView; devices: readonly FleetDeviceView[] }[] = [];
    for (const server of owned) {
      const devices = await api.devices({ expectedEpoch: epoch, linkId: server.id }, { signal, timeoutMilliseconds: 15_000 }).catch(() => null);
      if (devices?.status === "ok") lists.push({ server, devices: devices.devices });
    }
    if (signal.aborted) return;
    setServers(owned);
    setWaiting(waitingApprovals(lists));
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
    dismissToast(toastKey(approval));
    // An approval names the words the person compared in the toast: the host signs the key of those words only.
    const request = { expectedEpoch: epoch, linkId: approval.server.id, deviceId: approval.device.id, words: approve ? approval.device.words : null };
    await (approve ? api.approve(request, { timeoutMilliseconds: 30_000 }) : api.revoke(request, { timeoutMilliseconds: 30_000 })).catch(() => null);
    setWaiting(current => current.filter(item => item.device.id !== approval.device.id || item.server.id !== approval.server.id));
  }, [api, epoch]);

  useEffect(() => {
    // A device approved or refused elsewhere (the server's page, another computer, Add a machine…) no longer waits here.
    const keys = new Set(waiting.map(toastKey));
    for (const key of announced.current) if (!keys.has(key)) dismissToast(key);
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
  }, [act, t, waiting]);

  if (!epoch || servers.length === 0) return null;
  const label = waiting.length === 0 ? t("Fleet") : t(waiting.length === 1 ? "Fleet: {count} device waits for your approval" : "Fleet: {count} devices wait for your approval",
    { count: waiting.length });
  return <Button variant="minimal" size="small" className="fleet-approvals-button" data-waiting={waiting.length > 0 ? "true" : undefined}
    icon={<span className="fleet-approvals-icon"><AppIcon name="server" size={16} />{waiting.length > 0 && <span className="fleet-approvals-mark">{waiting.length}</span>}</span>}
    aria-label={label} title={label} onClick={onOpen} />;
}

const toastKey = (approval: WaitingApproval) => `fleet-approval:${approval.server.id}:${approval.device.id}`;
