import { useImperativeHandle, useLayoutEffect, useRef, useState, type ReactNode, type Ref } from "react";
import { useShellLanguage } from "../shellLanguage";
import { openTabSwitcher, stepTabSwitcher, type TabSwitcherState } from "./recentTabs";

/** A tab of the switcher: what the strip shows of it. A preview has its title in italics, as in the strip. */
export type TabSwitcherItem = Readonly<{ id: string; label: string; icon: ReactNode; detail?: string; preview?: boolean }>;

/** Steps through the current owner's tabs without re-rendering that owner. */
export type TabSwitcherHandle = Readonly<{ step: (delta: 1 | -1) => void }>;

/** Whether the key is Ctrl+Tab (optionally with Shift), not an operating-system shortcut. */
export function isTabSwitcherKey(event: Pick<KeyboardEvent, "key" | "ctrlKey" | "altKey" | "metaKey">): boolean {
  return event.key === "Tab" && event.ctrlKey && !event.altKey && !event.metaKey;
}

/**
 * Highlights the tabs in MRU order while Ctrl is held. `items` starts with the current tab and is read once per
 * gesture. The current tab or owner changing cancels the gesture, as does leaving it by keyboard or pointer.
 */
export function TabSwitcher({ current, items, show, ref }: Readonly<{
  current: string | null; items: () => readonly TabSwitcherItem[]; show: (id: string) => void; ref: Ref<TabSwitcherHandle>;
}>) {
  const { t } = useShellLanguage();
  const [state, setState] = useState<TabSwitcherState<TabSwitcherItem> | null>(null);
  // Subscribe before rendering: a quick press and release need not have a frame between them.
  const live = useRef<typeof state>(null);
  const stop = useRef<(() => void) | null>(null);
  const shows = useRef(show);
  shows.current = show;
  const selected = useRef<HTMLDivElement>(null);

  function close(apply: boolean) {
    const closing = live.current;
    if (!closing) return;
    live.current = null;
    stop.current?.(); stop.current = null;
    setState(null);
    if (apply && closing.index !== 0) shows.current(closing.order[closing.index].id);
  }

  useLayoutEffect(() => { close(false); }, [current]);
  useLayoutEffect(() => () => stop.current?.(), []);
  useLayoutEffect(() => { selected.current?.scrollIntoView({ block: "nearest" }); }, [state]);
  useImperativeHandle(ref, () => ({
    step(delta) {
      if (current === null) return;
      const next = live.current ? stepTabSwitcher(live.current, delta) : openTabSwitcher(items(), delta);
      if (!next) return;
      live.current = next; setState(next);
      if (stop.current) return;
      const released = (event: KeyboardEvent) => { if (event.key === "Control" && !event.ctrlKey) close(true); };
      const pressed = (event: KeyboardEvent) => {
        if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); }
        if (event.key !== "Control" && event.key !== "Shift" && !isTabSwitcherKey(event)) close(false);
      };
      const cancel = () => close(false);
      window.addEventListener("keyup", released, true);
      window.addEventListener("keydown", pressed, true);
      window.addEventListener("pointerdown", cancel, true);
      window.addEventListener("focusin", cancel, true);
      window.addEventListener("blur", cancel);
      stop.current = () => {
        window.removeEventListener("keyup", released, true);
        window.removeEventListener("keydown", pressed, true);
        window.removeEventListener("pointerdown", cancel, true);
        window.removeEventListener("focusin", cancel, true);
        window.removeEventListener("blur", cancel);
      };
    },
  }));

  const highlighted = state?.order[state.index];
  return <>
    <span className="sr-only" role="status">{highlighted && [highlighted.label, highlighted.detail].filter(Boolean).join(" · ")}</span>
    {state && <div className="tab-switcher" role="listbox" aria-label={t("Switch tabs")}>
      {state.order.map((item, at) => <div key={item.id} ref={at === state.index ? selected : undefined} role="option" aria-selected={at === state.index} className="tab-switcher-row" data-preview={item.preview || undefined} title={item.label}>
        {item.icon}<span className="tab-switcher-label">{item.label}</span>{item.detail && <span className="tab-switcher-detail">{item.detail}</span>}
      </div>)}
    </div>}
  </>;
}
