# Script API quick reference

The `codealta` module is supplied by the running desktop application. Import it by name; write plain JavaScript, not TypeScript or JSX. No npm install or bundle is needed. This reference describes CodeAlta's helpers, not all of React or Blueprint. Use `alta plugin api <name>` for the **C#** side.

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
| `FileLink` | **`path: string`**, `line?: number`, `children?` (label). Relative paths use the canvas project. |
| `SessionLink` | **`sessionId: string`**, `children?` |
| `StatTile` | **`label: string`, `value: string`** (already formatted); `current?: number`, `compared?: number \| null`, `goodWhen?: "up" \| "down" \| "neutral"`, `comparedLabel?: string`, `trend?: (number \| null)[]` |
| `Sparkline` | **`values: (number \| null)[]`**; `width?: number` (80), `height?: number` (24), `ariaLabel?: string`, `filled?: boolean`, `className?: string` |
| `CalendarHeatmap` | **`data: { date: string, value: number }[]`, `from: string`, `to: string`, `ariaLabel: string`**; dates are `YYYY-MM-DD`. `onSelect?: (date, value: number \| null) => void`, `describe?: (date, value) => string`, `weekStart?: number` (1 = Monday, 0 = Sunday). The callback's first argument is a **date string**, not an event or object. Unlisted days have null values; invalid/reversed date bounds throw. |
| `WeekdayHourHeatmap` | **`matrix: number[][]`, `ariaLabel: string`**; seven rows of 24 values, first row is `weekStart` (default Monday). `onSelect?: (weekday, hour, value) => void`, `describe?: (weekday, hour, value) => string`, `weekStart?: number` |
| `Chart` | **`option: object`, `ariaLabel: string`**; ECharts data-only option. `height?: number` (240), `renderer?: "svg" \| "canvas"`, `visible?: boolean`, `update?: "replace" \| "merge"`, `group?: string`, `hideTableToggle?: boolean`, `className?: string`. `onSelect?: (datum) => void`, where datum has `name`, `seriesName`, `seriesIndex`, `dataIndex`, `value`, `data`. |

Calendar heatmaps support keyboard arrows, Enter and Space. They draw SVG, not ECharts. Charts and heatmaps use the host's theme automatically. A chart's width follows its container.

## The `alta` object

- `context`: `{ pluginKey, canvasId, instanceId, spaceId, projectId, sessionId, key, input }`. Inapplicable identifiers are null; `input` is parsed JSON, not a string.
- `visible`, `theme`, `rpc.generation`, `rpc.connected`: **signals**, with `.value` and `.subscribe(listener)` returning an unsubscribe function. In a React component use `useVisible()` / `useTheme()` instead of reading the signal once.
- `closed`: `AbortSignal`, aborted when the content is removed/reloaded; pass it to work you start.
- `versions`: library/interface version values. The host lends `react`, `react-dom`, `@blueprintjs/core`, `@blueprintjs/table`, `flexlayout-react`, `lucide-react`; import the same instances rather than packaging another React.

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
openCanvas(canvasId, { pluginKey?, projectId?, sessionId?, key? }?)
openDocumentation(page?, { anchor? }?)   — e.g. "plugins/readme.md"
openLink(url)                           — http/https in the system browser
notify(message, { tone? }?)             — info | success | warning | danger
runCommand(name)                       — command of this plugin
setTitle(string | null)                 — null restores the declared title
setStatus(string | null)                — null clears it
setBadge(string | number | null)        — short tab badge
```

Do not append the project name to the title: the window already labels project-scoped tabs with their project. Icons on C# contributions use Lucide names; Blueprint button `icon` props use Blueprint's names.
