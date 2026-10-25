# Desktop tests

`CodeAlta.Desktop.Tests` holds the MSTest tests of the desktop host: command-line admission, the RPC services and their generated contract, window routing, the MCP server, startup and shutdown, and the desktop side of features (search, terminals, plugins in the window). They run with the solution:

```powershell
dotnet test src/CodeAlta.Desktop.Tests/CodeAlta.Desktop.Tests.csproj -c Release
npm --prefix src/CodeAlta/frontend test
```

Tests are behavioral or inspect compiled metadata; no test pins the text of source files. The page itself is tested by the frontend tests (`npm test`), including the browser tests that mount real components in headless Edge, and by driving the developer instance over its MCP server (see `AGENTS.md`).

The packaged desktop is not tested here. The isolated [native smoke fixture](NativeSmoke/README.md) keeps its own scripts outside the release solution, so its source and assets never enter the package; never publish its package.
