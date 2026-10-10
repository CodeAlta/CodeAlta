/** The version of the `alta` object and the `codealta` module that scripts of plugins are written against. It grows when either changes in a way a script can see. */
export const altaInterfaceVersion = 1;

/**
 * The versions of the libraries the application lends to scripts (`src/lent`), as `alta.versions` tells them. `pluginScript.test.ts`
 * compares them with `package.json`, so an update of a library cannot go by unnoticed here.
 */
export const lentVersions: Readonly<Record<string, string>> = Object.freeze({
  "react": "19.2.8",
  "react-dom": "19.2.8",
  "@blueprintjs/core": "6.21.0",
  "@blueprintjs/table": "6.3.0",
  "flexlayout-react": "0.11.0",
  "lucide-react": "0.511.0",
});
