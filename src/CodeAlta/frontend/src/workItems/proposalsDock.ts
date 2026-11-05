/** The side of Notes the proposals of a session sit on. */
export type DockSide = "left" | "right" | "above" | "below";

/** A rectangle relative to the top left corner of the session pane. */
export type PaneRect = { left: number; top: number; width: number; height: number };

/** Where the proposals go: beside Notes on a side, or in a strip at the bottom of a pane too small for any side. */
export type ProposalsPlacement =
  | { side: DockSide; left: number; top: number; width: number; maxHeight: number }
  | { side: "strip"; left: number; width: number };

export const dockSides: readonly DockSide[] = Object.freeze(["left", "right", "above", "below"]);
const opposite: Readonly<Record<DockSide, DockSide>> = Object.freeze({ left: "right", right: "left", above: "below", below: "above" });
const storageKey = "codealta.desktop.proposals-dock.v1";
// The margins of the pane: the cards keep clear of its sides, and of its top and bottom edges.
const margin = 14, edge = 8, gap = 8;

/**
 * The side the user chose last, for every session pane of the window; null (automatic) until then: on the left
 * of Notes, or on another side where Notes leave room.
 */
export function loadDockSide(storage: Pick<Storage, "getItem"> | undefined = globalThis.localStorage): DockSide | null {
  try {
    const value = storage?.getItem(storageKey);
    return dockSides.includes(value as DockSide) ? value as DockSide : null;
  } catch { return null; }
}

/** Keeps the side the user chose, or automatic (null); a storage that cannot write leaves it to this window. */
export function saveDockSide(side: DockSide | null, storage: Pick<Storage, "setItem" | "removeItem"> | undefined = globalThis.localStorage): void {
  try {
    if (side === null) storage?.removeItem(storageKey); else storage?.setItem(storageKey, side);
  } catch { /* The choice still applies until the window closes. */ }
}

/** The band along one edge of the pane that Notes keep clear of, so that the cards have room on the chosen side. */
export type NotesKeepOut = { side: DockSide; size: number };

/**
 * The band Notes keep clear of for the side the user chose: Notes move out of it, and only as far as they
 * reach into it, without changing their saved place. The band depends on the pane and the cards, not on where
 * Notes are, so that moving Notes cannot change it. None when the user chose no side, or when the pane is too
 * small for Notes and the band together: the cards then go where Notes leave room.
 * @param pane The size of the session pane.
 * @param card The natural size of the cards (their chip, or the card whole).
 * @param collapsed The cards are put away in their chip.
 * @param dock The side the user chose, or null.
 * @param notesMinimum The smallest size of Notes: its tab, or its smallest window.
 */
export function notesKeepOut(pane: { width: number; height: number }, card: { width: number; height: number }, collapsed: boolean,
  dock: DockSide | null, notesMinimum: { width: number; height: number }): NotesKeepOut | null {
  if (!dock) return null;
  const width = collapsed ? card.width : 280;
  // Above or below, a tall card keeps at most half the pane, and scrolls.
  const height = Math.min(card.height, Math.round(pane.height / 2));
  // The overlay of Notes is inset from the pane by 8 pixels (14 on the right), the cards keep their margins.
  const size = dock === "left" ? width + gap + margin - 8 : dock === "right" ? width + gap : height + gap;
  const room = dock === "left" || dock === "right" ? pane.width - 22 - size - notesMinimum.width : pane.height - 16 - size - notesMinimum.height;
  return room >= 0 ? { side: dock, size } : null;
}

/**
 * Places the proposals on the chosen side of Notes, or on the opposite side, then the others, when it has no
 * room; in a strip at the bottom when no side has. Automatic (no side chosen), the cards go on the left of Notes
 * first. A chosen side has room once Notes keep clear of their band (see {@link notesKeepOut}); until then, and
 * in a pane too small for the band, the fallback does not change the side the user chose.
 * @param pane The size of the session pane.
 * @param notes The visible surface of Notes (its tab, or its window when it is open).
 * @param card The natural size of the cards (their chip, or the card whole).
 * @param collapsed The cards are put away in their chip.
 * @param dock The side the user chose, or null for automatic.
 */
export function placeProposals(pane: { width: number; height: number }, notes: PaneRect, card: { width: number; height: number },
  collapsed: boolean, dock: DockSide | null): ProposalsPlacement {
  const required = collapsed ? card.width : 280, preferred = collapsed ? card.width : 380;
  // A card above or below Notes may scroll, but it must show a readable part of itself.
  const minimumHeight = collapsed ? card.height : Math.min(card.height, 160);
  const right = notes.left + notes.width, bottom = notes.top + notes.height;
  const besideTop = () => Math.max(edge, Math.min(notes.top, pane.height - edge - card.height));
  const stackedWidth = Math.min(preferred, pane.width - margin * 2);
  // Above or below, the cards keep the right edge of Notes, as long as they stay inside the pane.
  const stackedLeft = () => Math.min(Math.max(right - stackedWidth, margin), pane.width - margin - stackedWidth);
  const place = (side: DockSide): ProposalsPlacement | null => {
    switch (side) {
      case "left": {
        const space = notes.left - gap - margin;
        if (space + 1 < required) return null;
        const width = Math.min(preferred, space), top = besideTop();
        return { side, left: notes.left - gap - width, top, width, maxHeight: pane.height - edge - top };
      }
      case "right": {
        const space = pane.width - margin - (right + gap);
        if (space + 1 < required) return null;
        const width = Math.min(preferred, space), top = besideTop();
        return { side, left: right + gap, top, width, maxHeight: pane.height - edge - top };
      }
      case "above": {
        const available = notes.top - gap - edge;
        if (available + 1 < minimumHeight || stackedWidth < required) return null;
        const height = Math.min(card.height, available);
        return { side, left: stackedLeft(), top: notes.top - gap - height, width: stackedWidth, maxHeight: available };
      }
      case "below": {
        const top = bottom + gap, available = pane.height - edge - top;
        if (available + 1 < minimumHeight || stackedWidth < required) return null;
        return { side, left: stackedLeft(), top, width: stackedWidth, maxHeight: available };
      }
    }
  };
  const order = dock ? [dock, opposite[dock], ...dockSides.filter(value => value !== dock && value !== opposite[dock])] : dockSides;
  for (const side of order) {
    const placed = place(side);
    if (placed) return placed;
  }
  return { side: "strip", left: margin, width: pane.width - margin * 2 };
}

/** The targets shown while the user drags the proposals: a cross around the middle of Notes, kept inside the pane. */
export function dockTargets(pane: { width: number; height: number }, notes: PaneRect): Record<DockSide, PaneRect> {
  const size = 32, step = 40, reach = step + size / 2 + 2;
  const clamp = (value: number, length: number) => Math.min(Math.max(value, reach), Math.max(reach, length - reach));
  const x = clamp(notes.left + notes.width / 2, pane.width), y = clamp(notes.top + notes.height / 2, pane.height);
  const at = (dx: number, dy: number): PaneRect => ({ left: x + dx - size / 2, top: y + dy - size / 2, width: size, height: size });
  return { left: at(-step, 0), right: at(step, 0), above: at(0, -step), below: at(0, step) };
}

/** The target under a point of the pane, if any. */
export function dockTargetAt(targets: Record<DockSide, PaneRect>, x: number, y: number): DockSide | null {
  return dockSides.find(side => {
    const target = targets[side];
    return x >= target.left && x <= target.left + target.width && y >= target.top && y <= target.top + target.height;
  }) ?? null;
}
