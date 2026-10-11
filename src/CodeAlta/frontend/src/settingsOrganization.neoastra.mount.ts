// An isolated shell for the real Settings pages. Every mutation is recorded here, never sent to a host.
import { desktopShell as baseShell } from "./projectFocus.neoastra.mount";
export * from "./projectFocus.neoastra.mount";

const initial = { status: "ok", platform: "windows", canKeepRunning: true, onClose: "keep", sessionWidth: 75,
  reconnectRemoteControl: true, reviewPermissions: true, inheritPermissions: false, sessionWidths: [], zoom: 100 };
const preferences = { ...initial, ...JSON.parse(localStorage.getItem("organizationShell") ?? "{}") };
const calls: { kind: string; request: unknown }[] = [];
function save(kind: string, request: unknown, change: object) {
  calls.push({ kind, request }); Object.assign(preferences, change);
  localStorage.setItem("organizationShell", JSON.stringify(preferences));
  return Promise.resolve({ ...preferences });
}
Object.assign(window, { settingsOrganization: { calls, preferences } });
export const desktopShell = {
  ...baseShell,
  preferences: async () => localStorage.getItem("organizationNoShell") === "true" ? { status: "unavailable" } : { ...preferences },
  setSessionWidth: (request: { percent: number }) => save("width", request, { sessionWidth: request.percent }),
  setReconnectRemoteControl: (request: { reconnect: boolean }) => save("remote", request, { reconnectRemoteControl: request.reconnect }),
  setOnClose: (request: { onClose: string }) => save("close", request, { onClose: request.onClose }),
  setReviewPermissions: (request: { review: boolean }) => save("review", request, { reviewPermissions: request.review }),
  setInheritPermissions: (request: { inherit: boolean }) => save("inherit", request, { inheritPermissions: request.inherit }),
};
