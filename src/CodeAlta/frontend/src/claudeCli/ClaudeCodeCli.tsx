import { useEffect, useState } from "react";
import { Callout, Tag } from "@blueprintjs/core";
import { claudeCli as ClaudeCliClient, type ClaudeCliStatusResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { Copyable } from "../ProviderAccount";
import { useShellLanguage } from "../shellLanguage";
import { claudeLoginCommand } from "./claudeCli";
import { CliDownloadButton, CliDownloadProgress, useClaudeCliDownload } from "./cliDownload";

export type ClaudeCliApi = Pick<typeof ClaudeCliClient, "read" | "check" | "install" | "cancelInstall" | "watch">;

/**
 * The Claude Code CLI a provider runs: the version CodeAlta is tested with, downloaded here on first use with its
 * progress, or the one the user installed until then; and the command that signs in to the account of the
 * provider in a terminal, with its folder.
 */
export function ClaudeCodeCli({ epoch, api = ClaudeCliClient, providerKey = null, configDir, command }: {
  epoch: string; api?: ClaudeCliApi;
  /** The key of the saved provider, which says whether its `command` is there; null for a provider not saved yet. */
  providerKey?: string | null;
  /** The folder of the account of the provider, as the form has it; blank for the folder of Claude Code. */
  configDir: string;
  /** The executable the provider names in the configuration file, or null. */
  command: string | null;
}) {
  const { t } = useShellLanguage();
  const [status, setStatus] = useState<ClaudeCliStatusResponse | null>();
  const [generation, setGeneration] = useState(0);
  const download = useClaudeCliDownload(api, epoch, () => setGeneration(value => value + 1));
  // A command the configuration names may not be there: the host says whether the provider has an executable.
  const configured = command?.trim() || null;
  const [commandMissing, setCommandMissing] = useState(false);
  useEffect(() => {
    setCommandMissing(false);
    if (!configured || !providerKey) return;
    const abort = new AbortController();
    void api.check({ expectedEpoch: epoch, providerKey }, { signal: abort.signal, timeoutMilliseconds: 15000 })
      .then(value => { if (!abort.signal.aborted && value.status === "ok" && value.claude) setCommandMissing(!value.executable); }, () => { });
    return () => abort.abort();
  }, [api, epoch, providerKey, configured]);

  useEffect(() => {
    const abort = new AbortController();
    void api.read({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 20000 })
      .then(value => { if (!abort.signal.aborted) setStatus(value.status === "ok" ? value : null); },
        () => { if (!abort.signal.aborted) setStatus(null); });
    return () => abort.abort();
  }, [api, epoch, generation]);

  if (status === undefined) return <div className="provider-account provider-settings-wide"><ActivitySpinner size={14} /></div>;
  if (status === null) return <Callout compact className="provider-settings-wide" intent="warning">{t("The state of the Claude Code CLI could not be read.")}</Callout>;

  const differs = status.installedVersion !== null && status.installedVersion !== status.version;
  const executable = configured ?? status.pinnedPath ?? status.installedPath;
  const offer = !configured && !status.pinnedPath && status.supported;
  return <div className="provider-account provider-claude-cli provider-settings-wide">
    <div className="provider-account-status">
      <AppIcon name="terminal" size={16} />
      <strong>{t("Claude Code CLI")}</strong>
      {configured ? <Tag minimal intent={commandMissing ? "danger" : "none"}>{t(commandMissing ? "Not found" : "Command of the provider")}</Tag>
        : status.pinnedPath ? <Tag minimal intent="success">{t("Version {version}", { version: status.version })}</Tag>
        : status.installedPath ? <Tag minimal intent={differs ? "warning" : "none"}>{status.installedVersion ? t("Your version {version}", { version: status.installedVersion }) : t("Your version")}</Tag>
        : <Tag minimal intent="danger">{t("Not installed")}</Tag>}
      <span className="provider-settings-spacer" />
      {(offer || download.running) && <CliDownloadButton running={download.running} version={status.version} size={status.size}
        primary={!status.installedPath || differs} onStart={() => void download.start(status.size)} onCancel={download.cancel} />}
    </div>
    <CliDownloadProgress install={download.install} version={status.version} />
    <p className="bp6-text-muted">{configured ? t(commandMissing ? "This provider runs {command}, set in the configuration file, and it is not there." : "This provider runs {command}, set in the configuration file.", { command: configured })
      : status.pinnedPath ? t("CodeAlta runs Claude Code {version}, the version it is tested with.", { version: status.version })
      : !status.supported ? t("Claude Code has no version for this system.")
      : status.installedPath ? status.installedVersion === status.version
        ? t("Your Claude Code at {path} is {version}, the version CodeAlta is tested with.", { version: status.version, path: status.installedPath })
        : status.installedVersion
        ? t("CodeAlta is tested with Claude Code {version}. Until it is downloaded, your Claude Code {installed} at {path} is used.", { version: status.version, installed: status.installedVersion, path: status.installedPath })
        : t("CodeAlta is tested with Claude Code {version}. Until it is downloaded, your Claude Code at {path} is used.", { version: status.version, path: status.installedPath })
      : t("Claude Code is not installed. Download the version CodeAlta is tested with to use this provider.")}</p>
    <p>{t("Sign in to the account of this provider: run this command in a terminal, then type /login. CodeAlta never handles its credentials.")}</p>
    <Copyable value={claudeLoginCommand({ windows: status.windows, home: status.home }, executable, configDir)} label={t("Copy command")} />
  </div>;
}
