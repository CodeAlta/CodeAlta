import { useContext, useMemo, useRef } from "react";
import { fleetServer } from "#neoastra";
import { LandingShellContext } from "../../landing/landingShell";
import { useAlta } from "../../pluginScript/PluginScript";
import { useVisible } from "../../pluginScript/codealta";
import { FleetCanvas, type FleetCanvasActions, type FleetCanvasApi } from "./FleetCanvas";

// The module of the Fleet canvas, the one the built-in Fleet plugin names with `PluginScript.App("fleet")`. It asks the `fleetServer` service
// of the window for the directory of the server, for the host of the window (the shell around the tabs of canvases lends it, with the way to
// open a session).

/** The Fleet canvas, for the window to draw in a tab. */
export default function Canvas() {
  const alta = useAlta();
  const visible = useVisible();
  const shell = useContext(LandingShellContext);
  const epoch = shell?.epoch ?? null;
  const api = useMemo<FleetCanvasApi | null>(() => epoch ? {
    directory: signal => fleetServer.directory({ expectedEpoch: epoch, linkId: null }, { signal, timeoutMilliseconds: 20_000 }),
    watch: signal => fleetServer.watch({ expectedEpoch: epoch }, { signal }),
  } : null, [epoch]);
  // What the actions reach is read when they run: a new shell does not draw the canvas again.
  const latest = useRef({ shell, alta });
  latest.current = { shell, alta };
  const actions = useMemo<FleetCanvasActions>(() => ({
    openSession: id => latest.current.shell?.openSession(id),
    runCommand: name => latest.current.shell?.run(name) ?? false,
  }), []);
  return <FleetCanvas api={api} visible={visible} actions={actions} />;
}
