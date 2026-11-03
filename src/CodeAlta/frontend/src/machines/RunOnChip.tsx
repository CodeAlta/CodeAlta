import { useCallback, useEffect, useState } from "react";
import { Button, HTMLSelect, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { machines, type MachineNewSessionChoices, type MachineView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ProviderIcon } from "../ProviderIcon";
import { useShellLanguage } from "../shellLanguage";
import type { RemoteSelection, RunOnTarget } from "./runOn";

type MachinesApi = Pick<typeof machines, "list" | "watch" | "newSessionChoices">;

/** The paired machines and their state, read again each time the host says one changed; empty while there is no host. */
export function useMachines(epoch: string | null | undefined, api: MachinesApi = machines): readonly MachineView[] {
  const [list, setList] = useState<readonly MachineView[]>([]);
  useEffect(() => {
    if (!epoch) { setList([]); return; }
    const abort = new AbortController();
    const read = () => void api.list({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 15_000 })
      .then(result => { if (!abort.signal.aborted && result.status === "ok") setList(result.machines); })
      .catch(() => { /* The next change reads them again. */ });
    read();
    void (async () => {
      try { for await (const _ of await api.watch({ expectedEpoch: epoch }, { signal: abort.signal })) if (!abort.signal.aborted) read(); }
      catch { /* The bridge is gone: the list stays as it was read. */ }
    })();
    return () => abort.abort();
  }, [api, epoch]);
  return list;
}

/**
 * What a new session on a machine can be given (its projects, its ready providers), read from the machine when the chip
 * names it, with the project of this computer the person is in; null while it is read or no machine is named.
 */
export function useRemoteChoices(epoch: string | null | undefined, machineId: string | null, localProjectId: string | null,
  api: MachinesApi = machines): Readonly<{ choices: MachineNewSessionChoices | null; reload: () => void }> {
  const [state, setState] = useState<{ key: string; choices: MachineNewSessionChoices } | null>(null);
  const [generation, setGeneration] = useState(0);
  const key = epoch && machineId ? JSON.stringify([epoch, machineId, localProjectId, generation]) : null;
  useEffect(() => {
    if (!epoch || !machineId || !key) return;
    const abort = new AbortController();
    void api.newSessionChoices({ expectedEpoch: epoch, machineId, localProjectId }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(choices => { if (!abort.signal.aborted) setState({ key, choices }); })
      .catch(() => { if (!abort.signal.aborted) setState({ key, choices: { status: "machine_offline", machineName: null, projects: [], suggestedProjectId: null, providers: [], message: null } }); });
    return () => abort.abort();
  }, [api, key]);
  const reload = useCallback(() => setGeneration(value => value + 1), []);
  return { choices: state && state.key === key ? state.choices : null, reload };
}

/**
 * The Run on chip of a new session, beside its agent chip: this computer or a paired machine that is online. Discreet:
 * shown only when a machine can take the session.
 */
export function RunOnChip({ targets, value, locked = false, disabled = false, onChange }: {
  targets: readonly RunOnTarget[]; value: string | null;
  /** The machine of the row of the Explorer the prompt is for: the chip names it and offers nothing else. */
  locked?: boolean; disabled?: boolean; onChange: (machineId: string | null) => void;
}) {
  const { t } = useShellLanguage();
  const shown = targets.find(target => target.id === value) ?? targets[0];
  const label = `${t("Run on")}: ${shown?.name ?? ""}`;
  const chip = <Button variant="minimal" className="composer-selection composer-run-on" data-remote={value ? "true" : undefined} disabled={disabled}
    aria-label={label} title={label} endIcon={locked ? undefined : <AppIcon name="chevronDown" size={14} />}>
    <span className="composer-selection-part"><AppIcon name={value ? "remote" : "computer"} size={14} /><span>{shown?.name}</span></span>
  </Button>;
  if (locked) return chip;
  const menu = <Menu aria-label={t("Run on")}>
    {targets.map(target => <MenuItem key={target.id ?? ""} text={target.name} active={target.id === value}
      icon={<AppIcon name={target.id ? "remote" : "computer"} size={14} />} onClick={() => onChange(target.id)} />)}
  </Menu>;
  return <PopoverNext content={menu} placement="top-start" disabled={disabled}>{chip}</PopoverNext>;
}

/**
 * The project, the provider and the model of a new session on a machine, among the ones that machine has: in place of the
 * provider and model of this computer.
 */
export function RemoteSessionChoice({ choices, selection, disabled = false, onChange, onRetry }: {
  choices: MachineNewSessionChoices | null; selection: RemoteSelection; disabled?: boolean;
  onChange: (selection: RemoteSelection) => void; onRetry: () => void;
}) {
  const { t } = useShellLanguage();
  if (!choices) return <Button variant="minimal" className="composer-selection remote-session-choice" disabled>
    <span className="composer-selection-part"><span>{t("Reading the machine…")}</span></span></Button>;
  if (choices.status !== "ok") return <Button variant="minimal" className="composer-selection remote-session-choice" icon={<AppIcon name="refresh" size={14} />}
    title={choices.message ?? undefined} onClick={onRetry}><span className="composer-selection-part"><span>{t("Read the machine again")}</span></span></Button>;
  const project = choices.projects.find(value => value.id === selection.projectId);
  const provider = choices.providers.find(value => value.key === selection.providerKey) ?? null;
  const model = provider?.models.find(value => value.id === selection.modelId) ?? null;
  const projectName = selection.projectId === null ? t("Chat") : project?.name ?? t("Choose a project");
  const fields = <div className="remote-session-fields">
    <label className="remote-session-field"><span>{t("Project on {name}", { name: choices.machineName ?? "" })}</span>
      <HTMLSelect fill aria-label={t("Project on {name}", { name: choices.machineName ?? "" })} value={selection.projectId === undefined ? "" : selection.projectId ?? "\u0000chat"}
        disabled={disabled} onChange={event => {
          const value = event.currentTarget.value;
          onChange({ ...selection, projectId: value === "" ? undefined : value === "\u0000chat" ? null : value });
        }}>
        {selection.projectId === undefined && <option value="">{t("Choose a project")}</option>}
        {choices.projects.map(value => <option key={value.id} value={value.id}>{value.name}</option>)}
        <option value={"\u0000chat"}>{t("Chat (no project)")}</option>
      </HTMLSelect></label>
    <label className="remote-session-field"><span>{t("Provider")}</span>
      <HTMLSelect fill aria-label={t("Provider")} value={selection.providerKey ?? ""} disabled={disabled || choices.providers.length === 0}
        onChange={event => onChange({ ...selection, providerKey: event.currentTarget.value, modelId: null })}>
        {choices.providers.length === 0 && <option value="">{t("No provider")}</option>}
        {choices.providers.map(value => <option key={value.key} value={value.key}>{value.isDefault ? t("{name} (default)", { name: value.name }) : value.name}</option>)}
      </HTMLSelect></label>
    <label className="remote-session-field"><span>{t("Model")}</span>
      <HTMLSelect fill aria-label={t("Model")} value={selection.modelId ?? ""} disabled={disabled || !provider?.models.length}
        onChange={event => onChange({ ...selection, modelId: event.currentTarget.value || null })}>
        {!provider?.models.length && <option value="">{t("The default of the provider")}</option>}
        {provider?.models.map(value => <option key={value.id} value={value.id}>{value.name}</option>)}
      </HTMLSelect></label>
  </div>;
  return <PopoverNext content={fields} placement="top-start" disabled={disabled}>
    <Button variant="minimal" className="composer-selection remote-session-choice" disabled={disabled} aria-label={t("Project and model on {name}", { name: choices.machineName ?? "" })}
      data-missing={selection.projectId === undefined ? "true" : undefined} endIcon={<AppIcon name="chevronDown" size={14} />}>
      <span className="composer-selection-part"><AppIcon name={selection.projectId === null ? "chat" : "folder"} size={14} /><span>{projectName}</span></span>
      <span className="composer-selection-part"><ProviderIcon providerKey={provider?.key ?? null} size={14} /><span>{model?.name ?? provider?.name ?? t("No provider")}</span></span>
    </Button>
  </PopoverNext>;
}
