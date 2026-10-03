import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent, type RefObject } from "react";
import { Actions, Model, TabNode, TabSetNode, type Action } from "flexlayout-react";
import { sessionTabDrop, type SessionTabDrop } from "./sessionTabDrag";

// Native HTML5 drags enter WebView2/OLE host drop handling. Pointer capture keeps
// session docking entirely in the WebApp while FlexLayout still owns the model.
export function useSessionTabDrag(root: RefObject<HTMLDivElement | null>, model: Model, capture: () => () => boolean,
  allowed: (action: Action, current: () => boolean) => boolean, apply: (action: Action, current: () => boolean) => void) {
  const [preview, setPreview] = useState<SessionTabDrop | null>(null);
  const gesture = useRef<{ pointerId: number; node: TabNode; header: HTMLElement; x: number; y: number;
    dragging: boolean; current: () => boolean } | null>(null);
  const suppressClick = useRef(false);
  const cancelledPointer = useRef<number | null>(null);

  function cancel(updatePreview = true) {
    const pending = gesture.current;
    gesture.current = null;
    if (pending) cancelledPointer.current = pending.pointerId;
    if (pending && root.current?.hasPointerCapture(pending.pointerId)) root.current.releasePointerCapture(pending.pointerId);
    if (updatePreview) setPreview(null);
  }
  function current() {
    const pending = gesture.current;
    return !!pending && pending.current() && model.getNodeById(pending.node.getId()) === pending.node
      && allowed(Actions.selectTab(pending.node.getId()), pending.current)
      && !root.current?.ownerDocument.querySelector('dialog[open], [role="dialog"][aria-modal="true"]');
  }
  useLayoutEffect(() => { if (gesture.current && !current()) cancel(); });
  useEffect(() => {
    const window = root.current?.ownerDocument.defaultView;
    const blur = () => cancel();
    window?.addEventListener("blur", blur);
    return () => { window?.removeEventListener("blur", blur); cancel(false); };
  }, [model, root]);

  function dropAt(clientX: number, clientY: number): SessionTabDrop | null {
    const pending = gesture.current, container = root.current;
    if (!pending || !container || !current()) return null;
    const bounds = container.getBoundingClientRect(), point = { x: clientX - bounds.x, y: clientY - bounds.y };
    const hit = container.ownerDocument.elementFromPoint(clientX, clientY);
    // Do not dock through a dialog, popup or another workspace surface.
    if (!hit || !container.contains(hit) || hit.closest('[role="menu"], .session-tab-more')) return null;
    let drop: SessionTabDrop | null = null;
    model.visitNodes(node => {
      if (drop || !(node instanceof TabSetNode)) return;
      const rect = node.getRect();
      if (point.x < rect.x || point.x >= rect.x + rect.width || point.y < rect.y || point.y >= rect.y + rect.height) return;
      const strip = hit.closest(".flexlayout__tabset_tabbar_outer");
      const headers = strip ? Array.from(strip.querySelectorAll<HTMLElement>('[role="tab"]')).map(header => {
        const id = header.querySelector<HTMLElement>("[data-session-node]")?.dataset.sessionNode;
        const tab = id ? model.getNodeById(id) : undefined;
        return { tab, rect: header.getBoundingClientRect() };
      }).filter(value => value.tab instanceof TabNode && value.tab.getParent() === node && value.rect.width > 0) : [];
      let insertion: { index: number; rect: { x: number; y: number; width: number; height: number } } | undefined;
      if (headers.length) {
        const before = headers.find(value => clientX < value.rect.x + value.rect.width / 2);
        const header = before ?? headers[headers.length - 1];
        insertion = { index: before ? node.getTabNodes().indexOf(before.tab as TabNode) : -1,
          rect: { x: (before ? header.rect.x : header.rect.right) - bounds.x - 2, y: header.rect.y - bounds.y,
            width: 3, height: header.rect.height } };
      }
      const candidate = sessionTabDrop(pending.node, node, rect, point, insertion);
      if (candidate && allowed(candidate.action, pending.current)) drop = candidate;
    });
    return drop;
  }
  function down(event: PointerEvent<HTMLDivElement>) {
    if (event.isPrimary && !gesture.current) { suppressClick.current = false; cancelledPointer.current = null; }
    if (gesture.current || !event.isPrimary || event.button !== 0 || event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) return;
    const target = event.target as HTMLElement;
    if (target.closest('.flexlayout__tab_button_trailing, button, input, [role="button"]')) return;
    const header = target.closest<HTMLElement>('.flexlayout__tab_button[role="tab"]');
    const id = header?.querySelector<HTMLElement>("[data-session-node]")?.dataset.sessionNode;
    const node = id ? model.getNodeById(id) : undefined, captured = capture();
    if (!header || !(node instanceof TabNode) || !node.isEnableDrag() || !captured()
      || event.currentTarget.ownerDocument.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
      || !allowed(Actions.selectTab(node.getId()), captured)) return;
    // Do not select on down: App selection would invalidate the captured gesture
    // and may remove a temporary tab before its drop geometry has been used.
    suppressClick.current = false;
    gesture.current = { pointerId: event.pointerId, node, header, x: event.clientX, y: event.clientY, dragging: false, current: captured };
    event.preventDefault(); event.stopPropagation();
    header.focus({ preventScroll: true });
    event.currentTarget.setPointerCapture(event.pointerId);
  }
  function move(event: PointerEvent<HTMLDivElement>) {
    const pending = gesture.current;
    if (!pending || pending.pointerId !== event.pointerId) return;
    if (!current() || !(event.buttons & 1)) { cancel(); return; }
    event.preventDefault(); event.stopPropagation();
    if (!pending.dragging && Math.hypot(event.clientX - pending.x, event.clientY - pending.y) < 5) return;
    pending.dragging = true;
    setPreview(dropAt(event.clientX, event.clientY));
  }
  function up(event: PointerEvent<HTMLDivElement>) {
    const pending = gesture.current;
    if (!pending) {
      if (cancelledPointer.current === event.pointerId) {
        cancelledPointer.current = null; suppressClick.current = true;
        event.preventDefault(); event.stopPropagation();
      }
      return;
    }
    if (pending.pointerId !== event.pointerId) return;
    const valid = current(), drop = valid && pending.dragging ? dropAt(event.clientX, event.clientY) : null;
    suppressClick.current = true;
    cancel(); cancelledPointer.current = null; event.preventDefault(); event.stopPropagation();
    if (drop) apply(drop.action, pending.current);
    else if (valid && !pending.dragging) apply(Actions.selectTab(pending.node.getId()), pending.current);
    // A moved tab gets a new button, but its factory/editor remains mounted. Do
    // not steal focus from an editor, a popup, or a dialog opened meanwhile.
    requestAnimationFrame(() => {
      const container = root.current, document = container?.ownerDocument;
      if (!container || !document || model.getNodeById(pending.node.getId()) !== pending.node
        || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')
        || document.activeElement !== document.body && document.activeElement !== pending.header) return;
      Array.from(container.querySelectorAll<HTMLElement>("[data-session-node]")).find(node => node.dataset.sessionNode === pending.node.getId())
        ?.closest<HTMLElement>('[role="tab"]')?.focus({ preventScroll: true });
    });
  }
  function end(event: PointerEvent<HTMLDivElement>) {
    if (gesture.current?.pointerId === event.pointerId) cancel();
  }
  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (!gesture.current || event.key !== "Escape" || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) return false;
    cancel(); event.preventDefault(); event.stopPropagation(); return true;
  }
  function click(detail: number) {
    const suppressed = detail > 0 && suppressClick.current;
    suppressClick.current = false;
    return suppressed;
  }
  return { preview, down, move, up, end, keyDown, click };
}
