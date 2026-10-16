import { dismissToast, showToast } from "./appToaster";

const message = (element: Element) => element.querySelector(".bp6-toast-message")?.textContent ?? "";

Object.assign(window, {
  toasterFixture: {
    show: showToast,
    dismiss: dismissToast,
    messages: () => Array.from(document.querySelectorAll(".app-toaster .bp6-toast"), message),
  },
});
