import { useEffect, useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter, FormGroup, HTMLSelect, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, Spinner } from "@blueprintjs/core";
import { cloudSessions as cloudSessionsHost, type CloudReadinessResponse, type CloudStartResponse } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ProviderIcon } from "../ProviderIcon";
import { useShellLanguage } from "../shellLanguage";
import { pastedCloudSession, readinessLines, startFailure, startStepKey, type CloudSessionsApi } from "./cloudSessions";

/** An account of Claude Code the cloud session can run with. */
export type CloudAccount = Readonly<{ id: string; name: string }>;

/**
 * What the new session of a project says when it is to run in the cloud, under the welcome: what that means, then
 * what the project and the account allow (read when it is shown), and a link to the guide.
 */
export function CloudReadiness({ epoch, projectId, providerKey, provider, api = cloudSessionsHost, onLearnMore }: {
  epoch: string; projectId: string; providerKey: string | null; provider: string;
  api?: Pick<CloudSessionsApi, "readiness">;
  onLearnMore?: () => void;
}) {
  const { t } = useShellLanguage();
  const [readiness, setReadiness] = useState<CloudReadinessResponse | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    setReadiness(null);
    void api.readiness({ expectedEpoch: epoch, projectId, providerKey }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(reply => { if (!abort.signal.aborted && reply.status === "ok") setReadiness(reply); }, () => { /* Claude Code says what it refuses when it starts. */ });
    return () => abort.abort();
  }, [api, epoch, projectId, providerKey]);
  return <div className="cloud-readiness" data-cloud-readiness>
    <p>{t("Claude Code runs this session on Anthropic's machines. It goes on while this computer sleeps: follow it on claude.ai or in the Claude app, then continue it here when you want.")}</p>
    {readiness ? <ul>
      {readinessLines(readiness, provider).map(line => <li key={line.key} data-tone={line.tone}>
        <AppIcon name={line.tone === "ok" ? "check" : line.tone === "warning" ? "warning" : "info"} size={14} /><span>{t(line.key, line.parameters)}</span></li>)}
    </ul> : <p className="cloud-readiness-reading" role="status"><Spinner size={14} />{t("Reading the project…")}</p>}
    {onLearnMore && <Button variant="minimal" size="small" icon={<AppIcon name="documentation" size={14} />} onClick={onLearnMore}>{t("Learn more")}</Button>}
  </div>;
}

/**
 * The start of a cloud session, in the place of the welcome: where it is while it runs, with Stop, and how it ended
 * when no session came of it. A cloud session that runs but could not be recorded is given with its link.
 */
export function CloudStartProgress({ step, outcome, onStop, onShowTerminal, onOpenLink, onBringHere, onClose }: {
  step: string | null; outcome: CloudStartResponse | null;
  onStop: () => void;
  onShowTerminal?: (terminalId: string) => void;
  onOpenLink: (url: string) => void;
  onBringHere?: (url: string) => void;
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const failure = outcome && outcome.status !== "created" && outcome.status !== "existing" ? startFailure(outcome.status, outcome.message) : null;
  return <div className="cloud-start" role="group" aria-label={t("Cloud session")} data-cloud-start={outcome ? outcome.status : step ?? "starting"}>
    <h2><AppIcon name="cloud" size={18} />{t("Sending to the cloud")}</h2>
    {!outcome ? <>
      <p className="cloud-start-step" role="status"><Spinner size={16} />{t(startStepKey(step))}</p>
      <p className="detail">{t("A terminal tab opens if Claude Code asks something: whether it trusts the folder, a sign-in.")}</p>
      <div className="cloud-start-actions"><Button icon={<AppIcon name="stop" size={15} />} data-cloud-stop onClick={onStop}>{t("Stop")}</Button></div>
    </> : failure && <>
      <p className="cloud-start-error" role="alert">{t(failure.key)}</p>
      {failure.said && <pre className="cloud-start-said">{failure.said}</pre>}
      {outcome.status === "unrecorded" && outcome.url && <p className="cloud-start-link"><code>{outcome.url}</code></p>}
      <div className="cloud-start-actions">
        {outcome.status === "unrecorded" && outcome.url && <>
          <Button icon={<AppIcon name="openExternal" size={15} />} onClick={() => onOpenLink(outcome.url!)}>{t("Open on claude.ai")}</Button>
          {onBringHere && <Button icon={<AppIcon name="download" size={15} />} onClick={() => onBringHere(outcome.url!)}>{t("Bring it here")}</Button>}
        </>}
        {outcome.terminalId && onShowTerminal && <Button icon={<AppIcon name="terminal" size={15} />} onClick={() => onShowTerminal(outcome.terminalId!)}>{t("Show the terminal")}</Button>}
        <Button onClick={onClose}>{t("Close")}</Button>
      </div>
      {outcome.status !== "unrecorded" && <p className="detail">{t("The prompt keeps its text: nothing was sent.")}</p>}
    </>}
  </div>;
}

/**
 * The account chip of a new session that runs in the cloud, in the place of the agent and model chip: the cloud
 * session picks its own model, so only the accounts of Claude Code of the space are offered, and a way back to a
 * session on this computer.
 */
export function CloudAccountChoice({ accounts, value, disabled = false, onChange, onLocal }: {
  accounts: readonly CloudAccount[]; value: string | null; disabled?: boolean;
  onChange: (id: string) => void;
  /** Goes back to a session that works on this computer, with any provider. */
  onLocal: () => void;
}) {
  const { t } = useShellLanguage();
  const shown = accounts.find(account => account.id === value) ?? null;
  const menu = <Menu aria-label={t("Claude Code account")}>
    {accounts.map(account => <MenuItem key={account.id} text={account.name} icon={<ProviderIcon providerKey={account.id} size={14} />} active={account.id === value}
      onClick={() => onChange(account.id)} />)}
    <MenuDivider />
    <MenuItem text={t("Another provider: work on this computer")} icon={<AppIcon name="folder" size={14} />} onClick={onLocal} />
  </Menu>;
  return <PopoverNext content={menu} placement="top-start" disabled={disabled}>
    <Button variant="minimal" className="composer-selection cloud-account-choice" disabled={disabled} aria-label={t("Claude Code account")}
      title={`${t("Claude Code account")}: ${shown?.name ?? t("No provider")}\n${t("The cloud session chooses its own model.")}`} endIcon={<AppIcon name="chevronDown" size={14} />}>
      <span className="composer-selection-part"><AppIcon name="cloud" size={14} /><span>{t("Cloud")}</span></span>
      <span className="composer-selection-part"><ProviderIcon providerKey={shown?.id ?? null} size={14} /><span>{shown?.name ?? t("No provider")}</span></span>
    </Button>
  </PopoverNext>;
}

/**
 * Bring a cloud session here: one started on claude.ai or in the Claude app, from its link. It is recorded as a
 * session of the project in the cloud, then brought to this computer.
 */
export function BringHereDialog({ epoch, project, accounts, initialLink = "", spaceId, api = cloudSessionsHost, onBrought, onClose }: {
  epoch: string; project: Readonly<{ id: string; name: string }>; accounts: readonly CloudAccount[]; initialLink?: string; spaceId: string | null;
  api?: Pick<CloudSessionsApi, "bringHere">;
  /** The session it was recorded as, to open it and continue it here. */
  onBrought: (sessionId: string, existing: boolean) => void;
  onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [link, setLink] = useState(initialLink);
  const [account, setAccount] = useState(accounts[0]?.id ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const valid = pastedCloudSession(link) !== null;
  const bring = () => {
    if (!valid || !account || busy) return;
    setBusy(true); setError(null);
    void api.bringHere({ expectedEpoch: epoch, projectId: project.id, providerKey: account, spaceId, link: link.trim() }, { timeoutMilliseconds: 30_000 }).then(reply => {
      if ((reply.status === "created" || reply.status === "existing") && reply.sessionId) { onBrought(reply.sessionId, reply.status === "existing"); return; }
      const failure = reply.status === "invalid_link" ? null : startFailure(reply.status, reply.message);
      setError(failure ? [t(failure.key), failure.said].filter(Boolean).join(" ") : t("This is not the link of a cloud session of claude.ai/code."));
    }, () => setError(t("The request did not reach CodeAlta. Try again."))).finally(() => setBusy(false));
  };
  return <Dialog isOpen className="cloud-bring-dialog" title={t("Bring a cloud session to {project}", { project: project.name })} canOutsideClickClose={!busy} onClose={onClose}>
    <DialogBody>
      <p>{t("A session started on claude.ai or in the Claude app continues here: paste its link. Claude Code brings back its conversation and its branch.")}</p>
      <FormGroup label={t("Link of the cloud session")} labelFor="cloud-bring-link" helperText={link.trim() && !valid ? t("This is not the link of a cloud session of claude.ai/code.") : undefined}
        intent={link.trim() && !valid ? "danger" : "none"}>
        <InputGroup id="cloud-bring-link" autoFocus value={link} placeholder="https://claude.ai/code/session_…" disabled={busy}
          onChange={event => setLink(event.currentTarget.value)} onKeyDown={event => { if (event.key === "Enter") { event.preventDefault(); bring(); } }} />
      </FormGroup>
      {accounts.length > 1 && <FormGroup label={t("Claude Code account")} labelFor="cloud-bring-account">
        <HTMLSelect id="cloud-bring-account" value={account} disabled={busy} onChange={event => setAccount(event.currentTarget.value)}
          options={accounts.map(value => ({ value: value.id, label: value.name }))} />
      </FormGroup>}
      {error && <p className="cloud-continue-error" role="alert">{error}</p>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={busy} onClick={onClose}>{t("Cancel")}</Button>
      <Button intent="primary" loading={busy} disabled={!valid || !account} onClick={bring}>{t("Bring it here")}</Button>
    </>} />
  </Dialog>;
}
