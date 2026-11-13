# Canvas plot sample

A dashboard of test runs drawn with [Observable Plot](https://observablehq.com/plot/), with the data in the plugin. `plugin.cs` declares one canvas, `test-runs`, whose view names a script of the package folder (`PluginScript.File("ui/runs.js")`), and registers the call the script makes on `canvas.Rpc`: `runs.get`, thirty days of runs of four test suites (a day, a suite, seconds, failed tests), the same at every start.

`ui/runs.js` is a React component. It reads the runs with `useRpc("runs.get")` and draws three charts with `@observablehq/plot`, imported by its name: the application lends the one it runs, so the script ships nothing.

- **Durations:** a line per suite (a moving average with `windowY`), its runs as dots, and a `tip` under the pointer.
- **Spread:** a box per suite (`boxX`).
- **Failures:** a bar per suite and day (`barY`).

Plot does not know the window: `PlotChart` gives each chart the font and the colors of `useTheme()` (`fontFamily`, `text` as `color`, `series` as the range of the colors, `axis`, `tooltipBackground`), passes the width of its card, and draws again when the theme or that width changes; it puts the element Plot returns in its own `<div>` and removes it on cleanup, never as a string for `innerHTML`. The fourth card draws the same runs from a Vega-Lite spec with the `VegaLite` component of `codealta`, which the window draws as it draws a `vega-lite` block of Markdown.

Open it from the Canvases page (**Test runs**), or with `alta canvas open test-runs`; `alta canvas read test-runs` gives an agent what the canvas is about.
