# Browser

Sessions drive real web browsers with the tools of Chrome DevTools MCP, without Node.js: a .NET library that speaks to
each browser in its own protocol, and a built-in plugin that decides which browser a session gets, in which profile, and
what its tabs may reach.

## Projects

| Project | Role |
| --- | --- |
| `CodeAlta.Browser` | Headless and AOT-ready. Finding and choosing browsers, starting them and attaching to them, the CDP connection and its generated bindings, the BiDi driver, the in-page agent, the tools and their texts |
| `CodeAlta.Browser.Cdp.Generator` | Writes `Cdp/generated/*.gen.cs` from the protocol files of `ChromeDevTools/devtools-protocol` at a pinned revision, through a method map (`cdp-methods.json`): only the methods the drivers use are bound |
| `CodeAlta.Plugin.Browser` | The built-in plugin `browser` (desktop and `altacli`): `alta browser`, the tools of a session, consent, the sandbox, settings, the status item |

The hosts give the plugin what only they know through `BrowserPluginHost`: the folder of a session, its sandbox (as a
`SessionSandboxReach` of the runtime), how to ask its user, the ports of CodeAlta.

## Drivers

`IBrowserConnection` → `IBrowserContext` (the tabs of one session) → `IBrowserPage`, with two implementations:

- **Chromium (CDP).** Chrome and Edge have no WebDriver BiDi endpoint of their own; CDP needs no extra binary, has the
  Chromium-only features (traces, throttling, heap snapshots), and attaches only to the targets CodeAlta opened. One
  WebSocket, flattened sessions, one ordered queue of events per session.
- **Firefox (WebDriver BiDi)**, through the pinned `WebDriverBiDi` package, wrapped behind the interfaces, on the
  endpoint Firefox opens itself (`--remote-debugging-port 0`, its address read from its standard error). Events are
  subscribed per tab, never for the user's other tabs.
- **Safari** is not a driver: its own MCP server (`safaridriver --mcp`) is a built-in server of the MCP plugin on a
  Mac, which a session activates.

One in-page agent (`browser-agent.js`) makes the snapshot and its uids for every browser, in an isolated world (CDP)
or a sandbox realm (BiDi) the page can neither see nor change. Frames of other origins are read by their own agent and
joined under their frame element. Input is the browser's own (`Input.dispatch*`, `input.performActions` in the
frame that holds the element), so a page reacts as it does to a user. After an action the tools wait for a navigation
that starts within 100 ms, then for a quiet DOM, scaled by the emulated throttling.

## Choosing the browser

The session's `--browser`, then the preferred browser of Settings, then Automatic. Automatic takes the browser used
last, from the times of the files each browser writes while used (Chromium's `active_time` in `Local State`, its
`History` and `Sessions/`; Firefox's `places.sqlite`, session store and `prefs.js`), unless the system's default
browser was used within a day of it. It reads file times and profile entries only.

## Who owns a browser

`BrowserPool` keeps one browser per browser, profile and window mode, with a lease per session context; a browser
CodeAlta started ends with its last lease. The isolated profile is a temporary folder under
`<state root>/browser/temp`, deleted with its browser, a context per session; a persistent profile is
`<state root>/browser/profiles/<name>/<browser>`, a window per session. The user's Chrome or Edge stays connected while
CodeAlta runs, since Chrome asks the user at each connection; the user's Firefox, which CodeAlta starts on the user's
profile once the user closed it, is let go of, never ended. A session's tabs close after 15 idle minutes and open again
at the next call; its choice stays until it gives the tools back.

## Consent and guards

- **The user's own browser** takes the switch of Settings (off by default), the user's answer for the session, and the
  user's answer for each site: an `AgentBrowserPermissionRequest`, which no permission mode answers and a host without a
  user denies. Every page the session's tabs load is checked (`Fetch` or a BiDi intercept): a site not allowed is asked
  while a tool of the session runs, refused otherwise; a frame of a site not allowed is refused without a question.
- **Every profile:** `file:` pages and uploads inside the session's folders only, never a file that looks like it holds
  secrets; no request to the ports of CodeAlta; downloads to `<state root>/browser/downloads/<session>`, never opened;
  no raw CDP for the model; what a page shows is marked as data in every answer that carries it.
- **A sandboxed session** browses in the isolated profile only. Its context goes through a `SandboxNetworkProxy` of the
  network of its profile (`proxyServer` of a Chromium context, the proxy of a BiDi user context), and its files keep to
  the folders of its policy. The browser process runs outside the container, as plugin tools do, so the tools follow the
  sandbox themselves (`AdditionalToolsFollowSandbox`); a session whose sandbox changes loses its browser.

## One target per session

The browser tools and the UI tools of the window have the same names: activating one gives the other back
(`BrowserPluginHost.Activated`, `DesktopUiSessions.Activated`), and the running turn gets the new set with
`AgentRunTools.Set`.
