/** The version of the `alta` object and the `codealta` module that scripts of plugins are written against. It grows when either changes in a way a script can see. */
export const altaInterfaceVersion = 1;

/**
 * The versions of the libraries the application lends to scripts (`src/lent`), as `alta.versions` tells them. `pluginScript.test.ts`
 * compares them with `package.json`, so an update of a library cannot go by unnoticed here.
 */
export const lentVersions: Readonly<Record<string, string>> = Object.freeze({
  "react": "19.3.1",
  "react-dom": "19.3.1",
  "@blueprintjs/core": "6.23.2",
  "@blueprintjs/table": "6.3.4",
  "flexlayout-react": "0.11.2",
  "lucide-react": "0.577.0",
  "@xyflow/react": "12.12.1",
  "@observablehq/plot": "0.6.19",
});
