import { createContext, useContext, useEffect, useRef, useState } from "react";
import { Callout } from "@blueprintjs/core";
import { claudeCli as ClaudeCliClient, modelCatalog, type ClaudeCliCheckResponse, type ModelCatalogProbeRequest, type ModelCatalogProbeResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { Copyable } from "../ProviderAccount";
import { useShellLanguage } from "../shellLanguage";
import { claudeLoginCommand } from "./claudeCli";
import { CliDownloadButton, CliDownloadProgress, useClaudeCliDownload } from "./cliDownload";

export type ClaudeCliFirstUseApi = Pick<typeof ClaudeCliClient, "check" | "install" | "cancelInstall" | "watch">;
type Probe = (request: ModelCatalogProbeRequest, options: { signal: AbortSignal; timeoutMilliseconds: number }) => Promise<ModelCatalogProbeResponse>;

/** What follows the download: the provider is tested once, which says whether the CLI is signed in. */
type After = "none" | "testing" | "signed-out" | "ready";

/**
 * The first use of a Claude Code provider that has no CLI to run (no `command`, no download, no Claude Code of the
 * user): where the provider is chosen or its error shows, a compact card proposes the download of the version
 * CodeAlta is tested with, follows it, then gives the command that signs in when the CLI says it is signed out.
 * Nothing shows for a provider that has a CLI, or that does not run Claude Code.
 */
export function ClaudeCliFirstUse({ epoch, providerKey, api = ClaudeCliClient, probe = modelCatalog.probe }: {
  epoch: string; providerKey: string | null | undefined; api?: ClaudeCliFirstUseApi; probe?: Probe;
}) {
  const { t } = useShellLanguage();
  const [check, setCheck] = useState<ClaudeCliCheckResponse | null>(null);
  const [after, setAfter] = useState<After>("none");
  const [generation, setGeneration] = useState(0);
  // Only a card that proposed the download follows its end: a provider that had a CLI is not tested for it.
  const proposed = useRef(false);
  proposed.current = !!check?.claude && !check.executable;
  const download = useClaudeCliDownload(api, epoch, () => { if (proposed.current) setGeneration(value => value + 1); });

  useEffect(() => { setAfter("none"); setCheck(null); }, [providerKey]);
  useEffect(() => {
    if (!providerKey) return;
    const abort = new AbortController();
    void api.check({ expectedEpoch: epoch, providerKey }, { signal: abort.signal, timeoutMilliseconds: 15000 })
      .then(value => { if (!abort.signal.aborted && value.status === "ok" && value.providerKey === providerKey) setCheck(value); }, () => { });
    return () => abort.abort();
  }, [api, epoch, providerKey, generation]);

  // Once the download ended here, the provider is tested: a CLI that starts signed out says so.
  useEffect(() => {
    if (generation === 0 || !providerKey) return;
    const abort = new AbortController();
    setAfter("testing");
    void probe({ expectedEpoch: epoch, providerId: providerKey }, { signal: abort.signal, timeoutMilliseconds: 45000 })
      .then(reply => { if (!abort.signal.aborted) setAfter(reply.reason === "claude-code-signed-out" ? "signed-out" : reply.availability === "Ready" ? "ready" : "none"); },
        () => { if (!abort.signal.aborted) setAfter("none"); });
    return () => abort.abort();
  }, [probe, epoch, providerKey, generation]);

  if (!check?.claude) return null;
  const missing = !check.executable;
  if (!missing && after === "none" && !download.running) return null;
  return <Callout compact className="claude-cli-first-use" intent={after === "ready" ? "success" : "primary"}
    icon={<AppIcon name={after === "ready" ? "checked" : "terminal"} size={16} />}>
    {missing && <div className="claude-cli-first-use-head">
      <strong>{t("Claude Code CLI {version} is not installed", { version: check.version })}</strong>
      {check.supported && <CliDownloadButton running={download.running} version={check.version} size={check.size}
        onStart={() => void download.start(check.size)} onCancel={download.cancel} />}
    </div>}
    {missing && !check.supported && <p>{t("Claude Code has no version for this system.")}</p>}
    <CliDownloadProgress install={download.install} version={check.version} />
    {after === "testing" && <p><ActivitySpinner size={12} /> {t("Testing Claude Code…")}</p>}
    {after === "signed-out" && <>
      <p>{t("Claude Code is installed. Sign in: run this command in a terminal, then type /login.")}</p>
      <Copyable value={claudeLoginCommand({ windows: check.windows, home: check.home }, check.executable, check.configDir ?? "")} label={t("Copy command")} />
    </>}
    {after === "ready" && <p>{t("Claude Code {version} is ready.", { version: check.version })}</p>}
  </Callout>;
}

/** The epoch of the host whose sessions the timeline shows; null where nothing can be downloaded (a demo, a fixture). */
export const ClaudeCliEpochContext = createContext<string | null>(null);

/** The card of an error whose provider has no Claude Code CLI to run, in the timeline of its session. */
export function TimelineClaudeCli({ providerKey }: { providerKey: string }) {
  const epoch = useContext(ClaudeCliEpochContext);
  return epoch ? <ClaudeCliFirstUse epoch={epoch} providerKey={providerKey} /> : null;
}
