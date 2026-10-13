import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { Button, FormGroup, PopoverNext } from "@blueprintjs/core";
import { ComposerModelPicker } from "./ComposerModelPicker";
import { createModelPickerPreferences } from "./modelPicker";
import { ModelPickerSettings } from "./ModelPickerSettings";

// The model list of the composer where it lives, in the form a popover of the selection chip holds, over forty models,
// with its setting beside it. The chip itself is not drawn: it brings the prompt editor with it.
const models = Array.from({ length: 40 }, (_, index) => ({ id: `vendor/model-${index + 1}`, name: `Model ${index + 1}` }));
const preferences = createModelPickerPreferences(null);
const state = { chosen: [] as string[], listed: models.length, loading: false, apply: () => { } };
Object.assign(window, { pickerFixture: state });

function Fixture() {
  const [value, setValue] = useState<string | null>("vendor/model-25");
  const [, draw] = useState(0);
  state.apply = () => draw(count => count + 1);
  const form = <div className="composer-selection-form" role="group" aria-label="Session configuration">
    <FormGroup label="Model" labelFor="composer-model-test">
      <ComposerModelPicker id="composer-model-test" value={value} models={models.slice(0, state.listed)} loading={state.loading} preferences={preferences}
        onChange={id => { state.chosen.push(id); setValue(id); }} />
    </FormGroup>
  </div>;
  return <div style={{ padding: 16 }}>
    <ModelPickerSettings preferences={preferences} />
    <PopoverNext content={form} placement="bottom-start" popoverClassName="composer-selection-popover">
      <Button className="composer-selection" aria-label="Session configuration">{value}</Button>
    </PopoverNext>
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
