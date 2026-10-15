import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { Button, Card, HTMLSelect, HTMLTable, InputGroup, NonIdealState, PopoverNext, Switch } from "@blueprintjs/core";
import type { speech as speechService, SpeechListingResponse, SpeechModelInfo } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { commandDefinitions } from "../commandRegistry";
import { KeyGesture } from "../KeyGesture";
import { settingsFailure } from "../settingsEditing";
import { SettingsField as Field } from "../SettingsField";
import { SettingsFileLocation } from "../SettingsFileLocation";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { storageSize } from "../storage/storage";
import { microphoneAvailable } from "./capture";
import { LevelBar, MicrophoneSelect } from "./MicrophoneChoice";
import type { Speech } from "./speech";
import { chosenModel, installProgress, installing, modelBytes, modelLanguages } from "./speechStore";
import { sendMode } from "./stopping";

/** The part of the host the page reads and writes; the download itself goes through the window's `speech`. */
export type SpeechSettingsApi = Pick<typeof speechService, "read" | "saveSettings" | "delete">;

// The choice that opens the field of an address: no preset and no address is written this way.
const custom = ":custom";
const shortcut = commandDefinitions.find(command => command.id === "dictate")?.keys?.[0];

/**
 * Settings page of speech to prompt: whether it is on, the model that hears, and the models in the cache. A
 * change is saved at once. A download belongs to the application: the page shows it and can start or stop it.
 */
export function SpeechSettings({ epoch, api, speech, testLimit = 30_000 }: {
  epoch: string | null; api: SpeechSettingsApi; speech: Speech;
  /** How long the test of a microphone keeps it open, in milliseconds. */
  testLimit?: number;
}) {
  const { t, locale } = useShellLanguage();
  const [microphone] = useState(microphoneAvailable);
  // A window without speech to prompt (one started on folders of its own) says so, and only that.
  const [off, setOff] = useState(false);
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor(options => api.read({ expectedEpoch: epoch }, options)
    .then(reply => { if (!options.signal.aborted) setOff(reply.status === "unavailable"); return reply; }), `${epoch}`);
  // The buttons of the composers follow what this page was told, without another read.
  useEffect(() => { if (listing) speech.setListing(listing); }, [speech, listing]);

  // What the download does, by the facts the page shows: a byte more renders nothing, a percent more does.
  const phase = useSyncExternalStore(speech.subscribe, () => speech.getState().install?.state ?? "idle");
  const installingId = useSyncExternalStore(speech.subscribe, () => { const install = speech.getState().install; return installing(install) ? install!.modelId : null; });
  const percent = useSyncExternalStore(speech.subscribe, () => { const progress = installProgress(speech.getState().install); return progress === null ? -1 : Math.floor(progress * 100); });
  // A download that ends changes what is in the cache.
  const seen = useRef(phase);
  useEffect(() => {
    if (seen.current === phase) return;
    seen.current = phase;
    if (phase === "done" || phase === "failed" || phase === "canceled") reload();
  }, [phase, reload]);

  const [customOpen, setCustomOpen] = useState(false);
  const [address, setAddress] = useState("");
  const [addressError, setAddressError] = useState(false);
  const [applying, setApplying] = useState(false);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  const size = (model: SpeechModelInfo) => modelBytes(model) > 0 ? storageSize(modelBytes(model), locale) : "";
  const languages = (model: SpeechModelInfo) => { const key = modelLanguages(model); return key ? t(key) : ""; };
  // What is not changed is sent as it is; a send mode of null keeps the one that is saved.
  const save = (change: Partial<{ enabled: boolean; model: string; send: string }>, current: SpeechListingResponse) => mutate(async () => {
    const reply = await api.saveSettings({ expectedEpoch: epoch, enabled: current.enabled, model: current.model, send: null, ...change }, { timeoutMilliseconds: 30_000 });
    return { status: reply.status === "invalid_request" ? "invalid" : reply.status, message: reply.error };
  }, "Saved.");
  // The address is checked by the host: what it refuses is said beside the field, and the field keeps what was typed.
  async function applyAddress(current: SpeechListingResponse) {
    const model = address.trim();
    if (!model || applying) return;
    setApplying(true); setNotice(null);
    try {
      const reply = await api.saveSettings({ expectedEpoch: epoch, enabled: current.enabled, model, send: null }, { timeoutMilliseconds: 30_000 });
      if (!alive.current) return;
      if (reply.status === "invalid_request") { setAddressError(true); return; }
      const failure = settingsFailure(reply.status, reply.error);
      if (failure) { setNotice(failure); return; }
      setCustomOpen(false); setAddress(""); setAddressError(false);
      setNotice({ key: "Saved.", intent: "success" });
      reload();
    } catch { if (alive.current) setNotice({ key: "The operation did not complete.", intent: "danger" }); }
    finally { if (alive.current) setApplying(false); }
  }
  const remove = (model: SpeechModelInfo) => void mutate(async () => {
    const reply = await api.delete({ expectedEpoch: epoch, model: model.id }, { timeoutMilliseconds: 30_000 });
    return { status: reply.status, message: reply.error };
  });

  const working = busy || applying;
  const chosen = chosenModel(listing);
  const installed = listing?.models.filter(model => model.installed) ?? [];
  return <SettingsPage className="speech-settings" label={t("Speech")} group="Personalization" title="Speech"
    description="Dictate a prompt with a speech model that runs on this computer." notice={off ? null : notice} loading={loading} busy={working} onReload={reload}>
    {!listing && off && !loading ? <NonIdealState icon={<AppIcon name="mic" size={36} />} title={t("Speech to prompt is off in a window started on its own folders.")} />
      : !listing ? <SettingsUnavailable loading={loading} icon="mic" title="Speech settings unavailable" />
      : !listing.supported ? <NonIdealState icon={<AppIcon name="mic" size={36} />} title={t("Speech to prompt is not available on this system.")} />
      : <div className="speech-layout">
        <Card className="appearance-settings speech-card">
          <Field label={t("Speech to prompt")} htmlFor="settings-speech-enabled">
            <Switch id="settings-speech-enabled" checked={listing.enabled} disabled={working}
              onChange={event => void save({ enabled: event.currentTarget.checked }, listing)} />
          </Field>
          <Field label={t("Model")} htmlFor="settings-speech-model" notice={<>
            {customOpen && <div className="settings-field-wide speech-custom">
              <InputGroup id="settings-speech-address" fill value={address} disabled={working} placeholder="https://huggingface.co/owner/model"
                aria-label={t("Hugging Face repository address")} aria-invalid={addressError} aria-describedby={addressError ? "settings-speech-address-error" : undefined}
                intent={addressError ? "danger" : "none"} spellCheck={false}
                onValueChange={value => { setAddress(value); setAddressError(false); }}
                onKeyDown={event => { if (event.key === "Enter" && !event.nativeEvent.isComposing) { event.preventDefault(); void applyAddress(listing); } }} />
              <Button text={t("Apply")} disabled={working || !address.trim()} loading={applying} onClick={() => void applyAddress(listing)} />
              {addressError && <p id="settings-speech-address-error" role="alert" className="speech-address-error">
                {t("Enter the address of a Hugging Face repository, such as https://huggingface.co/owner/model.")}</p>}
            </div>}
            {!customOpen && chosen && <ModelState model={chosen} phase={installingId === chosen.id ? phase : "idle"} percent={percent} size={size(chosen)}
              disabled={working} blocked={installingId !== null} onDownload={() => void speech.install(null)} onCancel={speech.cancelInstall} onDelete={() => remove(chosen)} />}
          </>}>
            <HTMLSelect id="settings-speech-model" value={customOpen ? custom : listing.model} disabled={working}
              onChange={event => {
                const value = event.currentTarget.value;
                setAddressError(false);
                if (value === custom) { setCustomOpen(true); return; }
                setCustomOpen(false);
                if (value !== listing.model) void save({ model: value }, listing);
              }}>
              {!listing.models.some(model => model.value === listing.model) && <option value={listing.model}>{listing.model}</option>}
              {listing.models.map(model => <option key={model.id} value={model.value}>{[model.name, languages(model), size(model)].filter(Boolean).join(" — ")}</option>)}
              <option value={custom}>{t("Custom…")}</option>
            </HTMLSelect>
          </Field>
          {listing.enabled && microphone && <MicrophoneRow speech={speech} testLimit={testLimit} />}
          <Field label={t("When a dictation stops")} htmlFor="settings-speech-send">
            <HTMLSelect id="settings-speech-send" value={sendMode(listing.send)} disabled={working}
              onChange={event => { const send = event.currentTarget.value; if (send !== sendMode(listing.send)) void save({ send }, listing); }}>
              <option value="manual">{t("Keep the prompt for me to send")}</option>
              <option value="on_stop">{t("Send the prompt")}</option>
            </HTMLSelect>
          </Field>
          {shortcut && <Field label={t("Shortcut")}><KeyGesture gesture={shortcut} /></Field>}
        </Card>
        <Card className="speech-card speech-models" role="region" aria-labelledby="settings-speech-models-title">
          <h2 id="settings-speech-models-title">{t("Downloaded models")}</h2>
          {installed.length === 0 ? <p className="speech-hint" role="status">{t("No model is downloaded.")}</p>
            : <HTMLTable className="speech-table" compact>
              <thead><tr><th scope="col">{t("Name")}</th><th scope="col">{t("Author and license")}</th><th scope="col">{t("Size")}</th><th /></tr></thead>
              <tbody>{installed.map(model => <tr key={model.id} data-speech-model={model.id}>
                <th scope="row">{model.name}</th>
                <td>{[model.author, model.license].filter(Boolean).join(" · ")}</td>
                <td className="speech-size">{size(model)}</td>
                <td className="speech-actions"><DeleteButton name={model.name} disabled={working} onDelete={() => remove(model)} /></td>
              </tr>)}</tbody>
            </HTMLTable>}
          {listing.rootPath && <SettingsFileLocation path={listing.rootPath} label={t("Stored in")} />}
        </Card>
      </div>}
  </SettingsPage>;
}

// Who asks for the level of a microphone from this page.
const testSource = "settings";

/**
 * The microphone a dictation listens to, and a test of it: while the test is on, the chosen microphone is open
 * and a bar shows what it hears, so the user sees which device hears them. A second click, leaving the page and
 * the limit of time stop it.
 */
function MicrophoneRow({ speech, testLimit }: { speech: Speech; testLimit: number }) {
  const { t } = useShellLanguage();
  const device = useSyncExternalStore(speech.subscribe, () => speech.getState().microphone.device);
  const testing = useSyncExternalStore(speech.subscribe, () => speech.getState().monitor?.key === testSource);
  const silent = useSyncExternalStore(speech.subscribe, () => { const monitor = speech.getState().monitor; return monitor?.key === testSource && monitor.silent; });
  // A browser names the devices only to a page that was given a microphone: one is opened for a moment to list them.
  useEffect(() => { void speech.listDevices(true); return speech.watchDevices(); }, [speech]);
  useEffect(() => () => speech.stopMonitor(testSource), [speech]);
  useEffect(() => {
    if (!testing) return;
    const timer = setTimeout(() => speech.stopMonitor(testSource), testLimit);
    return () => clearTimeout(timer);
  }, [speech, testing, testLimit]);
  return <Field label={t("Microphone")} htmlFor="settings-speech-microphone" notice={testing && <div className="settings-field-wide speech-test">
    <LevelBar speech={speech} source={testSource} />
    {silent && <p className="speech-silent" role="status">{t("No sound comes from this microphone.")}</p>}
  </div>}>
    <MicrophoneSelect speech={speech} id="settings-speech-microphone" value={device}
      onChange={choice => { speech.setMicrophone(choice); if (testing) speech.startMonitor(testSource, choice); }} />
    <Button text={t(testing ? "Stop" : "Test")} active={testing} aria-pressed={testing} data-speech-test
      onClick={() => { if (testing) speech.stopMonitor(testSource); else speech.startMonitor(testSource, device); }} />
  </Field>;
}

/** Where the chosen model stands, under its name: to download, being downloaded, or in the cache. */
function ModelState({ model, phase, percent, size, disabled, blocked, onDownload, onCancel, onDelete }: {
  model: SpeechModelInfo; phase: string; percent: number; size: string; disabled: boolean;
  /** Another download runs: one at a time. */
  blocked: boolean; onDownload: () => void; onCancel: () => void; onDelete: () => void;
}) {
  const { t } = useShellLanguage();
  const running = phase === "downloading" || phase === "installing";
  const text = phase === "installing" ? t("Installing…")
    : phase === "downloading" ? percent >= 0 ? t("Downloading… {percent}", { percent: `${percent}%` }) : t("Downloading…")
    : model.installed ? t("Downloaded · {size}", { size })
    : size ? t("Not downloaded · {size}", { size }) : t("Not downloaded");
  return <div className="settings-field-wide speech-model-state" data-speech-model-state={running ? phase : model.installed ? "installed" : "missing"}>
    <span role="status">{text}</span>
    {running ? <Button size="small" text={t("Cancel")} onClick={onCancel} />
      : model.installed ? <DeleteButton name={model.name} text disabled={disabled} onDelete={onDelete} />
      : <Button size="small" intent="primary" icon={<AppIcon name="arrowDown" size={14} />} text={t("Download")} disabled={disabled || blocked} onClick={onDownload} />}
  </div>;
}

/** The red button that removes a model from the cache, once its name is confirmed in a small popover beside it. */
function DeleteButton({ name, text = false, disabled, onDelete }: { name: string; text?: boolean; disabled?: boolean; onDelete: () => void }) {
  const { t } = useShellLanguage();
  return <PopoverNext placement="top-end" content={<div className="provider-settings-confirm"><p>{t("Delete {name}?", { name })}</p>
    <Button className="bp6-popover-dismiss" intent="danger" disabled={disabled} onClick={onDelete}>{t("Delete")}</Button></div>}>
    {text ? <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={disabled} text={t("Delete")} />
      : <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={disabled}
        aria-label={t("Delete {name}", { name })} title={t("Delete")} />}
  </PopoverNext>;
}
