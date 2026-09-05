# M0 desktop package/native probe (not a production frontend)

This isolated consumer is deliberately **outside `CodeAlta.slnx`**. It references only the published
`NeoAstra` NuGet package, centrally pinned at **0.1.0**. It has no CodeAlta/provider/plugin references,
never discovers a production profile, and is not intended for publication. Do not migrate production
code into this prototype. The parent plan/parity ledger owns milestone acceptance.

## Build and reproduce

Requires .NET SDK 10, Node/npm for building, and a graphical desktop with the native browser runtime.
Run from this directory, not from the production application directory:

```powershell
dotnet restore
dotnet build -c Release
npm --prefix frontend test
npm --prefix frontend audit

# Existing checked-in lockfile: normal build stages the client and runs locked npm ci as needed.
# For an intentionally reviewed dependency/lockfile change only:
# dotnet build -t:NeoAstraStageFrontendClient --no-restore
# npm --prefix frontend install --no-audit --no-fund

./Verify-Native.ps1 -Executable ./bin/Release/net10.0/alta-desktop-probe.exe
./Verify-Package.ps1
./Verify-Package.ps1 -OutputType WinExe

# Interactive viewing (still fake data; frontend checks run automatically):
./bin/Release/net10.0/alta-desktop-probe.exe --data-root "$env:TEMP/codealta-probe-$([guid]::NewGuid())"
```

Both verification scripts require a **new**, task-owned evidence directory (default: unique directory
under the OS temporary directory). They retain evidence rather than deleting it. `Verify-Native.ps1`
launches from that directory with child PATH limited to Windows system directories and removes all
`NEOASTRA_*` / `WEBVIEW2_*` overrides from the child environment. It checks help/version exit 0,
missing/existing root exit 2, and the full smoke exit 0. It caps each process at 75 seconds, killing
only the process tree it created on timeout. The application's smoke has its own 60-second deadline;
each native dialog has an eight-second deadline.

`Verify-Package.ps1` uses ordinary `dotnet pack` (framework-dependent), validates every packaged asset
hash, inspects all six native binaries and the runtimeconfig, installs from a generated **local-only**
NuGet feed into a temporary `--tool-path`, then tests both standalone apphost and installed tool shim.
Build/publish/intermediate paths for these comparisons are isolated under the evidence directory.
It checks the Common Controls manifest marker and PE subsystem in **both** executables. No global
tool, custom shim, native-loader override, local project reference, or activation-context workaround
is used. The final installed run must load its native DLL from its own `.store` tree.

## Consumer wiring that matters

- `app.manifest` activates Common Controls v6: required by NeoAstra's Windows `TaskDialogIndirect`.
  Omitting it reproduced `EntryPointNotFoundException` for `TaskDialogIndirect` in `comctl32.dll`.
  The standard manifest fixes standalone **and installed tool** use; this is not an upstream blocker.
- `[STAThread]` owns the native loop. The main window is registered before yielding, shown before
  creating its fill-window browser, and the loop stays alive until asynchronous disposal completes.
  `NeoEnvironmentOptions.UserDataRoot` points exclusively beneath the required new `--data-root`.
- `NeoRpcService` / `NeoRpcMethod`, an explicit JSON source-generation context, and the package's
  generated contract hash provide `hello`, `observe`, and `state`. No hand-authored transport/hash.
  Two instance-owned counters prove enumeration began and actually disposed after cancellation.
- The NuGet package stages its **0.1.0** `@neoastra/client` under `obj/neoastra/client`. npm references
  that local package; TS/Vite alias `#neoastra` to the generated `obj/neoastra/neoastra.ts`.
- `neoastra.json` requires `$schema`, even though it is a local schema identifier rather than a
  runtime network fetch. Vite `base: "./"`, no source maps, explicit non-inlined fixture SVG, and
  manifest-backed `app://codealta/index.html` prove ordinary packaged local hosting without a server.
- The supported CSP adds **only style** `'unsafe-inline'` for trusted runtime styles. Script policy
  stays self-only. No nonce framework, response rewriting, auxiliary document, or sandbox.
- Chromium exposed zero Resource Timing entries for these custom-scheme loads. Assertions instead
  verify the executed lazy module's `import.meta.url`, stylesheet URL/computed style, decoded image
  URL/dimensions, and actual CodeMirror/Mermaid output. Zero timing entries are not a load failure or
  evidence of no network activity. Self-only policy, controlled imports, and no CSP violations are
  the relevant evidence; this is not a network-packet capture.

## Executed Windows evidence (2026-09-05)

Host: Windows **10.0.26200 x64**, SDK **10.0.303**, .NET runtime **10.0.11**, Node **22.14.0**,
npm **11.7.0**. Real WebView2 reported **152.0.4191.62** through browser high-entropy version data
(Chromium **152.0.7977.76**). NeoAstra's `RuntimeInfo.BrowserVersion` reports only `system`; the
frontend version report supplies the actual engine evidence.

Passed in normal Release output and in a local-feed installed tool, with Node/dev/native overrides absent:

- Generated typed C# RPC round-trip.
- Post-open `AbortSignal` cancellation and iterator-break cancellation, with **2 started / 2 disposed**
  backend iterators; no fake bridge substituted for native RPC.
- Relative dynamic JS, CSS and decoded SVG fixture, CodeMirror injected style/edit transaction,
  Radix dialog portal/autofocus/close/focus restoration, strict directly rendered Mermaid SVG with
  three visible flowchart nodes, and no observed CSP violation.
- Real **visible, owned Win32 TaskDialogs**, Cancel then Accept, driven by process/owner/title/class
  checks and `TDM_CLICK_BUTTON` (not global keystrokes or a fake dialog adapter).
- Asynchronous native close cancellation preserving the window, followed by approved close and
  joined binding/view/environment cleanup. The final driver also exercises native `WM_CLOSE`.
- Early help/version and argument rejection without a profile/native startup.

Package inspection found all six native RIDs: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`,
`linux-x64`, `linux-arm64`. No musl native binaries, Node modules, source maps or bundled .NET runtime.
The runtimeconfig requests `Microsoft.NETCore.App` **10.0.0**. The initial measured asset manifest
contained **100 assets**, approximately **3.82 MB** before the manifest; entry JS about **253 KB**,
lazy CodeMirror/probe JS **201 KB**, Mermaid core **95 KB** (uncompressed). Ordinary Mermaid imports
also emit unused diagram-family chunks, including a **662 KB** chunk and transitive KaTeX/Cytoscape.
Vite prints its >500 KB chunk advisory. This is an M0 feasibility fixture, **not** final bundle tuning,
an editor/diagram feature-parity claim, or permission to add those transitive libraries as direct features.

### Console / tool manifest findings

Both standalone apphost and the **ordinary installed .NET tool shim** retain the Common Controls v6
manifest and pass real native dialogs. `OutputType=Exe` gives PE subsystem **3 (console)** in both;
the command-line `-p:OutputType=WinExe` comparison gives subsystem **2 (GUI)** in both. Both preserve
help/version, stderr for invalid arguments and exit codes **when explicitly redirected** by the driver.
The probe remains `Exe` by default; no production console policy is chosen here.

The smoke logs also report the actual console HWND and redirection flags. Redirected-pipe success
does not establish every terminal shell's GUI-process wait/console-attachment behavior. Explorer launch
appearance/console flash, direct unredirected CLI UX across cmd/PowerShell, and plugin CLI output are
still production-head acceptance checks. No plugin CLI code runs in this probe.

### Evidence locations

Scripts print their exact evidence root and retain `pack.log`, `install.log`, `package-inspection.log`,
and `standalone/` / `installed/` reports. Each native report includes the loaded native library path,
engine versions, frontend assertions, native dialog/close results and final exit code. Local scratch
logs under `evidence/local/` are ignored. See `evidence/README.md` for the final run pointers.

## Dependency/advisory/license review

All direct registry dependencies use exact versions in `frontend/package.json`; the committed lockfile
uses npm registry HTTPS tarballs with integrity hashes, except the intended package-staged local client.
No Git dependencies, remote client runtime or floating `latest` direct dependencies. The two direct CM
packages are sufficient for this editing/style smoke; no language/editor framework is installed.

`type-fest` **4.41.0** is a build-only type dependency: Mermaid 11.17.2's published declarations import
it, but Mermaid lists it only as its own devDependency. Adding the matching reviewed type dependency
keeps strict library checking enabled rather than hiding the error with `skipLibCheck`. `@types/node`
**22.20.1** supports the Node-based tests. Neither adds an application Node runtime.

`npm audit --json` returned **zero vulnerabilities** for the reviewed lockfile on 2026-09-05. This is
a point-in-time registry advisory check, not proof against undisclosed issues. Reviewed lockfile
licenses are MIT, ISC, BSD-2/3-Clause, Apache-2.0, 0BSD, Unlicense, MIT-or-CC0, MPL-2.0, and
MPL-2.0-or-Apache-2.0. Important details:

- React, CM, Radix, Mermaid and Vite: MIT; NeoAstra/client: BSD-2-Clause.
- DOMPurify (Mermaid transitive): **MPL-2.0 OR Apache-2.0**; Apache-2.0 is the compatible alternative.
- Lightning CSS and platform variants: **MPL-2.0**, build tooling only (Vite), not packaged application
  runtime. Native npm musl tooling variants do **not** imply a desktop musl support claim.
- `khroma` omits license metadata; its installed `license` was inspected and is **MIT**, copyright
  Fabio Spampinato and Andrew Maney. `robust-predicates` is Unlicense; `type-fest` MIT-or-CC0.

This package is installed locally for qualification only, not published. A production release must retain
the required third-party notices/license texts for managed/native dependencies and the selected
frontend bundle; the lockfile/license review here is not a completed distribution-notices inventory.

## Remaining gates / deliberate limits

- **Only Windows x64 native execution is qualified here.** Five other native RIDs require real hosts;
  their packaged presence is not runtime proof. Desktop musl is unsupported. The native-dialog driver
  is Windows-only and reports an explicit gap elsewhere.
- Normal npm/.NET restores can contact feeds and execute reviewed package build scripts. Runtime
  needs no Node/server, but offline **build** cache provisioning is not established.
- One fake document/flowchart proves integration, not accessibility, physical keyboard/IME behavior,
  screenshot quality, full editor/diagram parity, performance budgets, file/folder picker interaction,
  restart recovery, renderer reload, or production admission/shutdown ownership.
- No NeoAstra source modification/version change was needed. Missing Common Controls manifest,
  schema identifier, Mermaid's missing consumer type dependency, and the custom-scheme timing
  assumption were corrected within ordinary consumer/test wiring.
