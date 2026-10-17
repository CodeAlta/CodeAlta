import { useContext, useMemo, useRef } from "react";
import { localSearch } from "#neoastra";
import { LandingShellContext } from "../../landing/landingShell";
import { useAlta } from "../../pluginScript/PluginScript";
import { useTheme, useVisible } from "../../pluginScript/codealta";
import { createSearchCanvasApi } from "./api";
import { SearchCanvas, type SearchCanvasActions } from "./SearchCanvas";

// The module of the Search canvas, the one the built-in Search plugin names with `PluginScript.App("search")`. It asks the `localSearch`
// service of the window for what it shows, for the host and the space of the window (the shell around the tabs of canvases lends them, with
// the projects of the space and the way to open a session or a message); the rest (a file, the guide) goes through `alta.host`.

/** The Search canvas, for the window to draw in a tab. */
export default function Canvas() {
  const alta = useAlta();
  const visible = useVisible();
  const theme = useTheme();
  const shell = useContext(LandingShellContext);
  const epoch = shell?.epoch ?? null;
  const spaceId = shell?.space.id ?? alta.context.spaceId ?? "default";
  const api = useMemo(() => epoch ? createSearchCanvasApi(localSearch, epoch, spaceId) : null, [epoch, spaceId]);
  // What the actions reach is read when they run: a new shell does not draw the canvas again.
  const latest = useRef({ shell, alta });
  latest.current = { shell, alta };
  const actions = useMemo<SearchCanvasActions>(() => ({
    openSession: id => latest.current.shell?.openSession(id),
    openMessage: (id, offset) => { const lent = latest.current.shell; if (lent?.openMessage) lent.openMessage(id, offset); else lent?.openSession(id); },
    openFile: path => latest.current.alta.host.openFile(path),
    openDocumentation: (page, anchor) => latest.current.alta.host.openDocumentation(page ?? undefined, anchor ? { anchor } : undefined),
    runCommand: name => latest.current.shell?.run(name) ?? false,
    notify: (message, tone) => latest.current.alta.host.notify(message, { tone }),
  }), []);
  const colors = useMemo(() => ({ series: theme.series, muted: theme.muted }), [theme]);
  return <SearchCanvas api={api} projects={shell?.projects ?? null} visible={visible} colors={colors} actions={actions} />;
}
