import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { Button, FormGroup, PopoverNext } from "@blueprintjs/core";
import { ComposerProviderPicker } from "./ComposerProviderPicker";
import { createModelPickerPreferences } from "./modelPicker";
import { defaultProviderListPreferences } from "./providerListOrder";
import { ProviderBrandsContext, type ProviderBrands } from "./ProviderIcon";

// The provider list of the composer where it lives, in the form a popover of the selection chip holds, over the providers of
// a configuration with their types; the favorites of Settings are those of the fixture. The chip itself is not drawn.
const providers = [
  { id: "xiaomi", name: "xiaomi", type: "openai" },
  { id: "codex", name: "codex", type: "codex" },
  { id: "codex-cli", name: "codex-cli", type: "codex-cli" },
  { id: "claude-code", name: "claude-code", type: "claude-code", isDefault: true },
  { id: "claude-max5", name: "Claude Max", type: "claude-code" },
  { id: "antigravity-acp", name: "antigravity-acp", type: "acp" },
  { id: "copilot", name: "copilot", type: "copilot" },
];
const brands: ProviderBrands = new Map(providers.map(provider => [provider.id, { key: provider.id, type: provider.type, name: provider.name }]));
const preferences = createModelPickerPreferences(null);
const state = { chosen: [] as string[], favorites: [] as string[], listed: providers.length, loading: false, apply: () => { } };
Object.assign(window, { pickerFixture: state });

function Fixture() {
  const [value, setValue] = useState<string | null>("antigravity-acp");
  const [, draw] = useState(0);
  state.apply = () => draw(count => count + 1);
  const form = <div className="composer-selection-form" role="group" aria-label="Session configuration">
    <FormGroup label="Provider">
      <ComposerProviderPicker id="composer-provider-test" value={value} providers={providers.slice(0, state.listed)} loading={state.loading} unlisted="Unavailable" preferences={preferences}
        readFavorites={() => ({ ...defaultProviderListPreferences, favorites: state.favorites })}
        onChange={id => { state.chosen.push(id); setValue(id); }} />
    </FormGroup>
  </div>;
  return <ProviderBrandsContext.Provider value={brands}>
    <div style={{ padding: 16 }}>
      <PopoverNext content={form} placement="bottom-start" popoverClassName="composer-selection-popover">
        <Button className="composer-selection" aria-label="Session configuration">{value}</Button>
      </PopoverNext>
    </div>
  </ProviderBrandsContext.Provider>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><Fixture /></StrictMode>);
