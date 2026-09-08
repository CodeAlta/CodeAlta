# Desktop tests

`CodeAlta.Desktop.Tests` is an MSTest project in the normal solution. Managed tests cover early
help/version/argument admission without native initialization, the boot RPC's generated JSON,
controlled navigation, frontend/package identity, dependency direction and structural lifecycle
guards. The first workspace vertical also covers actual CLI admission and durable-catalog
projection seams using inert inputs, plus generated RPC/frontend source wiring. It does not
construct real catalogs, SQLite, providers or a native application in behavioral fixtures.

For current scoped qualification, use only independently audited exact method filters with
`--no-build --no-restore`; the historical broad commands below are not isolation authorization.
The exact vertical inventory and remaining gaps are recorded in
[the parity ledger](../../doc/dual-head-desktop-parity.md). Native scripts remain separate.

```powershell
dotnet test src/CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj -c Release
npm --prefix src/CodeAlta/frontend test
dotnet test src/CodeAlta.Tests/CodeAlta.Tests.csproj -c Release --filter "FullyQualifiedName~ArchitectureGuardrailTests|FullyQualifiedName~TuiIdentityTests"
```

The `DesktopNative` category is **opt-in**. Without the opt-in flag, or without supported graphical
infrastructure, it reports inconclusive/skipped with the reason, not a fake native pass. The automated
driver currently requires interactive Windows x64 + installed WebView2, SDK 10 and build-time npm/Node.
A requested run with broken native initialization/build/assertions fails; it is not converted to a skip.

```powershell
$env:CODEALTA_DESKTOP_NATIVE_TESTS = '1'
dotnet test src/CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj -c Release --filter TestCategory=DesktopNative
# Equivalent directly invoked driver:
./src/CodeAlta.Desktop.Tests/Verify-DesktopPackage.ps1
```

`Verify-DesktopPackage.ps1` qualifies the actual packaged desktop boot/CLI/close, then builds/runs
the isolated [M0 native smoke fixture](NativeSmoke/README.md), preserving generated RPC/channel
cancellation, lazy local assets/CM/Radix/Mermaid, dialogs and canceled/approved native close checks.
The fixture is outside the release solution; its source/assets cannot enter the production package.
The pinned npm graph is preserved for both consumers; the production entry imports React and
its boot/workspace contracts, not the smoke-only visual libraries.

These scripts retain their unique evidence roots, local package feed/tool installs and native browser
cache. They do not clean user profiles or ignored original prototype artifacts. Never publish the
smoke package. [Qualification boundaries and historical evidence](../../doc/desktop-native-qualification.md).
