# CodeAlta desktop (in development)

`CodeAlta` is the native desktop .NET tool (`alta`), built with published NeoAstra 0.1.0,
generated RPC, React/strict TypeScript and packaged local Vite assets. Node/npm is needed
only to build. The installed application has no UI server or external asset origin.

**This is an in-development workspace browser, not an agent frontend.** Use `CodeAlta.Tui`
(`altatui`) for sending prompts and running agents. Without catalog opt-in, only the boot
surface is available. No provider, plugin, configuration or default profile is initialized.
Shared application ownership/composition is intentionally deferred.

```powershell
dotnet build -c Release
./bin/desktop/Release/net10.0/alta.exe --help
./bin/desktop/Release/net10.0/alta.exe --version
./bin/desktop/Release/net10.0/alta.exe --data-root "$env:TEMP/codealta-desktop-$([guid]::NewGuid())"
```

Native startup requires an explicitly injected **new absolute task-owned directory**, outside
`.alta`. Help/version and rejected arguments do not initialize native services or create storage.
This restriction is not the future cross-head ownership guard. Do not use a production profile.

## Browse a task-owned catalog copy

To display persisted projects and sessions, supply an existing **trusted, task-owned catalog
copy** separately from the new browser data directory:

```text
alta --data-root <new-absolute-browser-directory> --catalog-root <existing-absolute-catalog-copy> --allow-catalog-cache
```

Both catalog options are required together. The roots must not overlap, and neither may be
under a `.alta` path component. Path spelling does not prove ownership or protect against
symlinks/reparse points: do not point this mode at a production profile or an untrusted tree.
Preparing the copy is an explicit operator action; the application does not copy a profile.

**This is not read-only filesystem access.** The opt-in permits the shared durable catalog
to create, rebuild and update `cache/cache.sqlite3` and SQLite sidecars in the copy. It does
not enable config migration, provider/plugin startup, agent execution or journal editing.
Cache/read failures are surfaced; the desktop does not substitute a header scan.

The screen shows a bounded persisted snapshot, project/session selection and metadata, not
live run status. Wire responses are capped at 200 projects and 500 sessions with a truncation
notice; these limits do not bound the underlying catalog load or implement paging/history.
The shared catalog caches its snapshot, and closing/reloading the document is not a refresh
or run-abort contract. Relaunch with a fresh browser data directory to load another snapshot.
Canceling an RPC waiter does not stop the shared catalog's background load. Existing RPC
teardown waits only a bounded time, so cache work can outlive bridge teardown; complete
native lifecycle qualification remains open.

Build outputs use `bin/desktop` and `obj/desktop` to avoid reusing the pre-rename TUI residue.
Generated frontend contracts/client remain build-only inputs under `obj/neoastra`.

The desktop RID intent is Windows, macOS and glibc Linux x64/ARM64 (six RIDs, **no musl**).
Only Windows x64 has executed native evidence so far. Ordinary `Exe` output and the Common
Controls v6 manifest are retained; redirected CLI success does not qualify interactive
console/Explorer behavior. See [native qualification](../../doc/desktop-native-qualification.md)
and [test instructions](../CodeAlta.Desktop.Tests/README.md).
