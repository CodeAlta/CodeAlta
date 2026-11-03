import { useEffect, useState } from "react";
import { HTMLSelect } from "@blueprintjs/core";
import { fleetServer, type FleetServerView } from "#neoastra";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import { serverOfSpace } from "./fleetServer";

export type SpaceServerApi = Pick<typeof fleetServer, "status" | "linkSpace">;

/**
 * The server a space publishes to, in the General section of its page: a select of the servers this computer is linked to, shown only
 * when there is one. Linked, the sessions of the space that are not Private publish their facts there (never their content).
 */
export function SpaceServerField({ spaceId, epoch, disabled, api = fleetServer }: { spaceId: string; epoch: string | null; disabled: boolean; api?: SpaceServerApi }) {
  const { t } = useShellLanguage();
  const [servers, setServers] = useState<readonly FleetServerView[] | null>(null);
  const [saving, setSaving] = useState(false);
  const [failed, setFailed] = useState(false);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    api.status({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 15_000 })
      .then(value => { if (!abort.signal.aborted) setServers(value.status === "ok" ? value.servers : []); }, () => { if (!abort.signal.aborted) setServers([]); });
    return () => abort.abort();
  }, [api, epoch, generation]);
  if (!epoch || !servers || servers.length === 0) return null;
  const linked = serverOfSpace(servers, spaceId);
  const id = `space-${spaceId}-server`;
  async function choose(linkId: string | null) {
    setSaving(true);
    setFailed(false);
    try {
      const result = await api.linkSpace({ expectedEpoch: epoch!, spaceId, linkId }, { timeoutMilliseconds: 15_000 });
      setFailed(result.status !== "ok");
    } catch { setFailed(true); }
    finally { setSaving(false); setGeneration(value => value + 1); }
  }
  return <Field label={t("Server")} htmlFor={id}
    notice={<p className="settings-field-help" role={failed ? "alert" : undefined}>{t(failed ? "The operation did not complete."
      : linked ? "Its sessions publish their state, model and times there; Private ones and all content stay here." : "Its sessions stay on this computer.")}</p>}>
    <HTMLSelect id={id} value={linked?.id ?? ""} disabled={disabled || saving} onChange={event => void choose(event.target.value || null)}>
      <option value="">{t("Not linked")}</option>
      {servers.map(server => <option key={server.id} value={server.id}>{server.organization ? `${server.organization} (${server.url})` : server.url}</option>)}
    </HTMLSelect>
  </Field>;
}
