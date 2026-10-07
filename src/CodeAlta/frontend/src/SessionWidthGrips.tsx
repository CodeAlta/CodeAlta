import { createContext, useContext, useEffect, useRef, type CSSProperties, type KeyboardEvent, type PointerEvent } from "react";
import { applySessionWidth, clampSessionWidth, defaultSessionWidth, minimumSessionWidth, sessionWidthAt, sessionWidthProperty, sessionWidthStep } from "./sessionWidth";
import { useShellLanguage } from "./shellLanguage";

/**
 * The width of the conversations of the window, and how it is changed. `width` is the user's setting; `widths`
 * holds the sessions that are shown with a width of their own, by the lower-case id of each.
 */
export type SessionWidthControl = Readonly<{
  width: number; widths: ReadonlyMap<string, number>;
  /** Changes the user's setting. The session that was resized, if any, follows the setting again. */
  setWidth(value: number, resizedSessionId?: string | null): void;
}>;

/** Given by the window. Without it (a fixture) a conversation takes its whole space and has no grips. */
export const SessionWidthContext = createContext<SessionWidthControl | null>(null);

/** The style of the space of a session that is shown with a width of its own; nothing for the others. */
export function useSessionWidthStyle(sessionId: string | null | undefined): CSSProperties | undefined {
  const own = useContext(SessionWidthContext)?.widths.get(sessionId?.toLowerCase() ?? "");
  return own === undefined ? undefined : { [sessionWidthProperty]: `${own}%` } as CSSProperties;
}

/**
 * The two grips of a prompt, on its left and right edges. Dragging one moves both edges: the conversation stays
 * centered, and the timeline above follows. The width is kept when the pointer is released. A double click, or
 * Home, gives the whole space back; the arrow keys change the width by steps.
 */
export function SessionWidthGrips({ sessionId = null }: { sessionId?: string | null }) {
  const { t } = useShellLanguage();
  const control = useContext(SessionWidthContext);
  const drag = useRef<{ id: number; space: HTMLElement; left: number; width: number; value: number } | null>(null);
  // A drag that loses the window (another application takes the pointer) leaves the width as it was.
  useEffect(() => {
    const cancel = () => { drag.current?.space.style.removeProperty(sessionWidthProperty); drag.current = null; };
    window.addEventListener("blur", cancel);
    return () => { window.removeEventListener("blur", cancel); cancel(); };
  }, []);
  if (!control) return null;
  const shown = control.widths.get(sessionId?.toLowerCase() ?? "") ?? control.width;
  const label = t("Width of the conversation");
  function keep(value: number) {
    // The stylesheet has the new width before the host answers: nothing moves back for a moment.
    applySessionWidth(document.documentElement, value);
    control!.setWidth(value, sessionId);
  }
  function down(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return;
    const space = event.currentTarget.closest<HTMLElement>(".session-workspace");
    const box = space?.getBoundingClientRect();
    if (!space || !box || !(box.width > 0)) return;
    event.preventDefault();
    // Without the capture (a pointer that is gone already) the drag still follows what reaches the grip.
    try { event.currentTarget.setPointerCapture(event.pointerId); } catch { /* The pointer cannot be captured. */ }
    drag.current = { id: event.pointerId, space, left: box.left, width: box.width, value: shown };
  }
  function move(event: PointerEvent<HTMLDivElement>) {
    const current = drag.current;
    if (!current || current.id !== event.pointerId) return;
    current.value = sessionWidthAt(event.clientX, current.left, current.width);
    // While the pointer moves only this session follows: the setting is written once, at the end.
    applySessionWidth(current.space, current.value);
  }
  function up(event: PointerEvent<HTMLDivElement>) {
    const current = drag.current;
    if (!current || current.id !== event.pointerId) return;
    drag.current = null;
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId);
    if (event.type !== "pointercancel") keep(current.value);
    current.space.style.removeProperty(sessionWidthProperty);
  }
  function key(event: KeyboardEvent<HTMLDivElement>, side: "left" | "right") {
    if (event.altKey || event.ctrlKey || event.metaKey) return;
    // An arrow that points away from the middle widens the conversation, on either grip.
    const outward = side === "left" ? "ArrowLeft" : "ArrowRight", inward = side === "left" ? "ArrowRight" : "ArrowLeft";
    const next = event.key === outward ? shown + sessionWidthStep : event.key === inward ? shown - sessionWidthStep
      : event.key === "Home" ? defaultSessionWidth : event.key === "End" ? minimumSessionWidth : null;
    if (next === null) return;
    event.preventDefault();
    keep(clampSessionWidth(next));
  }
  return <>{(["left", "right"] as const).map(side => <div key={side} className="session-width-grip" data-side={side} role="separator" aria-orientation="vertical"
    aria-label={label} aria-valuemin={minimumSessionWidth} aria-valuemax={defaultSessionWidth} aria-valuenow={shown} aria-valuetext={`${shown}%`} tabIndex={0}
    title={t("Drag to change the width of the conversation; double-click to reset")}
    onPointerDown={down} onPointerMove={move} onPointerUp={up} onPointerCancel={up} onDoubleClick={() => keep(defaultSessionWidth)}
    onKeyDown={event => key(event, side)} />)}</>;
}
