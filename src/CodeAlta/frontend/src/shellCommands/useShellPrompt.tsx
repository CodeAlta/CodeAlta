import { useEffect, useRef, useSyncExternalStore, type KeyboardEvent, type ReactNode, type RefObject } from "react";
import { createPortal } from "react-dom";
import { Button } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import type { PromptInput } from "../PromptEditor";
import { useShellLanguage } from "../shellLanguage";
import { ShellCommandPanel } from "./ShellCommandPanel";
import { shellAttachment, shellCommandConcurrency, shellCommandMaxLength, type ShellCommandEntry, type ShellCommands, type ShellCommandTarget } from "./shellCommands";

/** How a prompt takes the output of a command: "later" while it takes no attachment (a Send on its way), "refused" when it cannot take this one. */
export type ShellPromptAttach = (entry: ShellCommandEntry, attached: boolean) => "done" | "later" | "refused";

const noSubscription = () => () => {};

/** Whether the attachments of a prompt hold the output of a command: the same command gives the same attachment. */
export function holdsShellOutput(attachments: readonly { mediaType: string; base64: string }[], entry: ShellCommandEntry): boolean {
  const file = shellAttachment(entry);
  return attachments.some(item => item.mediaType === file.mediaType && item.base64 === file.base64);
}

/**
 * The shell mode of a prompt, the same for the prompt of a session and for the one of a session to start: a `!` that
 * starts an empty prompt (typed, pasted, or typed while the keyboard was elsewhere) is no part of it, and the prompt
 * takes a command; Backspace in the empty prompt gives the prompt back; the command runs at once, and its output goes in
 * the attachments of the prompt once it ended. The prompt gives what is its own: its text, how it is emptied and how its
 * attachments change; it shows what this gives: the status, the Run button, the button of its commands and their panel.
 */
export function useShellPrompt({ store, prompt, target, input, text, value, clear, enabled, attach, attached, notice, onEdit, onPanel, slot, active, onSubmit }: {
  store: ShellCommands | undefined;
  /** The prompt the commands belong to: the id of a session, or the scope of a draft. */
  prompt: string;
  /** What the commands run for; null where none runs (a session of another machine, a draft of no project). */
  target: ShellCommandTarget | null;
  input: RefObject<PromptInput | null>;
  /** The text the editor holds now, and the one drawn last. */
  text: () => string; value: string;
  /** Empties the prompt. */
  clear: () => void;
  /** Whether the prompt takes a command now. */
  enabled: boolean;
  attach: ShellPromptAttach;
  /** Whether the attachments of the prompt hold the output of a command. */
  attached: (entry: ShellCommandEntry) => boolean;
  /** Says why a command did not run; an empty text takes the notice away. */
  notice: (message: string) => void;
  /** The prompt changed by a key of the shell mode: what follows its edits (its recall) starts again. */
  onEdit?: () => void;
  /** The panel of the commands opens: what else shows there closes. */
  onPanel?: () => void;
  /** Where the panel shows, over the timeline or the welcome. */
  slot: HTMLElement | null;
  active: boolean;
  /** What the Run button does: the Send of the prompt, which runs the command. */
  onSubmit: () => void;
}) {
  const { t } = useShellLanguage();
  const available = !!store && !!target;
  const subscribe = available ? store!.subscribe : noSubscription;
  // Numbers that change seldom, not the output that streams: the prompt is not drawn again for each piece.
  const mode = useSyncExternalStore(subscribe, () => available && store!.mode(prompt));
  const count = useSyncExternalStore(subscribe, () => available ? store!.count(prompt) : 0);
  const running = useSyncExternalStore(subscribe, () => available ? store!.runningCount(prompt) : 0);
  // The outputs of the commands that ended while the prompt took no attachment: attached once it does.
  const waiting = useRef<ShellCommandEntry[]>([]);
  const mounted = useRef(false);
  const attachLatest = useRef(attach); attachLatest.current = attach;
  useEffect(() => { mounted.current = true; return () => { mounted.current = false; }; }, []);
  useEffect(() => {
    if (!waiting.current.length) return;
    const pending = waiting.current;
    waiting.current = [];
    for (const entry of pending) if (attach(entry, true) === "later") waiting.current.push(entry);
  });

  const canStart = () => available && enabled && !store!.mode(prompt) && !text();
  function setMode(on: boolean) { onEdit?.(); store!.setMode(prompt, on); }
  function run(command: string): boolean {
    if (!available || !enabled) return false;
    if (store!.runningCount(prompt) >= shellCommandConcurrency) {
      notice(t("{count} commands of this session still run: stop one first.", { count: shellCommandConcurrency })); return false;
    }
    notice("");
    onPanel?.();
    return store!.run(prompt, command, target!, entry => {
      if (mounted.current && entry.state !== "failed" && attachLatest.current(entry, true) === "later") waiting.current.push(entry);
    }) !== null;
  }
  // A text that comes in an empty prompt at once, pasted or typed while the keyboard was elsewhere, and starts with `!`.
  function takeText(value: string): boolean {
    if (!value.startsWith("!") || !canStart()) return false;
    setMode(true);
    input.current?.replaceText(value.slice(1));
    return true;
  }
  return {
    available, mode, count,
    /** The keys of the shell mode, before the editor gets them; true when one was taken. */
    keyDown(event: KeyboardEvent<HTMLDivElement>): boolean {
      if (event.ctrlKey || event.altKey || event.metaKey || event.nativeEvent.isComposing) return false;
      // `!` at the start of an empty prompt turns it into a shell command, and is not written.
      const take = event.key === "!" ? canStart()
        // Backspace in an empty prompt in shell mode gives the prompt back, as it was before the `!`.
        : event.key === "Backspace" && !event.shiftKey && mode && !text();
      if (!take) return false;
      event.preventDefault(); event.stopPropagation();
      setMode(event.key === "!");
      return true;
    },
    takeText,
    /** A prompt in shell mode is a command, run at once: true when the prompt was one (sent or not), for its Send to stop there. */
    take(): boolean {
      if (!available || !store!.mode(prompt)) return false;
      const command = text().trim();
      if (!command) return true;
      if (command.length > shellCommandMaxLength) { notice(t("A shell command can have at most 16384 characters.")); return true; }
      if (!run(command)) return true;
      setMode(false);
      clear();
      return true;
    },
    /** The command of the window (`/shell`): the prompt takes a shell command. */
    start() {
      if (!available) return;
      setMode(true);
      requestAnimationFrame(() => input.current?.focus());
    },
    /** In shell mode, the commands run before, for the prompt to bring them back; null otherwise. */
    commands: (): readonly string[] | null => available && store!.mode(prompt) ? store!.commands(prompt) : null,
    editor: { shell: mode, placeholder: mode ? t("Type a shell command to run in the folder of this session…") : undefined },
    status: mode ? <span className="composer-foreground-status" data-tone="shell">
      <AppIcon name="terminal" size={14} /><span className="composer-foreground-text">{t("Shell command")}</span>
      <span className="composer-shell-folder">{t("Enter runs it · Backspace on an empty line returns to the prompt")}</span></span> : null,
    runButton: mode ? <Button data-composer-command={active ? "send" : undefined} className="composer-shell-run" intent="primary" aria-label={t("Run the command")} title={t("Run the command (Enter)")}
      icon={<AppIcon name="play" size={16} />} disabled={!enabled || !value.trim()}
      data-unavailable={!value.trim() ? t("Type a command first.") : undefined} onClick={onSubmit} /> : null,
    // The commands of the prompt, once it has some: `!` and `/shell` start the first one.
    button: available && count > 0 ? <button type="button" className="composer-shell-commands" disabled={!enabled}
      data-shell={mode || undefined} data-running={running > 0 || undefined}
      aria-label={t("{count} shell commands: show them", { count })} title={t("{count} shell commands: show them", { count })}
      onClick={() => {
        if (store!.isOpen(prompt)) store!.close(prompt);
        else { onPanel?.(); store!.list(prompt); }
      }}>
      {running > 0 ? <ActivitySpinner size={13} /> : <span className="composer-shell-sigil" aria-hidden="true">!</span>}
      <AppIcon name="terminal" size={14} /><span className="composer-shell-count">{count}</span></button> : null,
    panel: available && slot ? createPortal(<ShellCommandPanel store={store!} sessionId={prompt} active={active} onRun={run}
      attachments={enabled ? { has: attached, toggle: entry => { attach(entry, !attached(entry)); } } : null} />, slot) : null as ReactNode,
  };
}
