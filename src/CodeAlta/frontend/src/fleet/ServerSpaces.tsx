import { useEffect, useState } from "react";
import { Button, Callout, Switch, Tag } from "@blueprintjs/core";
import { fleetServer, spaces as spacesApi, type SpaceItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { serverProblem } from "./fleetServer";
import { spacesToLink } from "./joinOrganization";

/** The calls of the section on the server. */
export type ServerSpacesApi = Pick<typeof fleetServer, "linkSpace">;

/** The calls of the section on the spaces of this computer. */
export type ServerSpacesListApi = Pick<typeof spacesApi, "list">;

/**
 * The spaces of this computer that show their sessions on a server, each with its switch: offered once a server is created, and on
 * the card of each server. A server shows nothing of a space that is not linked, the owner's own sessions included; Link all my
 * spaces does it in one click. A linked space publishes the states and counts of its sessions, never their content.
 */
export function ServerSpaces({ epoch, linkId, linked, api = fleetServer, spaces = spacesApi, onChanged }: {
  epoch: string; linkId: string; linked: readonly string[]; api?: ServerSpacesApi; spaces?: ServerSpacesListApi; onChanged?: () => void;
}) {
  const { t } = useShellLanguage();
  const [list, setList] = useState<readonly SpaceItem[] | null>(null);
  // The links as this section changed them, over what the server said.
  const [shown, setShown] = useState<readonly string[]>(linked);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  useEffect(() => setShown(linked), [linked]);
  useEffect(() => {
    const abort = new AbortController();
    spaces.list({ expectedEpoch: epoch }, { signal: abort.signal }).then(value => { if (!abort.signal.aborted) setList(value.status === "ok" ? value.spaces : []); },
      () => { if (!abort.signal.aborted) setList([]); });
    return () => abort.abort();
  }, [epoch, spaces]);

  async function link(ids: readonly string[], on: boolean) {
    setBusy(true);
    setFailed(null);
    try {
      for (const spaceId of ids) {
        const result = await api.linkSpace({ expectedEpoch: epoch, spaceId, linkId: on ? linkId : null }, { timeoutMilliseconds: 15_000 });
        if (result.status !== "ok") { setFailed(t(serverProblem(result.status) ?? "The operation did not complete.")); return; }
        setShown(current => on ? [...current.filter(id => id !== spaceId), spaceId] : current.filter(id => id !== spaceId));
      }
    } catch {
      setFailed(t("The operation did not complete."));
    } finally {
      setBusy(false);
      onChanged?.();
    }
  }

  if (!list || list.length === 0) return null;
  const missing = spacesToLink(list, shown);
  return <section className="fleet-server-spaces" aria-label={t("Spaces on this server")}>
    <div className="fleet-server-spaces-head">
      <AppIcon name="space" size={14} />
      <strong>{t("Spaces on this server")}</strong>
      <Tag minimal round intent={shown.length === 0 ? "warning" : "success"}>{shown.length === 0 ? t("None") : `${list.filter(space => shown.includes(space.id)).length} / ${list.length}`}</Tag>
      <span className="fleet-server-recovery-spacer" />
      {missing.length > 0 && <Button size="small" intent={shown.length === 0 ? "primary" : undefined} variant={shown.length === 0 ? undefined : "minimal"} disabled={busy}
        text={t("Link all my spaces")} onClick={() => void link(missing, true)} />}
    </div>
    <p className="muted-text">{t(shown.length === 0 ? "No space shows its sessions there yet, not even yours: link the spaces whose sessions the server shows."
      : "Their sessions show their states and counts there, never their content.")}</p>
    <div className="fleet-server-spaces-list" role="group" aria-label={t("Spaces on this server")}>
      {list.map(space => <Switch key={space.id} inline checked={shown.includes(space.id)} disabled={busy} label={space.name}
        onChange={event => { const on = event.currentTarget.checked; void link([space.id], on); }} />)}
    </div>
    {failed && <Callout compact intent="warning" role="alert">{failed}</Callout>}
  </section>;
}
