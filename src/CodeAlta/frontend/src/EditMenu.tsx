import { useEffect, useState } from "react";
import type { IconName } from "./AppIcon";
import { isMacPlatform } from "./commandRegistry";
import { takeKeyContextMenu } from "./keyContextMenu";
import { isSessionContextKey } from "./sessionRowActions";
import { SessionTabMenu, type SessionMenuEntry } from "./SessionTabMenu";
import { useShellLanguage } from "./shellLanguage";
import { copyPageSelection, fieldIsDisabled, fieldIsWritable, hasPageSelectionAt, hasSelection, runTextFieldEdit, textFieldEdits, textFieldOf,
  type TextClipboard, type TextFieldEdit } from "./textFieldEdits";

/** What the menu of a right-click edits: a text field, an editor of Monaco, or the text selected in the page. */
export type EditTarget = Readonly<{
  /** The entries the menu offers, each enabled or not: the text of the page is only copied. */
  edits: Readonly<Partial<Record<TextFieldEdit, boolean>>>;
  /** What was clicked: the modal dialog around it holds the menu, which shows above the rest of the page. */
  element: HTMLElement;
  /** Where the menu opens when the keyboard asked for it (the menu key, Shift+F10): under the field, or the caret. */
  keyboardPoint: () => Readonly<{ x: number; y: number }>;
  /** Does what an entry asks. Rejects when the clipboard cannot be read or written, and nothing then changed. */
  run: (edit: TextFieldEdit, clipboard: TextClipboard) => Promise<void>;
  /** Calls `close` when the target loses the focus; returns what stops listening. */
  onBlur?: (close: () => void) => () => void;
}>;

/**
 * Finds what a request for a menu edits, or null when it is not for that kind of target.
 * @param target Where the request lands: what was clicked, or what has the focus for a request of the keyboard.
 * @param point Where the pointer was pressed, in the coordinates of the window; null for a request of the keyboard.
 */
export type EditTargetResolver = (target: EventTarget | null, point: Readonly<{ x: number; y: number }> | null) => EditTarget | null;

type Opened = Readonly<{ serial: number; target: EditTarget; at: Readonly<{ x: number; y: number }>; keyboard: boolean }>;

// The entries in their order, each with its icon and the letter of its key; select all stands apart from the clipboard.
const entries: readonly Readonly<{ edit: TextFieldEdit; label: "Cut" | "Copy" | "Paste" | "Select all"; icon: IconName; letter: string }>[] = [
  { edit: "cut", label: "Cut", icon: "cut", letter: "X" },
  { edit: "copy", label: "Copy", icon: "copy", letter: "C" },
  { edit: "paste", label: "Paste", icon: "paste", letter: "V" },
  { edit: "selectAll", label: "Select all", icon: "selectAll", letter: "A" },
];

/** The point under an element, where a menu the keyboard asked for opens. */
function belowElement(element: Element): Readonly<{ x: number; y: number }> {
  const box = element.getBoundingClientRect();
  return { x: box.left, y: box.bottom + 2 };
}

function fieldTarget(target: EventTarget | null): EditTarget | null {
  const field = textFieldOf(target);
  if (!field || fieldIsDisabled(field)) return null;
  if (field.ownerDocument.activeElement !== field) field.focus({ preventScroll: true });
  return {
    edits: textFieldEdits({ hidden: field.type === "password", length: field.value.length, writable: fieldIsWritable(field) }, hasSelection(field)),
    element: field,
    keyboardPoint: () => belowElement(field),
    run: (edit, clipboard) => runTextFieldEdit(field, edit, clipboard),
    onBlur: close => { field.addEventListener("blur", close); return () => field.removeEventListener("blur", close); },
  };
}

function pageTarget(target: EventTarget | null): EditTarget | null {
  if (!(target instanceof HTMLElement) || !hasPageSelectionAt(target)) return null;
  return { edits: { copy: true }, element: target, keyboardPoint: () => belowElement(target),
    run: (_edit, clipboard) => copyPageSelection(target.ownerDocument, clipboard) };
}

/**
 * The menu of a right-click that no control took, since the window shows no menu of its own: cut, copy, paste and
 * select all in a text field of the page (the key of a provider, a description, a search) and, through `editors`, in
 * the editors (the prompt, a file, a prompt of an agent), all with the same entries; copy on text selected in the page.
 * A control that shows a menu of its own (a row, a tab, a terminal, a link) keeps it.
 */
export function EditMenu({ clipboard, editors }: {
  /** The clipboard of the system when none is given. */
  clipboard?: TextClipboard;
  /** Finds the editor a request lands in: the editors of Monaco, which the page has from the application. */
  editors?: EditTargetResolver;
}) {
  const [opened, setOpened] = useState<Opened | null>(null);
  useEffect(() => {
    let serial = 0;
    const open = (where: EventTarget | null, point: Readonly<{ x: number; y: number }> | null) => {
      const target = editors?.(where, point) ?? fieldTarget(where) ?? pageTarget(where);
      // Asked by the keyboard, the menu opens under the field or the caret.
      if (target) setOpened({ serial: ++serial, target, keyboard: !point, at: point ?? target.keyboardPoint() });
      return !!target;
    };
    const show = (event: MouseEvent) => {
      if (event.defaultPrevented) return;
      // A right-click in the menu itself opens no other one over it.
      if (event.target instanceof Element && event.target.closest(".edit-menu")) { event.preventDefault(); return; }
      // A request without a press of the pointer (a key the page did not read) has no place of its own.
      const pointer = event.button === 2 || event.ctrlKey;
      if (open(event.target, pointer ? { x: event.clientX, y: event.clientY } : null)) event.preventDefault();
    };
    // The page reads the menu key and Shift+F10 itself, as for the other menus of the application: macOS asks for no menu
    // with them, and Windows and Linux ask once the key is up, which is then taken (takeKeyContextMenu).
    const key = (event: KeyboardEvent) => {
      if (event.defaultPrevented || event.repeat || !isSessionContextKey(event.key, event.shiftKey, event.isComposing || event.keyCode === 229, false)) return;
      if (!open(event.target, null)) return;
      event.preventDefault();
      takeKeyContextMenu();
    };
    document.addEventListener("contextmenu", show);
    document.addEventListener("keydown", key);
    return () => { document.removeEventListener("contextmenu", show); document.removeEventListener("keydown", key); };
  }, [editors]);
  if (!opened) return null;
  return <EditPopup key={opened.serial} opened={opened} clipboard={clipboard}
    onClose={() => setOpened(current => current === opened ? null : current)} />;
}

function EditPopup({ opened, clipboard, onClose }: { opened: Opened; clipboard?: TextClipboard; onClose: () => void }) {
  const { t } = useShellLanguage();
  const { target } = opened;
  // The keys of the system the page runs on.
  const control = isMacPlatform() ? "⌘" : "Ctrl+";
  const offered = entries.filter(entry => target.edits[entry.edit] !== undefined);
  const enabled = offered.filter(entry => target.edits[entry.edit]).map(entry => entry.edit);
  // The entry the keys choose: the first one when the keyboard opened the menu, none under the pointer.
  const [active, setActive] = useState<TextFieldEdit | null>(opened.keyboard ? enabled[0] ?? null : null);

  function run(edit: TextFieldEdit) {
    void target.run(edit, clipboard ?? navigator.clipboard)
      .catch(() => { /* The clipboard cannot be read or written: nothing changed, and the keys still cut, copy and paste. */ });
  }

  // The focus stays in the text, as under the menu of the system: a field that saves or closes when it loses it stays.
  // The keys of the menu are taken before the page sees them (Escape closes the menu, not the dialog around the field);
  // any other key closes it and goes on to the text.
  useEffect(() => {
    const keyDown = (event: KeyboardEvent) => {
      if (event.isComposing || event.keyCode === 229) return;
      const index = active ? enabled.indexOf(active) : -1;
      const take = () => { event.preventDefault(); event.stopPropagation(); };
      const next = event.key === "ArrowDown" ? (index + 1) % enabled.length : event.key === "ArrowUp" ? (index <= 0 ? enabled.length - 1 : index - 1)
        : event.key === "Home" ? 0 : event.key === "End" ? enabled.length - 1 : null;
      if (next !== null) { take(); if (enabled.length) setActive(enabled[next]); return; }
      if (event.key === "Enter") { take(); onClose(); if (active) run(active); return; }
      if (event.key === "Escape") { take(); onClose(); return; }
      if (!["Shift", "Control", "Alt", "Meta"].includes(event.key)) onClose();
    };
    const scroll = (event: Event) => { if (!(event.target instanceof Element && event.target.closest(".edit-menu"))) onClose(); };
    window.addEventListener("keydown", keyDown, true);
    window.addEventListener("blur", onClose);
    window.addEventListener("scroll", scroll, true);
    const stopBlur = target.onBlur?.(onClose);
    return () => {
      window.removeEventListener("keydown", keyDown, true);
      window.removeEventListener("blur", onClose);
      window.removeEventListener("scroll", scroll, true);
      stopBlur?.();
    };
  });

  const items: SessionMenuEntry[] = offered.flatMap(entry => [
    ...entry.edit === "selectAll" && offered.length > 1 ? [{ key: "divider", divider: true } as const] : [],
    { key: entry.edit, label: t(entry.label), icon: entry.icon, disabled: !target.edits[entry.edit], endNode: <span>{control + entry.letter}</span>,
      onSelect: () => run(entry.edit) },
  ]);
  return <SessionTabMenu anchor={target.element} at={opened.at} items={items} title={t("Edit")} className="edit-menu"
    container={target.element.closest("dialog") ?? document.body} current={() => true} onClose={onClose}
    keepFocus={{ active, onActive: key => setActive(key as TextFieldEdit) }} />;
}
