import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { useShellLanguage } from "./shellLanguage";
import { openTabSwitcher, stepTabSwitcher, type TabSwitcherState } from "./recentTabs";

/** A tab of the switcher: what the strip shows of it. A preview has its title in italics, as in the strip. */
export type TabSwitcherItem = Readonly<{ id: string; label: string; icon: ReactNode; detail?: string; preview?: boolean }>;

/**
 * The state of a tab switcher: opened by `step` (Ctrl+Tab), moved by it again while Ctrl is held, and closed when Ctrl is
 * released, which shows the tab it is on, or by Escape or by leaving the window, which show none.
 */
export function useTabSwitcher(show: (id: string) => void) {
  const [state, setState] = useState<TabSwitcherState | null>(null);
  // The listeners are added when the switcher opens, not after it renders: a quick Ctrl+Tab is released before then.
  const live = useRef<TabSwitcherState | null>(null);
  const stop = useRef<(() => void) | null>(null);
  const shows = useRef(show);
  shows.current = show;
  function close(apply: boolean) {
    const closing = live.current;
    live.current = null; setState(null);
    stop.current?.(); stop.current = null;
    if (apply && closing) shows.current(closing.order[closing.index]);
  }
  function step(order: readonly string[], delta: 1 | -1) {
    const next = live.current ? stepTabSwitcher(live.current, delta) : openTabSwitcher(order, delta);
    if (!next) return;
    live.current = next; setState(next);
    if (stop.current) return;
    const released = (event: KeyboardEvent) => { if (event.key === "Control") close(true); };
    const escaped = (event: KeyboardEvent) => { if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); close(false); } };
    const left = () => close(false);
    window.addEventListener("keyup", released, true); window.addEventListener("keydown", escaped, true); window.addEventListener("blur", left);
    stop.current = () => { window.removeEventListener("keyup", released, true); window.removeEventListener("keydown", escaped, true); window.removeEventListener("blur", left); };
  }
  useEffect(() => () => stop.current?.(), []);
  return { state, step, open: state !== null };
}

/** The tabs in the order of the switcher, the one it is on highlighted: shown in front of the window while Ctrl is held. */
export function TabSwitcher({ state, items }: Readonly<{ state: TabSwitcherState; items: ReadonlyMap<string, TabSwitcherItem> }>) {
  const { t } = useShellLanguage();
  const selected = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => { selected.current?.scrollIntoView({ block: "nearest" }); }, [state.index]);
  return <div className="tab-switcher" role="listbox" aria-label={t("Switch tabs")}>
    {state.order.map((id, at) => {
      const item = items.get(id);
      return item && <div key={id} ref={at === state.index ? selected : undefined} role="option" aria-selected={at === state.index} className="tab-switcher-row" data-preview={item.preview || undefined} title={item.label}>
        {item.icon}<span className="tab-switcher-label">{item.label}</span>{item.detail && <span className="tab-switcher-detail">{item.detail}</span>}
      </div>;
    })}
  </div>;
}
