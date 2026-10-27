import { Button, Card, HTMLSelect, Tag } from "@blueprintjs/core";
import { globalConfig, type GlobalConfigCodeModeScopeItem } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { codeModeLabels } from "./CodeModeScopeControls";
import type { MessageKey } from "./localization";
import { SettingsField as Field } from "./SettingsField";
import { SettingsFileLocations } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "./SettingsPage";
import { useShellLanguage } from "./shellLanguage";

// The modes this version shows, with their help; a mode the host knows and this list does not is shown by its name.
const modes: readonly { value: string; label: MessageKey; help: MessageKey }[] = [
  { value: "off", label: "Off", help: "Agents call their tools one at a time, as usual." },
  { value: "on", label: "On", help: "Agents also get exec, which runs JavaScript that calls their tools in loops and in parallel, and wait, for a script that goes on in the background." },
  { value: "only", label: "Only", help: "Agents get exec and wait in place of their tools, which they call from scripts; they still look at images and ask you questions directly." },
];

/**
 * Settings page of Code Mode: whether the sessions get the `exec` and `wait` tools (On when the file says nothing), and
 * the providers and models that choose a mode of their own, each a link to the editor of its provider. The choice is
 * saved at once in the configuration file of the user; a session takes it with its next prompt.
 */
export function CodeModeSettings({ epoch, api = globalConfig, onOpenFile, filesApi, onOpenScope }: {
  epoch: string | null;
  api?: Pick<typeof globalConfig, "codeModeSettings" | "saveCodeModeSettings">;
  /** Called once the code editor was asked to show the configuration file: the window leaves Settings. */
  onOpenFile?: () => void;
  /** Says where the configuration file is and opens it; the host by default. */
  filesApi?: SettingsFilesApi;
  /** Opens the editor of the provider where a scope chooses its mode. */
  onOpenScope?: (scope: GlobalConfigCodeModeScopeItem) => void;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor(
    options => api.codeModeSettings({ expectedEpoch: epoch }, options), `${epoch}`);
  const files = useSettingsFiles({ page: "config", epoch, projectId: null, revision: listing, onOpened: onOpenFile, setNotice, api: filesApi });
  const known = modes.find(mode => mode.value === listing?.mode);
  const label = (mode: string) => mode in codeModeLabels ? t(codeModeLabels[mode]) : mode;

  return <SettingsPage className="code-mode-settings" label={t("Code Mode")} group="Agent & models" title="Code Mode"
    description="Agents write scripts that call their tools, instead of calling them one by one." notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="code" title="Code Mode settings unavailable" /> : <>
      <SettingsFileLocations files={files} disabled={busy} />
      <div className="settings-grid"><Card className="appearance-settings" aria-labelledby="settings-code-mode-title">
        <h2 id="settings-code-mode-title">{t("Code Mode")}</h2>
        <Field label={t("Mode")} htmlFor="settings-code-mode"
          notice={<p className="settings-field-help">{known && <>{t(known.help)} </>}{t("A session takes the change with its next prompt.")}</p>}>
          <HTMLSelect id="settings-code-mode" value={listing.mode ?? "on"} disabled={busy}
            onChange={event => {
              const mode = event.target.value;
              void mutate(() => api.saveCodeModeSettings({ expectedEpoch: epoch, mode }, { timeoutMilliseconds: 30_000 }), "Saved.");
            }}>
            {listing.modes.map(mode => <option key={mode} value={mode}>{label(mode)}</option>)}
          </HTMLSelect>
        </Field>
      </Card>
      <Card className="appearance-settings code-mode-scopes" aria-labelledby="settings-code-mode-scopes-title">
        <h2 id="settings-code-mode-scopes-title">{t("Where it applies")}</h2>
        <p className="settings-field-help">{t("A session takes the mode of its model, else of its provider, else this one. Each provider chooses in its settings.")}</p>
        {listing.scopes.length === 0 ? <p className="code-mode-scopes-none">{t("No provider or model chooses a mode of its own.")}</p>
          : <ul className="sandbox-scope-list code-mode-scope-list">
            {listing.scopes.map(scope => <li key={`${scope.providerKey}:${scope.modelId ?? ""}`} data-code-mode-scope={scope.modelId ? "model" : "provider"}>
              <Button variant="minimal" size="small" className="sandbox-scope-link" icon={<AppIcon name={scope.modelId ? "model" : "provider"} size={14} />}
                disabled={!onOpenScope} title={t("Change it where it is chosen")} onClick={() => onOpenScope?.(scope)}>
                <span className="sandbox-scope-kind">{t(scope.modelId ? "Model" : "Provider")}</span>
                <span className="sandbox-scope-name">{scope.modelId ? `${scope.providerName} · ${scope.modelId}` : scope.providerName}</span></Button>
              <span className="sandbox-scope-arrow" aria-hidden="true">→</span>
              <Tag minimal round className="code-mode-scope-mode">{label(scope.mode)}</Tag>
            </li>)}
          </ul>}
      </Card></div>
    </>}
  </SettingsPage>;
}
