import { useLayoutEffect } from "react";

/** Arrange this pane's proposals around its visible Notes surface, never its saved geometry or state. */
export function useWorkCardsPlacement(cards: HTMLElement | null, collapsed: boolean) {
  useLayoutEffect(() => {
    const pane = cards?.parentElement;
    const notes = pane?.querySelector<HTMLElement>(":scope > .session-notes-overlay");
    if (!cards || !pane?.classList.contains("session-timeline-area") || !notes) return;
    let frame = 0;
    const arrange = () => {
      frame = 0;
      const area = pane.getBoundingClientRect();
      const surface = notes.querySelector<HTMLElement>(notes.dataset.collapsed === "true" ? ".session-notes-toggle" : ".session-notes-content");
      if (!area.width || !area.height || !surface) return;
      const obstacle = surface.getBoundingClientRect();
      if (!obstacle.width || !obstacle.height) return;
      const margin = 14, gap = 8;
      const required = collapsed ? cards.firstElementChild!.getBoundingClientRect().width : 280;
      const preferred = collapsed ? required : 380;
      const leftEnd = obstacle.left - area.left - gap;
      const rightStart = obstacle.right - area.left + gap;
      const leftSpace = leftEnd - margin, rightSpace = area.width - margin - rightStart;
      // Prefer the left of Notes. A user can drag Notes left, so the right is available too.
      // If neither side can hold a readable card, reserve a bounded bottom strip. The Notes
      // owner clamps to its smaller bounds without changing the user's stored preference.
      const docked = leftSpace < required && rightSpace < required;
      const width = Math.min(preferred, docked ? area.width - margin * 2 : leftSpace >= required ? leftSpace : rightSpace);
      const left = !docked && leftSpace >= required ? leftEnd - width : area.width - margin - width;
      cards.style.left = `${left}px`;
      cards.style.right = "auto";
      cards.style.width = `${width}px`;
      cards.style.top = docked ? "auto" : "44px";
      cards.style.bottom = docked ? "8px" : "auto";
      cards.style.maxHeight = docked ? "40%" : "calc(100% - 52px)";
      if (docked) pane.style.setProperty("--session-proposals-reserved", `${cards.getBoundingClientRect().height + gap}px`);
      else pane.style.removeProperty("--session-proposals-reserved");
    };
    const schedule = () => { if (!frame) frame = requestAnimationFrame(arrange); };
    const resize = new ResizeObserver(schedule);
    resize.observe(pane); resize.observe(cards); resize.observe(notes);
    // react-rnd moves by transform, not resize. Observe only this sibling's geometry/disclosure
    // attributes; no global registry, polling, shared owner or additional notes reader.
    const mutations = new MutationObserver(schedule);
    mutations.observe(notes, { subtree: true, childList: true, attributes: true, attributeFilter: ["style", "hidden", "data-collapsed"] });
    arrange();
    return () => {
      cancelAnimationFrame(frame); resize.disconnect(); mutations.disconnect();
      pane.style.removeProperty("--session-proposals-reserved");
    };
  }, [cards, collapsed]);
}
