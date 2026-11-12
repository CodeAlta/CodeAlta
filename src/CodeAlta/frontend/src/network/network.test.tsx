import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import type { NetworkCheckResponse, NetworkProxyResponse } from "#neoastra";
import { messages } from "../localization";
import { ShellLanguageContext } from "../shellLanguage";
import { networkMessages } from "./messages";
import { NetworkSettings, NetworkView } from "./NetworkSettings";
import { bypassEntries, checkRequest, describeCheck, formChanged, formError, formOf, isProxyAddress, saveRequest, type ProxyForm } from "./network";

const never = () => assert.fail("rendering must not act");
const render = (element: ReactElement) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
const silent = () => new Promise<never>(() => { });
const listing = (change: Partial<NetworkProxyResponse> = {}): NetworkProxyResponse => ({ status: "ok", message: null, mode: "system", url: "", bypass: [],
  authentication: "none", user: "", passwordSet: false, appliedMode: "system", applyError: null, systemProxy: null, pending: false, restartAdvised: false, ...change });
const manual = (change: Partial<ProxyForm> = {}): ProxyForm => ({ mode: "manual", url: "http://proxy.corp:8080", bypass: "", authentication: "none", user: "", password: "", ...change });
const view = (value: NetworkProxyResponse) => render(createElement(NetworkView, { listing: value, busy: false, onSave: never, onCheck: never }));
const check = (change: Partial<NetworkCheckResponse>): NetworkCheckResponse => ({ status: "ok", message: null, reached: true, httpStatus: 200, via: null,
  elapsedMilliseconds: 42, target: "https://www.codealta.io/", ...change });

test("the page is drawn before the host answered, and says what it is for", () => {
  const html = render(createElement(NetworkSettings, { epoch: "e", api: { read: silent, save: never, check: never },
    filesApi: { list: silent, open: never, reveal: never } }));
  assert.match(html, /<h1>Network<\/h1>/);
  assert.match(html, /How CodeAlta and the programs it starts reach the internet\./);
  assert.match(html, /Loading…/);
});

test("the three modes are offered, the system one says where it goes, and the fields of a manual proxy show with it alone", () => {
  const system = view(listing({ systemProxy: "http://env-proxy:3128" }));
  for (const text of ["Use the system settings", "No proxy", "Manual proxy configuration", "Check connection"]) assert.ok(system.includes(text), text);
  assert.match(system, /data-setting="network\.proxy\.mode"/);
  assert.match(system, /Now through http:\/\/env-proxy:3128\./);
  assert.equal(system.includes("Proxy address"), false);
  assert.match(view(listing()), /Now without a proxy\./);
  // Restarting what runs is said on the page: the running programs keep their proxy.
  assert.match(system, /Agent CLIs, MCP servers and terminals already running keep their proxy until they restart\./);

  const configured = view(listing({ mode: "manual", url: "http://proxy.corp:8080", bypass: ["*.corp.example", "10.0.0.0/8"], authentication: "password", user: "jdoe", passwordSet: true }));
  for (const id of ["network.proxy.url", "network.proxy.no_proxy", "network.proxy.authentication", "network.proxy.user"])
    assert.ok(configured.includes(`data-setting="${id}"`), id);
  assert.match(configured, /value="http:\/\/proxy\.corp:8080"/);
  assert.match(configured, />\*\.corp\.example\n10\.0\.0\.0\/8<\/textarea>/);
  assert.match(configured, /value="jdoe"/);
  // The password is never shown: the field says one is kept.
  assert.match(configured, /type="password"[^>]*placeholder="Kept as a secret: type to replace it"[^>]*value=""|placeholder="Kept as a secret: type to replace it"[^>]*type="password"/);
  assert.match(configured, /NETWORK_PROXY_PASSWORD, never in config\.toml/);
});

test("what the host could not apply, and settings of the file that do not run yet, are said", () => {
  const html = view(listing({ applyError: "The secret NETWORK_PROXY_PASSWORD that keeps the proxy password is not set.", pending: true }));
  assert.match(html, /The proxy settings could not be applied as saved\./);
  assert.match(html, /The secret NETWORK_PROXY_PASSWORD that keeps the proxy password is not set\./);
  assert.match(html, /save them here, or restart CodeAlta\./);
  assert.equal(html.includes("Restart CodeAlta so that"), false);
  // The settings left the system mode while CodeAlta ran: the clients that used the system settings keep them.
  assert.match(view(listing({ mode: "manual", url: "http://proxy.corp:8080", appliedMode: "manual", restartAdvised: true })),
    /Restart CodeAlta so that the connections it opened before this change use these settings too\./);
  // Back to the system settings: the clients of the manual proxy keep it, without the PAC failover of Windows.
  assert.match(view(listing({ restartAdvised: true })), /Restart CodeAlta so that the connections it opened before this change use these settings too\./);
});

test("a manual proxy needs an address the host takes, and a password needs a user name and a password once", () => {
  assert.equal(formError({ ...manual(), mode: "system", url: "" }, false), null);
  assert.equal(formError(manual({ url: "" }), false), "Type the address of the proxy, such as http://proxy.example.com:8080.");
  assert.equal(formError(manual({ url: "http://user:secret@proxy:8080" }), false), "Put the user name and password in the authentication of the proxy, not in its address.");
  assert.equal(formError(manual({ url: "http://proxy:8080/path" }), false), "The address of the proxy must be http://host:port, without a path.");
  assert.equal(formError(manual({ url: "ftp://proxy:21" }), false), "The address of the proxy must be http://host:port, without a path.");
  assert.equal(formError(manual({ url: "proxy.corp:8080" }), false), null, "an address without a scheme is http");
  assert.equal(formError(manual({ authentication: "password" }), false), "Type the user name of the proxy.");
  assert.equal(formError(manual({ authentication: "password", user: "jdoe" }), false), "Type the password of the proxy.");
  assert.equal(formError(manual({ authentication: "password", user: "jdoe" }), true), null, "the password kept is used");
  assert.ok(isProxyAddress("https://proxy.corp:443"));
  assert.ok(!isProxyAddress("http://proxy.corp:8080?x=1"));
});

test("the bypass list is read as it is typed, and the requests carry the form, the password only when one is typed", () => {
  assert.deepEqual(bypassEntries(" *.corp.example, 10.0.0.0/8;intranet\n\nINTRANET  localhost "), ["*.corp.example", "10.0.0.0/8", "intranet", "localhost"]);
  const form = manual({ bypass: "a.example, b.example", authentication: "password", user: " jdoe ", password: "" });
  assert.deepEqual(saveRequest(form, "e"), { expectedEpoch: "e", mode: "manual", url: "http://proxy.corp:8080", bypass: ["a.example", "b.example"],
    authentication: "password", user: "jdoe", password: null });
  assert.equal(saveRequest({ ...form, password: "pa ss" }, "e").password, "pa ss");
  assert.deepEqual(checkRequest(form, "  ", "e"), { expectedEpoch: "e", target: "https://www.codealta.io", mode: "manual", url: "http://proxy.corp:8080",
    bypass: ["a.example", "b.example"], authentication: "password", user: "jdoe", password: null });
  assert.equal(checkRequest(form, "https://api.anthropic.com", "e").target, "https://api.anthropic.com");
});

test("the form is changed only by what differs from the settings kept", () => {
  const kept = listing({ mode: "manual", url: "http://proxy.corp:8080", bypass: ["a.example", "b.example"] });
  const form = formOf(kept);
  assert.equal(form.bypass, "a.example\nb.example");
  assert.equal(formChanged(form, kept), false);
  assert.equal(formChanged({ ...form, bypass: "a.example, b.example" }, kept), false, "the same entries, written otherwise");
  assert.equal(formChanged({ ...form, mode: "none" }, kept), true);
  assert.equal(formChanged({ ...form, password: "x" }, kept), true);
  assert.equal(formOf(listing({ mode: "elsewhere", authentication: "ntlm" })).mode, "system", "an unknown mode is the system one");
});

test("a check says how the address answered, through which proxy, or why it was not reached", () => {
  assert.deepEqual(describeCheck(check({ via: "http://proxy.corp:8080" })), { key: "{target} answered {status} through {proxy} in {milliseconds} ms.",
    values: { target: "https://www.codealta.io/", status: 200, milliseconds: 42, proxy: "http://proxy.corp:8080" }, intent: "success", detail: null });
  assert.equal(describeCheck(check({})).key, "{target} answered {status} directly in {milliseconds} ms.");
  assert.equal(describeCheck(check({ reached: false, httpStatus: 407, message: "Proxy Authentication Required" })).key, "The proxy asks for a user name and password, or refused them.");
  const refused = describeCheck(check({ reached: false, httpStatus: null, message: "Connection refused (proxy.corp:8080)" }));
  assert.equal(refused.key, "{target} could not be reached.");
  assert.equal(refused.detail, "Connection refused (proxy.corp:8080)");
  assert.equal(describeCheck(check({ status: "invalid", message: "bad" })).intent, "danger");
});

test("every text of the page is translated in every language", () => {
  for (const key of Object.keys(networkMessages)) {
    assert.ok(key in messages, `${key} is a text of the window`);
    assert.equal(networkMessages[key as keyof typeof networkMessages].length, 5, key);
  }
});
