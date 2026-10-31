import { useCallback, useEffect, useId, useRef, useState } from "react";
import { Button, Dialog, DialogBody, DialogFooter, PopoverNext, Radio, RadioGroup, Spinner, TextArea } from "@blueprintjs/core";
import { cloudSessions as cloudSessionsHost, type CloudContinueResponse, type CloudReadinessResponse, type CloudSessionDetailsResponse, type WorkspaceSession } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { RemoteLinkQr } from "../RemoteLinkQr";
import { plainTitle } from "../sessionTitle";
import { useShellLanguage } from "../shellLanguage";
import { resumeCommandLine } from "../claudeCodeOpen/claudeCodeOpen";
import { bringBackCommand, cloudMessages, cloudOf, continueFailure, sendFailure, type CloudSessionsApi } from "./cloudSessions";

/** A request of a menu or a command to open Continue here, until the pane opened it. */
export type CloudContinueRequest = Readonly<{ done: () => void }>;

/**
 * Runs the command of a return in a terminal of the session: the command Claude Code is given there, and the folder of
 * its account. Settles with why it could not, or null.
 */
export type CloudTerminalRunner = (reply: CloudContinueResponse) => Promise<string | null>;

/**
 * The tab of a session that runs in the cloud: a card in the place of its conversation, which Claude Code keeps on
 * Anthropic's machines (followed on claude.ai or the Claude app), the messages sent to it, a prompt that sends more,
 * and Continue here, which brings the conversation back to this computer.
 */
export function CloudSessionPane({ epoch, session, provider, projectPath, canMutate, folderBusy = false, api = cloudSessionsHost, onOpenLink, onChanged, onTerminal, continueRequest = null }: {
  epoch: string; session: WorkspaceSession;
  /** The name of the account of Claude Code the session runs with. */
  provider: string | null;
  /** The folder of the project, where Continue here can bring the session. */
  projectPath: string | null;
  canMutate: boolean;
  /** Whether another session works in the folder of the project now: Continue here then recommends a new worktree. */
  folderBusy?: boolean;
  api?: CloudSessionsApi;
  onOpenLink: (url: string) => void;
  /** The session changed in the host: its list is read again. */
  onChanged: () => void;
  onTerminal?: CloudTerminalRunner;
  continueRequest?: CloudContinueRequest | null;
}) {
  const { t, locale } = useShellLanguage();
  const cloud = cloudOf(session);
  const [details, setDetails] = useState<CloudSessionDetailsResponse | null>(null);
  const [text, setText] = useState("");
  const [sending, setSending] = useState(false);
  const [sendError, setSendError] = useState<string | null>(null);
  const [continuing, setContinuing] = useState(false);
  const [bringing, setBringing] = useState(false);
  const [bringNote, setBringNote] = useState<string | null>(null);
  const read = useCallback(() => {
    void api.details({ expectedEpoch: epoch, sessionId: session.id }, { timeoutMilliseconds: 15_000 }).then(reply => setDetails(reply), () => { /* Read again on the next change. */ });
  }, [api, epoch, session.id]);
  useEffect(read, [read, cloud?.continuing]);
  useEffect(() => { if (continueRequest) { setContinuing(true); continueRequest.done(); } }, [continueRequest]);
  if (!cloud) return null;
  const url = cloud.url;
  const title = plainTitle(session.title) || cloud.title || t("Cloud session");
  const time = (at: string | null) => {
    const date = at ? new Date(at) : null;
    return date && !Number.isNaN(date.getTime()) ? date.toLocaleString(locale, { hour: "2-digit", minute: "2-digit", day: "numeric", month: "short" }) : "";
  };
  const started = details?.status === "ok" ? time(details.startedAt) : "";
  const branch = details?.status === "ok" ? details.branch : null;
  const busy = cloud.continuing || details?.continuing === true;
  // A return made in a terminal waits on the user: once Claude Code wrote the conversation there, it is read into the session.
  const inTerminal = details?.status === "ok" && details.continuing && details.inTerminal;
  const bringFromTerminal = () => {
    if (bringing || !canMutate) return;
    setBringing(true); setBringNote(null);
    void api.continueHere({ expectedEpoch: epoch, sessionId: session.id, worktree: false, fromTerminal: true }, { timeoutMilliseconds: 120_000 }).then(reply => {
      if (reply.status === "ok") { onChanged(); return; }
      if (reply.status === "terminal") { setBringNote(t("Claude Code has not written the conversation yet. Let it finish in the terminal, exit it, then try again.")); return; }
      const failure = continueFailure(reply);
      setBringNote([t(failure.key, failure.parameters), failure.said].filter(Boolean).join(" "));
      read();
    }, () => setBringNote(t("The request did not reach CodeAlta. Try again."))).finally(() => setBringing(false));
  };
  const send = () => {
    const message = text.trim();
    if (!message || sending || busy || !canMutate) return;
    setSending(true); setSendError(null);
    void api.send({ expectedEpoch: epoch, sessionId: session.id, text: message }, { timeoutMilliseconds: 120_000 }).then(reply => {
      // What was typed meanwhile stays: only the message that was sent leaves the prompt.
      if (reply.status === "ok") { setText(current => current.trim() === message ? "" : current); read(); return; }
      const failure = sendFailure(reply.status, reply.message);
      setSendError([t(failure.key), failure.said].filter(Boolean).join(" "));
    }, () => setSendError(t("The request did not reach CodeAlta. Try again."))).finally(() => setSending(false));
  };
  return <div className="cloud-session-pane" data-cloud-session={cloud.id}>
    <div className="timeline-scroll cloud-session-scroll">
      <section className="cloud-session-card" aria-label={t("Cloud session")}>
        <header>
          <span className="cloud-session-mark"><AppIcon name="cloud" size={18} /></span>
          <div className="cloud-session-heading">
            <h2><span className="cloud-session-kind">{t("In the cloud")}</span><span className="cloud-session-title">{title}</span></h2>
            <p className="cloud-session-meta">{[started && (branch ? t("Started {time} from {branch}", { time: started, branch }) : t("Started {time}", { time: started })), provider]
              .filter(Boolean).join(" · ")}</p>
          </div>
          <div className="cloud-session-links">
            {url && <Button size="small" icon={<AppIcon name="openExternal" size={14} />} data-cloud-open onClick={() => onOpenLink(url)}>{t("Open on claude.ai")}</Button>}
            {url && <CloudPhoneButton url={url} />}
          </div>
        </header>
        <p className="cloud-session-about">{t("Claude works on Anthropic's machines and goes on while this computer sleeps. Follow it on claude.ai or your phone, then continue it here to get the conversation and its branch.")}</p>
        {details?.status === "ok" && details.note && <p className="cloud-session-note"><AppIcon name="info" size={14} /><span>{details.note}</span></p>}
        {inTerminal ? <div className="cloud-session-terminal" role="group" aria-label={t("Continue here in a terminal")}>
          <p><AppIcon name="terminal" size={14} /><span>{t("Claude Code brings this session back in a terminal tab. Once it is done there, exit it, then bring the conversation into this session.")}</span></p>
          {bringNote && <p className="cloud-session-error" role="status">{bringNote}</p>}
          <div className="cloud-session-actions">
            <Button intent="primary" icon={<AppIcon name="download" size={15} />} loading={bringing} disabled={!canMutate} data-cloud-bring-terminal onClick={bringFromTerminal}>{t("Bring it into this session")}</Button>
            <Button disabled={!canMutate || bringing} onClick={() => setContinuing(true)}>{t("Continue here…")}</Button>
          </div>
        </div>
        : busy ? <div className="cloud-session-busy-row">
            <p className="cloud-session-busy" role="status"><Spinner size={14} />{t("The conversation is being brought here…")}</p>
            {/* A return that was interrupted (CodeAlta closed meanwhile) is taken up again from here. */}
            <Button size="small" disabled={!canMutate} data-cloud-continue onClick={() => setContinuing(true)}>{t("Continue here…")}</Button>
          </div>
          : <div className="cloud-session-actions">
            <Button intent="primary" icon={<AppIcon name="download" size={15} />} disabled={!canMutate} data-cloud-continue onClick={() => setContinuing(true)}>{t("Continue here…")}</Button>
          </div>}
      </section>
      <ol className="cloud-session-messages" aria-label={t("Messages sent to the cloud session")}>
        {cloudMessages(details).map((message, index) => <li key={index} className="cloud-session-message">
          <div className="cloud-session-message-head"><strong>{t("You")}</strong>{message.at && <span>{time(message.at)}</span>}
            <span className="cloud-session-message-sent"><AppIcon name="check" size={12} />{t(message.task ? "Started the cloud session" : "Sent to the cloud")}</span></div>
          <p>{message.text}</p>
        </li>)}
      </ol>
    </div>
    <div className="cloud-session-prompt">
      <TextArea fill autoResize value={text} disabled={!canMutate || busy} aria-label={t("Message the cloud session")} placeholder={t("Message the cloud session…")}
        onChange={event => setText(event.currentTarget.value)}
        onKeyDown={event => { if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) { event.preventDefault(); send(); } }} />
      <Button intent="primary" icon={<AppIcon name="send" size={16} />} loading={sending} disabled={!text.trim() || !canMutate || busy} aria-label={t("Send to the cloud")}
        title={t("Send to the cloud")} data-cloud-send onClick={send} />
      {sendError ? <p className="cloud-session-error" role="alert">{sendError}</p>
        : <p className="cloud-session-hint">{t("Claude answers on claude.ai and in the Claude app.")}</p>}
    </div>
    {continuing && <ContinueHereDialog epoch={epoch} session={session} title={title} projectPath={projectPath} folderBusy={folderBusy} api={api} onTerminal={onTerminal}
      onClose={done => { setContinuing(false); read(); if (done) onChanged(); }} />}
  </div>;
}

/** The link of a cloud session as a QR code, to follow it on a phone, with a button that copies it. */
function CloudPhoneButton({ url }: { url: string }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [copied, setCopied] = useState(false);
  useEffect(() => { if (!copied) return; const timer = setTimeout(() => setCopied(false), 1400); return () => clearTimeout(timer); }, [copied]);
  const content = <div className="cloud-phone" role="group" aria-label={t("Follow on a phone")}>
    <h6>{t("Follow on a phone")}</h6>
    <RemoteLinkQr url={url} label={t("QR code of the link, to open the session on a phone")} />
    <p className="detail">{t("Scan it with the camera of the phone: the session opens in the Claude app or on claude.ai.")}</p>
    <Button size="small" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />}
      onClick={() => void navigator.clipboard.writeText(url).then(() => setCopied(true), () => { /* No clipboard: nothing is copied. */ })}>{t(copied ? "Copied" : "Copy link")}</Button>
  </div>;
  return <PopoverNext content={content} placement="bottom-end" isOpen={open} onInteraction={setOpen} popoverClassName="cloud-phone-popover">
    <Button size="small" icon={<AppIcon name="smartphone" size={14} />} data-cloud-phone>{t("Phone")}</Button>
  </PopoverNext>;
}

type ContinueStep = "choose" | "running" | "terminal" | "failed";

/**
 * Continue here: where the session goes on (the folder of the project, or a new worktree, the default when the folder
 * has changes Claude Code's checkout would meet), then Claude Code brings the conversation back. When it refuses, the
 * same return can be made in a terminal, then brought into the session.
 */
export function ContinueHereDialog({ epoch, session, title, projectPath, folderBusy = false, api = cloudSessionsHost, onTerminal, onClose }: {
  epoch: string; session: WorkspaceSession; title: string; projectPath: string | null;
  /** Whether another session works in the folder of the project now. */
  folderBusy?: boolean;
  api?: CloudSessionsApi;
  onTerminal?: CloudTerminalRunner;
  /** Closes it: `done` when the session was brought here. */
  onClose: (done: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const [readiness, setReadiness] = useState<CloudReadinessResponse | null>(null);
  const [worktree, setWorktree] = useState<boolean | null>(null);
  const [step, setStep] = useState<ContinueStep>("choose");
  const [error, setError] = useState<string | null>(null);
  // A terminal whose shell cannot be given the command: the command to type there, with why.
  const [terminalCommand, setTerminalCommand] = useState<Readonly<{ note: string; command: string }> | null>(null);
  // A return made in a terminal keeps the folder it reserved: it is not asked for again.
  const reserved = useRef(false);
  useEffect(() => {
    if (!session.projectId) return;
    const abort = new AbortController();
    void api.readiness({ expectedEpoch: epoch, projectId: session.projectId, providerKey: session.providerKey ?? null }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(reply => { if (!abort.signal.aborted && reply.status === "ok") setReadiness(reply); }, () => { /* The folder is offered without what it holds. */ });
    return () => abort.abort();
  }, [api, epoch, session.projectId, session.providerKey]);
  const dirty = (readiness?.changed ?? 0) > 0;
  // The folder of the project is switched to another branch: not under another session that works there.
  const inWorktree = worktree ?? (dirty || folderBusy);
  const run = (fromTerminal: boolean) => {
    setStep("running"); setError(null);
    void api.continueHere({ expectedEpoch: epoch, sessionId: session.id, worktree: !reserved.current && inWorktree, fromTerminal }, { timeoutMilliseconds: 10 * 60_000 })
      .then(async reply => {
        if (reply.status === "ok") { onClose(true); return; }
        if (reply.status === "terminal") {
          reserved.current = true;
          // The terminal tab is where the user goes on: the dialog leaves it, and the card brings the conversation in afterwards.
          const refusal = onTerminal ? await onTerminal(reply) : t("No terminal can be opened from here.");
          if (!refusal) { onClose(false); return; }
          setTerminalCommand({ note: refusal, command: reply.command ? resumeCommandLine({ resumeCommand: bringBackCommand(reply.command, reply.executable,
            { windows: reply.windows, home: reply.home }), configDir: reply.configDirectory, windows: reply.windows, home: reply.home }) : "" });
          setStep("terminal");
          return;
        }
        if (reply.status === "uncommitted_changes") setWorktree(true);
        const failure = continueFailure(reply);
        setError([t(failure.key, failure.parameters), failure.said].filter(Boolean).join(" "));
        setStep(reply.status === "uncommitted_changes" ? "choose" : "failed");
      }, () => { setError(t("The request did not reach CodeAlta. Try again.")); setStep("failed"); });
  };
  const running = step === "running";
  const changedReason = readiness && readiness.changed > 0 ? t(readiness.changed === 1 ? "1 file has changes that are not committed" : "{count} files have changes that are not committed",
    { count: readiness.changed }) : null;
  return <Dialog isOpen className="cloud-continue-dialog" title={t("Continue “{title}” on this computer", { title })} canOutsideClickClose={!running}
    canEscapeKeyClose={!running} isCloseButtonShown={!running} onClose={() => onClose(false)}>
    <DialogBody>
      <p>{t("Claude Code brings back the whole conversation and checks out the branch the cloud session worked on. The cloud session stays on claude.ai; what you do next stays here.")}</p>
      {step !== "terminal" && !reserved.current && <RadioGroup className="cloud-continue-place" label={t("Where")} selectedValue={inWorktree ? "worktree" : "project"} disabled={running}
        onChange={event => setWorktree(event.currentTarget.value === "worktree")}>
        <Radio value="worktree" labelElement={<span className="cloud-continue-choice"><strong>{t("New worktree")}</strong>
          <small>{dirty ? t("Recommended: the folder of the project has changes that are not committed.")
            : folderBusy ? t("Recommended: another session works in the folder of the project.")
            : t("A checkout of its own: the folder of the project stays as it is.")}</small></span>} />
        <Radio value="project" disabled={dirty} labelElement={<span className="cloud-continue-choice"><strong>{t("Project folder")}</strong>
          <small id={`${id}-project`}>{changedReason ? `${changedReason}: ${t("commit them first, or choose a new worktree.")}`
            : projectPath ? t("{folder}: switches it to the branch of the session.", { folder: projectPath }) : t("Switches it to the branch of the session.")}</small></span>} />
      </RadioGroup>}
      {running && <p className="cloud-continue-progress" role="status"><Spinner size={16} />{t("Claude Code brings the conversation back…")}</p>}
      {step === "terminal" && terminalCommand && <div className="cloud-continue-terminal">
        <p className="cloud-continue-note" role="status">{terminalCommand.note}</p>
        {terminalCommand.command && <><p>{t("Run this command in a terminal of the folder, then bring the conversation into this session from its card:")}</p>
          <pre className="cloud-continue-command"><code>{terminalCommand.command}</code></pre></>}
      </div>}
      {error && <p className="cloud-continue-error" role="alert">{error}</p>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={running} onClick={() => onClose(false)}>{t("Cancel")}</Button>
      {step === "failed" && onTerminal && <Button icon={<AppIcon name="terminal" size={15} />} disabled={running} data-cloud-terminal
        onClick={() => run(true)}>{t("Try in a terminal")}</Button>}
      {step !== "terminal" && <Button intent="primary" loading={running} data-cloud-continue-run onClick={() => run(false)}>{t(step === "failed" ? "Try again" : "Continue here")}</Button>}
    </>} />
  </Dialog>;
}

/** The line above the conversation of a session that was brought back from the cloud. */
export function CloudOriginLine({ session, onOpenLink }: { session: WorkspaceSession; onOpenLink: (url: string) => void }) {
  const { t } = useShellLanguage();
  const cloud = cloudOf(session);
  if (!cloud?.continued) return null;
  return <div className="cloud-origin-line" role="note"><AppIcon name="cloud" size={14} /><span>{t("Started in the cloud")}</span>
    {cloud.url && <Button variant="minimal" size="small" onClick={() => onOpenLink(cloud.url!)}>{t("Open on claude.ai")}</Button>}</div>;
}

/** The mark of the row of a session that runs in the cloud. */
export function CloudMark({ session }: { session: WorkspaceSession }) {
  const { t } = useShellLanguage();
  const cloud = cloudOf(session);
  if (!cloud || cloud.continued) return null;
  return <span className="session-cloud-mark" role="img" aria-label={t("Runs in the cloud")} title={t("Runs in the cloud")}><AppIcon name="cloud" size={12} /></span>;
}