# Script libraries

Read this index to choose a library; follow its versioned declarations only for the props or methods you need. [Script API](script-api.md) covers `codealta`, `alta`, lifecycle and RPC. These are the **complete bare import names** available to plugin scripts, not a list of everything installed in CodeAlta's frontend.

## Imports, versions and upstream references

Versions below describe this CodeAlta build. In a running script, `alta.versions` is authoritative: package-name keys for the six libraries, plus numeric `interface` for `codealta`. React subpaths share their parent package's version; they have no separate version key. Upstream websites can describe newer releases: use the **pinned declarations** for exact signatures, including deprecated aliases.

| Import | Version | Surface | API / repository / pinned declarations |
| --- | --- | --- | --- |
| `react` | 19.2.8 | Named exports below + default React | [API](https://react.dev/reference/react) · [GitHub](https://github.com/react/react) |
| `react/jsx-runtime` | 19.2.8 | Named exports below; compiled JSX | [JSX transform](https://legacy.reactjs.org/blog/2020/09/22/introducing-the-new-jsx-transform.html) · same repository |
| `react-dom` | 19.2.8 | Named exports below + default ReactDOM | [API](https://react.dev/reference/react-dom) · same repository |
| `react-dom/client` | 19.2.8 | Named exports below + default client namespace | [API](https://react.dev/reference/react-dom/client) · same repository |
| `@blueprintjs/core` | 6.21.0 | All root named runtime exports | [API](https://blueprintjs.com/docs/#core) · [GitHub](https://github.com/palantir/blueprint) · [types](https://unpkg.com/@blueprintjs/core@6.21.0/lib/esm/index.d.ts) |
| `@blueprintjs/table` | 6.3.0 | All root named runtime exports | [API](https://blueprintjs.com/docs/#table) · same repository · [types](https://unpkg.com/@blueprintjs/table@6.3.0/lib/esm/index.d.ts) |
| `flexlayout-react` | 0.11.0 | All root named runtime exports | [Guide / GitHub](https://github.com/caplin/FlexLayout#usage) · [types](https://unpkg.com/flexlayout-react@0.11.0/types/index.d.ts) |
| `lucide-react` | 0.511.0 | All root named runtime exports | [API](https://lucide.dev/guide/packages/lucide-react) · [GitHub](https://github.com/lucide-icons/lucide) · [types / every icon name](https://unpkg.com/lucide-react@0.511.0/dist/lucide-react.d.ts) |
| `codealta` | 1 | CodeAlta interface, not an npm package | [Exports and contracts](script-api.md) · [GitHub](https://github.com/CodeAlta/CodeAlta) |

**Import rules.** Use named imports except for the three defaults explicitly listed above. Only the listed subpaths resolve: no `react-dom/server`, `react/jsx-dev-runtime`, Blueprint deep imports, `@blueprintjs/icons`, `@blueprintjs/select`, `@blueprintjs/datetime`, or `lucide-react/dynamic`. CSS imports, Node modules, CDN imports and `eval` are not an authoring path. Blueprint/Table/FlexLayout styles already belong to the window; do not import their CSS, bundle React or replace the host's Blueprint providers. Own modules may use relative `.js` imports inside the plugin package. If compiling JSX yourself, emit JavaScript and keep the host libraries external; target `react/jsx-runtime`, not its development counterpart.

The following inventories are **runtime values**, not TypeScript-only names such as `ButtonProps`, `IconName` or `IJsonModel`. Being exported does not grant access to the application's state or make every upstream feature work in a desktop WebView (for example, server rendering or a FlexLayout browser-window popout).

## React

The default component of a script is already mounted in CodeAlta's React tree. Prefer that form: its hooks, Blueprint portals and `useAlta()` share the host context. No `createRoot` is needed.

<!-- exports: react -->
| Use | Named exports |
| --- | --- |
| Elements and components | `Activity`, `Children`, `Component`, `Fragment`, `Profiler`, `PureComponent`, `StrictMode`, `Suspense`, `cloneElement`, `createElement`, `isValidElement`, `lazy`, `memo` |
| Context and refs | `createContext`, `createRef`, `forwardRef`, `useContext`, `useId`, `useImperativeHandle`, `useRef` |
| State and effects | `use`, `useActionState`, `useCallback`, `useDebugValue`, `useDeferredValue`, `useEffect`, `useEffectEvent`, `useInsertionEffect`, `useLayoutEffect`, `useMemo`, `useOptimistic`, `useReducer`, `useState`, `useSyncExternalStore`, `useTransition` |
| Other | `act`, `cache`, `cacheSignal`, `captureOwnerStack`, `startTransition`, `version` |
<!-- /exports -->

React's internal exports and `unstable_useCacheRefresh` are not named exports here. Follow upstream environment restrictions: export presence does not make server-only APIs useful in a client component.

### react/jsx-runtime

<!-- exports: react/jsx-runtime -->
`Fragment`, `jsx`, `jsxs`.
<!-- /exports -->

### react-dom

<!-- exports: react-dom -->
`createPortal`, `flushSync`, `preconnect`, `prefetchDNS`, `preinit`, `preinitModule`, `preload`, `preloadModule`, `requestFormReset`, `unstable_batchedUpdates`, `useFormState`, `useFormStatus`, `version`.
<!-- /exports -->

### react-dom/client

<!-- exports: react-dom/client -->
`createRoot`, `hydrateRoot`.
<!-- /exports -->

A `mount(root, alta)` may create its **own** React root and return `() => reactRoot.unmount()`, but that separate tree does not inherit CodeAlta's contexts. Pass `alta` explicitly there; `useAlta()` and helpers relying on it will not work. Prefer the default component for UI with CodeAlta helpers. `hydrateRoot` is not needed for a normal plugin fragment. Resource-hint APIs do not bypass the page's content security policy.

## Blueprint core

All root exports, grouped for lookup. For each component's props, follow the API or pinned `lib/esm/index.d.ts` above and its relative declaration links. Prefer current components over legacy aliases; in particular **use `PopoverNext`, not `Popover`**, with React 19. Do not change shared providers or global focus styling.

<!-- exports: @blueprintjs/core -->
| Use | Named exports |
| --- | --- |
| Buttons and forms | `AnchorButton`, `Button`, `ButtonGroup`, `Checkbox`, `CheckboxCard`, `ControlGroup`, `EditableText`, `FileInput`, `FormGroup`, `HTMLSelect`, `InputGroup`, `Label`, `MultiSlider`, `MultiSliderHandle`, `NumericInput`, `Radio`, `RadioCard`, `RadioGroup`, `RangeSlider`, `SegmentedControl`, `Slider`, `Switch`, `SwitchCard`, `TagInput`, `TextArea` |
| Content and layout | `Blockquote`, `Breadcrumb`, `Breadcrumbs`, `Callout`, `Card`, `CardList`, `Code`, `Collapse`, `CompoundTag`, `Divider`, `EntityTitle`, `H1`, `H2`, `H3`, `H4`, `H5`, `H6`, `HTMLTable`, `Icon`, `IconNext`, `Link`, `Navbar`, `NavbarDivider`, `NavbarGroup`, `NavbarHeading`, `NonIdealState`, `OL`, `OverflowList`, `Pre`, `ProgressBar`, `ResizeSensor`, `Section`, `SectionCard`, `Spinner`, `Tag`, `Text`, `UL` |
| Menus, tabs and trees | `ContextMenu`, `ContextMenuPopover`, `Menu`, `MenuDivider`, `MenuItem`, `PanelStack`, `PanelStack2`, `Tab`, `TabPanel`, `Tabs`, `TabsExpander`, `Tree`, `TreeNode`, `hideContextMenu`, `showContextMenu` |
| Overlays | `Alert`, `Dialog`, `DialogBody`, `DialogFooter`, `DialogStep`, `Drawer`, `MultistepDialog`, `Overlay`, `Overlay2`, `OverlayToaster`, `Popover`, `PopoverNext`, `Portal`, `Toast`, `Toast2`, `Tooltip` |
| Keyboard | `Hotkey`, `Hotkeys`, `HotkeysTarget`, `HotkeysTarget2`, `KeyComboTag`, `comboMatches`, `getKeyCombo`, `getKeyComboString`, `parseKeyCombo`, `useHotkeys` |
| Constants and enums | `Alignment`, `Boundary`, `ButtonVariant`, `Classes`, `Colors`, `DISPLAYNAME_PREFIX`, `DrawerSize`, `Elevation`, `HandleInteractionKind`, `HandleType`, `IconSize`, `Intent`, `Keys`, `NonIdealStateIconSize`, `POPOVER_NEXT_PLACEMENTS`, `PopoverAnimation`, `PopoverInteractionKind`, `PopoverPosition`, `PopperPlacements`, `PopupKind`, `Position`, `Size`, `SpinnerSize`, `TextAlignment` |
| Providers and infrastructure | `AbstractComponent`, `AbstractPureComponent`, `BlueprintProvider`, `FocusStyleManager`, `HotkeysContext`, `HotkeysProvider`, `OverlaysContext`, `OverlaysProvider`, `PortalContext`, `PortalProvider`, `Utils`, `getRef`, `isRefCallback`, `isRefObject`, `mergeRefs`, `refHandler`, `removeNonHTMLProps`, `setRef`, `useAsyncControllableValue`, `useIsomorphicLayoutEffect`, `useOverlayStack`, `usePrevious` |
| Popover migration | `popoverPlacementToNextPlacement`, `popoverPositionToNextPlacement`, `popoverPropsToNextProps`, `popperBoundaryToNextBoundary`, `popperModifiersToNextMiddleware` |
<!-- /exports -->

Common contracts: `Button` takes `text`/children, `onClick`, `intent`, `disabled`, `loading`; `InputGroup`/`TextArea`/`HTMLSelect` use `value` and `onChange(event)`; `Checkbox`/`Switch` use `checked` and `onChange(event)`; `NumericInput.onValueChange(number, text, input)` is **not** a DOM change event. `Tabs` takes `selectedTabId`, `onChange(id)` and `Tab` children with `id`, `title`, `panel`. Use React state for controlled values.

```js
import { useId, useState } from "react";
import { Button, FormGroup, InputGroup, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { html, useAlta } from "codealta";

export default function Controls() {
    const alta = useAlta(), id = useId();
    const [name, setName] = useState("");
    const menu = html`<${Menu}><${MenuItem} text="Greet"
        onClick=${() => alta.host.notify(`Hello ${name}`)} /><//>`;
    return html`<div className="alta-column">
        <${FormGroup} label="Name" labelFor=${id}>
            <${InputGroup} id=${id} value=${name} onChange=${e => setName(e.target.value)} />
        <//>
        <${PopoverNext} content=${menu} placement="bottom-start">
            <${Button} icon="more" text="Actions" />
        <//>
    </div>`;
}
```

Blueprint `icon="more"` uses [Blueprint icon names](https://blueprintjs.com/docs/#icons), not Lucide names. To use a CodeAlta/Lucide icon in a button, pass a React element to `icon`. Alias colliding imports (`Code`, `Icon`, `Utils`, `Orientation`, `Rect`) rather than assuming the libraries share their meanings.

## Blueprint table

Use `HTMLTable` from core for a simple HTML table; use this package for virtualized rows, selection, resizing and editing. Give its container a height.

<!-- exports: @blueprintjs/table -->
| Use | Named exports |
| --- | --- |
| Rendering and editing | `Cell`, `Column`, `ColumnHeaderCell`, `CopyCellsMenuItem`, `EditableCell`, `EditableCell2`, `EditableName`, `JSONFormat`, `RowHeaderCell`, `Table`, `Table2`, `TruncatedFormat` |
| Selection and geometry | `Clipboard`, `DragSelectable`, `Draggable`, `Grid`, `HorizontalCellDivider`, `Orientation`, `Rect`, `RegionCardinality`, `Regions`, `ResizeHandle`, `SelectionModes`, `Utils` |
| Modes | `ColumnLoadingOption`, `FocusMode`, `RenderMode`, `RowLoadingOption`, `TableLoadingOption`, `TruncatedPopoverMode` |
<!-- /exports -->

```js
import { Cell, Column, Table } from "@blueprintjs/table";
import { html } from "codealta";

export default function Rows() {
    const rows = ["Build", "Test"];
    return html`<div style=${{ height: 240 }}>
        <${Table} numRows=${rows.length}>
            <${Column} name="Step" cellRenderer=${row => html`<${Cell}>${rows[row]}<//>`} />
        <//>
    </div>`;
}
```

`cellRenderer(rowIndex, columnIndex)` returns an element. Selection uses regions (`Regions.row`, `.column`, `.cell`), not row records. Keep edited data in React/plugin state; editable cells do not persist it for you.

## FlexLayout

A plugin may own a dock **inside its canvas**, not read or modify CodeAlta's dock. Start with `Model.fromJson({ global: {}, borders: [], layout: { type: "row", children: [...] } })`; retain the model with `useState(() => ...)`. Render `Layout` with `model` and `factory(node)`, which returns content for `node.getComponent()`. Its container needs `position: "relative"` and an explicit height. Use `model.doAction(Actions...)` to change it, `model.toJson()` to serialize it, and your plugin RPC to persist it. Do not assume browser-window popouts are supported.

<!-- exports: flexlayout-react -->
| Use | Named exports |
| --- | --- |
| Layout and model | `Action`, `Actions`, `BorderNode`, `BorderSet`, `ContextMenuBuilder`, `DockLocation`, `DropInfo`, `GroupAction`, `Layout`, `Model`, `ModelLayout`, `Node`, `Orientation`, `Rect`, `RowNode`, `TabGroupNode`, `TabLayout`, `TabNode`, `TabSetNode`, `useUndo` |
| Constants and menus | `BULK_CLOSE_ACTIONS`, `CLASSES`, `DefaultMax`, `DefaultMin`, `FlexLayoutVersion`, `I18nLabel`, `I18nLabelDefaults`, `ICloseType`, `NODE_CONTEXT_ACTIONS`, `PopupMenu`, `defaultKeyMap`, `getNodeContextActions`, `getNodeContextMenuItem`, `getNodeContextMenuItems`, `getTabGroupMenuItems`, `showGroupMenu`, `showOverflowMenu`, `showPopupMenu` |
| Icons | `AddIcon`, `AsterickIcon`, `CloseIcon`, `EdgeIcon`, `GripIcon`, `MaximizeIcon`, `MenuIcon`, `OverflowIcon`, `PinIcon`, `PopoutFloatIcon`, `PopoutIcon`, `RestoreIcon`, `SettingsIcon` |
<!-- /exports -->

## Lucide

Every root icon export is available: PascalCase components such as `ChartColumn`, their aliases (`ChartColumnIcon`, `LucideChartColumn`), plus `Icon`, `createLucideIcon` and the `icons` namespace. The [icon browser](https://lucide.dev/icons/) is searchable; the pinned declarations above are the exact version's complete inventory (including aliases), avoiding thousands of repeated names here. No default export. `LucideIcon` and `IconNode` are types, not runtime exports.

```js
import { ChartColumn } from "lucide-react";
import { html } from "codealta";
// In a component's return value:
html`<${ChartColumn} size=${16} strokeWidth=${1.5} aria-label="Readings" />`;
```

Icon props include `size` (24), `color` (`currentColor`), `strokeWidth` (2), `absoluteStrokeWidth`, `className` and SVG attributes. Prefer `codealta.Icon name="chart-column"` for a string name, package SVG or brand; prefer a named Lucide import when you need its SVG props. Importing `lucide-react` loads the full lent icon module; `codealta.Icon` lazily loads it only for names outside the window's usual icons. Brand logos are the curated `codealta.BrandIcon`/`brandIconNames`, not the Lucide set.

## Libraries reached through CodeAlta helpers only

These are **not import-map entries**. Use the helper, not `import ... from "echarts"` (or another name below). Versions are build dependencies, not keys of `alta.versions`.

| Library | Version | Entry available to scripts | Upstream |
| --- | --- | --- | --- |
| `echarts` | 6.1.0 | `Chart`, distribution helpers; registered features below | [Option API](https://echarts.apache.org/en/option.html) · [GitHub](https://github.com/apache/echarts) |
| `markdown-it` | 15.0.2 | `Markdown` | [API / GitHub](https://github.com/markdown-it/markdown-it) |
| `highlight.js` | 11.12.0 | `Code`, Markdown code fences | [Languages / GitHub](https://github.com/highlightjs/highlight.js) |
| `mermaid` | 11.17.2 | `Diagram`, Mermaid fences | [Syntax](https://mermaid.js.org/intro/) · [GitHub](https://github.com/mermaid-js/mermaid) |
| `dompurify` | 3.4.14 | `alta.html(text)` with CodeAlta's fragment policy | [API / GitHub](https://github.com/cure53/DOMPurify) |
| `@lobehub/icons-static-svg` | 1.95.1 | Curated `BrandIcon` assets, not the whole collection | [GitHub](https://github.com/lobehub/lobe-icons) |
| `simple-icons` | 16.34.0 | Curated `BrandIcon` assets, not the whole collection | [Catalog](https://simpleicons.org) · [GitHub](https://github.com/simple-icons/simple-icons) |

`Chart` registers ECharts **bar, boxplot, custom, heatmap, line, pie, scatter, treemap**; grid, calendar, dataset, transform, data zoom, brush, legend, title, tooltip, visual map, mark area, mark line and ARIA; label layout, universal transition, SVG/canvas renderers. It does not expose the engine, registration APIs, map/geo, or every chart type shown upstream. Script options can contain functions (for example `custom.renderItem`); HTML `alta-chart` JSON cannot. Prefer data-only options when possible. [Chart props and distribution helpers](script-api.md#components) describe CodeAlta's wrapper.

Monaco, CodeMirror, xterm, the app's RPC client, internal React contexts, and other frontend dependencies are **not** plugin modules. Use `alta.host.openFile`/`openSession` for host views and `alta.rpc` for the C# plugin's data; do not inspect the window's private state or bundled chunks.
