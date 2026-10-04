import { useEffect, useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import { Button, PopoverNext, Portal, type PopoverNextPlacement } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";

/** One stop of a tour: the element it points at, and what it says about it. */
export type GuidedTourStep = Readonly<{
  id: string; title: string; body: ReactNode;
  /** The element this stop is about; null while it is not on the page (the card then stands in the middle). */
  target: () => Element | null;
  placement?: PopoverNextPlacement;
  /** Brings the page to the state the stop describes (a selection, say), each time the stop shows. */
  enter?: () => void;
}>;

type Box = Readonly<{ top: number; left: number; width: number; height: number }>;
const margin = 6;

/** The part of an element that can be seen: its rectangle within every ancestor that clips or scrolls it. */
export function visibleBox(element: Element): Box | null {
  let { top, left, bottom, right } = element.getBoundingClientRect();
  for (let parent = element.parentElement; parent; parent = parent.parentElement) {
    const style = getComputedStyle(parent);
    if (style.overflowX === "visible" && style.overflowY === "visible") continue;
    const clip = parent.getBoundingClientRect();
    top = Math.max(top, clip.top); left = Math.max(left, clip.left); bottom = Math.min(bottom, clip.bottom); right = Math.min(right, clip.right);
  }
  top = Math.max(top, 0); left = Math.max(left, 0); bottom = Math.min(bottom, window.innerHeight); right = Math.min(right, window.innerWidth);
  return right - left > 0 && bottom - top > 0 ? { top: Math.round(top), left: Math.round(left), width: Math.round(right - left), height: Math.round(bottom - top) } : null;
}

/**
 * A step-by-step tour of what is on the page: the element of the current stop is lit while the rest dims,
 * and a card beside it explains it, with Back, Next and Skip. The page stays usable underneath, so a stop
 * can ask for a real click. It ends with the last stop's Done, with Skip, or with Escape.
 */
export function GuidedTour({ steps, label, onClose }: {
  steps: readonly GuidedTourStep[]; label: string;
  /** `finished` is false when the tour was skipped. */
  onClose: (finished: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [index, setIndex] = useState(0);
  const [box, setBox] = useState<Box | null>(null);
  const next = useRef<HTMLButtonElement>(null);
  const step = steps[Math.min(index, steps.length - 1)];
  const last = index >= steps.length - 1;
  // The owner describes its stops anew on every render; a stop is entered once, when the tour reaches it.
  const current = useRef(step); current.current = step;

  useLayoutEffect(() => { current.current?.enter?.(); }, [index]);
  // The lit area follows its element through layout changes, scrolling and the window being moved.
  useEffect(() => {
    let frame = 0, shown = "";
    const follow = () => {
      const element = current.current?.target();
      const seen = element ? visibleBox(element) : null;
      const value = seen ? { top: seen.top - margin, left: seen.left - margin, width: seen.width + 2 * margin, height: seen.height + 2 * margin } : null;
      const key = JSON.stringify(value);
      if (key !== shown) { shown = key; setBox(value); }
      frame = requestAnimationFrame(follow);
    };
    follow();
    return () => cancelAnimationFrame(frame);
  }, []);
  // Enter continues the tour: the main button has the focus at each stop, and from the moment the card is there.
  const focusNext = () => next.current?.focus({ preventScroll: true });
  useEffect(focusNext, [index]);
  if (!step) return null;

  const style: CSSProperties = box ? { top: box.top, left: box.left, width: box.width, height: box.height }
    : { top: "50%", left: "50%", width: 0, height: 0 };
  const card = <div className="guided-tour-card" role="dialog" aria-label={label} aria-describedby="guided-tour-body"
    onKeyDown={event => {
      // The tour's own keys do not reach the window it is shown in (Escape would close it).
      if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); onClose(false); }
      else if (event.key === "ArrowRight" && !last) { event.stopPropagation(); setIndex(index + 1); }
      else if (event.key === "ArrowLeft" && index > 0) { event.stopPropagation(); setIndex(index - 1); }
    }}>
    <header><span className="guided-tour-count">{t("Step {current} of {total}", { current: index + 1, total: steps.length })}</span>
      <Button variant="minimal" size="small" icon={<AppIcon name="close" size={14} />} aria-label={t("Skip")} title={t("Skip")} onClick={() => onClose(false)} /></header>
    <h3>{step.title}</h3>
    <p id="guided-tour-body">{step.body}</p>
    <footer>
      <span className="guided-tour-dots" aria-hidden="true">{steps.map((value, position) => <i key={value.id} data-current={position === index ? "" : undefined} />)}</span>
      {!last && <Button variant="minimal" onClick={() => onClose(false)}>{t("Skip")}</Button>}
      {index > 0 && <Button onClick={() => setIndex(index - 1)}>{t("Back")}</Button>}
      <Button ref={next} intent="primary" endIcon={last ? undefined : <AppIcon name="chevronRight" size={15} />}
        onClick={() => last ? onClose(true) : setIndex(index + 1)}>{t(last ? "Done" : "Next")}</Button>
    </footer>
  </div>;
  return <Portal className="guided-tour">
    <PopoverNext isOpen content={card} placement={box ? step.placement ?? "right" : "bottom"} arrow={!!box} usePortal={false} enforceFocus={false} autoFocus={false}
      canEscapeKeyClose={false} autoUpdateOptions={{ animationFrame: true }} popoverClassName="guided-tour-popover" onOpened={focusNext}
      renderTarget={({ isOpen: _open, ref, ...target }) => <div {...target} ref={ref} className="guided-tour-spotlight" data-placed={box ? "" : undefined} style={style} />} />
  </Portal>;
}
