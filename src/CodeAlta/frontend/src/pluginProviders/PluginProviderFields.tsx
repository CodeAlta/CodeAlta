import { FormGroup, InputGroup, Switch, TextArea } from "@blueprintjs/core";
import type { GlobalConfigPluginProviderSetting, GlobalConfigPluginProviderType } from "#neoastra";
import { ProviderEnvironment } from "../ProviderEnvironment";
import { useShellLanguage } from "../shellLanguage";
import type { PluginFieldValues } from "./pluginProviderForm";

/** The settings of a provider type of a plugin that one part of the form shows: the main ones, or the advanced ones. */
export function pluginSettingsOf(type: GlobalConfigPluginProviderType, part: "main" | "advanced"): readonly GlobalConfigPluginProviderSetting[] {
  return type.settings.filter(setting => !!setting.advanced === (part === "advanced"));
}

/**
 * The settings a provider type of a plugin declares, in one part of the form of the provider: the main ones with what the
 * provider runs, the advanced ones with the other settings few people change. The labels are the plugin's.
 */
export function PluginProviderFields({ epoch, type, part, values, disabled, onChange }: {
  epoch: string; type: GlobalConfigPluginProviderType; part: "main" | "advanced"; values: PluginFieldValues; disabled: boolean;
  onChange: (values: PluginFieldValues) => void;
}) {
  return <>{pluginSettingsOf(type, part).map(setting => <PluginSettingField key={setting.key} epoch={epoch} setting={setting} value={values[setting.key] ?? ""}
    disabled={disabled} onChange={value => onChange({ ...values, [setting.key]: value })} />)}</>;
}

function PluginSettingField({ epoch, setting, value, disabled, onChange }: {
  epoch: string; setting: GlobalConfigPluginProviderSetting; value: string; disabled: boolean; onChange: (value: string) => void;
}) {
  const { t } = useShellLanguage();
  const id = `plugin-setting-${setting.key}`;
  const multiline = setting.kind === "list" || setting.kind === "map";
  const helper = setting.description ?? (setting.kind === "list" ? t("One per line.") : undefined);
  if (setting.kind === "boolean") {
    return <FormGroup label={setting.label} labelFor={id} helperText={setting.description ?? undefined}>
      <Switch id={id} checked={value === "true"} disabled={disabled || setting.readOnly} onChange={event => onChange(event.currentTarget.checked ? "true" : "false")} /></FormGroup>;
  }

  // Names and values are rows, where a value can be a secret of this computer.
  if (setting.kind === "map") {
    return <FormGroup label={setting.label} helperText={helper} className="provider-settings-wide">
      <ProviderEnvironment id={id} epoch={epoch} value={value} disabled={disabled || !!setting.readOnly} onChange={onChange} /></FormGroup>;
  }

  // A list takes the room beside the field before it, where the form is wide enough.
  return <FormGroup label={setting.label} labelFor={id} helperText={helper} className={multiline ? "provider-settings-rest" : undefined}>
    {multiline
      ? <TextArea id={id} fill rows={Math.min(6, Math.max(2, value.split("\n").length))} value={value} disabled={disabled} readOnly={setting.readOnly} spellCheck={false}
        placeholder={setting.placeholder ?? undefined} className="plugin-provider-textarea" onChange={event => onChange(event.target.value)} />
      : <InputGroup id={id} value={value} disabled={disabled} readOnly={setting.readOnly} spellCheck={false} placeholder={setting.placeholder ?? undefined}
        onChange={event => onChange(event.target.value)} />}
  </FormGroup>;
}
