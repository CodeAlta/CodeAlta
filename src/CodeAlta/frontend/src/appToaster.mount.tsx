import { dismissToast, showToast } from "./appToaster";

const message = (element: Element) => element.querySelector(".bp6-toast-message")?.textContent ?? "";

Object.assign(window, {
  toasterFixture: {
    show: showToast,
    dismiss: dismissToast,
    messages: () => Array.from(document.querySelectorAll(".app-toaster .bp6-toast"), message),
    // Where the toaster is: "body", or the class of the dialog that holds it.
    host: () => { const parent = document.querySelector(".app-toaster")?.parentElement?.parentElement; return parent === document.body ? "body" : parent?.className ?? null; },
    // A modal window as the app opens one: a <dialog> layer over the whole page (.app-window-layer), its window inside.
    openModal: (className: string) => {
      const dialog = document.createElement("dialog"); dialog.className = className; dialog.textContent = className;
      dialog.style.cssText = "position: fixed; inset: 0; width: 100vw; height: 100dvh; max-width: none; max-height: none; margin: 0; padding: 0; border: 0; overflow: hidden; background: transparent";
      document.body.append(dialog); dialog.showModal();
    },
    closeModal: (className: string, remove: boolean) => { const dialog = document.querySelector<HTMLDialogElement>(`dialog.${className}`); if (!dialog) return; if (remove) dialog.remove(); else dialog.close(); },
    // Whether a click at the middle of the first toast reaches it, as it does only outside an inert page.
    toastTakesClicks: () => { const toast = document.querySelector(".app-toaster .bp6-toast"); if (!toast) return false; const box = toast.getBoundingClientRect(); const hit = document.elementFromPoint(box.left + box.width / 2, box.top + box.height / 2); return toast.contains(hit) || `${hit?.tagName}.${hit?.className} ${JSON.stringify(box)}`; },
  },
});
