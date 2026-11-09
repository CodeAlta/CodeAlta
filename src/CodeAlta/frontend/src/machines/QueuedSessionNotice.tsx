import { Button } from "@blueprintjs/core";
import { useState } from "react";
import { machines, type WorkspaceSession } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { queuedLabel } from "./seats";

/** The calls of the notice: the seats of the machine, and the start now or the cancel of a run that waits. */
export type QueuedSessionApi = Pick<typeof machines, "seats" | "startSeat" | "cancelSeat">;

/**
 * Above the prompt of a session of another machine whose run waits for a seat there: its place, and **Start now** (for a
 * computer that manages the machine) or **Take out of the queue**. The row of the session says the same in the Explorer.
 */
export function QueuedSessionNotice({ session, epoch, api = machines }: {
  session: Pick<WorkspaceSession, "id" | "machineId" | "machineName" | "remoteQueuedPosition">; epoch: string | null | undefined; api?: QueuedSessionApi;
}) {
  const { t } = useShellLanguage();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  if (session.remoteQueuedPosition == null || !session.machineId || !epoch) return null;
  const machineId = session.machineId;
  const name = session.machineName ?? machineId;
  const label = queuedLabel(name, session.remoteQueuedPosition);
  const act = async (kind: "start" | "cancel") => {
    setBusy(true);
    setProblem(null);
    try {
      // The runs of the session that wait: its first one is the one the row says.
      const seats = await api.seats({ expectedEpoch: epoch, machineId });
      const item = seats.queue.find(value => value.sessionId === session.id);
      if (!item) return;
      const request = { expectedEpoch: epoch, machineId, id: item.id };
      const result = kind === "start" ? await api.startSeat(request) : await api.cancelSeat(request);
      if (result.status !== "ok" && result.status !== "not_found") setProblem(result.message ?? result.status);
    } catch {
      setProblem(t("The machine does not answer: it is offline."));
    } finally {
      setBusy(false);
    }
  };
  return <div className="queued-session-notice" role="status">
    <AppIcon name="timer" size={14} />
    <span className="queued-session-text"><strong>{t(label.key, label.vars)}</strong>
      <span className="muted-text"> {t("{machine} runs as many sessions as its owner lets it: this session starts when its turn comes.", { machine: name })}</span>
      {problem && <span className="queued-session-problem"> {problem}</span>}</span>
    <Button size="small" variant="minimal" text={t("Start now")} disabled={busy} onClick={() => void act("start")} />
    <Button size="small" variant="minimal" text={t("Take out of the queue")} disabled={busy} onClick={() => void act("cancel")} />
  </div>;
}
