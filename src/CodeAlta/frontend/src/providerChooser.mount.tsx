import { useState } from "react";
import { createRoot } from "react-dom/client";
import { ProviderChooser } from "./ProviderChooser";
import "./style.css";

function Fixture() {
  const [provider, setProvider] = useState("original"), [busy, setBusy] = useState(false);
  const [draft, setDraft] = useState("Keep my unsent prompt");
  const [shown, setShown] = useState(true);
  Object.assign(window, { providerFixture: { busy: setBusy, shown: setShown } });
  return <><textarea aria-label="Draft" value={draft} onChange={event => setDraft(event.target.value)} />
    <span id="selected-provider">{provider}</span>
    {shown && <ProviderChooser epoch="fixture" sessionId="one" providerKey={provider} disabled={busy} current={() => !busy}
      onSelected={async () => { setProvider(value => value === "original" ? "target" : "original"); }} />}</>;
}
createRoot(document.getElementById("root")!).render(<Fixture />);
