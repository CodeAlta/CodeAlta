import { useEffect, useRef, useState } from "react";
import { Button, Callout, ProgressBar, Tag } from "@blueprintjs/core";
import { claudeCli as ClaudeCliClient, type ClaudeCliStatusResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { Copyable } from "../ProviderAccount";
import { useShellLanguage } from "../shellLanguage";
import { storageSize } from "../storage/storage";
import { claudeLoginCommand, cliBytes, cliInstallFailure, cliInstalling, cliInstallView, type CliInstallView } from "./claudeCli";

export type ClaudeCliApi = Pick<typeof ClaudeCliClient, "read" | "install" | "cancelInstall" | "watch">;

/**
 * The Claude Code CLI a provider runs: the version CodeAlta is tested with, downloaded here on first use with its
 * progress, or the one the user installed until then; and the command that signs in to the account of the
 * provider in a terminal, with its folder.
 */
export function ClaudeCodeCli({ epoch, api = ClaudeCliClient, configDir, command }: {
  epoch: string; api?: ClaudeCliApi;
  /** The folder of the account of the provider, as the form has it; blank for the folder of Claude Code. */
  configDir: string;
  /** The executable the provider names in the configuration file, or null. */
  command: string | null;
}) {
  const { t, locale } = useShellLanguage();
  const [status, setStatus] = useState<ClaudeCliStatusResponse | null>();
  const [install, setInstall] = useState<CliInstallView | null>(null);
  const [generation, setGeneration] = useState(0);
  const previous = useRef<CliInstallView | null>(null);

  useEffect(() => {
    const abort = new AbortController();
    void api.read({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 20000 })
      .then(value => { if (!abort.signal.aborted) setStatus(value.status === "ok" ? value : null); },
        () => { if (!abort.signal.aborted) setStatus(null); });
    return () => abort.abort();
  }, [api, epoch, generation]);

  // The download belongs to the application: the page follows it, and another window may have started it.
  useEffect(() => {
    const abort = new AbortController();
    void (async () => {
      try {
        const events = await api.watch({ expectedEpoch: epoch }, { signal: abort.signal });
        for await (const event of events) {
          if (abort.signal.aborted) return;
          const view = cliInstallView(event);
          if (view.state === "done" && cliInstalling(previous.current)) setGeneration(value => value + 1);
          previous.current = view;
          setInstall(view);
        }
      } catch { /* The page keeps what it read: the download goes on without it. */ }
    })();
    return () => abort.abort();
  }, [api, epoch]);

  async function download() {
    const reply = await api.install({ expectedEpoch: epoch }, { timeoutMilliseconds: 15000 }).catch(() => null);
    if (reply?.status === "ok") setInstall({ state: "downloading", received: 0, total: cliBytes(status?.size), error: null });
  }

  if (status === undefined) return <div className="provider-account provider-settings-wide"><ActivitySpinner size={14} /></div>;
  if (status === null) return <Callout compact className="provider-settings-wide" intent="warning">{t("The state of the Claude Code CLI could not be read.")}</Callout>;

  const running = cliInstalling(install);
  const configured = command?.trim() || null;
  const differs = status.installedVersion !== null && status.installedVersion !== status.version;
  const executable = configured ?? status.pinnedPath ?? status.installedPath;
  const offer = !configured && !status.pinnedPath && status.supported;
  const size = storageSize(cliBytes(status.size), locale);
  const known = install?.state === "downloading" && install.total > 0;
  return <div className="provider-account provider-claude-cli provider-settings-wide">
    <div className="provider-account-status">
      <AppIcon name="terminal" size={16} />
      <strong>{t("Claude Code CLI")}</strong>
      {configured ? <Tag minimal>{t("Command of the provider")}</Tag>
        : status.pinnedPath ? <Tag minimal intent="success">{t("Version {version}", { version: status.version })}</Tag>
        : status.installedPath ? <Tag minimal intent={differs ? "warning" : "none"}>{status.installedVersion ? t("Your version {version}", { version: status.installedVersion }) : t("Your version")}</Tag>
        : <Tag minimal intent="danger">{t("Not installed")}</Tag>}
      <span className="provider-settings-spacer" />
      {offer && !running && <Button size="small" intent={status.installedPath && !differs ? "none" : "primary"} icon={<AppIcon name="download" size={14} />}
        onClick={() => void download()}>{t("Download {version} ({size})", { version: status.version, size })}</Button>}
      {running && <Button size="small" onClick={() => void api.cancelInstall({ expectedEpoch: epoch }, { timeoutMilliseconds: 15000 }).catch(() => null)}>{t("Cancel")}</Button>}
    </div>
    {running && <div className="provider-claude-cli-progress" role="status">
      <span>{install!.state === "installing" ? t("Installing Claude Code {version}…", { version: status.version })
        : t("Downloading Claude Code {version} — {received} of {total}", { version: status.version,
          received: storageSize(Math.min(install!.received, install!.total), locale), total: storageSize(install!.total, locale) })}</span>
      <ProgressBar intent="primary" stripes={!known} animate={!known} value={known ? Math.min(1, install!.received / install!.total) : undefined} />
    </div>}
    {install?.state === "failed" && <Callout compact intent="danger" role="alert">{t(cliInstallFailure(install.error))}</Callout>}
    <p className="bp6-text-muted">{configured ? t("This provider runs {command}, set in the configuration file.", { command: configured })
      : status.pinnedPath ? t("CodeAlta runs Claude Code {version}, the version it is tested with.", { version: status.version })
      : !status.supported ? t("Claude Code has no version for this system.")
      : status.installedPath ? status.installedVersion
        ? t("CodeAlta is tested with Claude Code {version}. Until it is downloaded, your Claude Code {installed} at {path} is used.", { version: status.version, installed: status.installedVersion, path: status.installedPath })
        : t("CodeAlta is tested with Claude Code {version}. Until it is downloaded, your Claude Code at {path} is used.", { version: status.version, path: status.installedPath })
      : t("Claude Code is not installed. Download the version CodeAlta is tested with to use this provider.")}</p>
    <p>{t("Sign in to the account of this provider: run this command in a terminal, then type /login. CodeAlta never handles its credentials.")}</p>
    <Copyable value={claudeLoginCommand({ windows: status.windows, home: status.home }, executable, configDir)} label={t("Copy command")} />
  </div>;
}
