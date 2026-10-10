import { useLayoutEffect, useRef, useState, type ReactElement } from "react";
import { Menu, MenuDivider, MenuItem, Overlay2 } from "@blueprintjs/core";
import { AppIcon, type IconName } from "./AppIcon";

export type SessionMenuEntry = { key: string; label: string; disabled?: boolean; icon?: IconName; danger?: boolean; onSelect: () => void;
    /** An icon that is not one of the application's, and what is shown at the end of the line (a badge); the line of a button of a plugin has both. */
    iconNode?: ReactElement; endNode?: ReactElement }
  /** A separator, optionally titled, between groups of entries. */
  | { key: string; divider: true; label?: string };

// The portal stays inside the themed shell, not FlexLayout's unthemed popup container.
export function SessionTabMenu({ anchor, at, items, title, container, current, onClose }: {
  anchor: HTMLElement; items: SessionMenuEntry[]; title: string; container: HTMLElement;
  /** A point of the window the menu opens at (a context menu), instead of under its anchor. */
  at?: Readonly<{ x: number; y: number }>;
  current: () => boolean; onClose: () => void;
}) {
  // Blueprint's childRef declaration predates React 19's nullable RefObject type.
  // DOM access below still checks the actual mount lifetime.
  const popup = useRef<HTMLDivElement>(null!);
  const [position, setPosition] = useState({ left: 0, top: 0 });
  useLayoutEffect(() => {
    const place = () => {
      if (!anchor.isConnected || !current()) { onClose(); return; }
      const rect = anchor.getBoundingClientRect(), menu = popup.current?.getBoundingClientRect();
      setPosition({ left: Math.max(4, Math.min(at ? at.x : rect.right - (menu?.width ?? 220), window.innerWidth - (menu?.width ?? 220) - 4)),
        top: Math.max(4, Math.min(at ? at.y : rect.bottom + 4, window.innerHeight - (menu?.height ?? 160) - 4)) });
    };
    place();
    window.addEventListener("resize", place);
    return () => window.removeEventListener("resize", place);
  }, [anchor, at?.x, at?.y, current, onClose]);
  function close(restore: boolean) {
    if (restore && anchor.isConnected && current() && popup.current?.contains(document.activeElement)) anchor.focus({ preventScroll: true });
    onClose();
  }
  return <Overlay2 isOpen hasBackdrop={false} enforceFocus={false} autoFocus={false} shouldReturnFocusOnClose={false}
    transitionDuration={0} portalContainer={container} childRef={popup}
    onOpening={() => popup.current?.querySelector<HTMLElement>('[role="menuitem"]:not([aria-disabled="true"])')?.focus()}
    onClose={event => close("key" in event && event.key === "Escape")}>
    <div ref={popup} className="session-tab-popup" style={position} onKeyDown={event => {
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat) return;
      if (event.key === "Tab") { close(true); return; }
      const entries = Array.from(popup.current?.querySelectorAll<HTMLElement>('[role="menuitem"]:not([aria-disabled="true"])') ?? []);
      const index = entries.indexOf(document.activeElement as HTMLElement);
      const next = event.key === "Home" ? 0 : event.key === "End" ? entries.length - 1
        : event.key === "ArrowDown" ? (index + 1) % entries.length : event.key === "ArrowUp" ? (index + entries.length - 1) % entries.length : null;
      if (next !== null) { event.preventDefault(); event.stopPropagation(); entries[next]?.focus(); }
    }}>
      <Menu role="menu" aria-label={title}>
        {items.map(item => "divider" in item ? <MenuDivider key={item.key} title={item.label} /> : <MenuItem key={item.key} role="menuitem" text={item.label} disabled={item.disabled} tabIndex={-1}
          icon={item.iconNode ?? (item.icon ? <AppIcon name={item.icon} size={15} /> : undefined)} labelElement={item.endNode} intent={item.danger ? "danger" : undefined}
          onClick={() => { if (!item.disabled && current()) { close(true); item.onSelect(); } }} />)}
      </Menu>
    </div>
  </Overlay2>;
}
