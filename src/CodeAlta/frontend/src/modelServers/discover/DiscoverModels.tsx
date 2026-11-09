import { useEffect, useMemo, useState } from "react";
import { Button, HTMLSelect, InputGroup, NonIdealState, Tag } from "@blueprintjs/core";
import { AppIcon } from "../../AppIcon";
import { ActivitySpinner } from "../../ActivitySpinner";
import { MarkdownContent } from "../../MarkdownContent";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import type { ModelServersApi } from "../api";
import { defaultBackend, formatBytes, modelName, newServer } from "../modelServers";
import type { HubModel, HubModelView, HubQuantView, ModelHostStatus, ModelServersMachine } from "../types";

/** What the canvas does through the window. */
export type DiscoverActions = Readonly<{
  /** Says how an action went, as a toast. */
  notify(message: string, intent: "success" | "danger"): void;
  /** Opens Settings > Fleet > Model servers. */
  openServers(): void;
}>;

const sorts: readonly Readonly<{ value: string; label: MessageKey }>[] = [
  { value: "trendingScore", label: "Trending" }, { value: "downloads", label: "Most downloaded" }, { value: "likes", label: "Most liked" }, { value: "lastModified", label: "Recently updated" },
];
const kinds: readonly Readonly<{ value: string; label: MessageKey }>[] = [
  { value: "", label: "All kinds" }, { value: "text-generation", label: "Text" }, { value: "image-text-to-text", label: "Text and images" },
  { value: "feature-extraction", label: "Embeddings" }, { value: "text-ranking", label: "Rerank" },
];
const publishers = ["unsloth", "bartowski", "ggml-org", "lmstudio-community", "Qwen", "mistralai", "google"];

/** A count as people read it: `1.2 M`. */
export function compactCount(value: number, locale: string): string {
  return new Intl.NumberFormat(locale, { notation: "compact", maximumFractionDigits: 1 }).format(value);
}

/**
 * Discover models: the GGUF models of Hugging Face, searched and sorted as the hub does, each with its card, its
 * quantizations and whether each fits the GPUs of this computer and of the machines it manages; a quantization downloads
 * to a machine, or is served there at once (downloaded first).
 */
export function DiscoverModels({ api, visible, actions }: { api: ModelServersApi | null; visible: boolean; actions: DiscoverActions }) {
  const { t, locale } = useShellLanguage();
  const [query, setQuery] = useState("");
  const [typed, setTyped] = useState("");
  const [sort, setSort] = useState("trendingScore");
  const [kind, setKind] = useState("");
  const [author, setAuthor] = useState("");
  const [models, setModels] = useState<readonly HubModel[] | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [chosen, setChosen] = useState<string | null>(null);
  const [machines, setMachines] = useState<readonly (ModelServersMachine & { status: ModelHostStatus | null })[]>([]);
  useEffect(() => {
    const timer = window.setTimeout(() => setQuery(typed.trim()), 350);
    return () => window.clearTimeout(timer);
  }, [typed]);
  useEffect(() => {
    if (!api || !visible) return;
    const controller = new AbortController();
    setFailed(null);
    api.hubSearch({ search: query || undefined, author: author || undefined, pipeline: kind || undefined, sort, limit: 40 }, controller.signal)
      .then(value => { if (!controller.signal.aborted) setModels(value); }, error => { if (!controller.signal.aborted) { setModels([]); setFailed(error instanceof Error ? error.message : String(error)); } });
    return () => controller.abort();
  }, [api, visible, query, sort, kind, author]);
  useEffect(() => {
    if (!api || !visible) return;
    const controller = new AbortController();
    void api.machines(controller.signal).then(async listed => {
      const usable = listed.filter(machine => machine.manages && (machine.local || machine.state === "online"));
      const read = await Promise.all(usable.map(async machine => ({ ...machine, status: await api.status(machine.id, controller.signal).catch(() => null) })));
      if (!controller.signal.aborted) setMachines(read.filter(machine => machine.status));
    }, () => { /* Without machines, the models are shown without their fit. */ });
    return () => controller.abort();
  }, [api, visible]);
  return <div className="discover-models">
    <header className="discover-models-bar">
      <InputGroup type="search" leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} placeholder={t("Search the GGUF models of Hugging Face")} aria-label={t("Search the GGUF models of Hugging Face")}
        value={typed} onValueChange={setTyped} className="discover-models-search" />
      <HTMLSelect value={sort} aria-label={t("Sort")} onChange={event => setSort(event.currentTarget.value)} options={sorts.map(item => ({ value: item.value, label: t(item.label) }))} />
      <HTMLSelect value={kind} aria-label={t("Kind")} onChange={event => setKind(event.currentTarget.value)} options={kinds.map(item => ({ value: item.value, label: t(item.label) }))} />
      <div className="discover-models-publishers" role="group" aria-label={t("Publisher")}>
        {publishers.map(name => <Tag key={name} interactive round minimal={author !== name} intent={author === name ? "primary" : "none"} onClick={() => setAuthor(value => value === name ? "" : name)}>{name}</Tag>)}
      </div>
      <Button variant="minimal" icon={<AppIcon name="server" size={14} />} text={t("Model servers")} onClick={actions.openServers} />
    </header>
    <div className={`discover-models-body${chosen ? " has-details" : ""}`}>
      <section className="discover-models-list" aria-label={t("Models")}>
        {!models && <NonIdealState icon={<ActivitySpinner size={24} />} title={t("Loading…")} />}
        {models && models.length === 0 && <NonIdealState icon={<AppIcon name="model" size={32} />} title={t(failed ? "Hugging Face does not answer." : "No model matches.")} description={failed ?? undefined} />}
        {models?.map(model => <button key={model.id} type="button" className={`discover-model-card${chosen === model.id ? " is-chosen" : ""}`} aria-pressed={chosen === model.id}
          onClick={() => setChosen(value => value === model.id ? null : model.id)}>
          <span className="discover-model-title"><strong>{modelName(model.id)}</strong><span className="muted-text">{model.author}</span></span>
          <span className="discover-model-facts muted-text">
            <span title={t("Downloads")}><AppIcon name="download" size={12} /> {compactCount(model.downloads, locale)}</span>
            <span title={t("Likes")}><AppIcon name="star" size={12} /> {compactCount(model.likes, locale)}</span>
            {model.lastModified && <span>{new Intl.DateTimeFormat(locale, { dateStyle: "medium" }).format(new Date(model.lastModified))}</span>}
          </span>
          <span className="discover-model-tags">
            {model.pipelineTag && <Tag minimal>{model.pipelineTag}</Tag>}
            {model.tags.includes("vision") || model.pipelineTag === "image-text-to-text" ? <Tag minimal intent="primary">{t("vision")}</Tag> : null}
            {model.gated && <Tag minimal intent="warning">{t("gated")}</Tag>}
          </span>
        </button>)}
      </section>
      {chosen && api && <ModelPage key={chosen} api={api} repo={chosen} machines={machines} actions={actions} onClose={() => setChosen(null)} />}
    </div>
  </div>;
}

function ModelPage({ api, repo, machines, actions, onClose }: {
  api: ModelServersApi; repo: string; machines: readonly (ModelServersMachine & { status: ModelHostStatus | null })[]; actions: DiscoverActions; onClose: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const [view, setView] = useState<HubModelView | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [machine, setMachine] = useState<string>("");
  const [working, setWorking] = useState<string | null>(null);
  const ids = useMemo(() => machines.map(item => item.id), [machines]);
  useEffect(() => {
    const controller = new AbortController();
    api.hubModel(repo, ids, controller.signal).then(value => { if (!controller.signal.aborted) setView(value); },
      error => { if (!controller.signal.aborted) setFailed(error instanceof Error ? error.message : String(error)); });
    return () => controller.abort();
  }, [api, repo, ids]);
  useEffect(() => { if (!machine && machines[0]) setMachine(machines[0].id); }, [machine, machines]);
  const target = machines.find(item => item.id === machine) ?? null;
  async function act(quant: HubQuantView, serve: boolean) {
    if (!target?.status) return;
    const model = `${repo}:${quant.tag}`;
    setWorking(quant.tag);
    try {
      if (serve) {
        const name = modelName(model).split(" · ")[0];
        const result = await api.act(target.id, { action: "setServer", server: newServer(name, defaultBackend(target.status), [model]) });
        // The sessions of this computer use it, as a server added from its page does by default.
        if (result.server) await api.useInSessions(target.id, result.server.definition.id, name, ["local"]).catch(() => []);
        actions.notify(t("{model} is served on {machine} once it is downloaded.", { model: modelName(model), machine: target.name }), "success");
      } else {
        await api.act(target.id, { action: "download", model });
        actions.notify(t("{model} downloads to {machine}.", { model: modelName(model), machine: target.name }), "success");
      }
    } catch (error) {
      actions.notify(error instanceof Error ? error.message : String(error), "danger");
    } finally {
      setWorking(null);
    }
  }
  const fitIntent = (fit: string) => fit === "gpu" || fit === "gpus" ? "success" : fit === "partly" ? "warning" : "danger";
  const fitLabel = (fit: string): MessageKey => fit === "gpu" ? "fits its GPU" : fit === "gpus" ? "fits its GPUs" : fit === "partly" ? "partly on the GPU" : "too large";
  return <section className="discover-model-page" aria-label={repo}>
    <header>
      <div><h2>{modelName(repo)}</h2><a className="muted-text" href={`https://huggingface.co/${repo}`} target="_blank" rel="noreferrer">{repo}</a></div>
      <Button variant="minimal" icon={<AppIcon name="close" size={14} />} aria-label={t("Close")} onClick={onClose} />
    </header>
    {!view && !failed && <ActivitySpinner size={20} />}
    {failed && <p className="model-servers-reason is-failed"><AppIcon name="error" size={14} />{failed}</p>}
    {view && <>
      <dl className="discover-model-facts-list">
        {view.parameters ? <div><dt>{t("Parameters")}</dt><dd>{compactCount(view.parameters, locale)}</dd></div> : null}
        {view.architecture && <div><dt>{t("Architecture")}</dt><dd>{view.architecture}</dd></div>}
        {view.contextLength ? <div><dt>{t("Context")}</dt><dd>{new Intl.NumberFormat(locale).format(view.contextLength)}</dd></div> : null}
        {view.license && <div><dt>{t("License")}</dt><dd>{view.license}</dd></div>}
        {view.baseModels.length > 0 && <div><dt>{t("Base model")}</dt><dd>{view.baseModels.join(", ")}</dd></div>}
      </dl>
      {machines.length > 0 ? <label className="discover-model-machine"><span>{t("For")}</span>
        <HTMLSelect value={machine} onChange={event => setMachine(event.currentTarget.value)}
          options={machines.map(item => ({ value: item.id, label: item.local ? `${item.name} (${t("this computer")})` : item.name }))} /></label>
        : <p className="muted-text">{t("No machine to download to: this computer runs no model host, and it manages no machine that does.")}</p>}
      <table className="model-servers-models-table discover-model-quants">
        <thead><tr><th>{t("Quantization")}</th><th>{t("Size")}</th><th>{t("Fit")}</th><th aria-label={t("Actions")} /></tr></thead>
        <tbody>{view.quantizations.map(quant => {
          const fit = quant.fits.find(item => item.machine === machine);
          return <tr key={quant.tag}>
            <td><strong>{quant.tag}</strong>{quant.files > 1 && <span className="muted-text"> · {t("{count} files", { count: quant.files })}</span>}</td>
            <td className="numeric">{formatBytes(quant.modelBytes + quant.mmprojBytes, locale)}</td>
            <td>{fit ? <Tag minimal intent={fitIntent(fit.fit.fit)} title={fit.fit.message}>{t(fitLabel(fit.fit.fit))}</Tag> : <span className="muted-text">—</span>}
              {fit?.onMachine && <Tag minimal intent="success" className="model-servers-vision">{t("on the disk")}</Tag>}</td>
            <td className="model-servers-model-actions">{target && <>
              <Button size="small" variant="minimal" icon={working === quant.tag ? <ActivitySpinner size={12} /> : <AppIcon name="download" size={13} />} disabled={!!working || fit?.onMachine}
                text={t("Download")} onClick={() => void act(quant, false)} />
              <Button size="small" variant="minimal" intent="primary" icon={<AppIcon name="server" size={13} />} disabled={!!working} text={t("Serve")} onClick={() => void act(quant, true)} />
            </>}</td>
          </tr>;
        })}</tbody>
      </table>
      {view.quantizations.length === 0 && <p className="muted-text">{t("This repository has no GGUF file.")}</p>}
      {view.card.trim() && <div className="discover-model-card-text"><MarkdownContent source={view.card} document /></div>}
    </>}
  </section>;
}
