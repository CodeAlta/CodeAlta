import { useLayoutEffect, useRef, useState, type RefObject } from "react";
import type { PromptInput } from "./PromptEditor";

/** The token at the caret that opened a picker, with the prompt text it was found in. */
export type PromptTrigger = Readonly<{ text: string; start: number; end: number; query: string }>;
export type PromptTokenDetector = (text: string, caret: number) => { start: number; end: number; query: string } | null;

/**
 * Shared behavior of the prompt pickers (`@` files, `#` issues): watches the prompt editor, opens a modal
 * dialog when the caret sits in a trigger token, and on close either applies the replacement text or
 * leaves the text as typed; the caret then returns to the editor.
 */
export function usePromptPicker({ input, edit, enabled, detect, onOpen, focus }: {
  input: RefObject<PromptInput | null>; edit: (text: string) => void; enabled: boolean; detect: PromptTokenDetector;
  onOpen: (trigger: PromptTrigger) => void; focus: RefObject<HTMLElement | null>;
}) {
  const latest = useRef({ edit, enabled, detect, onOpen }); latest.current = { edit, enabled, detect, onOpen };
  const dialog = useRef<HTMLDialogElement>(null);
  // The prompt text for which the picker was closed without a choice: it stays closed until the text changes.
  const dismissed = useRef<string | null>(null);
  const composing = useRef(false);
  const [trigger, setTrigger] = useState<PromptTrigger | null>(null);
  const open = !!trigger;

  useLayoutEffect(() => {
    const element = input.current;
    if (!element) return;
    const inspect = () => {
      if (composing.current || !latest.current.enabled || element.disabled || !element.contains(document.activeElement)) return;
      const value = element.value;
      if (dismissed.current !== null && dismissed.current !== value) dismissed.current = null;
      if (dismissed.current === value || element.selectionStart !== element.selectionEnd) return;
      const span = latest.current.detect(value, element.selectionStart);
      if (!span) return;
      const parent = element.closest("dialog");
      if (Array.from(document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]')).some(node => node !== parent)) return;
      const next = { text: value, ...span };
      setTrigger(next);
      latest.current.onOpen(next);
    };
    const begin = () => { composing.current = true; };
    const end = () => { composing.current = false; inspect(); };
    element.addEventListener("input", inspect); element.addEventListener("keyup", inspect); element.addEventListener("click", inspect);
    element.addEventListener("compositionstart", begin); element.addEventListener("compositionend", end);
    return () => {
      element.removeEventListener("input", inspect); element.removeEventListener("keyup", inspect); element.removeEventListener("click", inspect);
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

  /** Closes the picker; with `next` the prompt takes the replacement text, otherwise it stays as typed. */
  function close(next?: { text: string; caret: number }) {
    const value = trigger;
    if (!value) return;
    setTrigger(null);
    if (next) latest.current.edit(next.text); else dismissed.current = value.text;
    const caret = next?.caret ?? value.end;
    // After the editor has taken the new text, put the caret back where the user continues typing.
    requestAnimationFrame(() => {
      const element = input.current;
      if (!element?.isConnected || element.disabled) return;
      element.focus(); element.setSelectionRange(caret, caret);
    });
  }
  return { trigger, dialog, close };
}
