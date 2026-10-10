# Canvas board sample

A tab that a plugin provides and a script draws, with the data in the plugin. `plugin.cs` declares one canvas, `board`, whose view names a script of the package folder (`PluginScript.File("ui/board.js")`), and registers what that script calls on `canvas.Rpc`: `board.get` (a call), `board.add` and `board.move` (calls that change the board and throw `PluginRpcException` for what the script should test), `board.explode` (a handler that throws: the script gets `internal_error`, the text stays in the log) and `board.watch` (a stream: the board now, then after each change). Each change also sends the event `board.changed`.

`ui/board.js` is a React component: the window draws its default export in its own tree, so it imports `react` and `@blueprintjs/core` and gets the instances the application runs, and a menu or a popover opens in the layer of the window, above the tabs. It reads the board with `useRpc` and `useStream`, changes it with `alta.rpc.invoke`, hears the event with `alta.rpc.subscribe`, and uses the `codealta` module (`html`, `Chart`, `Markdown`, `Icon`, `FileLink`) and the `alta` object (`alta.host.openDiff`, `setBadge`, `alta.visible`). No build step: the file is the module.

`alta board open` opens the tab; `alta board add --title "..."` adds a card from a terminal, and every open tab shows it at once.

A plugin of one file keeps to one file by giving the module as text (`PluginCanvasView.ScriptSource` or `PluginHtml.Script(code)`), served under a generated name. A reload of the plugin, or an edit of the file, is a new address: the tab mounts the new module, connects to the new plugin and lets the old one go.
