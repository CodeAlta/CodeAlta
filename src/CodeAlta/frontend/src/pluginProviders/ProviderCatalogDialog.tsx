import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Callout, Dialog, DialogBody, InputGroup, NonIdealState, Tag } from "@blueprintjs/core";
import { globalConfig, type GlobalConfigPluginCatalogEntry, type GlobalConfigPluginCatalogResponse } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { PaintedProgressBar } from "../PaintedProgressBar";
import { Logo } from "../ProviderIcon";
import { providerBrand } from "../brands";
import { useShellLanguage } from "../shellLanguage";
import { downloadSize } from "./pluginProviderForm";

export type ProviderCatalogApi = Pick<typeof globalConfig, "pluginCatalog" | "installPluginProvider">;

type Install = Readonly<{ entryId: string; message: string | null; received: number | null; total: number | null }>;

/**
 * The catalog of a provider type of a plugin, such as the agents of the ACP registry: one card per entry, added in one
 * click. Adding installs the entry (a download, with its progress) and writes its provider, enabled.
 */
export function ProviderCatalogDialog({ epoch, type, title, api = globalConfig, onAdded, onOpen, onClose }: {
  epoch: string; type: string; title: string; api?: ProviderCatalogApi;
  /** Called once an entry is added, with the key of its provider. */
  onAdded: (key: string) => void;
  /** Shows the provider of an entry that was added before. */
  onOpen: (key: string) => void;
  onClose: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const [catalog, setCatalog] = useState<GlobalConfigPluginCatalogResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [query, setQuery] = useState("");
  const [install, setInstall] = useState<Install | null>(null);
  const [notice, setNotice] = useState<Readonly<{ text: string; intent: "success" | "danger" }> | null>(null);
  const [added, setAdded] = useState<ReadonlyMap<string, string>>(new Map());
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  useEffect(() => {
    const abort = new AbortController();
    setLoading(true);
    void api.pluginCatalog({ expectedEpoch: epoch, type, refresh: refresh > 0 }, { signal: abort.signal, timeoutMilliseconds: 60000 })
      .then(reply => { if (!abort.signal.aborted) setCatalog(reply); }, () => { if (!abort.signal.aborted) setCatalog(null); })
      .finally(() => { if (!abort.signal.aborted) setLoading(false); });
    return () => abort.abort();
  }, [api, epoch, type, refresh]);

  const entries = useMemo(() => {
    const words = query.trim().toLowerCase().split(/\s+/).filter(Boolean);
    return (catalog?.entries ?? []).filter(entry => words.every(word =>
      `${entry.name} ${entry.id} ${entry.description ?? ""} ${entry.authors ?? ""}`.toLowerCase().includes(word)));
  }, [catalog, query]);

  async function add(entry: GlobalConfigPluginCatalogEntry) {
    if (install) return;
    setNotice(null);
    setInstall({ entryId: entry.id, message: null, received: null, total: null });
    try {
      const events = await api.installPluginProvider({ expectedEpoch: epoch, type, entryId: entry.id }, { timeoutMilliseconds: 900000 });
      for await (const event of events) {
        if (!alive.current) return;
        if (event.state === "progress") setInstall({ entryId: entry.id, message: event.message, received: event.received, total: event.total });
        else if (event.state === "done" && event.key) {
          setAdded(current => new Map(current).set(entry.id, event.key!));
          setNotice({ text: t("{name} was added.", { name: entry.name }), intent: "success" });
          onAdded(event.key);
        } else setNotice({ text: t("{name} could not be added: {reason}", { name: entry.name, reason: event.message ?? event.status ?? "failed" }), intent: "danger" });
      }
    } catch {
      if (alive.current) setNotice({ text: t("{name} could not be added: {reason}", { name: entry.name, reason: "failed" }), intent: "danger" });
    } finally { if (alive.current) setInstall(null); }
  }

  const fetched = catalog?.fetchedAt ? new Date(catalog.fetchedAt).toLocaleDateString(locale) : null;
  // A click outside closes the dialog, unless an entry is being added.
  return <Dialog isOpen className="provider-catalog-dialog" title={catalog?.title ?? title} icon={<AppIcon name="plugin" size={16} />}
    canOutsideClickClose={!install} canEscapeKeyClose={!install} onClose={onClose}>
    <DialogBody className="provider-catalog-body">
      <div className="provider-catalog-toolbar">
        <InputGroup type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} value={query} autoFocus spellCheck={false}
          placeholder={t("Search the agents")} aria-label={t("Search the agents")} onChange={event => setQuery(event.target.value)} />
        <Button variant="minimal" icon={loading ? <ActivitySpinner size={14} /> : <AppIcon name="refresh" size={15} />} disabled={loading || !!install}
          aria-label={t("Reload")} title={t("Reload")} onClick={() => setRefresh(value => value + 1)} />
      </div>
      {catalog?.error && catalog.entries.length > 0 && <Callout compact intent="warning" role="status">
        {catalog.error}{fetched ? ` ${t("Showing the copy kept from {date}.", { date: fetched })}` : ""}</Callout>}
      {notice && <Callout compact intent={notice.intent} role={notice.intent === "danger" ? "alert" : "status"}>{notice.text}</Callout>}
      {!catalog || catalog.entries.length === 0
        ? <NonIdealState icon={loading ? <ActivitySpinner size={28} /> : <AppIcon name="plugin" size={32} />}
            title={t(loading ? "Loading the agents." : "The catalog could not be read.")}
            description={!loading && catalog?.error ? catalog.error : undefined}
            action={!loading ? <Button icon={<AppIcon name="refresh" size={14} />} onClick={() => setRefresh(value => value + 1)}>{t("Retry")}</Button> : undefined} />
        : entries.length === 0
          ? <NonIdealState icon={<AppIcon name="search" size={28} />} title={t("No agent matches.")} />
          : <ul className="provider-catalog-grid" aria-label={catalog.title ?? title}>
            {entries.map(entry => {
              const key = added.get(entry.id) ?? entry.installedKey;
              const installing = install?.entryId === entry.id;
              return <li key={entry.id} className="provider-catalog-card" data-entry={entry.id}>
                <div className="provider-catalog-card-head">
                  <span className="provider-catalog-logo"><Logo brand={providerBrand({ key: entry.id, name: entry.name, iconData: entry.iconData })} size={22} fallback="plugin" /></span>
                  <span className="provider-catalog-name"><strong>{entry.name}</strong>
                    <small>{[entry.version, entry.authors].filter(Boolean).join(" · ")}</small></span>
                  {entry.distribution && <Tag minimal round className="provider-catalog-distribution">{entry.distribution}</Tag>}
                </div>
                {entry.description && <p className="provider-catalog-description" title={entry.description}>{entry.description}</p>}
                {installing && install
                  ? <div className="provider-catalog-progress" role="status">
                      <small>{install.message ?? t("Adding…")}{install.total ? ` — ${t("{received} of {total}", { received: downloadSize(install.received), total: downloadSize(install.total) })}` : ""}</small>
                      <PaintedProgressBar value={install.total ? (install.received ?? 0) / install.total : undefined} />
                    </div>
                  : <div className="provider-catalog-actions">
                      {entry.unavailable && <Tag minimal intent="warning" className="provider-catalog-unavailable" title={entry.unavailable}>{entry.unavailable}</Tag>}
                      {entry.website && <a className="provider-catalog-link" href={entry.website} target="_blank" rel="noreferrer" title={entry.website}><AppIcon name="openExternal" size={13} /></a>}
                      <span className="provider-settings-spacer" />
                      {key
                        ? <><Tag minimal round intent="success" icon={<AppIcon name="check" size={12} />}>{t("Added")}</Tag>
                          <Button size="small" variant="minimal" onClick={() => onOpen(key)}>{t("Open")}</Button></>
                        : <Button size="small" intent="primary" icon={<AppIcon name="plus" size={13} />} disabled={!!install || !!entry.unavailable}
                          onClick={() => void add(entry)}>{t("Add")}</Button>}
                    </div>}
              </li>;
            })}
          </ul>}
    </DialogBody>
  </Dialog>;
}
