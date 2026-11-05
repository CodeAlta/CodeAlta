import { useLayoutEffect } from "react";
import { dockSides, notesKeepOut, placeProposals, type DockSide, type PaneRect } from "./proposalsDock";

/** The pane property that holds the band Notes keep clear of on one side, in pixels. */
export const keepOutProperty = (side: DockSide) => `--session-notes-keep-out-${side}`;

/** The visible surface of the Notes of a pane, relative to the pane: its tab, or its window when it is open. */
export function notesSurface(pane: HTMLElement): PaneRect | null {
  const notes = pane.querySelector<HTMLElement>(":scope > .session-notes-overlay");
  if (!notes || notes.dataset.hidden === "true") return null;
  const surface = notes.querySelector<HTMLElement>(notes.dataset.collapsed === "true" ? ".session-notes-toggle" : ".session-notes-content");
  const area = pane.getBoundingClientRect(), box = surface?.getBoundingClientRect();
  if (!box?.width || !box.height || !area.width || !area.height) return null;
  return { left: box.left - area.left, top: box.top - area.top, width: box.width, height: box.height };
}

/**
 * Arrange this pane's proposals on the side of its visible Notes surface that the user chose, never changing
 * the saved geometry or state of Notes: the two move together when the user drags Notes. A side the user chose
 * gets room: Notes keep clear of a band along that edge of the pane. Hidden Notes leave the proposals at their
 * place of the stylesheet, over the top right corner.
 */
export function useWorkCardsPlacement(cards: HTMLElement | null, collapsed: boolean, dock: DockSide | null) {
  useLayoutEffect(() => {
    const pane = cards?.parentElement;
    const notes = pane?.querySelector<HTMLElement>(":scope > .session-notes-overlay");
    if (!cards || !pane?.classList.contains("session-timeline-area") || !notes) return;
    let frame = 0;
    const keepOut = (band: { side: DockSide; size: number } | null) => {
      for (const side of dockSides) {
        const value = band?.side === side ? `${band.size}px` : "";
        if (pane.style.getPropertyValue(keepOutProperty(side)) === value) continue;
        if (value) pane.style.setProperty(keepOutProperty(side), value); else pane.style.removeProperty(keepOutProperty(side));
      }
    };
    const release = () => {
      for (const property of ["left", "right", "width", "top", "bottom", "max-height"]) cards.style.removeProperty(property);
      delete cards.dataset.side;
      pane.style.removeProperty("--session-proposals-reserved");
      keepOut(null);
    };
    const arrange = () => {
      frame = 0;
      if (notes.dataset.hidden === "true") { release(); return; }
      const surface = notesSurface(pane), card = cards.firstElementChild as HTMLElement | null;
      if (!surface || !card) return;
      const box = pane.getBoundingClientRect(), area = { width: box.width, height: box.height };
      // The natural height of the card, the same whether it is cut short now or not: a card cut short gives way
      // rather than scroll. Measured so, the band would change with it, Notes would move, the card would be cut
      // short again, and so on. The limit is lifted for the measure only, within this frame.
      const limit = cards.style.maxHeight;
      cards.style.maxHeight = "none";
      const size = { width: card.getBoundingClientRect().width, height: card.getBoundingClientRect().height };
      cards.style.maxHeight = limit;
      // Notes move out of the band of the chosen side; the band does not depend on where they are.
      const notesCollapsed = notes.dataset.collapsed === "true";
      keepOut(notesKeepOut(area, size, collapsed, dock, notesCollapsed ? surface : { width: 240, height: 140 }));
      // The strip makes Notes smaller: measured so, a side below Notes would get room, the strip would go,
      // Notes would grow back and the strip return. While it is there, Notes count with the room it took.
      const reserved = parseFloat(pane.style.getPropertyValue("--session-proposals-reserved")) || 0;
      const placed = placeProposals(area, { ...surface, height: surface.height + reserved }, size, collapsed, dock);
      cards.dataset.side = placed.side;
      cards.style.left = `${Math.round(placed.left)}px`;
      cards.style.right = "auto";
      cards.style.width = `${Math.round(placed.width)}px`;
      if (placed.side === "strip") {
        // No side has room: a bounded strip at the bottom, which the Notes owner clamps above without
        // changing the user's stored preference.
        cards.style.top = "auto";
        cards.style.bottom = "8px";
        cards.style.maxHeight = "40%";
        pane.style.setProperty("--session-proposals-reserved", `${cards.getBoundingClientRect().height + 8}px`);
      } else {
        cards.style.top = `${Math.round(placed.top)}px`;
        cards.style.bottom = "auto";
        cards.style.maxHeight = `${Math.round(placed.maxHeight)}px`;
        pane.style.removeProperty("--session-proposals-reserved");
      }
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(arrange); };
    const resize = new ResizeObserver(schedule);
    resize.observe(pane); resize.observe(cards); resize.observe(notes);
    // react-rnd moves by transform, not resize. Observe only this sibling's geometry/disclosure
    // attributes; no global registry, polling, shared owner or additional notes reader.
    const mutations = new MutationObserver(schedule);
    mutations.observe(notes, { subtree: true, childList: true, attributes: true, attributeFilter: ["style", "hidden", "data-collapsed", "data-hidden"] });
    arrange();
    return () => {
      cancelAnimationFrame(frame); resize.disconnect(); mutations.disconnect();
      release();
    };
  }, [cards, collapsed, dock]);
}
