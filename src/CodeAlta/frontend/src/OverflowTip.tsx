import { useEffect, useRef, useState, type RefObject } from "react";
import { Portal } from "@blueprintjs/core";

type Tip = Readonly<{ text: string; left: number; top: number | null; bottom: number | null; maxWidth: number }>;
const hoverDelay = 350;
const margin = 8;

/** The whole text of a label: what its target names in `data-full-text`, else the words of the label. */
export function overflowTipText(target: HTMLElement, label: HTMLElement): string | null {
  const shown = (label.textContent ?? "").trim();
  const full = target.dataset.fullText?.trim() || shown;
  if (!full) return null;
  // Cut by its width (one line), by its lines (a label that wraps), or by the host, which gave a shorter text.
  const cut = label.scrollWidth > label.clientWidth + 1 || label.scrollHeight > label.clientHeight + 1 || !shown.includes(full);
  return cut ? full : null;
}

/**
 * The whole text of the labels of a container that their place cuts: a tip under the label while the pointer rests
 * on it or the keyboard is on it, and only when the label is cut. One tip serves every label of the container, so
 * a long list costs nothing more. The tip is drawn in a portal, over the panel that clips the label, and in the
 * window (a dialog) that holds the container; it is not read by a screen reader, which reads the label whole.
 */
export function OverflowTip({ within, target, label }: {
  within: RefObject<HTMLElement | null>;
  /** The elements that have a tip: the one the pointer rests on, or the one that has the keyboard. */
  target: string;
  /** The element of a target whose text can be cut. */
  label: string;
}) {
  const [tip, setTip] = useState<Tip | null>(null);
  const shownFor = useRef<HTMLElement | null>(null);
  useEffect(() => {
    const container = within.current;
    if (!container) return;
    let timer = 0, frame = 0;
    // A native tooltip of the target would say the same words a moment later: it is put aside while the tip is shown.
    let quieted: { element: HTMLElement; title: string } | null = null;
    const restore = () => {
      if (quieted && !quieted.element.hasAttribute("title")) quieted.element.setAttribute("title", quieted.title);
      quieted = null;
    };
    const hide = () => {
      clearTimeout(timer);
      cancelAnimationFrame(frame);
      restore();
      if (shownFor.current) { shownFor.current = null; setTip(null); }
    };
    const show = (element: HTMLElement) => {
      const text = element.isConnected ? overflowTipText(element, element.querySelector<HTMLElement>(label) ?? element) : null;
      if (!text) { hide(); return; }
      const place = (element.querySelector<HTMLElement>(label) ?? element).getBoundingClientRect(), row = element.getBoundingClientRect();
      const width = window.innerWidth, height = window.innerHeight;
      const maxWidth = Math.max(120, Math.min(420, width - 2 * margin));
      // Under the row, where its words start; above it when the row is at the foot of the window.
      const below = row.bottom + 4 + 60 < height;
      restore();
      const title = element.getAttribute("title");
      if (title !== null) { quieted = { element, title }; element.removeAttribute("title"); }
      shownFor.current = element;
      setTip({ text, left: Math.max(margin, Math.min(place.left - 9, width - maxWidth - margin)),
        top: below ? row.bottom + 4 : null, bottom: below ? null : height - row.top + 4, maxWidth });
    };
    const targetOf = (node: EventTarget | null) => node instanceof Element ? node.closest<HTMLElement>(target) : null;
    const over = (event: PointerEvent) => {
      const element = targetOf(event.target);
      if (element === shownFor.current && element) return;
      clearTimeout(timer);
      if (!element || !container.contains(element)) { if (shownFor.current && !shownFor.current.matches(":focus-visible")) hide(); return; }
      timer = window.setTimeout(() => show(element), shownFor.current ? 0 : hoverDelay);
    };
    const leave = (event: PointerEvent) => {
      if (event.relatedTarget instanceof Node && container.contains(event.relatedTarget)) return;
      clearTimeout(timer);
      if (shownFor.current && !shownFor.current.matches(":focus-visible")) hide();
    };
    // The keyboard: the tip of the target that takes it shows at once, as the focus ring does.
    const focus = (event: FocusEvent) => {
      const element = targetOf(event.target);
      if (element && element === event.target && element.matches(":focus-visible")) { clearTimeout(timer); show(element); }
      else if (shownFor.current) hide();
    };
    const blur = (event: FocusEvent) => { if (event.target === shownFor.current) hide(); };
    const key = (event: KeyboardEvent) => { if (event.key === "Escape" && shownFor.current) hide(); };
    // A scroll that moves the label away from its tip takes the tip away, as a change of the size of the window does;
    // a scroll elsewhere (a conversation that is being written) does not. The label that has the keyboard (which the
    // list scrolls to) keeps its tip, moved with it.
    const scroll = (event: Event) => {
      const element = shownFor.current;
      if (!element || !(event.target instanceof Node) || !event.target.contains(element)) return;
      if (!element.matches(":focus-visible")) { hide(); return; }
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => { if (shownFor.current === element) show(element); });
    };
    container.addEventListener("pointerover", over);
    container.addEventListener("pointerleave", leave);
    container.addEventListener("pointerdown", hide);
    container.addEventListener("focusin", focus);
    container.addEventListener("focusout", blur);
    container.addEventListener("keydown", key);
    window.addEventListener("scroll", scroll, true);
    window.addEventListener("resize", hide);
    return () => {
      container.removeEventListener("pointerover", over);
      container.removeEventListener("pointerleave", leave);
      container.removeEventListener("pointerdown", hide);
      container.removeEventListener("focusin", focus);
      container.removeEventListener("focusout", blur);
      container.removeEventListener("keydown", key);
      window.removeEventListener("scroll", scroll, true);
      window.removeEventListener("resize", hide);
      hide();
    };
  }, [within, target, label]);
  return tip && <Portal className="overflow-tip-portal">
    <div className="overflow-tip" aria-hidden="true" style={{ left: tip.left, top: tip.top ?? undefined, bottom: tip.bottom ?? undefined, maxWidth: tip.maxWidth }}>{tip.text}</div>
  </Portal>;
}
