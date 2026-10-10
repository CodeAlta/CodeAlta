// Test-only native-window preview. This is not imported by the app and calls no RPC service.
// Bundle it with esbuild, evaluate it in the developer window, then use window.updateNoticePreview.
import { OverlayToaster, type ToastProps } from "@blueprintjs/core";
import { createRoot, type Root } from "react-dom/client";
import { availableUpdate, updateNotification, updateToAnnounce, UpdateToaster } from "./UpdateNotice";
import type { Locale } from "./localization";

let root: Root | undefined;
let container: HTMLElement | undefined;
let ordinaryToaster: OverlayToaster | null = null;
const announced = new Set<string>();
const actions: string[] = [];
function render(notification: ToastProps | null) {
  if (!container) {
    container = document.createElement("div");
    container.className = "update-notice-preview";
    document.body.append(container);
    root = createRoot(container);
  }
  root!.render(<><UpdateToaster notification={notification} />
    <OverlayToaster ref={value => { ordinaryToaster = value; }} position="top-right" maxToasts={4} className="app-toaster" /></>);
}
const preview = {
  actions,
  async show(version = "99.0.0", canInstall = true, locale: Locale = "en", visible = true) {
    const update = updateToAnnounce(announced, availableUpdate({ status: "available", latestVersion: version,
      command: "dotnet tool update -g CodeAlta", releaseNotes: "preview-only", canInstall }), visible);
    if (!update) return false;
    announced.add(update.version);
    render(updateNotification(update, locale, () => actions.push("release-notes (preview only)"),
      () => actions.push("install (preview only)")));
    return true;
  },
  async dismiss() { render(null); },
  async ordinaryNotices() {
    for (let index = 0; index < 4; index++) ordinaryToaster?.show({ message: <span data-ordinary-notice>Other message {index}</span>, timeout: 0 }, `ordinary-${index}`);
  },
  async reset() { if (root) render(null); ordinaryToaster?.clear(); announced.clear(); actions.length = 0; },
  async dispose() {
    await preview.reset(); root?.unmount(); container?.remove();
    delete (window as Window & { updateNoticePreview?: unknown }).updateNoticePreview;
  },
};
Object.assign(window, { updateNoticePreview: preview });
