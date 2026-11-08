import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Button, Callout, Checkbox, Dialog, DialogBody, DialogFooter, HTMLSelect, InputGroup, SegmentedControl, Switch, Tag, TextArea } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import type { ModelServersApi } from "./api";
import { backendName, defaultBackend, devicesOf, formatBytes, installedBackends, modelName, newServer, newServerModel, parseModelReference, type MachineStatus } from "./modelServers";
import type { RunAction } from "./ModelServersSettings";
import type { HubModelView, ServerDefinition, ServerModel, UpdatePolicy } from "./types";

/** The machines a dialog may change: those this computer manages, whose model host answered. */
function managedMachines(machines: readonly MachineStatus[]): MachineStatus[] {
  return machines.filter(entry => entry.machine.manages && entry.status);
}

function MachineField({ machines, value, onChange, disabled }: { machines: readonly MachineStatus[]; value: string; onChange: (value: string) => void; disabled?: boolean }) {
  const { t } = useShellLanguage();
  return <label><span>{t("Machine")}</span>
    <HTMLSelect value={value} disabled={disabled || machines.length < 2} onChange={event => onChange(event.currentTarget.value)}
      options={machines.map(entry => ({ value: entry.machine.id, label: entry.machine.local ? `${entry.machine.name} (${t("this computer")})` : entry.machine.name }))} /></label>;
}

function Problem({ problem }: { problem: Readonly<{ key: MessageKey; detail?: string | null }> | null }) {
  const { t } = useShellLanguage();
  return problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>;
}

/** The context a model asks, as typed: empty for what fits. */
function contextText(model: ServerModel): string {
  return model.context ? String(model.context) : "";
}

/** The problem of a server being written, or null when it can be saved. */
export function serverDraftProblem(draft: ServerDefinition): MessageKey | null {
  if (!draft.name.trim()) return "Give the server a name.";
  if (draft.models.length === 0) return "Add a model to the server.";
  if (draft.models.some(model => !parseModelReference(model.model)?.tag)) return "Name each model as publisher/repository:QUANT.";
  if (draft.kind === "one" && draft.models.length > 1) return "A server of one model serves one model.";
  if (draft.port !== 0 && (draft.port < 1024 || draft.port > 65535)) return "Choose a port between 1024 and 65535.";
  return null;
}

/**
 * Adds or changes a model server: its machine, its name, whether it loads several models on demand or keeps one loaded, its
 * models (on the disk, or downloaded when saved), and, under More settings, its GPUs, its build, its port and who reaches it.
 * A new server for chats is offered to the sessions of this computer once saved.
 */
export function ServerDialog({ api, machines, machine: given, server, model, run, onClose }: {
  api: ModelServersApi; machines: readonly MachineStatus[]; machine: string | null; server: ServerDefinition | null; model: string | null; run: RunAction; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const managed = managedMachines(machines);
  const [machine, setMachine] = useState(given ?? managed[0]?.machine.id ?? "");
  const entry = managed.find(item => item.machine.id === machine) ?? null;
  const status = entry?.status ?? null;
  const editing = !!server?.id;
  const [draft, setDraft] = useState<ServerDefinition>(() => server ?? newServer(model ? modelName(model).split(" · ")[0] : "", status ? defaultBackend(status) : "cpu", model ? [model] : []));
  const [adding, setAdding] = useState("");
  const [more, setMore] = useState(false);
  const [useIn, setUseIn] = useState<readonly string[]>(() => editing ? [] : ["local"]);
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const update = (change: Partial<ServerDefinition>) => setDraft(value => ({ ...value, ...change }));
  const updateModel = (index: number, change: Partial<ServerModel>) => setDraft(value => ({ ...value, models: value.models.map((item, at) => at === index ? { ...item, ...change } : item) }));
  // A new server follows the machine chosen: its best build.
  useEffect(() => {
    if (!editing && status) setDraft(value => ({ ...value, backend: installedBackends(status).includes(value.backend) ? value.backend : defaultBackend(status) }));
  }, [editing, status]);
  const stored = status?.models ?? [];
  const backends = status ? [...new Set([...installedBackends(status), ...status.candidates.filter(candidate => candidate.verdict === "fits").map(candidate => candidate.backend)])] : [];
  const devices = status ? devicesOf(status, draft.backend) : [];
  const invalid = serverDraftProblem(draft);
  const missing = draft.models.filter(item => !stored.some(stored => stored.id.toLowerCase() === item.model.toLowerCase()));
  const notInstalled = !!status && !installedBackends(status).includes(draft.backend);
  const addModel = (id: string) => {
    const reference = parseModelReference(id);
    if (!reference?.tag) { setProblem({ key: "Name each model as publisher/repository:QUANT." }); return; }
    const full = `${reference.repo}:${reference.tag}`;
    if (draft.models.some(item => item.model.toLowerCase() === full.toLowerCase())) return;
    setProblem(null);
    setDraft(value => ({ ...value, name: value.name || modelName(full).split(" · ")[0], models: [...value.models, newServerModel(full)] }));
    setAdding("");
  };
  async function save() {
    if (invalid || !entry) return;
    setWorking(true);
    setProblem(null);
    const definition: ServerDefinition = { ...draft, name: draft.name.trim(), models: draft.kind === "one" ? draft.models.map(item => ({ ...item, loadOnStart: true })) : draft.models };
    const result = await run(machine, { action: "setServer", server: definition }, editing ? "The server was saved." : "The server was added.");
    if (!result?.server) { setWorking(false); return; }
    if (useIn.length > 0 && definition.purpose === "chat") {
      try {
        const answers = await api.useInSessions(machine, result.server.definition.id, definition.name, useIn);
        const failed = answers.find(answer => answer.problem);
        if (failed) { setProblem({ key: "The server was added, but the sessions do not use it yet.", detail: failed.problem }); setWorking(false); return; }
      } catch (error) {
        setProblem({ key: "The server was added, but the sessions do not use it yet.", detail: error instanceof Error ? error.message : null });
        setWorking(false);
        return;
      }
    }
    setWorking(false);
    onClose();
  }
  const useChoices = entry ? [{ id: "local", label: managed.find(item => item.machine.local)?.machine.name ?? t("This computer") },
    ...(entry.machine.local ? [] : [{ id: entry.machine.id, label: entry.machine.name }])] : [];
  return <Dialog isOpen className="model-server-dialog" title={t(editing ? "Change {name}" : "Add a model server", { name: server?.name ?? "" })} canOutsideClickClose={false}
    onClose={() => { if (!working) onClose(); }}>
    <DialogBody>
      <form className="fleet-server-form model-server-form" onSubmit={event => { event.preventDefault(); void save(); }}>
        <div className="model-server-row">
          <MachineField machines={managed} value={machine} disabled={editing || working} onChange={setMachine} />
          <label><span>{t("Name")}</span><InputGroup value={draft.name} disabled={working} placeholder={t("Qwen on the GPUs")} onChange={event => update({ name: event.target.value })} autoFocus /></label>
        </div>
        <div className="model-server-kind" role="group" aria-label={t("Kind")}>
          <SegmentedControl small value={draft.kind} disabled={working} onValueChange={value => update({ kind: value === "one" ? "one" : "several" })}
            options={[{ value: "several", label: t("Several models, loaded on demand") }, { value: "one", label: t("One model, always loaded") }]} />
          <span className="muted-text">{t(draft.kind === "several" ? "It loads a model at its first request, and frees the GPU of the one used least when it needs room."
            : "It loads its model when it starts and keeps it: the fastest answers, the GPU always taken.")}</span>
        </div>
        <div className="model-server-models" role="group" aria-label={t("Models")}><span>{t("Models")}</span>
          {draft.models.length === 0 && <span className="muted-text">{t("No model yet.")}</span>}
          {draft.models.map((item, index) => {
            const on = stored.find(value => value.id.toLowerCase() === item.model.toLowerCase());
            return <div key={item.model} className="model-server-model">
              <span className="model-server-model-name"><strong>{modelName(item.model)}</strong>
                <span className="muted-text">{on ? formatBytes(on.sizeBytes) : t("downloaded when saved")}</span></span>
              <InputGroup size="small" className="model-server-context" value={contextText(item)} disabled={working} placeholder={t("Context: what fits")} aria-label={t("Context")}
                inputMode="numeric" onChange={event => { const value = Number.parseInt(event.target.value.replace(/\D/gu, ""), 10); updateModel(index, { context: Number.isFinite(value) && value > 0 ? value : undefined }); }} />
              <HTMLSelect minimal value={String(item.parallel ?? 0)} disabled={working} aria-label={t("Requests at once")}
                options={[{ value: "0", label: t("Requests: auto") }, ...[1, 2, 4, 8].map(count => ({ value: String(count), label: t("{count} at once", { count }) }))]}
                onChange={event => { const value = Number(event.currentTarget.value); updateModel(index, { parallel: value > 0 ? value : undefined }); }} />
              {draft.kind === "several" && <Checkbox className="model-server-preload" checked={item.loadOnStart} disabled={working} label={t("Load at start")}
                onChange={event => updateModel(index, { loadOnStart: event.currentTarget.checked })} />}
              <Button size="small" variant="minimal" icon={<AppIcon name="close" size={13} />} aria-label={t("Remove {name}", { name: modelName(item.model) })} disabled={working}
                onClick={() => setDraft(value => ({ ...value, models: value.models.filter((_, at) => at !== index) }))} />
            </div>;
          })}
          {(draft.kind === "several" || draft.models.length === 0) && <div className="model-server-add">
            <HTMLSelect value="" disabled={working || stored.length === 0} aria-label={t("A model on the disk")} onChange={event => { if (event.currentTarget.value) addModel(event.currentTarget.value); }}
              options={[{ value: "", label: t(stored.length === 0 ? "No model on the disk" : "Add a model on the disk…") },
                ...stored.filter(value => !draft.models.some(item => item.model.toLowerCase() === value.id.toLowerCase())).map(value => ({ value: value.id, label: `${modelName(value.id)} · ${formatBytes(value.sizeBytes)}` }))]} />
            <InputGroup size="small" value={adding} disabled={working} placeholder="unsloth/Qwen3-8B-GGUF:Q4_K_M" aria-label={t("Another model, from Hugging Face")} spellCheck={false}
              onChange={event => setAdding(event.target.value)} onKeyDown={event => { if (event.key === "Enter" && adding.trim()) { event.preventDefault(); addModel(adding); } }}
              rightElement={<Button size="small" variant="minimal" disabled={!adding.trim() || working} text={t("Add")} onClick={() => addModel(adding)} />} />
          </div>}
        </div>
        {draft.purpose === "chat" && !editing && useChoices.length > 0 && <div className="model-server-use" role="group" aria-label={t("Use in the sessions of")}><span>{t("Use in the sessions of")}</span>
          {useChoices.map(choice => <Checkbox key={choice.id} inline checked={useIn.includes(choice.id)} disabled={working} label={choice.label}
            onChange={event => setUseIn(value => event.currentTarget.checked ? [...value, choice.id] : value.filter(item => item !== choice.id))} />)}</div>}
        <Button variant="minimal" size="small" className="model-server-more" icon={<AppIcon name={more ? "chevronDown" : "chevronRight"} size={13} />} text={t("More settings")}
          aria-expanded={more} onClick={() => setMore(value => !value)} />
        {more && <div className="model-server-advanced">
          <div className="model-server-row">
            <label><span>{t("Build")}</span><HTMLSelect value={draft.backend} disabled={working} onChange={event => update({ backend: event.currentTarget.value, devices: [] })}
              options={backends.map(backend => ({ value: backend, label: `${backendName(backend)}${status && !installedBackends(status).includes(backend) ? ` (${t("not installed")})` : ""}` }))} /></label>
            <label><span>{t("Purpose")}</span><HTMLSelect value={draft.purpose} disabled={working} onChange={event => update({ purpose: event.currentTarget.value as ServerDefinition["purpose"] })}
              options={[{ value: "chat", label: t("Chats") }, { value: "embedding", label: t("Embeddings") }, { value: "rerank", label: t("Rerank") }]} /></label>
            <label><span>{t("Port")}</span><InputGroup value={draft.port ? String(draft.port) : ""} placeholder={t("The first free one")} disabled={working} inputMode="numeric"
              onChange={event => update({ port: Number.parseInt(event.target.value.replace(/\D/gu, ""), 10) || 0 })} /></label>
          </div>
          {devices.length > 0 && <div className="model-server-devices" role="group" aria-label={t("GPUs")}><span>{t("GPUs")}</span>
            {devices.map(device => <Checkbox key={device.name} inline disabled={working} checked={draft.devices.length === 0 || draft.devices.includes(device.name)}
              labelElement={<span>{device.description} <span className="muted-text">{formatBytes(device.totalMiB * 1024 * 1024)}</span></span>}
              onChange={event => {
                const all = devices.map(item => item.name);
                const chosen = (draft.devices.length === 0 ? all : draft.devices).filter(name => name !== device.name).concat(event.currentTarget.checked ? [device.name] : []);
                update({ devices: chosen.length === all.length ? [] : all.filter(name => chosen.includes(name)) });
              }} />)}</div>}
          <div className="model-server-row">
            {draft.kind === "several" && <label><span>{t("Models loaded at once")}</span><HTMLSelect value={String(draft.loadedAtOnce)} disabled={working}
              options={[1, 2, 3, 4].map(count => ({ value: String(count), label: String(count) }))} onChange={event => update({ loadedAtOnce: Number(event.currentTarget.value) })} /></label>}
            <label><span>{t("Reached by")}</span><HTMLSelect value={draft.access} disabled={working} onChange={event => update({ access: event.currentTarget.value === "this-machine" ? "this-machine" : "my-computers" })}
              options={[{ value: "my-computers", label: t("This machine and your computers") }, { value: "this-machine", label: t("This machine only") }]} /></label>
          </div>
          <Switch checked={draft.keepRunning} disabled={working} label={t("Keep running when CodeAlta exits")} onChange={event => update({ keepRunning: event.currentTarget.checked })} />
          <Switch checked={draft.chatPage} disabled={working} label={t("Its chat page")} onChange={event => update({ chatPage: event.currentTarget.checked })} />
          <label><span>{t("Other llama-server options")}</span><InputGroup value={draft.extraArguments.join(" ")} disabled={working} placeholder="--jinja --swa-full" spellCheck={false}
            onChange={event => update({ extraArguments: event.target.value.split(/\s+/u).filter(Boolean) })} /></label>
        </div>}
        <button type="submit" hidden />
      </form>
      {notInstalled && <Callout compact intent="primary" icon={<AppIcon name="download" size={14} />}>{t("Its build of llama.cpp ({backend}) is installed first.", { backend: backendName(draft.backend) })}</Callout>}
      {missing.length > 0 && <Callout compact intent="primary" icon={<AppIcon name="download" size={14} />}>
        {t("{count} models are downloaded first; the server starts once they are on the disk.", { count: missing.length })}</Callout>}
      <Problem problem={problem ?? (invalid && draft.models.length > 0 ? { key: invalid } : null)} />
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={onClose} />
      <Button intent="primary" disabled={!!invalid || working || !entry} icon={working ? <ActivitySpinner size={14} /> : undefined} text={t(editing ? "Save" : "Add")} onClick={() => void save()} />
    </>} />
  </Dialog>;
}

/**
 * Downloads a model to a machine: a repository of Hugging Face (its name or its address), then one of its quantizations,
 * each with whether it fits the GPUs of that machine.
 */
export function DownloadDialog({ api, machines, machine: given, model, run, onClose }: {
  api: ModelServersApi; machines: readonly MachineStatus[]; machine: string | null; model: string | null; run: RunAction; onClose: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const managed = managedMachines(machines);
  const [machine, setMachine] = useState(given ?? managed[0]?.machine.id ?? "");
  const [text, setText] = useState(model ?? "");
  const reference = useMemo(() => parseModelReference(text), [text]);
  const [details, setDetails] = useState<HubModelView | null>(null);
  const [reading, setReading] = useState(false);
  const [tag, setTag] = useState<string | null>(null);
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const repo = reference?.repo ?? null;
  useEffect(() => {
    if (!repo) { setDetails(null); return; }
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      setReading(true);
      setProblem(null);
      api.hubModel(repo, [machine], controller.signal).then(value => {
        setDetails(value);
        setTag(current => current && value.quantizations.some(quant => quant.tag === current) ? current : null);
      }, error => {
        if (controller.signal.aborted) return;
        setDetails(null);
        setProblem({ key: "This model cannot be read on Hugging Face.", detail: error instanceof Error ? error.message : null });
      }).finally(() => { if (!controller.signal.aborted) setReading(false); });
    }, 400);
    return () => { controller.abort(); window.clearTimeout(timer); };
  }, [api, repo, machine]);
  useEffect(() => { if (reference?.tag) setTag(reference.tag); }, [reference?.tag]);
  const chosen = tag ?? (details && details.quantizations.length === 1 ? details.quantizations[0].tag : null);
  const quants = details?.quantizations ?? [];
  async function download() {
    if (!repo || !chosen) return;
    setWorking(true);
    const result = await run(machine, { action: "download", model: `${repo}:${chosen}` }, "The download started.");
    setWorking(false);
    if (result) onClose();
  }
  const fitIntent = (fit: string) => fit === "gpu" || fit === "gpus" ? "success" : fit === "partly" ? "warning" : "danger";
  return <Dialog isOpen className="model-download-dialog" title={t("Download a model")} canOutsideClickClose={!working} onClose={() => { if (!working) onClose(); }}>
    <DialogBody>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void download(); }}>
        <MachineField machines={managed} value={machine} disabled={working} onChange={setMachine} />
        <label><span>{t("Model")}</span><InputGroup value={text} disabled={working} autoFocus spellCheck={false} placeholder="unsloth/Qwen3-8B-GGUF"
          rightElement={reading ? <ActivitySpinner size={14} /> : undefined} onChange={event => setText(event.target.value)} /></label>
        {!repo && text.trim() && <span className="muted-text">{t("A repository of Hugging Face: publisher/name, or its address.")}</span>}
        {details && <div className="model-download-quants" role="radiogroup" aria-label={t("Quantization")}>
          <span>{t("Quantization")}{details.parameters ? <span className="muted-text"> · {t("{count} B parameters", { count: Math.round(details.parameters / 1e8) / 10 })}</span> : null}</span>
          {quants.length === 0 && <span className="muted-text">{t("This repository has no GGUF file.")}</span>}
          {quants.map(quant => {
            const fit = quant.fits.find(item => item.machine === machine) ?? quant.fits[0];
            return <button key={quant.tag} type="button" role="radio" aria-checked={chosen === quant.tag} className={`model-download-quant${chosen === quant.tag ? " is-chosen" : ""}`}
              disabled={working} onClick={() => setTag(quant.tag)}>
              <strong>{quant.tag}</strong>
              <span className="muted-text">{formatBytes(quant.modelBytes + quant.mmprojBytes, locale)}</span>
              {fit && <Tag minimal intent={fitIntent(fit.fit.fit)} title={fit.fit.message}>{t(fit.fit.fit === "gpu" ? "fits its GPU" : fit.fit.fit === "gpus" ? "fits its GPUs"
                : fit.fit.fit === "partly" ? "partly on the GPU" : "too large")}</Tag>}
              {fit?.onMachine && <Tag minimal intent="success">{t("on the disk")}</Tag>}
            </button>;
          })}
        </div>}
        <button type="submit" hidden />
      </form>
      <Problem problem={problem} />
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={onClose} />
      <Button intent="primary" icon={working ? <ActivitySpinner size={14} /> : <AppIcon name="download" size={14} />} disabled={!repo || !chosen || working} text={t("Download")}
        onClick={() => void download()} />
    </>} />
  </Dialog>;
}

/** Makes a server of the command line of a llama-server a person runs: it opens as a new server, to check before it is saved. */
export function ImportDialog({ machines, machine: given, run, onImported, onClose }: {
  machines: readonly MachineStatus[]; machine: string | null; run: RunAction; onImported: (machine: string, server: ServerDefinition) => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const managed = managedMachines(machines);
  const [machine, setMachine] = useState(given ?? managed[0]?.machine.id ?? "");
  const unmanaged = managed.find(entry => entry.machine.id === machine)?.status?.unmanaged ?? [];
  const [text, setText] = useState(() => unmanaged[0]?.commandLine ?? "");
  const [name, setName] = useState("");
  const [working, setWorking] = useState(false);
  const [notes, setNotes] = useState<readonly string[]>([]);
  async function read() {
    setWorking(true);
    const result = await run(machine, { action: "import", text, name: name.trim() || undefined });
    setWorking(false);
    if (!result?.imported) return;
    if (result.notes && result.notes.length > 0 && notes.length === 0) { setNotes(result.notes); return; }
    onImported(machine, result.imported);
  }
  return <Dialog isOpen className="model-import-dialog" title={t("Import a command line")} canOutsideClickClose={false} onClose={() => { if (!working) onClose(); }}>
    <DialogBody>
      <p className="muted-text">{t("Paste the command that starts your llama-server: its models, GPUs and options become a server that CodeAlta runs and updates.")}</p>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void read(); }}>
        <MachineField machines={managed} value={machine} disabled={working} onChange={setMachine} />
        {unmanaged.length > 0 && <label><span>{t("Running on this machine")}</span>
          <HTMLSelect value="" onChange={event => { if (event.currentTarget.value) setText(event.currentTarget.value); }}
            options={[{ value: "", label: t("Choose one…") }, ...unmanaged.map(item => ({ value: item.commandLine, label: `${t("port {port}", { port: item.port ?? "?" })} · ${item.commandLine.slice(0, 80)}` }))]} /></label>}
        <label><span>{t("Command line")}</span><TextArea value={text} disabled={working} fill autoResize spellCheck={false} className="model-import-text"
          placeholder="llama-server -hf unsloth/Qwen3-8B-GGUF:Q4_K_M -c 32768 --port 8080" onChange={event => { setText(event.target.value); setNotes([]); }} /></label>
        <label><span>{t("Name")}</span><InputGroup value={name} disabled={working} placeholder={t("From its model")} onChange={event => setName(event.target.value)} /></label>
        <button type="submit" hidden />
      </form>
      {notes.length > 0 && <Callout compact intent="warning" title={t("These options are left out:")}><ul>{notes.map(note => <li key={note}>{note}</li>)}</ul></Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={onClose} />
      <Button intent="primary" disabled={!text.trim() || working} icon={working ? <ActivitySpinner size={14} /> : undefined} text={t(notes.length > 0 ? "Continue" : "Import")} onClick={() => void read()} />
    </>} />
  </Dialog>;
}

const days = ["monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday"] as const;
const dayLabels: Readonly<Record<string, MessageKey>> = { monday: "Monday", tuesday: "Tuesday", wednesday: "Wednesday", thursday: "Thursday", friday: "Friday", saturday: "Saturday", sunday: "Sunday" };

/**
 * The update policy of llama.cpp on one machine or several: which builds (stable ones, every build, or one kept), and when
 * (when the person clicks Update, every day or every week at a time of the machine), with the models too.
 */
export function PolicyDialog({ machines, run, onClose }: { machines: readonly MachineStatus[]; run: RunAction; onClose: () => void }) {
  const { t } = useShellLanguage();
  const first = machines.find(entry => entry.status)?.status;
  const [policy, setPolicy] = useState<UpdatePolicy>(() => first?.settings.updates ?? { channel: "stable", schedule: "manual", time: "04:00", day: "sunday", updateModels: false, allowLemonadeRocm: false });
  const [working, setWorking] = useState(false);
  const update = (change: Partial<UpdatePolicy>) => setPolicy(value => ({ ...value, ...change }));
  const invalid: MessageKey | null = policy.channel === "pinned" && !/^b\d+$/u.test(policy.pinnedBuild ?? "") ? "Name the build to keep, such as b12110." : null;
  async function save() {
    if (invalid) return;
    setWorking(true);
    for (const entry of machines) {
      if (!entry.status) continue;
      const result = await run(entry.machine.id, { action: "setSettings", settings: { ...entry.status.settings, updates: policy } }, "The update policy was saved.");
      if (!result) { setWorking(false); return; }
    }
    setWorking(false);
    onClose();
  }
  return <Dialog isOpen className="model-policy-dialog" title={machines.length === 1 ? t("Update policy of {name}", { name: machines[0].machine.name }) : t("Update policy of {count} machines", { count: machines.length })}
    canOutsideClickClose={false} onClose={() => { if (!working) onClose(); }}>
    <DialogBody>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void save(); }}>
        <label><span>{t("Builds")}</span><HTMLSelect value={policy.channel} disabled={working} onChange={event => update({ channel: event.currentTarget.value as UpdatePolicy["channel"] })}
          options={[{ value: "stable", label: t("Stable: the release llama.cpp marks as latest") }, { value: "every", label: t("Every build: the newest, a few each day") },
            { value: "pinned", label: t("This build: stay on one") }]} /></label>
        {policy.channel === "pinned" && <label><span>{t("Build")}</span><InputGroup value={policy.pinnedBuild ?? ""} disabled={working} placeholder="b12110" spellCheck={false}
          onChange={event => update({ pinnedBuild: event.target.value.trim() })} /></label>}
        <label><span>{t("When")}</span><HTMLSelect value={policy.schedule} disabled={working} onChange={event => update({ schedule: event.currentTarget.value as UpdatePolicy["schedule"] })}
          options={[{ value: "manual", label: t("When I click Update") }, { value: "daily", label: t("Every day") }, { value: "weekly", label: t("Every week") }]} /></label>
        {policy.schedule !== "manual" && <div className="model-server-row">
          {policy.schedule === "weekly" && <label><span>{t("Day")}</span><HTMLSelect value={policy.day} disabled={working} onChange={event => update({ day: event.currentTarget.value })}
            options={days.map(day => ({ value: day, label: t(dayLabels[day]) }))} /></label>}
          <label><span>{t("Time of the machine")}</span><input className="bp6-input" type="time" value={policy.time} disabled={working} onChange={event => update({ time: event.target.value || "04:00" })} /></label>
        </div>}
        <Checkbox checked={policy.updateModels} disabled={working} label={t("Update the models too, when their repository changed")} onChange={event => update({ updateModels: event.currentTarget.checked })} />
        <p className="muted-text">{t("An update waits for the servers to be idle, tests each one with the new build, and goes back to the previous build when a test fails.")}</p>
        <button type="submit" hidden />
      </form>
      <Problem problem={invalid ? { key: invalid } : null} />
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={onClose} />
      <Button intent="primary" disabled={!!invalid || working} icon={working ? <ActivitySpinner size={14} /> : undefined} text={t("Save")} onClick={() => void save()} />
    </>} />
  </Dialog>;
}

/** The local time of an hour from now, as a `datetime-local` field reads it. */
function inAnHour(): string {
  const date = new Date(Date.now() + 3_600_000);
  date.setMinutes(0, 0, 0);
  const pad = (value: number) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** Plans one update of llama.cpp at a time: each machine keeps it and runs it then, even while this computer is off. */
export function UpdateAtDialog({ machines, run, onClose }: { machines: readonly MachineStatus[]; run: RunAction; onClose: () => void }) {
  const { t } = useShellLanguage();
  const [at, setAt] = useState(inAnHour);
  const [models, setModels] = useState(false);
  const [working, setWorking] = useState(false);
  const when = new Date(at);
  const invalid: MessageKey | null = Number.isNaN(when.getTime()) ? "Choose a time." : when.getTime() < Date.now() ? "Choose a time to come." : null;
  async function plan() {
    if (invalid) return;
    setWorking(true);
    for (const entry of machines) {
      const result = await run(entry.machine.id, { action: "planUpdate", at: when.toISOString(), models }, "The update is planned.");
      if (!result) { setWorking(false); return; }
    }
    setWorking(false);
    onClose();
  }
  const names: ReactNode = machines.map(entry => entry.machine.name).join(", ");
  return <Dialog isOpen className="model-update-at-dialog" title={t("Update llama.cpp at a time")} canOutsideClickClose={false} onClose={() => { if (!working) onClose(); }}>
    <DialogBody>
      <p className="muted-text">{names}</p>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void plan(); }}>
        <label><span>{t("Time")}</span><input className="bp6-input" type="datetime-local" value={at} disabled={working} onChange={event => setAt(event.target.value)} /></label>
        <Checkbox checked={models} disabled={working} label={t("Update the models too")} onChange={event => setModels(event.currentTarget.checked)} />
        <p className="muted-text">{t("Each machine keeps the time and updates then, even when this computer is off.")}</p>
        <button type="submit" hidden />
      </form>
      <Problem problem={invalid ? { key: invalid } : null} />
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={onClose} />
      <Button intent="primary" disabled={!!invalid || working} icon={working ? <ActivitySpinner size={14} /> : <AppIcon name="timer" size={14} />} text={t("Plan")} onClick={() => void plan()} />
    </>} />
  </Dialog>;
}
