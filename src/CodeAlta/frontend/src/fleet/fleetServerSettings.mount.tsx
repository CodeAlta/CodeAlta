// Disposable page: Settings > Fleet > Server on a host that holds one server of this computer, the owner's, whose fleet sends
// its telemetry through it. Driven by fleetServerSettings.browser.test.ts through `fleetFixture`: what the page asks is recorded.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { FleetServerView, FleetTelemetryRequest, FleetTelemetryView } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { FleetServerSettings, type FleetServerApi } from "./FleetServerSettings";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const server: FleetServerView = { id: "home", url: "https://fleet.example", organization: "Home", link: "approved", connection: "online", problem: null, controller: true,
  spaces: [] };
let telemetry: FleetTelemetryView = { enabled: true, endpoint: "https://otlp.example:4318", protocol: "http/protobuf", route: "server", signals: ["traces", "metrics"],
  headers: [{ name: "x-api-key", masked: "••••6789" }], environment: null, updatedAt: null };
const unavailable = async () => { throw new Error("not in this fixture"); };
const api: FleetServerApi = {
  status: async () => ({ status: "ok", servers: [server], deviceId: "device-1", words: "apple river stone" }),
  devices: async () => ({ status: "ok", devices: [], message: null }),
  telemetry: async () => ({ status: "ok", telemetry, message: null }),
  setTelemetry: async (request: FleetTelemetryRequest) => {
    calls.push(JSON.stringify({ enabled: request.enabled, endpoint: request.endpoint, route: request.route, signals: request.signals, headers: request.headers }));
    telemetry = { ...telemetry, enabled: request.enabled, signals: [...request.signals] };
    return { status: "ok", telemetry, message: null };
  },
  watch: async () => (async function* () { })(),
  create: unavailable, join: unavailable, leave: unavailable, createToken: unavailable, approve: unavailable, revoke: unavailable,
};
Object.assign(window, { fleetFixture: { calls } });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell settings-dialog-content" style={{ padding: 12, height: "100%", boxSizing: "border-box" }}>
      <FleetServerSettings epoch="12345678-1234-1234-1234-123456789abc" api={api} />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
