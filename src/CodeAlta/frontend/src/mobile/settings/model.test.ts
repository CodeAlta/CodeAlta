import assert from "node:assert/strict";
import test from "node:test";
import { changedPorts, readMobileSettings, readPort } from "./model";

const answer = { port: 2584, httpsPort: 2584, address: "https://desk.tail1234.ts.net:2584", server: "listening", serverDetail: null, tailscale: { state: "serving", detail: "Tailscale serves it" }, error: null };

test("the settings of the plugin are read for what they say, and anything else is no settings", () => {
  assert.deepEqual(readMobileSettings(answer), answer);
  assert.deepEqual(readMobileSettings({ ...answer, address: null, serverDetail: "", error: "" }), { ...answer, address: null, serverDetail: null, error: null }, "an empty text is no text");
  assert.equal(readMobileSettings({ ...answer, address: "http://desk:2584" })?.address, null, "the address of the tailnet is HTTPS");
  assert.equal(readMobileSettings({ ...answer, error: "The port on this computer must be a number from 1024 to 65535." })?.error, "The port on this computer must be a number from 1024 to 65535.");
  for (const bad of [null, "settings", { ...answer, port: 80 }, { ...answer, httpsPort: 70000 }, { ...answer, port: 2584.5 }, { ...answer, server: 3 }, { ...answer, tailscale: null }, { ...answer, tailscale: { state: "serving" } }]) {
    assert.equal(readMobileSettings(bad), null, JSON.stringify(bad));
  }
});

test("a port is a whole number from 1024 to 65535, and Apply sends the ports only when one of them changes", () => {
  assert.deepEqual(["1024", " 2584 ", "65535"].map(readPort), [1024, 2584, 65535]);
  for (const bad of ["", "1023", "65536", "80", "2584.0", "-2584", "2e3", "abc", "123456"]) assert.equal(readPort(bad), null, bad);
  const settings = { port: 2584, httpsPort: 2584 };
  assert.equal(changedPorts(settings, "2584", "2584"), null, "nothing changed");
  assert.deepEqual(changedPorts(settings, "2585", "2584"), { port: 2585, httpsPort: 2584 });
  assert.deepEqual(changedPorts(settings, "2584", "8443"), { port: 2584, httpsPort: 8443 });
  assert.equal(changedPorts(settings, "80", "8443"), null, "a field that is not a port sends nothing");
});
