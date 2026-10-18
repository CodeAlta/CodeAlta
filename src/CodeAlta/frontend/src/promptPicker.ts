import { useLayoutEffect, useRef, useState, type RefObject } from "react";
import type { PromptInput } from "./PromptEditor";

/** The token at the caret that opened a picker, with the prompt text it was found in. */
export type PromptTrigger = Readonly<{ text: string; start: number; end: number; query: string }>;
export type PromptTokenDetector = (text: string, caret: number) => { start: number; end: number; query: string } | null;
/** A trigger token closed without a choice: its start and the prompt text up to its trigger character. */
export type PromptTriggerDismissal = Readonly<{ start: number; prefix: string }>;

export function dismissPromptTrigger(trigger: PromptTrigger): PromptTriggerDismissal {
  return { start: trigger.start, prefix: trigger.text.slice(0, trigger.start + 1) };
}

/**
 * Whether a dismissal still holds for the prompt text: it lasts while the text up to the trigger character is
 * unchanged, so typing on after the token keeps the picker closed, and deleting the trigger character ends it.
 */
export function promptTriggerDismissalHolds(dismissal: PromptTriggerDismissal, text: string) {
  return text.startsWith(dismissal.prefix);
}

/** Ctrl+Space in the prompt opens the picker again for the token at the caret, also one that was closed without a choice. */
export function isPromptPickerReopenKey(event: Pick<KeyboardEvent, "key" | "ctrlKey" | "altKey" | "metaKey" | "shiftKey" | "repeat" | "isComposing">) {
  return event.key === " " && event.ctrlKey && !event.altKey && !event.metaKey && !event.shiftKey && !event.repeat && !event.isComposing;
}

/**
 * Shared behavior of the prompt pickers (`@` files, `#` issues): watches the prompt editor, opens a modal
 * dialog when the caret sits in a trigger token, and on close either applies the replacement text or
 * leaves the text as typed; the caret then returns to the editor. A token closed without a choice stays
 * closed while typing goes on, until Ctrl+Space opens it again with the text after the trigger as query.
 */
export function usePromptPicker({ input, edit, enabled, detect, onOpen, focus }: {
  input: RefObject<PromptInput | null>; edit: (text: string) => void; enabled: boolean; detect: PromptTokenDetector;
  onOpen: (trigger: PromptTrigger) => void; focus: RefObject<HTMLElement | null>;
}) {
  const latest = useRef({ edit, enabled, detect, onOpen }); latest.current = { edit, enabled, detect, onOpen };
  const dialog = useRef<HTMLDialogElement>(null);
  // The token for which the picker was closed without a choice: it stays closed for that token.
  const dismissed = useRef<PromptTriggerDismissal | null>(null);
  const composing = useRef(false);
  const [trigger, setTrigger] = useState<PromptTrigger | null>(null);
  const open = !!trigger;

  useLayoutEffect(() => {
    const element = input.current;
    if (!element) return;
    // Opens the picker for the token at the caret; `reopen` also opens it for a token closed without a choice.
    const show = (reopen: boolean) => {
      if (composing.current || !latest.current.enabled || element.disabled || !element.contains(document.activeElement)) return false;
      const value = element.value;
      if (dismissed.current && !promptTriggerDismissalHolds(dismissed.current, value)) dismissed.current = null;
      if (element.selectionStart !== element.selectionEnd) return false;
      const span = latest.current.detect(value, element.selectionStart);
      if (!span || (!reopen && span.start === dismissed.current?.start)) return false;
      const parent = element.closest("dialog");
      if (Array.from(document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]')).some(node => node !== parent)) return false;
      const next = { text: value, ...span };
      setTrigger(next);
      latest.current.onOpen(next);
      return true;
    };
    const inspect = () => { show(false); };
    // Captured before the editor, which would otherwise show its own suggestions for Ctrl+Space.
    const reopen = (event: Event) => {
      if (event instanceof KeyboardEvent && isPromptPickerReopenKey(event) && show(true)) { event.preventDefault(); event.stopPropagation(); }
    };
    const begin = () => { composing.current = true; };
    const end = () => { composing.current = false; inspect(); };
    element.addEventListener("input", inspect); element.addEventListener("keyup", inspect); element.addEventListener("click", inspect);
    element.addEventListener("keydown", reopen, true);
    element.addEventListener("compositionstart", begin); element.addEventListener("compositionend", end);
    return () => {
      element.removeEventListener("input", inspect); element.removeEventListener("keyup", inspect); element.removeEventListener("click", inspect);
      element.removeEventListener("keydown", reopen, true);
      element.removeEventListener("compositionstart", begin); element.removeEventListener("compositionend", end);
    };
  }, [input]);

  useLayoutEffect(() => {
    const element = dialog.current;
    if (!open || !element) return;
    element.showModal();
    focus.current?.focus();
    return () => { if (element.open) element.close(); };
  }, [open]);

  /**
   * Opens the picker at the caret without a trigger token, from a command (the attach menu of the composer): what is chosen
   * is inserted there, and closing without a choice changes nothing.
   */
  function openAt() {
    const element = input.current;
    if (!element || element.disabled || !latest.current.enabled || trigger) return false;
    const caret = Math.min(element.selectionEnd ?? element.value.length, element.value.length);
    const next = { text: element.value, start: caret, end: caret, query: "" };
    setTrigger(next);
    latest.current.onOpen(next);
    return true;
  }

  /** Closes the picker; with `next` the prompt takes the replacement text, otherwise it stays as typed. */
  function close(next?: { text: string; caret: number }) {
    const value = trigger;
    if (!value) return;
    setTrigger(null);
    // A picker opened at the caret (`openAt`) has no token to keep closed.
    if (next) latest.current.edit(next.text); else if (value.end > value.start) dismissed.current = dismissPromptTrigger(value);
    const caret = next?.caret ?? value.end;
    // After the editor has taken the new text, put the caret back where the user continues typing.
    requestAnimationFrame(() => {
      const element = input.current;
      if (!element?.isConnected || element.disabled) return;
      element.focus(); element.setSelectionRange(caret, caret);
    });
  }
  return { trigger, dialog, close, openAt };
}
