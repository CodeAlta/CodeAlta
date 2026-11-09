import { useEffect, useMemo, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import type { MachineView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { settingsNavigation } from "../settingsNavigation";
import { modelServersApi, type ModelServersApi } from "./api";
import { backendName, currentBuilds, gpuSummary, modelName, serverStateOf, stateIntent, updateAvailable } from "./modelServers";
import { stateLabel } from "./ModelServersSettings";
import type { ModelHostStatus } from "./types";

/**
 * The model servers of a machine, on its page: its GPUs, its builds of llama.cpp and its servers with where each stands.
 * Settings > Fleet > Model servers changes them. Nothing shows for a machine that runs no model host.
 */
export function MachineModelServers({ epoch, machine, api: given }: { epoch: string; machine: Pick<MachineView, "id" | "name" | "state">; api?: ModelServersApi }) {
  const { t } = useShellLanguage();
  const api = useMemo(() => given ?? modelServersApi(epoch), [given, epoch]);
  const [status, setStatus] = useState<ModelHostStatus | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    api.status(machine.id, abort.signal).then(value => { if (!abort.signal.aborted) setStatus(value); }, () => { if (!abort.signal.aborted) setStatus(null); });
    return () => abort.abort();
  }, [api, machine.id, machine.state]);
  if (!status) return null;
  const builds = currentBuilds(status.builds);
  return <section className="machine-section machine-model-servers" aria-label={t("Model servers")}>
    <h2>{t("Model servers")}</h2>
    <p className="muted-text">{[gpuSummary(status.hardware) || t("No GPU"), builds || t("llama.cpp is not installed")].join(" · ")}
      {updateAvailable(status) && <Tag minimal intent="primary" className="model-servers-update">{t("update")}</Tag>}</p>
    {status.servers.length === 0 ? <p className="muted-text machine-empty">{t("No model server on {name} yet.", { name: machine.name })}</p>
      : <ul className="machine-model-servers-list">{status.servers.map(server => <li key={server.definition.id}>
        <AppIcon name="server" size={15} />
        <span className="machine-project-main"><strong>{server.definition.name}</strong>
          <small className="muted-text">{server.definition.models.map(model => modelName(model.model)).join(", ")} · {backendName(server.definition.backend)}</small></span>
        <Tag minimal round intent={stateIntent(serverStateOf(server))}>{t(stateLabel(serverStateOf(server)))}</Tag>
      </li>)}</ul>}
    <Button size="small" variant="minimal" icon={<AppIcon name="model" size={14} />} text={t("Open Model servers")} onClick={() => settingsNavigation.open("modelServers")} />
  </section>;
}
