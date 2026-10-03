import { useLayoutEffect, useRef, useState } from "react";
import { Menu, MenuItem, Overlay2 } from "@blueprintjs/core";

export type SessionMenuEntry = { key: string; label: string; disabled?: boolean; onSelect: () => void };

// The portal stays inside the themed shell, not FlexLayout's unthemed popup container.
export function SessionTabMenu({ anchor, items, title, container, current, onClose }: {
  anchor: HTMLElement; items: SessionMenuEntry[]; title: string; container: HTMLElement;
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
      setPosition({ left: Math.max(4, Math.min(rect.right - (menu?.width ?? 220), window.innerWidth - (menu?.width ?? 220) - 4)),
        top: Math.max(4, Math.min(rect.bottom + 4, window.innerHeight - (menu?.height ?? 160) - 4)) });
    };
    place();
    window.addEventListener("resize", place);
    return () => window.removeEventListener("resize", place);
  }, [anchor, current, onClose]);
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
        {items.map(item => <MenuItem key={item.key} role="menuitem" text={item.label} disabled={item.disabled} tabIndex={-1}
          onClick={() => { if (!item.disabled && current()) { close(true); item.onSelect(); } }} />)}
      </Menu>
    </div>
  </Overlay2>;
}
