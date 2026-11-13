import { useContext, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { Button, InputGroup, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { TerminalPasteContext } from "../MarkdownContent";
import type { MessageKey } from "../localization";
import { modalDialogOpen } from "../modalDialogs";
import { sessionTime } from "../sessionTime";
import { useShellLanguage } from "../shellLanguage";
import { plainOutput, shellCommandMaxLength, shellOutputText, type ShellCommandEntry, type ShellCommands } from "./shellCommands";
import "./shellCommands.css";

/** The output of a command attached to the prompt of its session, or not; null where the prompt takes no attachment. */
export type ShellAttachments = Readonly<{ has: (entry: ShellCommandEntry) => boolean; toggle: (entry: ShellCommandEntry) => void }>;

// Why a command did not run, by the status of the host; what the host said goes after it only when nothing more precise does.
const failures: Readonly<Record<string, MessageKey>> = {
  missing_session: "This session can no longer be read.",
  missing_folder: "The folder of this session no longer exists.",
  stale_epoch: "CodeAlta restarted since this command was run: run it again.",
  closed: "CodeAlta restarted since this command was run: run it again.",
  invalid_request: "This command cannot be run.",
};

/** A duration as a person reads it: tenths under ten seconds, then seconds, then minutes and seconds. */
export function formatDuration(milliseconds: number): string {
  const seconds = Math.max(0, milliseconds) / 1000;
  if (seconds < 10) return `${seconds.toFixed(1)} s`;
  if (seconds < 60) return `${Math.round(seconds)} s`;
  const whole = Math.round(seconds);
  return `${Math.floor(whole / 60)}m ${String(whole % 60).padStart(2, "0")}s`;
}

function elapsed(entry: ShellCommandEntry, now: number): string {
  return formatDuration((entry.endedAt ? Date.parse(entry.endedAt) : now) - Date.parse(entry.startedAt));
}

/** How a command ended, short: its exit code, or that it runs, was stopped or could not run. */
function Outcome({ entry, now }: { entry: ShellCommandEntry; now: number }) {
  const { t } = useShellLanguage();
  const time = elapsed(entry, now);
  if (entry.state === "running") return <span className="shell-command-outcome" data-state="running"><ActivitySpinner size={12} />{t("Running · {elapsed}", { elapsed: time })}</span>;
  if (entry.state === "stopped") return <span className="shell-command-outcome" data-state="stopped"><AppIcon name="stop" size={12} />{t("Stopped · {elapsed}", { elapsed: time })}</span>;
  if (entry.state === "failed") return <span className="shell-command-outcome" data-state="failed"><AppIcon name="warning" size={12} />{t("Did not run")}</span>;
  const ok = entry.exitCode === 0;
  return <span className="shell-command-outcome" data-state={ok ? "ok" : "error"}><AppIcon name={ok ? "check" : "error"} size={12} />
    {t("Exit code {code} · {elapsed}", { code: entry.exitCode ?? -1, elapsed: time })}</span>;
}

/** Copies a text, and says so on the button for a moment. */
function CopyButton({ text, label }: { text: string; label: MessageKey }) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    if (!copied) return;
    const timer = window.setTimeout(() => setCopied(false), 1500);
    return () => window.clearTimeout(timer);
  }, [copied]);
  return <Button variant="minimal" size="small" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} aria-label={t(label)} title={t(label)}
    onClick={() => void navigator.clipboard?.writeText(text).then(() => setCopied(true), () => { /* The clipboard is unavailable: nothing is copied. */ })} />;
}

/**
 * What a command wrote, as plain text: as tall as its lines up to a limit, then scrolled; while the command runs, it
 * follows the last line unless the user scrolled up to read.
 */
function ShellOutput({ entry }: { entry: ShellCommandEntry }) {
  const element = useRef<HTMLPreElement>(null);
  const following = useRef(true);
  const text = shellOutputText(entry).replace(/\n$/u, "");
  useLayoutEffect(() => { following.current = true; }, [entry.id]);
  useLayoutEffect(() => {
    const node = element.current;
    if (node && following.current) node.scrollTop = node.scrollHeight;
  }, [text]);
  return <pre ref={element} className="shell-command-output" tabIndex={0}
    onScroll={event => { const node = event.currentTarget; following.current = node.scrollHeight - node.scrollTop - node.clientHeight < 4; }}>{text}</pre>;
}

/**
 * The shell commands of a session over its timeline: the command shown, its output in a terminal as it runs, how it
 * ended and what can be done with it; or the list of the commands of the session, the latest first. A field at its
 * foot runs another command. Closing it leaves the commands running.
 */
export function ShellCommandPanel({ store, sessionId, active, onRun, attachments }: {
  store: ShellCommands; sessionId: string; active: boolean;
  /** Runs a command as the prompt does: its output then goes with the next prompt. False when it was not run. */
  onRun: (command: string) => boolean;
  attachments: ShellAttachments | null;
}) {
  const { t, locale } = useShellLanguage();
  const [draft, setDraft] = useState("");
  const view = useSyncExternalStore(store.subscribe, () => store.view(sessionId));
  const paste = useContext(TerminalPasteContext);
  const visible = view.listing || view.shown !== null;
  const entry = view.entries.find(item => item.id === view.shown);
  const running = view.entries.some(item => item.state === "running");
  // The time of what runs goes on while it is shown.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!visible || !running) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [visible, running]);
  useEffect(() => {
    if (!active || !visible) return;
    const escape = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || event.defaultPrevented || event.isComposing || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey || modalDialogOpen()) return;
      event.preventDefault(); event.stopPropagation();
      store.close(sessionId);
    };
    window.addEventListener("keydown", escape, true);
    return () => window.removeEventListener("keydown", escape, true);
  }, [active, visible, store, sessionId]);
  if (!visible) return null;
  const count = view.entries.length;
  const shownNow = running ? now : Date.now();
  function run() {
    const command = draft.trim();
    if (command && onRun(command)) setDraft("");
  }
  const actions = (item: ShellCommandEntry) => <>
    {paste && <Button variant="minimal" size="small" icon={<AppIcon name="terminal" size={14} />} aria-label={t("Paste in terminal")} title={t("Paste in terminal")}
      onClick={() => paste(item.command, { sessionId })} />}
    <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={14} />} aria-label={t("Run again")} title={t("Run again")} onClick={() => { onRun(item.command); }} />
    <Button variant="minimal" size="small" icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove from the list")} title={t("Remove from the list")}
      onClick={() => store.remove(sessionId, item.id)} />
  </>;
  return <section className="shell-command" aria-label={t("Shell command")}>
    <header>
      <AppIcon name="terminal" size={15} className="shell-command-icon" />
      <strong>{t(view.listing ? "Recent shell commands" : "Shell command")}</strong>
      {view.listing && count > 0 && <Tag minimal round>{count}</Tag>}
      <span className="shell-command-hint" title={entry?.folder ?? undefined}>{entry?.folder ?? (view.listing ? "" : t("Starting…"))}</span>
      {view.listing && view.entries.some(item => item.state !== "running") && <Button variant="minimal" size="small" icon={<AppIcon name="eraser" size={14} />}
        onClick={() => store.clear(sessionId)}>{t("Clear")}</Button>}
      {!view.listing && count > 1 && <Button className="shell-command-recent" variant="minimal" size="small" icon={<AppIcon name="history" size={14} />}
        title={t("Recent shell commands")} onClick={() => store.list(sessionId)}>{t("Recent")}<Tag minimal round>{count}</Tag></Button>}
      <Button variant="minimal" size="small" icon={<AppIcon name="close" size={14} />} aria-label={t("Close")} title={t("Close (Esc)")} onClick={() => store.close(sessionId)} />
    </header>
    <div className="shell-command-body" data-view={view.listing ? "list" : "command"}>
      {view.listing ? count ? <ul className="shell-command-list">{[...view.entries].reverse().map(item => {
        const started = sessionTime(item.startedAt, locale);
        const last = shellOutputText(item).trimEnd().split("\n").at(-1)?.trim();
        return <li key={item.id} data-state={item.state}>
          <button type="button" className="shell-command-row" onClick={() => store.show(sessionId, item.id)}>
            <span className="shell-command-row-head"><code><span className="shell-command-sigil">!</span>{item.command}</code>
              <time dateTime={started.dateTime} title={started.title}>{started.label}</time></span>
            <span className="shell-command-row-foot"><Outcome entry={item} now={shownNow} />{last && <span className="shell-command-row-last">{last}</span>}</span>
          </button>
          <span className="shell-command-row-actions">{actions(item)}</span>
        </li>;
      })}</ul>
        : <div className="shell-command-empty"><AppIcon name="terminal" size={26} /><strong>{t("No shell commands yet.")}</strong>
          <span>{t("Type ! at the start of the prompt, then a command: it runs in the folder of the session.")}</span></div>
      : entry && <>
        <div className="shell-command-line"><span className="shell-command-sigil" aria-hidden="true">!</span><code>{entry.command}</code>
          <CopyButton text={entry.command} label="Copy the command" /></div>
        {entry.state === "failed" ? <p role="alert" className="shell-command-failure"><AppIcon name="warning" size={14} />
          <span>{entry.failure && failures[entry.failure.status] ? t(failures[entry.failure.status])
            : <>{t("The command could not be run.")}{entry.failure?.message && <> {entry.failure.message}</>}</>}</span></p>
          : entry.state === "running" && !entry.output ? null
          : entry.output
            ? <div className="shell-command-output-frame"><ShellOutput entry={entry} />
              {entry.output && <span className="shell-command-output-copy"><CopyButton text={shellOutputText(entry)} label="Copy the output" /></span>}</div>
            : <p className="shell-command-quiet">{t("No output.")}</p>}
        <div className="shell-command-footer">
          <Outcome entry={entry} now={shownNow} />
          {entry.state === "running" && <Button size="small" intent="danger" variant="outlined" icon={<AppIcon name="stop" size={12} fill="currentColor" />}
            onClick={() => store.stop(sessionId, entry.id)}>{t("Stop")}</Button>}
          <span className="shell-command-actions">
            {entry.state !== "running" && entry.state !== "failed" && attachments && (() => {
              const attached = attachments.has(entry);
              return <Button variant="minimal" size="small" className="shell-command-attach" active={attached} icon={<AppIcon name={attached ? "check" : "attach"} size={14} />}
                title={t(attached ? "The output goes with your next prompt: click to take it out" : "Add the output to your next prompt")}
                onClick={() => attachments.toggle(entry)}>{t(attached ? "In the prompt" : "Add to the prompt")}</Button>;
            })()}
            {actions(entry)}
          </span>
        </div>
      </>}
    </div>
    {/* Another command, without going back to the prompt: the panel shows it. */}
    <form className="shell-command-run" onSubmit={event => { event.preventDefault(); run(); }}>
      <InputGroup size="small" value={draft} onValueChange={setDraft} maxLength={shellCommandMaxLength} aria-label={t("Run another command…")}
        placeholder={t("Run another command…")} leftElement={<span className="shell-command-sigil shell-command-field-sigil" aria-hidden="true">!</span>} />
      <Button type="submit" size="small" intent="primary" icon={<AppIcon name="play" size={14} />} disabled={!draft.trim()} aria-label={t("Run")} title={t("Run")} />
    </form>
  </section>;
}
