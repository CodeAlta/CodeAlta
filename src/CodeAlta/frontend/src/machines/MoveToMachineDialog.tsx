import { useEffect, useRef, useState } from "react";
import { Button, Callout, Checkbox, Dialog, DialogBody, DialogFooter, ProgressBar, Radio, RadioGroup, Switch, Tag } from "@blueprintjs/core";
import { machines, type MachineMovePreview, type MachineMoveResult, type MachineView, type WorkspaceSession } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { machineIntent, machineStateLabel, moveProblem, moveReady, moveTargets } from "./machines";

type MoveApi = Pick<typeof machines, "list" | "moveSettings" | "setReceiveMoves" | "movePreview" | "moveSession">;

/**
 * Move to machine…: the session goes to a paired machine, or from one back to this computer, with its conversation and
 * its work in progress. The dialog says first what the move meets (a session at work, files that stay behind, the
 * project of the target), then moves it and says where it runs.
 */
export function MoveToMachineDialog({ epoch, session, api = machines, onClose, onMoved }: {
  epoch: string; session: Pick<WorkspaceSession, "id" | "title" | "machineId" | "machineName">; api?: MoveApi;
  onClose: () => void; onMoved: (result: MachineMoveResult, machine: string) => void;
}) {
  const { t } = useShellLanguage();
  const [listed, setListed] = useState<{ machines: readonly MachineView[]; here: string | null } | null>(null);
  const [target, setTarget] = useState<string | null | undefined>(undefined);
  const [preview, setPreview] = useState<MachineMovePreview | null>(null);
  const [reading, setReading] = useState(false);
  const [clone, setClone] = useState(false);
  const [moving, setMoving] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [reload, setReload] = useState(0);
  const ticket = useRef(0);

  useEffect(() => {
    const abort = new AbortController();
    void Promise.all([api.list({ expectedEpoch: epoch }, { signal: abort.signal }), api.moveSettings({ expectedEpoch: epoch }, { signal: abort.signal })])
      .then(([list, settings]) => {
        if (abort.signal.aborted) return;
        setListed({ machines: list.machines, here: settings.machineName });
        // The first machine online is proposed once: a choice of the person stays.
        const first = moveTargets({ machineId: session.machineId }, list.machines, "").find(value => value.state === "online");
        setTarget(current => current !== undefined ? current : first ? first.id : undefined);
      }, () => { if (!abort.signal.aborted) setProblem({ key: "Machines unavailable" }); });
    return () => abort.abort();
  }, [api, epoch, session.machineId]);

  useEffect(() => {
    if (target === undefined) return;
    const mine = ++ticket.current;
    setPreview(null);
    setProblem(null);
    setReading(true);
    const abort = new AbortController();
    api.movePreview({ expectedEpoch: epoch, sessionId: session.id, sourceMachineId: session.machineId ?? null, targetMachineId: target, cloneRepository: false },
      { signal: abort.signal, timeoutMilliseconds: 60_000 }).then(value => { if (mine === ticket.current) setPreview(value); },
      () => { if (mine === ticket.current && !abort.signal.aborted) setProblem({ key: "The machine does not answer." }); })
      .finally(() => { if (mine === ticket.current) setReading(false); });
    return () => abort.abort();
  }, [api, epoch, session.id, session.machineId, target, reload]);

  const here = listed?.here ?? t("This computer");
  const targets = listed ? moveTargets(session, listed.machines, here) : [];
  const chosen = targets.find(value => value.id === target);
  const toHere = target === null;
  const ready = !moving && moveReady(preview, toHere, clone);
  const sourceProblem = preview && preview.status !== "ok" ? moveProblem(preview.status) : preview?.busy ? moveProblem(preview.busy) : null;

  async function receiveHere(enabled: boolean) {
    const settings = await api.setReceiveMoves({ expectedEpoch: epoch, enabled }).catch(() => null);
    if (settings?.status === "ok") setReload(value => value + 1);
  }

  async function move() {
    if (target === undefined) return;
    setMoving(true);
    setProblem(null);
    try {
      const result = await api.moveSession({ expectedEpoch: epoch, sessionId: session.id, sourceMachineId: session.machineId ?? null, targetMachineId: target,
        cloneRepository: clone }, { timeoutMilliseconds: 3_600_000 });
      if (result.status === "finished") { onMoved(result, chosen?.name ?? ""); return; }
      setProblem({ key: moveProblem(result.status, result.code) ?? "The session could not move.", detail: result.message });
    } catch {
      setProblem({ key: "The machine does not answer." });
    } finally {
      setMoving(false);
    }
  }

  return <Dialog isOpen className="move-machine-dialog" title={<><AppIcon name="remote" size={14} /> {t("Move to machine")}<span className="reference-project"> · {session.title}</span></>}
    canOutsideClickClose={!moving} canEscapeKeyClose={!moving} onClose={() => { if (!moving) onClose(); }}>
    <DialogBody>
      {!listed ? <ActivitySpinner size={16} />
        : targets.length === 0 ? <Callout compact intent="primary">{t("No other machine is paired.")}</Callout>
        : <RadioGroup className="move-machine-targets" label={t("Move to")} disabled={moving} selectedValue={target === null ? "" : target ?? undefined}
          onChange={event => setTarget(event.currentTarget.value || null)}>
          {targets.map(value => <Radio key={value.id ?? ""} value={value.id ?? ""} disabled={value.state !== "online"}
            labelElement={<span className="move-machine-target"><AppIcon name="remote" size={14} />{value.name}
              {value.id === null ? <span className="muted-text">{t("This computer")}</span>
                : <Tag minimal round intent={machineIntent(value.state)}>{t(machineStateLabel(value.state))}</Tag>}</span>} />)}
        </RadioGroup>}
      {reading && <div className="move-machine-reading muted-text"><ActivitySpinner size={14} /> {t("Checking the session and the machine…")}</div>}
      {preview && !moving && <section className="move-machine-preview" aria-label={t("Before the move")}>
        {sourceProblem && <Callout compact intent="warning">{t(sourceProblem)}{preview.message && <div className="muted-text">{preview.message}</div>}
          {preview.jobs.length > 0 && <ul>{preview.jobs.map(job => <li key={job}><code>{job}</code></li>)}</ul>}</Callout>}
        {preview.status === "ok" && <p className="muted-text">{t(preview.unpushedCommits > 0 ? "Its commits that are not pushed and its changes move with it, on {branch}."
          : preview.dirty ? "Its changes that are not committed move with it, on {branch}." : "Its branch {branch} is pushed and clean: the conversation moves.",
          { branch: preview.branch ?? "HEAD" })}</p>}
        {preview.secrets.length > 0 && <Callout compact intent="primary" icon={<AppIcon name="shield" size={14} />}>
          {t("These files look like secrets and stay behind:")} {preview.secrets.map(file => <code key={file}>{file}</code>)}</Callout>}
        {toHere && !preview.receiveMoves && <Callout compact intent="warning">{t("This computer does not receive moved sessions.")}
          <Switch className="move-machine-receive" checked={false} label={t("Receive moved sessions on this computer")} onChange={() => void receiveHere(true)} /></Callout>}
        {preview.targetStatus === "accepted" && <p className="move-machine-project">{t("It works in a worktree of its own of {project}, in {path}.",
          { project: preview.projectName ?? "", path: preview.projectPath ?? "" })}</p>}
        {preview.targetStatus && preview.targetStatus !== "accepted" && (toHere ? preview.receiveMoves : true) && <Callout compact intent="warning">
          {t(moveProblem(preview.targetStatus === "refused" ? "refused" : preview.targetStatus, preview.targetCode) ?? "The session could not move.")}
          {preview.targetMessage && <div className="muted-text">{preview.targetMessage}</div>}
          {preview.targetCode === "project_missing" && preview.canClone && <Checkbox checked={clone} label={t("Clone the repository there")}
            onChange={event => setClone(event.currentTarget.checked)} />}</Callout>}
        {preview.targetWarnings.map(warning => <p key={warning} className="muted-text">{warning}</p>)}
        {preview.status === "ok" && <p className="muted-text">{t("Background jobs, reminders and terminals stay on {machine}.",
          { machine: session.machineName ?? here })}</p>}
      </section>}
      {moving && <div className="move-machine-progress" role="status"><ProgressBar intent="primary" />
        <span>{t("Moving the session to {machine}…", { machine: chosen?.name ?? "" })}</span></div>}
      {problem && <Callout compact intent="danger" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={moving} text={t("Cancel")} onClick={onClose} />
      <Button intent="primary" disabled={!ready} icon={moving ? <ActivitySpinner size={14} /> : <AppIcon name="remote" size={14} />} text={t("Move")} onClick={() => void move()} />
    </>} />
  </Dialog>;
}
