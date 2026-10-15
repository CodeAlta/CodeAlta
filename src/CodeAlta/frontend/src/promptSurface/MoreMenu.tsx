import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";

/** Opens the `⋯` menu around a control, for a control inside it that is asked to show itself (Remote Control). */
export const MoreMenuContext = createContext<(() => void) | null>(null);

/** Opens the `⋯` menu the calling control is in; nothing outside one. */
export function useOpenMoreMenu() { return useContext(MoreMenuContext); }

/**
 * The `⋯` menu of the composer: the controls used less often. They stay mounted while the menu is closed, so that
 * their shortcuts and the windows they open keep finding them; the shell opens the menu (`details.composer-actions-menu`)
 * before it clicks one. The menu is placed against the window, since the composer scrolls and would cut it. A press
 * outside or Escape closes it; a press in a popover of one of its controls does not. A control marked
 * `data-keeps-menu` leaves it open when clicked, the others close it.
 */
export function MoreMenu({ attention, label, children }: {
  /** What inside needs the user: shown on the button, a count or a dot. */
  attention?: Readonly<{ count?: number; tone: "info" | "warning" | "danger" }> | null;
  /** Says what needs the user, after the name of the button. */
  label?: string;
  children: ReactNode;
}) {
  const { t } = useShellLanguage();
  const details = useRef<HTMLDetailsElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const [place, setPlace] = useState<{ right: number; top?: number; bottom?: number; maxHeight: number } | null>(null);
  const show = useCallback(() => { if (details.current) details.current.open = true; }, []);
  const close = useCallback((focus = false) => {
    if (!details.current?.open) return;
    details.current.open = false;
    if (focus) details.current.querySelector<HTMLElement>(":scope > summary")?.focus();
  }, []);
  const measure = useCallback(() => {
    const summary = details.current?.querySelector(":scope > summary");
    if (!summary) return;
    const box = summary.getBoundingClientRect();
    const right = Math.max(8, window.innerWidth - box.right);
    // Above the button, where the composer leaves room; below it where there is more room there.
    const above = box.top - 14, below = window.innerHeight - box.bottom - 14;
    const needed = menu.current?.scrollHeight ?? 0;
    setPlace(above >= needed || above >= below ? { right, bottom: window.innerHeight - box.top + 6, maxHeight: above }
      : { right, top: box.bottom + 6, maxHeight: below });
  }, []);
  useLayoutEffect(() => { if (open) measure(); }, [open, measure]);
  useEffect(() => {
    if (!open) return;
    const press = (event: PointerEvent) => {
      const target = event.target instanceof Element ? event.target : null;
      if (!target || details.current?.contains(target) || target.closest(".bp6-portal")) return;
      close();
    };
    const key = (event: KeyboardEvent) => {
      if (event.key !== "Escape" || event.defaultPrevented || !details.current?.contains(document.activeElement)) return;
      event.preventDefault(); event.stopPropagation(); close(true);
    };
    const moved = () => measure();
    document.addEventListener("pointerdown", press, true);
    document.addEventListener("keydown", key, true);
    window.addEventListener("resize", moved);
    return () => { document.removeEventListener("pointerdown", press, true); document.removeEventListener("keydown", key, true); window.removeEventListener("resize", moved); };
  }, [open, close, measure]);
  const name = t("More actions");
  return <MoreMenuContext.Provider value={show}>
    <details ref={details} className="composer-actions-menu prompt-surface-more" onToggle={event => setOpen(event.currentTarget.open)}>
      <summary className="bp6-button bp6-minimal prompt-surface-more-button" aria-label={label ? `${name} · ${label}` : name} title={label ? `${name}\n${label}` : name}>
        <AppIcon name="ellipsis" size={16} />
        {attention && <span className="prompt-surface-more-mark" data-tone={attention.tone} aria-hidden="true">{attention.count ? attention.count : ""}</span>}
      </summary>
      <div ref={menu} className="prompt-surface-more-menu" role="group" aria-label={name}
        style={place ? { right: place.right, top: place.top, bottom: place.bottom, maxHeight: place.maxHeight } : undefined}
        onClick={event => {
          const button = event.target instanceof Element ? event.target.closest("button") : null;
          if (button && !button.closest("[data-keeps-menu]")) close();
        }}>{children}</div>
    </details>
  </MoreMenuContext.Provider>;
}
