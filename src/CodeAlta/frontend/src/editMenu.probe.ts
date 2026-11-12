// What the fixtures of the edit menu (editMenu.mount.tsx, editMenu.editors.mount.tsx) share: a clipboard of their own,
// and the entries of the open menu as the tests read them.

/** A clipboard that keeps its text in `state.clipboard`, so that a test does not touch the clipboard of the system. */
export function fixtureClipboard(state: { clipboard: string }) {
  return { readText: async () => state.clipboard, writeText: async (text: string) => { state.clipboard = text; } };
}

/** An entry of the open menu: its text, whether it is disabled, and whether the keys chose it. */
export type MenuEntry = Readonly<{ text: string; disabled: boolean; active: boolean }>;

/** The entries of the open menu, or null when none is open. */
export function readEditMenu(): MenuEntry[] | null {
  const items = Array.from(document.querySelectorAll<HTMLElement>(".edit-menu [role=menuitem]"));
  return items.length ? items.map(item => ({ text: item.querySelector(".bp6-fill")?.textContent ?? "", disabled: item.getAttribute("aria-disabled") === "true",
    active: item.classList.contains("session-tab-active") })) : null;
}

/** The middle of an element, in the coordinates of the page. */
export function middleOf(element: Element) {
  const box = element.getBoundingClientRect();
  return { x: Math.round(box.x + box.width / 2), y: Math.round(box.y + box.height / 2) };
}
