# CodeAlta desktop (in development)

`CodeAlta` is the native desktop .NET tool (`alta`), built with published NeoAstra 0.1.0,
generated RPC, React/strict TypeScript and packaged local Vite assets. Node/npm is needed
only to build. The installed application has no UI server or external asset origin.

**This milestone is a boot surface, not an agent frontend.** Use `CodeAlta.Tui` (`altatui`)
for current agent functionality. No provider, plugin, catalog, configuration or default
profile is initialized. Shared application ownership/composition is intentionally deferred.

```powershell
dotnet build -c Release
./bin/desktop/Release/net10.0/alta.exe --help
./bin/desktop/Release/net10.0/alta.exe --version
./bin/desktop/Release/net10.0/alta.exe --data-root "$env:TEMP/codealta-desktop-$([guid]::NewGuid())"
```

Native startup requires an explicitly injected **new absolute task-owned directory**, outside
`.alta`. Help/version and rejected arguments do not initialize native services or create storage.
This restriction is not the future cross-head ownership guard. Do not use a production profile.

Build outputs use `bin/desktop` and `obj/desktop` to avoid reusing the pre-rename TUI residue.
Generated frontend contracts/client remain build-only inputs under `obj/neoastra`.

The desktop RID intent is Windows, macOS and glibc Linux x64/ARM64 (six RIDs, **no musl**).
Only Windows x64 has executed native evidence so far. Ordinary `Exe` output and the Common
Controls v6 manifest are retained; redirected CLI success does not qualify interactive
console/Explorer behavior. See [native qualification](../../doc/desktop-native-qualification.md)
and [test instructions](../CodeAlta.Desktop.Tests/README.md).
