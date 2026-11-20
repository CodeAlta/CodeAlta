import { OverlayToaster, type Toaster, type ToastProps } from "@blueprintjs/core";
import { createRoot } from "react-dom/client";

/** What is known of the toast shown with a key: the message that came in while it was not yet in the toaster, and the timer that waits for it. */
interface KeyedToast {
  pending?: ToastProps;
  timer?: ReturnType<typeof setTimeout>;
}

/** A toast on screen: whether it stays until it is closed (a timeout of 0). */
interface ShownToast {
  readonly kept: boolean;
}

interface AppToaster {
  readonly toaster: Promise<Toaster>;
  readonly keyed: Map<string, KeyedToast>;
  /** The toasts on screen by key, the oldest first: what the limits below count. */
  readonly shown: Map<string, ShownToast>;
  /** The toasts that stay until closed and wait for room, by key, the oldest first. */
  readonly held: Map<string, ToastProps>;
  /** The popover the toaster is rendered in (see `raise`). */
  readonly layer: HTMLElement;
  /** The number of the next toast shown without a key. */
  next: number;
}

let app: AppToaster | undefined;

const settleDelay = 16;
const settleAttempts = 60;
// How long a toast that gives way may wait in Blueprint's queue (a toast every 50 ms) before it is on screen to dismiss.
const queuedAttempts = 250;

/**
 * How many toasts are on screen at once. A toast that goes by itself gives way to the next one past the limit, the
 * oldest first. A toast that stays until it is closed (a notice that came while the user was away, a task that runs) is
 * never taken away by another: past its own limit, the next ones wait, and each that is closed shows the next. Blueprint's
 * `maxToasts` would take away the oldest toast of any kind: the limits are kept here instead.
 */
export const maximumPassingToasts = 4;
export const maximumKeptToasts = 10;

function appToaster(): AppToaster {
  if (app) return app;
  const layer = document.createElement("div");
  layer.className = "app-toaster-layer";
  layer.popover = "manual";
  document.body.append(layer);
  return app = {
    toaster: OverlayToaster.create({ position: "top-right", className: "app-toaster" },
      { container: layer, domRenderer: (element, container) => createRoot(container).render(element) }),
    keyed: new Map(),
    shown: new Map(),
    held: new Map(),
    layer,
    next: 0,
  };
}

// Shows a toast under its key, within the limit of its kind.
function place(state: AppToaster, toaster: Toaster, toast: ToastProps, key: string): void {
  const entry: ShownToast = { kept: toast.timeout === 0 };
  // A toast that takes the place of another of the same key counts once, as the newest of its kind.
  const replaces = state.shown.delete(key);
  const same = [...state.shown].filter(([, shown]) => shown.kept === entry.kept).map(([shownKey]) => shownKey);
  if (entry.kept && same.length >= maximumKeptToasts) {
    // The one it replaces left the screen for it: it is no longer there to be updated.
    if (replaces) toaster.dismiss(key);
    // One that waits already keeps its turn.
    state.held.set(key, toast);
    return;
  }
  if (!entry.kept) {
    for (const oldest of same.slice(0, Math.max(0, same.length - maximumPassingToasts + 1))) {
      state.shown.delete(oldest);
      dismissWhenShown(toaster, oldest, 0);
    }
  }
  state.held.delete(key);
  state.shown.set(key, entry);
  toaster.show({
    ...toast,
    onDismiss: expired => {
      if (state.shown.get(key) === entry) {
        state.shown.delete(key);
        // A kept toast that is closed makes room for the oldest that waits: shown after the update of Blueprint's state
        // that calls this one, which must not update it again.
        if (entry.kept && state.held.size > 0) queueMicrotask(() => showHeld(state, toaster));
      }
      toast.onDismiss?.(expired);
    },
  }, key);
}

// Shows the oldest kept toast that waits, when there is room for it.
function showHeld(state: AppToaster, toaster: Toaster): void {
  const waiting = state.held.entries().next().value;
  if (waiting) place(state, toaster, waiting[1], waiting[0]);
}

// Blueprint dismisses only the toasts of its React state: one still in its queue is dismissed once it is there.
function dismissWhenShown(toaster: Toaster, key: string, attempts: number): void {
  if (isShown(toaster, key) || attempts >= queuedAttempts) toaster.dismiss(key);
  else setTimeout(() => dismissWhenShown(toaster, key, attempts + 1), settleDelay);
}

// A window of the app is a modal <dialog>: it is in the top layer, above any z-index, and makes the rest of the page inert.
// The toasts are shown in a popover layer, inside the topmost open modal dialog when there is one, shown after it so that
// they are above it and can be clicked (a machine that asks to join while Settings is open); back on the page once it closes.
// Another layer of the same kind (the prompt of a secret that asks before use) is raised the same way.
// Over a window they start below its title bar, whose buttons (Close) stay within reach.
export function raiseAboveWindows(layer: HTMLElement): void {
  const modals = Array.from(document.querySelectorAll("dialog")).filter(dialog => dialog.open && dialog.matches(":modal"));
  const host = modals.at(-1) ?? document.body;
  if (layer.parentElement !== host) {
    host.append(layer);
    if (host !== document.body) host.addEventListener("close", () => raiseAboveWindows(layer), { once: true });
  }
  const bar = host === document.body ? null : host.querySelector<HTMLElement>(".app-window-titlebar");
  if (bar) layer.style.setProperty("--app-toaster-top", `${Math.round(bar.getBoundingClientRect().bottom + 8)}px`);
  else layer.style.removeProperty("--app-toaster-top");
  if (!layer.matches(":popover-open")) layer.showPopover();
}

/**
 * Shows a short message at the top right of the window, below the title bar: the place for an outcome the
 * user must not miss (a send that was not accepted) without writing prose into the layout. With a key, the
 * message takes the place of the one shown with the same key: what is said again is not listed twice.
 */
export function showToast(toast: ToastProps, key?: string): void {
  const state = appToaster();
  void state.toaster.then(value => {
    raiseAboveWindows(state.layer);
    if (key === undefined) place(state, value, toast, `app-toast-${state.next++}`);
    else showKeyed(state, value, key, toast);
  }, () => { /* Without a toaster the message is simply not shown. */ });
}

// Blueprint decides whether a keyed message replaces a toast or is a new one from the toasts its React state holds, and
// that state is applied after the toast has been queued: a message of the same key that comes in before (a task that
// reports "running" and, at once, its outcome) is shown as a second toast, and the first, with no timeout, stays for good.
// So the first message of a key is shown, and the next ones wait, only the last kept, until the toast is in the state.
function showKeyed(state: AppToaster, toaster: Toaster, key: string, toast: ToastProps): void {
  const keyed = state.keyed;
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
    place(state, toaster, props, key);
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
    if (next) place(state, toaster, next, key);
  };
  entry.timer = setTimeout(settle, settleDelay);
}

function isShown(toaster: Toaster, key: string): boolean {
  return toaster.getToasts().some(shown => shown.key === key);
}

/** Takes away the message shown with a key: one that was kept until what it follows ended. */
export function dismissToast(key: string): void {
  if (!app) return;
  const { toaster, keyed, held } = app;
  const entry = keyed.get(key);
  if (entry?.timer !== undefined) clearTimeout(entry.timer);
  keyed.delete(key);
  held.delete(key);
  void toaster.then(value => { value.dismiss(key); }, () => { /* There was no toaster to show it. */ });
}
