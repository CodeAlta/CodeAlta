# Final M0 run pointers — 2026-09-05

These are task-owned **local** evidence directories, not release artifacts or portable CI links.
No user profile/configuration/provider/plugin was opened. The prototype changes are left uncommitted
for the parent to review; this file does not update the driving plan's checkboxes.

## Final commands and results

Commands were run from `src/prototypes/CodeAlta.Desktop.Probe`:

| Command | Result |
| --- | --- |
| `dotnet restore` | Passed; `NeoAstra` and `NeoAstra.Core` 0.1.0 cache metadata both record `https://api.nuget.org/v3/index.json` as source. |
| `dotnet build -t:NeoAstraStageFrontendClient --no-restore` | Passed; staged package-owned client version 0.1.0. |
| `dotnet build -c Release --no-restore` | Passed, zero MSBuild warnings/errors; normal build performs generated contract checking, locked npm restore and manifest asset preparation. Vite's oversized-chunk advisory is noted in the main README. |
| `npm --prefix frontend test` | **3 passed**, including a source guard that a native/disposal failure resets the smoke exit code to 1. |
| `npm --prefix frontend audit --json` | Exit 0, **zero reported vulnerabilities** for the reviewed lockfile. |
| `dotnet list package --vulnerable --include-transitive --no-restore` | Exit 0, no vulnerable packages reported. |
| `./Verify-Package.ps1 -OutputType Exe` | Passed final source, package/hash validation, local-only install, standalone native smoke and installed-shim native smoke. |
| `./Verify-Package.ps1 -OutputType WinExe` | Passed the same final checks with isolated GUI-subsystem outputs. |

The last two scripts pack and compile the **final code**, including the parent-reviewed disposal-exit
fix. Help/version returned 0 with expected stdout; missing/existing roots returned 2 with stderr;
each full native smoke returned 0. The source regression guard is not fault-injected native teardown
coverage. No unknown-profile full solution tests or production application launch were performed here.

## Retained final evidence

Relative to `%TEMP%` (`C:\Users\alexa\AppData\Local\Temp` on this host):

| Variant | Evidence root | Package SHA-256 |
| --- | --- | --- |
| Exe | `codealta-m0-package-f3d70d0e83c741d69a64a61d6e572140` | `5110EDBD6E3BF0C98FD3567A469D2499316B8E3B9BEEB09D4C155CADE7F81236` |
| WinExe | `codealta-m0-package-050b46ecec344885b838b0a27cfea761` | `FDB960AA89F0938ABBB69A37C7B015490FAA9505C7161237896BF2009C2E5417` |

Each root contains:

- `pack.log`, `install.log`, `package-inspection.log`, generated local-only `NuGet.Config`.
- `feed/CodeAlta.Desktop.Probe.0.1.0.nupkg`, isolated `bin/`, `obj/`, `publish/`, and `tools/`.
- `standalone/` and `installed/`: `verification.log`, help/version/error stdout/stderr logs,
  `smoke.stdout.log`, `smoke.stderr.log`, and `profile/smoke.log` plus the isolated WebView profile.

Additional ignored task-local logs: `evidence/local/{build.log,tests.log,npm-audit.json,nuget-audit.log,final-exe.log,final-winexe.log}`
(relative to the prototype root). Earlier diagnostic runs were retained,
not deleted or overwritten. Final assertions and package hashes above supersede those earlier runs.

Lockfile SHA-256: `6A3D2B8D19E647B526D571B2D1991FC7CF1846501E1D8344513323FC8BD5EA39`.
Generated RPC contract: `4c5698aab420c2fa6ec9083f456ca3edbfbc8729f6c5d72194e7550f1995c4ec`.

## Observed gates

- Windows **10.0.26200 x64**, .NET **10.0.11**, real WebView2 **152.0.4191.62** / Chromium
  **152.0.7977.76**. No other native RID execution is claimed.
- Both installed variants loaded
  `tools/.store/codealta.desktop.probe/0.1.0/codealta.desktop.probe/0.1.0/tools/net10.0/any/runtimes/win-x64/native/neoastra_native.dll`
  under their respective evidence roots. No checkout fallback or native override.
- Each final package had **100 hash-verified assets**, **3,823,561 bytes** excluding the manifest,
  and native libraries for exactly six non-musl RIDs. No Node modules, source maps, or bundled .NET runtime.
- Exe apphost **and installed shim**: subsystem 3; WinExe apphost **and installed shim**: subsystem 2.
  All four contain the Common Controls v6 manifest marker and pass real owned native TaskDialogs
  (Cancel and Accept), proving manifest effectiveness rather than just inspecting build output.
- All four pass generated RPC, post-open abort and iterator-break disposal (**2/2**), dynamic local
  assets, CM styling/editing, Radix portal/focus, strict direct Mermaid, native WM_CLOSE cancellation,
  subsequent approved programmatic close, and async cleanup before native-loop exit.
- This command host yielded console HWND **0** with redirected stdout/stderr for both subsystems.
  It therefore establishes redirected CLI output, **not** Explorer flash or unredirected terminal UX.

No upstream NeoAstra blocker remains from these Windows package/native checks. Five other RID hosts,
interactive console/Explorer behavior, production plugin CLI, full editor/diagram parity/performance,
and production distribution notices remain explicit later gates; see the main README.
