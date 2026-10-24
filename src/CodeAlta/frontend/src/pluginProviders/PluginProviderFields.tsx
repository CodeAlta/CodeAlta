import { useState } from "react";
import { Button, FormGroup, InputGroup, Switch, TextArea } from "@blueprintjs/core";
import type { GlobalConfigPluginProviderSetting, GlobalConfigPluginProviderType } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { PluginFieldValues } from "./pluginProviderForm";

/**
 * The settings a provider type of a plugin declares, in the form of the provider: the main ones in the grid of the
 * form, the advanced ones under a button. The labels are the plugin's.
 */
export function PluginProviderFields({ type, values, disabled, onChange }: {
  type: GlobalConfigPluginProviderType; values: PluginFieldValues; disabled: boolean;
  onChange: (values: PluginFieldValues) => void;
}) {
  const { t } = useShellLanguage();
  // A setting that has a value is not hidden behind the button: the user sees what the provider runs with.
  const advanced = type.settings.filter(setting => setting.advanced);
  const [open, setOpen] = useState(() => advanced.some(setting => !setting.readOnly && (values[setting.key] ?? "") !== ""));
  const field = (setting: GlobalConfigPluginProviderSetting) => <PluginSettingField key={setting.key} setting={setting} value={values[setting.key] ?? ""}
    disabled={disabled} onChange={value => onChange({ ...values, [setting.key]: value })} />;
  return <>
    {type.settings.filter(setting => !setting.advanced).map(field)}
    {advanced.length > 0 && <div className="provider-settings-wide plugin-provider-advanced">
      <Button variant="minimal" size="small" icon={<AppIcon name={open ? "chevronDown" : "chevronRight"} size={13} />} aria-expanded={open}
        onClick={() => setOpen(value => !value)}>{t("Advanced")}</Button>
    </div>}
    {open && advanced.map(field)}
  </>;
}

function PluginSettingField({ setting, value, disabled, onChange }: {
  setting: GlobalConfigPluginProviderSetting; value: string; disabled: boolean; onChange: (value: string) => void;
}) {
  const { t } = useShellLanguage();
  const id = `plugin-setting-${setting.key}`;
  const multiline = setting.kind === "list" || setting.kind === "map";
  const helper = setting.description ?? (setting.kind === "list" ? t("One per line.") : setting.kind === "map" ? t("NAME=value, one per line.") : undefined);
  if (setting.kind === "boolean") {
    return <FormGroup label={setting.label} labelFor={id} helperText={setting.description ?? undefined}>
      <Switch id={id} checked={value === "true"} disabled={disabled || setting.readOnly} onChange={event => onChange(event.currentTarget.checked ? "true" : "false")} /></FormGroup>;
  }

  return <FormGroup label={setting.label} labelFor={id} helperText={helper} className={multiline ? "provider-settings-wide" : undefined}>
    {multiline
      ? <TextArea id={id} fill rows={Math.min(6, Math.max(2, value.split("\n").length))} value={value} disabled={disabled} readOnly={setting.readOnly} spellCheck={false}
        placeholder={setting.placeholder ?? undefined} className="plugin-provider-textarea" onChange={event => onChange(event.target.value)} />
      : <InputGroup id={id} value={value} disabled={disabled} readOnly={setting.readOnly} spellCheck={false} placeholder={setting.placeholder ?? undefined}
        onChange={event => onChange(event.target.value)} />}
  </FormGroup>;
}
