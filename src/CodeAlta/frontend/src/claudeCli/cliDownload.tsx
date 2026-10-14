import { useEffect, useRef, useState } from "react";
import { Button, Callout, ProgressBar } from "@blueprintjs/core";
import { claudeCli as ClaudeCliClient } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { storageSize } from "../storage/storage";
import { cliBytes, cliInstallFailure, cliInstalling, cliInstallView, type CliInstallView } from "./claudeCli";

export type ClaudeCliDownloadApi = Pick<typeof ClaudeCliClient, "install" | "cancelInstall" | "watch">;

/**
 * Follows the download of the pinned Claude Code CLI, which belongs to the application: another window or page may
 * have started it. `onDone` is called when a download that ran here or elsewhere ends installed.
 */
export function useClaudeCliDownload(api: ClaudeCliDownloadApi, epoch: string, onDone: () => void) {
  const [install, setInstall] = useState<CliInstallView | null>(null);
  const previous = useRef<CliInstallView | null>(null);
  const done = useRef(onDone);
  done.current = onDone;
  useEffect(() => {
    const abort = new AbortController();
    void (async () => {
      try {
        const events = await api.watch({ expectedEpoch: epoch }, { signal: abort.signal });
        for await (const event of events) {
          if (abort.signal.aborted) return;
          const view = cliInstallView(event);
          if (view.state === "done" && cliInstalling(previous.current)) done.current();
          previous.current = view;
          setInstall(view);
        }
      } catch { /* The page keeps what it read: the download goes on without it. */ }
    })();
    return () => abort.abort();
  }, [api, epoch]);

  return {
    install,
    running: cliInstalling(install),
    /** Starts the download; `size` is shown until the host says how far it is. */
    start: async (size: string | null | undefined) => {
      const reply = await api.install({ expectedEpoch: epoch }, { timeoutMilliseconds: 15000 }).catch(() => null);
      if (reply?.status === "ok") setInstall({ state: "downloading", received: 0, total: cliBytes(size), error: null });
      // Refused: the page says why instead of doing nothing.
      else if (reply?.status === "disabled" || reply?.status === "unsupported") setInstall({ state: "failed", received: 0, total: 0, error: reply.status === "disabled" ? "disabled" : "unsupported_platform" });
    },
    cancel: () => void api.cancelInstall({ expectedEpoch: epoch }, { timeoutMilliseconds: 15000 }).catch(() => null),
  };
}

/** The progress of a download that runs, with its bar, or the reason of one that failed. */
export function CliDownloadProgress({ install, version }: { install: CliInstallView | null; version: string }) {
  const { t, locale } = useShellLanguage();
  if (install?.state === "failed") return <Callout compact intent="danger" role="alert">{t(cliInstallFailure(install.error))}</Callout>;
  if (!cliInstalling(install)) return null;
  const known = install!.state === "downloading" && install!.total > 0;
  return <div className="provider-claude-cli-progress" role="status">
    <span>{install!.state === "installing" ? t("Installing Claude Code {version}…", { version })
      : t("Downloading Claude Code {version} — {received} of {total}", { version,
        received: storageSize(Math.min(install!.received, install!.total), locale), total: storageSize(install!.total, locale) })}</span>
    <ProgressBar intent="primary" stripes={!known} animate={!known} value={known ? Math.min(1, install!.received / install!.total) : undefined} />
  </div>;
}

/** The button that starts the download, with its size, or the one that cancels it. */
export function CliDownloadButton({ running, version, size, primary = true, onStart, onCancel }: {
  running: boolean; version: string; size: string; primary?: boolean; onStart: () => void; onCancel: () => void;
}) {
  const { t, locale } = useShellLanguage();
  return running
    ? <Button size="small" onClick={onCancel}>{t("Cancel")}</Button>
    : <Button size="small" intent={primary ? "primary" : "none"} icon={<AppIcon name="download" size={14} />} onClick={onStart}>
        {t("Download {version} ({size})", { version, size: storageSize(cliBytes(size), locale) })}</Button>;
}
