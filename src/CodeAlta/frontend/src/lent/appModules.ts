/** A module of the application's own build that a built-in plugin names with `PluginScript.App(name)`: a canvas, a dialog or a card written in the frontend. */
export type AppModule = Readonly<{
  /** The name the plugin gives to `PluginScript.App`: lowercase letters, digits and `-`, starting with a letter. */
  name: string;
  /** The entry file, relative to the frontend folder. It exports a default component or `mount(root, alta)`, as every script does, and imports application code directly. */
  entry: string;
}>;

/**
 * The modules of the application's own build that built-in plugins load. Each is an entry of the Vite build that the plugin
 * `codealta:lent-modules` of `vite.config.ts` emits at `lib/app/<name>.js` and the application serves with its own files, sharing its chunks (and its
 * libraries) with the rest of the page: no import map is needed for it, and a module is loaded when a tab first shows it. Add a line to give a
 * built-in plugin a module (see "Application modules" in doc/development-guide.md); `appModules.test.ts` checks the list.
 */
export const appModules: readonly AppModule[] = [];

/** The address of a module in the build output, relative to the application's origin. */
export const appModuleFile = (name: string): string => `lib/app/${name}.js`;
