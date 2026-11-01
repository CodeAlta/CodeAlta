import { useEffect, useState } from "react";
import { NumericInput } from "@blueprintjs/core";
import { maximumModelPickerRows, minimumModelPickerRows, modelPickerPreferences, modelPickerRows, useModelPickerRows, type ModelPickerPreferenceStore } from "./modelPicker";
import { SettingsField as Field } from "./SettingsField";
import { useShellLanguage } from "./shellLanguage";

/** How many models the model list of the prompt shows before it scrolls. A change applies at once, also to an open list. */
export function ModelPickerSettings({ preferences = modelPickerPreferences }: { preferences?: ModelPickerPreferenceStore }) {
  const { t } = useShellLanguage();
  const rows = useModelPickerRows(preferences);
  // What is typed, which is a number of models only once it is a whole number the list takes.
  const [typed, setTyped] = useState(String(rows));
  useEffect(() => { setTyped(String(rows)); }, [rows]);
  return <div className="model-picker-settings">
    <Field setting="models.picker_rows" label={t("Models shown in the model list of the prompt")} htmlFor="settings-model-picker-rows">
      <NumericInput id="settings-model-picker-rows" className="model-picker-rows" value={typed} min={minimumModelPickerRows} max={maximumModelPickerRows}
        stepSize={1} majorStepSize={5} minorStepSize={null} allowNumericCharactersOnly selectAllOnFocus
        onValueChange={(value, text) => { setTyped(text); if (modelPickerRows(value) !== null) preferences.set(value); }}
        onBlur={() => setTyped(String(preferences.get()))} />
    </Field>
  </div>;
}
