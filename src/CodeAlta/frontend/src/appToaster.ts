import { OverlayToaster, type Toaster, type ToastProps } from "@blueprintjs/core";
import { createRoot } from "react-dom/client";

let toaster: Promise<Toaster> | undefined;

/**
 * Shows a short message at the top right of the window, below the title bar: the place for an outcome the
 * user must not miss (a send that was not accepted) without writing prose into the layout. With a key, the
 * message takes the place of the one shown with the same key: what is said again is not listed twice.
 */
export function showToast(toast: ToastProps, key?: string): void {
  toaster ??= OverlayToaster.create({ position: "top-right", maxToasts: 4, className: "app-toaster" },
    { domRenderer: (element, container) => createRoot(container).render(element) });
  void toaster.then(value => { value.show(toast, key); }, () => { /* Without a toaster the message is simply not shown. */ });
}
