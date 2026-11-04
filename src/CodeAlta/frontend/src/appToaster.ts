import { OverlayToaster, type Toaster, type ToastProps } from "@blueprintjs/core";
import { createRoot } from "react-dom/client";

/** What is known of the toast shown with a key: the message that came in while it was not yet in the toaster, and the timer that waits for it. */
interface KeyedToast {
  pending?: ToastProps;
  timer?: ReturnType<typeof setTimeout>;
}

interface AppToaster {
  readonly toaster: Promise<Toaster>;
  readonly keyed: Map<string, KeyedToast>;
  /** The popover the toaster is rendered in (see `raise`). */
  readonly layer: HTMLElement;
}

let app: AppToaster | undefined;

const settleDelay = 16;
const settleAttempts = 60;

function appToaster(): AppToaster {
  if (app) return app;
  const layer = document.createElement("div");
  layer.className = "app-toaster-layer";
  layer.popover = "manual";
  document.body.append(layer);
  return app = {
    toaster: OverlayToaster.create({ position: "top-right", maxToasts: 4, className: "app-toaster" },
      { container: layer, domRenderer: (element, container) => createRoot(container).render(element) }),
    keyed: new Map(),
    layer,
  };
}

// A window of the app is a modal <dialog>: it is in the top layer, above any z-index, and makes the rest of the page inert.
// The toasts are shown in a popover layer, inside the topmost open modal dialog when there is one, shown after it so that
// they are above it and can be clicked (a machine that asks to join while Settings is open); back on the page once it closes.
function raise(layer: HTMLElement): void {
  const modals = Array.from(document.querySelectorAll("dialog")).filter(dialog => dialog.open && dialog.matches(":modal"));
  const host = modals.at(-1) ?? document.body;
  if (layer.parentElement !== host) {
    host.append(layer);
    if (host !== document.body) host.addEventListener("close", () => raise(layer), { once: true });
  }
  if (!layer.matches(":popover-open")) layer.showPopover();
}

/**
 * Shows a short message at the top right of the window, below the title bar: the place for an outcome the
 * user must not miss (a send that was not accepted) without writing prose into the layout. With a key, the
 * message takes the place of the one shown with the same key: what is said again is not listed twice.
 */
export function showToast(toast: ToastProps, key?: string): void {
  const { toaster, keyed, layer } = appToaster();
  void toaster.then(value => {
    raise(layer);
    if (key === undefined) value.show(toast);
    else showKeyed(value, keyed, key, toast);
  }, () => { /* Without a toaster the message is simply not shown. */ });
}

// Blueprint decides whether a keyed message replaces a toast or is a new one from the toasts its React state holds, and
// that state is applied after the toast has been queued: a message of the same key that comes in before (a task that
// reports "running" and, at once, its outcome) is shown as a second toast, and the first, with no timeout, stays for good.
// So the first message of a key is shown, and the next ones wait, only the last kept, until the toast is in the state.
function showKeyed(toaster: Toaster, keyed: Map<string, KeyedToast>, key: string, toast: ToastProps): void {
  const known = keyed.get(key);
  const entry = known ?? {};
  if (!known) keyed.set(key, entry);
  const props: ToastProps = {
    ...toast,
    onDismiss: expired => {
      if (keyed.get(key) === entry) keyed.delete(key);
      toast.onDismiss?.(expired);
    },
  };
  // The first message of a key, or the toast is in the state already: Blueprint replaces it in place.
  if (!known || (entry.timer === undefined && isShown(toaster, key))) {
    toaster.show(props, key);
    return;
  }
  entry.pending = props;
  if (entry.timer !== undefined) return;
  let attempts = 0;
  const settle = () => {
    entry.timer = undefined;
    // Dismissed meanwhile: what comes next is a new toast, and this message is not one that anyone is waiting for.
    if (keyed.get(key) !== entry) return;
    if (!isShown(toaster, key) && ++attempts < settleAttempts) {
      entry.timer = setTimeout(settle, settleDelay);
      return;
    }
    const next = entry.pending;
    entry.pending = undefined;
    if (next) toaster.show(next, key);
  };
  entry.timer = setTimeout(settle, settleDelay);
}

function isShown(toaster: Toaster, key: string): boolean {
  return toaster.getToasts().some(shown => shown.key === key);
}

/** Takes away the message shown with a key: one that was kept until what it follows ended. */
export function dismissToast(key: string): void {
  if (!app) return;
  const { toaster, keyed } = app;
  const entry = keyed.get(key);
  if (entry?.timer !== undefined) clearTimeout(entry.timer);
  keyed.delete(key);
  void toaster.then(value => { value.dismiss(key); }, () => { /* There was no toaster to show it. */ });
}
