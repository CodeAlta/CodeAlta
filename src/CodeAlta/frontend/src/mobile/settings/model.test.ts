import assert from "node:assert/strict";
import test from "node:test";
import { changedSettings, draftOf, fieldsOf, readMobileSettings, readPort, type MobileSettingsView } from "./model";

const answer: MobileSettingsView = {
  providers: ["tailscale", "devtunnel"], provider: { id: "tailscale", state: "serving", detail: "Tailscale serves it" }, port: 2584, httpsPort: 2584, host: null,
  address: "https://desk.tail1234.ts.net:2584", server: "listening", serverDetail: null, error: null,
};

test("the settings of the plugin are read for what they say, and anything else is no settings", () => {
  assert.deepEqual(readMobileSettings(answer), answer);
  assert.deepEqual(readMobileSettings({ ...answer, address: null, serverDetail: "", error: "" }), { ...answer, address: null, serverDetail: null, error: null }, "an empty text is no text");
  assert.equal(readMobileSettings({ ...answer, address: "http://desk:2584" })?.address, null, "the address of the view is HTTPS");
  assert.deepEqual(readMobileSettings({ ...answer, providers: undefined })?.providers, ["tailscale"], "without a list, the provider of now");
  assert.equal(readMobileSettings({ ...answer, error: "The port on this computer must be a number from 1024 to 65535." })?.error, "The port on this computer must be a number from 1024 to 65535.");
  for (const bad of [null, "settings", { ...answer, port: 80 }, { ...answer, httpsPort: 70000 }, { ...answer, port: 2584.5 }, { ...answer, server: 3 }, { ...answer, provider: null },
    { ...answer, provider: { state: "serving", detail: "" } }]) {
    assert.equal(readMobileSettings(bad), null, JSON.stringify(bad));
  }
});

test("a port is a whole number from 1024 to 65535, and Apply sends the fields of the provider chosen only when something changes", () => {
  assert.deepEqual(["1024", " 2584 ", "65535"].map(readPort), [1024, 2584, 65535]);
  for (const bad of ["", "1023", "65536", "80", "2584.0", "-2584", "2e3", "abc", "123456"]) assert.equal(readPort(bad), null, bad);
  const fields = draftOf(answer);
  assert.equal(changedSettings(answer, fields), null, "nothing changed");
  assert.deepEqual(changedSettings(answer, { ...fields, port: "2585" }), { provider: "tailscale", port: 2585, httpsPort: 2584 });
  assert.deepEqual(changedSettings(answer, { ...fields, httpsPort: "8443" }), { provider: "tailscale", port: 2584, httpsPort: 8443 });
  assert.equal(changedSettings(answer, { ...fields, port: "80", httpsPort: "8443" }), null, "a field that is not a port sends nothing");
  assert.deepEqual(changedSettings(answer, { ...fields, provider: "devtunnel", httpsPort: "x" }), { provider: "devtunnel", port: 2584 },
    "Dev Tunnels has no port on the tailnet: it is neither checked nor sent");
  assert.equal(changedSettings({ ...answer, provider: { ...answer.provider, id: "devtunnel" } }, { ...fields, provider: "devtunnel" }), null);
  assert.deepEqual([fieldsOf("tailscale"), fieldsOf("devtunnel"), fieldsOf("internet"), fieldsOf("pigeons")], [["httpsPort"], [], ["host"], []]);
  assert.deepEqual(changedSettings(answer, { ...fields, provider: "internet", host: " pulsar.example.com " }), { provider: "internet", port: 2584, host: "pulsar.example.com" },
    "the Internet sends its name, trimmed, and the plugin judges it");
});
