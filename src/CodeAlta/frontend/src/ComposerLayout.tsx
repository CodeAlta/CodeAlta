import { useEffect, useLayoutEffect, useRef, useState, type PointerEvent } from "react";
import { composerAvailableHeight, composerBounds, resizeComposerHeight } from "./composerHeight";
import { useShellLanguage } from "./shellLanguage";

export function useComposerLayout(preferredHeight: number | undefined, onHeight: (height: number | undefined) => void) {
  // A FlexLayout factory can mount these nodes after its owning component's effects.
  // A callback ref makes the observer follow the actual portal DOM lifetime.
  const [workspace, workspaceRef] = useState<HTMLDivElement | null>(null);
  const barRef = useRef<HTMLDivElement>(null);
  const regionRef = useRef<HTMLDivElement>(null);
  const [layout, setLayout] = useState({ available: 0, rendered: 0 });
  useLayoutEffect(() => {
    const region = regionRef.current;
    if (!workspace || !region) return;
    const measure = () => {
      const style = getComputedStyle(workspace);
      let chromeHeight = 0;
      for (const child of Array.from(workspace.children)) {
        if (!(child instanceof HTMLElement) || child.classList.contains("timeline-scroll") || child.classList.contains("session-timeline-area") || child === region) continue;
        const childStyle = getComputedStyle(child);
        if (childStyle.display === "none" || childStyle.position === "fixed" || childStyle.position === "absolute") continue;
        chromeHeight += child.offsetHeight + parseFloat(childStyle.marginTop) + parseFloat(childStyle.marginBottom);
      }
      const available = composerAvailableHeight(workspace.clientHeight,
        parseFloat(style.paddingTop) + parseFloat(style.paddingBottom), chromeHeight);
      const rendered = Math.round(region.getBoundingClientRect().height);
      setLayout(old => old.available === available && old.rendered === rendered ? old : { available, rendered });
    };
    const sizes = new ResizeObserver(measure);
    sizes.observe(workspace);
    const changed = () => {
      sizes.disconnect(); sizes.observe(workspace);
      for (const child of Array.from(workspace.children)) sizes.observe(child);
      measure();
    };
    const children = new MutationObserver(changed);
    children.observe(workspace, { childList: true });
    changed();
    return () => { sizes.disconnect(); children.disconnect(); };
  }, [workspace]);
  const bounds = composerBounds(layout.available);
  const height = preferredHeight === undefined ? undefined : resizeComposerHeight(preferredHeight, 0, bounds);
  const pendingHeight = useRef<number | null>(null);
  useLayoutEffect(() => { pendingHeight.current = null; }, [preferredHeight]);
  const resize = (delta: number) => {
    // Hidden/unplaced panes have no viewport. Never save a zero-height preference.
    if (layout.available <= 0) return;
    const base = pendingHeight.current ?? height ?? regionRef.current?.getBoundingClientRect().height ?? layout.rendered;
    const next = resizeComposerHeight(base, delta, bounds);
    pendingHeight.current = next;
    onHeight(next);
  };
  return { workspaceRef, barRef, regionRef, height,
    splitter: { value: layout.rendered, min: bounds.min, max: bounds.max, automatic: preferredHeight === undefined,
      onResize: resize, onReset: () => onHeight(undefined) } };
}

export function ComposerSplitter({ value, min, max, automatic, onResize, onReset }: {
  value: number; min: number; max: number; automatic: boolean; onResize: (delta: number) => void; onReset: () => void;
}) {
  const { t } = useShellLanguage();
  const handle = useRef<HTMLDivElement>(null);
  const pointer = useRef<{ id: number; y: number } | null>(null);
  useEffect(() => {
    const blur = () => {
      const id = pointer.current?.id;
      pointer.current = null;
      if (id !== undefined && handle.current?.hasPointerCapture(id)) handle.current.releasePointerCapture(id);
    };
    window.addEventListener("blur", blur);
    return () => { window.removeEventListener("blur", blur); blur(); };
  }, []);
  function end(event: PointerEvent<HTMLDivElement>) {
    if (pointer.current?.id !== event.pointerId) return;
    pointer.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
  }
  return <div ref={handle} className="composer-splitter" role="separator" aria-label={t("Resize timeline and composer")} aria-orientation="horizontal"
    aria-valuemin={Math.min(min, value)} aria-valuemax={Math.max(max, value)} aria-valuenow={value}
    aria-valuetext={t(automatic ? "Automatic, {height} pixels" : "{height} pixels", { height: value })}
    title={t("Arrow Up enlarges composer; Arrow Down shrinks; Home resets to automatic")} tabIndex={0}
    onKeyDown={event => {
      if (event.key !== "ArrowUp" && event.key !== "ArrowDown" && event.key !== "Home") return;
      event.stopPropagation();
      if (event.defaultPrevented || event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229
        || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
      event.preventDefault();
      if (event.key === "Home") onReset(); else onResize(event.key === "ArrowUp" ? 16 : -16);
    }}
    onDoubleClick={onReset}
    onPointerDown={event => {
      if (!event.isPrimary || event.button !== 0 || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
      pointer.current = { id: event.pointerId, y: event.clientY };
      event.currentTarget.focus(); event.currentTarget.setPointerCapture(event.pointerId); event.preventDefault();
    }}
    onPointerMove={event => {
      if (pointer.current?.id !== event.pointerId || !event.currentTarget.hasPointerCapture(event.pointerId)) return;
      const delta = pointer.current.y - event.clientY;
      pointer.current.y = event.clientY;
      onResize(delta);
    }} onPointerUp={end} onPointerCancel={end}
    onLostPointerCapture={() => { pointer.current = null; }}><span /></div>;
}
