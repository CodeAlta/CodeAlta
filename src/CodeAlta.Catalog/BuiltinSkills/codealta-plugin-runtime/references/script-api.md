# Script API reference

The `codealta` module is supplied by the running desktop application. Import it by name; write plain JavaScript, not TypeScript or JSX. No npm install or bundle is needed. **Choosing a library?** [Script libraries](script-libraries.md) lists every import name, exact version, exports, upstream API and repository (React, Blueprint, FlexLayout, Lucide), plus the libraries reached only through helpers. Use `alta plugin api <name>` for the **C#** side.

## All `codealta` exports

Named runtime exports only; no default export. `interfaceVersion` is **1**, also available as `alta.versions.interface`.

<!-- exports: codealta -->
| Use | Exports |
| --- | --- |
| Elements and hooks | `html`, `useAlta`, `useVisible`, `useTheme`, `useRpc`, `useStream` |
| Content and links | `Markdown`, `Code`, `Diagram`, `Equation`, `FileLink`, `SessionLink` |
| Icons | `Icon`, `BrandIcon`, `brandIconNames`, `isBrandIcon` |
| Charts | `Chart`, `Sparkline`, `StatTile`, `CalendarHeatmap`, `WeekdayHourHeatmap` |
| Distribution data/options | `histogram`, `histogramOption`, `boxStats`, `boxPlotOption`, `quantile` |
| Interface and errors | `interfaceVersion`, `AltaError` |
<!-- /exports -->

Names such as `Alta`, `ChartProps` and `RpcResult` are TypeScript types, not JavaScript imports. Other internal chart helpers (`compact`, `calendarLayout`, `seriesColor`, `sanitizeChartOption`, etc.) are **not** exported by `codealta`.

## React and HTML

```js
import { useState } from "react";
import { html, useAlta, useRpc, useTheme, CalendarHeatmap, StatTile, Code } from "codealta";

export default function Dashboard() {
    const alta = useAlta();
    const theme = useTheme();
    const result = useRpc("readings.get", {});
    const [selected, select] = useState(null);
    if (result.error) return html`<p role="alert">${result.error.message}</p>`;
    if (!result.data) return html`<p>Loading…</p>`;
    // The plugin returns { days: [{ date: "2026-01-01", value: 2 }], total: 2 }.
    return html`<section style=${{ color: theme.text, background: theme.surface }}>
        <${StatTile} label="Total" value=${String(result.data.total)} />
        <${CalendarHeatmap} data=${result.data.days} from="2026-01-01" to="2026-01-31"
            ariaLabel="Daily readings" onSelect=${(date, value) => select(date)} />
        <p>Selected: ${selected ?? "none"}</p>
        <${Code} code=${JSON.stringify(result.data, null, 2)} language="json" />
        <button onClick=${() => result.reload()} disabled=${result.loading}>Refresh</button>
    </section>`;
}
```

`html` creates React elements; it is not `innerHTML`. Dynamic values use `${value}`, including callbacks, arrays of elements, and style objects. Use React names (`className`, `onClick`, `strokeWidth`). Close a dynamic component with `<//>` or `/>`. Pass characters such as `→` directly, not HTML entities. Give mapped elements a `key`. A script can render a scoped `<style>` element; do not style or inspect the host's private ancestors. A string assigned to `innerHTML` must first go through `alta.html(text)`, which sanitizes it and removes scripts, styles and event attributes.

### Entry points and lifetime

- Export a **default React component** (preferred), or `mount(root, alta)` returning an optional cleanup function, synchronously or asynchronously. If both exist, the default component wins. The script is given alongside the C# view's HTML, never as a `<script>` inside that HTML.
- A default component replaces the HTML; a mount function fills its fragment. Replacing that fragment ends the old mount and runs it again. A reload replaces either form; `alta.closed` aborts and cleanup runs. Clear timers, observers and subscriptions; do not keep instance state in module globals.
- `useAlta()` works only inside the component tree CodeAlta renders for the script; a separately created React root does not inherit that context. There is no global `alta`. Call hooks from components/custom hooks, not `mount` or event handlers.
- Hidden is not closed: pause work with `useVisible()`/`alta.visible`. Hooks handle RPC visibility; read `const visible = useVisible()` before early returns and pass `visible=${visible}` to a chart. Timeline scripts start when first shown. Landing cards have no script.

## Hooks and RPC

| Export | Contract |
| --- | --- |
| `useAlta()` | The instance's `alta` object (below). |
| `useTheme()` | Current theme **value**, rerenders on theme changes. |
| `useVisible()` | Boolean; false when the tab is hidden. |
| `useRpc(name, input?)` | `{ data, error, loading, reload }`. `data` starts `undefined`, `error` starts `null`; **not a tuple**. `reload()` returns void. |
| `useStream(name, input?)` | `{ latest, items, error, active }`; `latest` starts `undefined`, `items` keeps the last 1000 items. |

`useRpc` is for reads: it reads on mount, input change, reload, tab becoming visible and reconnection. It may retain previous data while loading or after an error. Do not use it for mutations; call `await alta.rpc.invoke(name, input)` from an event handler, then `result.reload()`. Hooks must run unconditionally, before early returns. Effects may mount twice: clean up what each effect starts.

```js
try {
    await alta.rpc.invoke("readings.set", { date, value: Number(value) }, { signal: alta.closed });
    result.reload();
} catch (error) {
    // PluginRpcException("invalid_value", "...") in C# becomes this error.
    setError(`${error.code}: ${error.message}`);
}
```

- `invoke(name, input?, { signal }?)` → `Promise<unknown>`; omitted input becomes `{}`.
- `stream(name, input?, { signal }?)` → `Promise<AsyncIterable<unknown>>`; await it before `for await`.
- `subscribe(name, handler, { signal }?)` → `Promise<() => void>`; await it to obtain the unsubscribe function. Events sent before subscribing are not kept.
- `AltaError` is exported: `code`, `message`, `retryable`, optional `correlationId`.
- RPC is available only in a canvas script. Register all C# handlers in `Open` before returning the view. JSON uses camelCase by default.
- There are **8 simultaneous calls**, 8 streams and 8 subscriptions per connection, a 30-second call timeout and 1 MiB frame limit. Do not `Promise.all` an arbitrary list of calls: use a sequential loop or bounded workers. Mutations are not automatically replayed.
- `alta.rpc.generation` and `alta.rpc.connected` are signals. Re-subscribe after reconnection if managing subscriptions yourself; prefer hooks for reads/streams.

## Components

Required props are in **bold**. Optional callbacks are functions, not strings.

| Component | Props |
| --- | --- |
| `Markdown` | **`source: string`** |
| `Code` | **`code: string`**, `language?: string` (e.g. `json`, `csharp`, `diff`). It does not take `source` or children as the code. |
| `Diagram` | **`source: string`** (Mermaid) |
| `Equation` | **`source: string`** (LaTeX, without delimiters; `\ce{…}` for chemistry). `Markdown` draws `$…$` and `$$…$$` formulas and footnotes too. |
| `FileLink` | **`path: string`**, `line?: number`, `children?` (label). Relative paths use the canvas project. |
| `SessionLink` | **`sessionId: string`**, `children?` |
| `StatTile` | **`label: string`, `value: string`** (already formatted); `current?: number`, `compared?: number \| null`, `goodWhen?: "up" \| "down" \| "neutral"`, `comparedLabel?: string`, `trend?: (number \| null)[]`, `note?: string` (a short line under the number) |
| `Sparkline` | **`values: (number \| null)[]`**; `width?: number` (80), `height?: number` (24), `ariaLabel?: string`, `filled?: boolean`, `className?: string` |
| `CalendarHeatmap` | **`data: { date: string, value: number }[]`, `from: string`, `to: string`, `ariaLabel: string`**; dates are `YYYY-MM-DD`. `onSelect?: (date, value: number \| null) => void`, `describe?: (date, value) => string`, `weekStart?: number` (1 = Monday, 0 = Sunday), `hatched?: string[]` (days drawn hatched over their color; say why in `describe`). The callback's first argument is a **date string**, not an event or object. Unlisted days have null values; invalid/reversed date bounds throw. |
| `WeekdayHourHeatmap` | **`matrix: number[][]`, `ariaLabel: string`**; seven rows of 24 values, first row is `weekStart` (default Monday). `onSelect?: (weekday, hour, value) => void`, `describe?: (weekday, hour, value) => string`, `weekStart?: number` |
| `Chart` | **`option: object`, `ariaLabel: string`**; ECharts option (prefer plain data). `height?: number` (240), `renderer?: "svg" \| "canvas"`, `visible?: boolean`, `update?: "replace" \| "merge"`, `group?: string`, `hideTableToggle?: boolean`, `className?: string`. `onSelect?: (datum) => void`, where datum has `name`, `seriesName`, `seriesIndex`, `dataIndex`, `value`, `data`. `onPeriod?: ({ start, end, from, to }) => void`: start/end are percentages; from/to are axis values. `table?: { columns: string[], rows: (string \| number \| null)[][] }` overrides the inferred table. |

Calendar heatmaps support keyboard arrows, Enter and Space. They draw SVG, not ECharts. Charts and heatmaps use the host's theme automatically. A chart's width follows its container.

### Icons

| Export | Contract |
| --- | --- |
| `Icon` | **`name: string`**; `size?: number` (14), `title?: string`, `className?: string`. Accepts a Lucide kebab-case name (`chart-column`), a curated brand id or a plugin-relative SVG path (`icons/star.svg`). Unknown names show a neutral icon; a title provides the accessible label. |
| `BrandIcon` | **`name: string`** from the supported ids; `size?: number` (16), `color?: string`, `title?: string`, `className?: string`. Original brand colors by default; an explicit color draws its one-color mark. |
| `brandIconNames` | Array of supported brand ids, sorted by display title; treat it as read-only. Use it for a picker rather than assuming every upstream brand exists. |
| `isBrandIcon(name)` | Boolean check before passing an unknown string to `BrandIcon`. |

`Icon` needs the script's React context to resolve a package SVG. Blueprint's `Icon` is a different component; use aliases when importing both. Neither `brandTitle` nor the raw SVG data is exported.

### Distribution helpers

All are pure functions; options are optional. Render the returned chart option with `Chart`.

| Function | Input → output |
| --- | --- |
| `histogram(input, options?)` | `number[]` or `{ value, count }[]`; options `{ bins?: number = 20, scale?: "linear" \| "log", min?, max? }` → `{ bins: { from, to, count }[], total, excluded }`. Log scale counts positive values only; nonfinite/out-of-range values are excluded. Bins include `from`, exclude `to`, except the last includes its end. |
| `histogramOption(result, options?)` | A histogram result; `{ name?: string = "Count", label?: (from, to) => string }` → bar-chart option. |
| `quantile(sorted, p)` | Already sorted numeric values; fraction clamped to 0–1 → interpolated number (`NaN` if empty). Does not sort/filter input. |
| `boxStats(values)` | Filters nonfinite values and sorts → `{ count, min, q1, median, q3, p90, max }`, or `null` without finite values. |
| `boxPlotOption(items, options?)` | `{ name, values: number[] }[]` or items with `{ name, stats: boxStatsResult }`; `{ horizontal?: boolean, p90Name?: string = "P90", name?: string = "Distribution", log?: boolean }` → boxplot + P90 scatter option. Empty value series are omitted. |

```js
import { Chart, histogram, histogramOption, html } from "codealta";
// In a component: values can come from useRpc().data.
const option = histogramOption(histogram([1, 2, 2, 4, 8], { bins: 4 }), { name: "Runs" });
html`<${Chart} option=${option} ariaLabel="Duration distribution" />`;
```

See [registered ECharts features](script-libraries.md#libraries-reached-through-codealta-helpers-only) before using an upstream chart example; not every ECharts series is loaded.

## The `alta` object

- `context`: `{ pluginKey, canvasId, instanceId, spaceId, projectId, sessionId, key, input }`. Inapplicable identifiers are null; `input` is parsed JSON, not a string.
- `visible`, `theme`, `rpc.generation`, `rpc.connected`: **signals**, with `.value` and `.subscribe(listener)` returning an unsubscribe function. In a React component use `useVisible()` / `useTheme()` instead of reading the signal once.
- `closed`: `AbortSignal`, aborted when the content is removed/reloaded; pass it to work you start.
- `versions`: frozen library/interface version values, e.g. `alta.versions["@blueprintjs/core"]`; [version and import index](script-libraries.md#imports-versions-and-upstream-references). Import the host instances rather than packaging another React.

### Theme value

`useTheme()` or `alta.theme.value` gives:

```text
dark: boolean
fontFamily, text, muted, axis, grid, surface, tooltipBackground, tooltipBorder: string
series: string[]        — categorical colors
ramp: string[]          — lightest to strongest sequential colors
diverging: [string, string, string] — low, middle, high
```

Use those values for custom styles/SVG/canvas. There is no `theme.colors` object. Do not guess private CSS variables or inspect document classes to determine the theme.

### Host methods

Call these on `alta.host`, for example `alta.host.openFile("readme.md")`. All return void; an unavailable request does nothing. They are navigation/UI requests, not data-query APIs.

```text
openFile(path, { line?, column? }?)       — relative to the context project; lines are 1-based
openDiff({ projectId? }?)
openSession(sessionId)
openCanvas(canvasId, { pluginKey?, projectId?, sessionId?, key? }?)   — a settings page of a plugin opens Settings on it
openDocumentation(page?, { anchor? }?)   — e.g. "plugins/readme.md"
openLink(url)                           — http/https in the system browser
notify(message, { tone? }?)             — info | success | warning | danger
runCommand(name)                       — command of this plugin
setTitle(string | null)                 — null restores the declared title
setStatus(string | null)                — null clears it
setBadge(string | number | null)        — short tab badge
```

Do not append the project name to the title: the window already labels project-scoped tabs with their project. Icons on C# contributions use Lucide names; Blueprint button `icon` props use Blueprint's names.
