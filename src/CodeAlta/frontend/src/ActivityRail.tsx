import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { Button, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { AppIcon, type IconName } from "./AppIcon";

/** A button of the rail that folds into its menu when the title bar is too narrow for it. */
export type ActivityRailItem = Readonly<{
  key: string; icon: IconName; label: string; title: string; className: string;
  active?: boolean; disabled?: boolean;
  /** A dot over the icon, and beside the line of the menu. */
  dot?: boolean;
  onClick: () => void;
}>;

/**
 * How many of `count` buttons of `slot` pixels fit in `available` pixels beside what always shows (`fixed`); a menu
 * button of a slot is taken from the room as soon as one of them does not fit.
 */
export function fittingRailItems(available: number, fixed: number, slot: number, count: number): number {
  if (slot <= 0 || fixed + count * slot <= available) return count;
  return Math.max(0, Math.min(count, Math.floor((available - fixed - slot) / slot)));
}

/**
 * The navigation of the title bar: `first` (the Explorer) and `last` (the buttons of plugins and Settings) always show;
 * the items between them that do not fit the room the title bar leaves fold, from the last one, into a "…" menu.
 */
export function ActivityRail({ label, moreLabel, first, items, last }: {
  label: string; moreLabel: string; first: ReactNode; items: readonly ActivityRailItem[]; last: ReactNode;
}) {
  const nav = useRef<HTMLElement>(null);
  const [shown, setShown] = useState(items.length);
  const count = items.length;
  useLayoutEffect(() => {
    const element = nav.current;
    if (!element) return;
    // The menu button is in the target of its popover.
    const always = () => (Array.from(element.children) as HTMLElement[]).filter(child => !child.matches("[data-rail-item], [data-rail-more], :has([data-rail-more])"));
    const measure = () => {
      const gap = parseFloat(getComputedStyle(element).columnGap) || 0;
      const fixed = always().reduce((total, child) => total + child.offsetWidth + gap, -gap);
      // Every button of the rail has the width of the first one, the Explorer.
      const slot = ((element.firstElementChild as HTMLElement | null)?.offsetWidth ?? 0) + gap;
      const fit = fittingRailItems(element.clientWidth, fixed, slot, count);
      setShown(current => current === fit ? current : fit);
    };
    const resized = new ResizeObserver(measure);
    const observe = () => { resized.disconnect(); resized.observe(element); for (const child of always()) resized.observe(child); };
    // The buttons of plugins come and go after the rail is drawn.
    const changed = new MutationObserver(() => { observe(); measure(); });
    changed.observe(element, { childList: true });
    observe();
    measure();
    return () => { resized.disconnect(); changed.disconnect(); };
  }, [count]);
  const inline = items.slice(0, shown), folded = items.slice(shown);
  return <nav ref={nav} className="activity-rail" aria-label={label}>
    {first}
    {inline.map(item => <Button key={item.key} data-rail-item variant="minimal" size="small" icon={<AppIcon name={item.icon} size={16} />} className={item.className}
      disabled={item.disabled} active={item.active} aria-label={item.label} title={item.title} onClick={item.onClick}>
      {item.dot && <span className="activity-rail-dot" aria-hidden="true" />}</Button>)}
    {folded.length > 0 && <PopoverNext placement="bottom-end" content={<Menu aria-label={moreLabel}>
      {folded.map(item => <MenuItem key={item.key} className={item.className} icon={<AppIcon name={item.icon} size={15} />} text={item.label} title={item.title}
        disabled={item.disabled} active={item.active} labelElement={item.dot ? <span className="activity-rail-dot" aria-hidden="true" /> : undefined}
        onClick={item.onClick} />)}
    </Menu>}>
      {/* What a folded button shows (the tab in front, a dot) shows on the menu button. */}
      <Button data-rail-more variant="minimal" size="small" className="activity-more" icon={<AppIcon name="ellipsis" size={16} />}
        active={folded.some(item => item.active)} aria-haspopup="menu" aria-label={moreLabel} title={moreLabel}>
        {folded.some(item => item.dot) && <span className="activity-rail-dot" aria-hidden="true" />}</Button>
    </PopoverNext>}
    {last}
  </nav>;
}
