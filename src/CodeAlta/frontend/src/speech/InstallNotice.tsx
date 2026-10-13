import { ProgressBar, type ToastProps } from "@blueprintjs/core";
import { translate, type Locale, type MessageKey } from "../localization";
import { storageSize } from "../storage/storage";
import type { InstallNotice as Notice } from "./speechStore";

type Progress = Extract<Notice, { kind: "progress" }>;

/** What the notice of a download says: its model, and how far it is once its size is known. */
export function installText(notice: Progress, locale: Locale): string {
  const t = (key: MessageKey, parameters?: Readonly<Record<string, string>>) => translate(locale, key, parameters);
  if (notice.phase === "installing") return t("Installing {model}…", { model: notice.model });
  return notice.total > 0 ? t("Downloading {model} — {received} of {total}", { model: notice.model,
    received: storageSize(Math.min(notice.received, notice.total), locale), total: storageSize(notice.total, locale) })
    : t("Downloading {model}…", { model: notice.model });
}

/**
 * The download of a speech model in its notice: what it does, and a bar that fills once the size is known. It
 * is shown in a toast, which lives outside the application's tree, so it is given its language.
 */
export function InstallNotice({ notice, locale }: { notice: Progress; locale: Locale }) {
  const known = notice.phase === "downloading" && notice.total > 0;
  return <div className="speech-install-notice">
    <span role="status">{installText(notice, locale)}</span>
    <ProgressBar intent="primary" stripes={!known} animate={!known} value={known ? Math.min(1, notice.received / notice.total) : undefined} />
  </div>;
}

/** The toast of a state of the download; the notice of progress stays until the download ends. */
export function installToast(notice: Exclude<Notice, { kind: "dismiss" }>, locale: Locale, onCancel: () => void, onDismiss: () => void): ToastProps {
  if (notice.kind === "ready") return { message: translate(locale, "Speech to prompt is ready"), intent: "success", icon: "tick", timeout: 5000 };
  if (notice.kind === "failed") return { message: translate(locale, notice.message), intent: "danger", icon: "error", timeout: 10_000 };
  return { message: <InstallNotice notice={notice} locale={locale} />, icon: "download", timeout: 0,
    action: { text: translate(locale, "Cancel"), onClick: onCancel }, onDismiss: expired => { if (!expired) onDismiss(); } };
}
