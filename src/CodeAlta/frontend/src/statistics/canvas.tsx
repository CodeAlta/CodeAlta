import { useEffect, useMemo, useState } from "react";
import { useAlta } from "../pluginScript/PluginScript";
import { useVisible } from "../pluginScript/codealta";
import { statisticsContext } from "./canvasContext";
import { createRpcApi, type StatisticsDirectory } from "./rpcApi";
import { StatisticsCanvas } from "./StatisticsCanvas";

// The module of the Statistics canvas, the one the built-in Statistics plugin names with `PluginScript.App("statistics")`: it gives the canvas its
// questions over `alta.rpc` and the place it was opened at, and mounts it.

/** The Statistics canvas, for the window to draw in a tab. */
export default function Canvas() {
  const alta = useAlta();
  const visible = useVisible();
  const api = useMemo(() => createRpcApi(alta.rpc), [alta]);
  // The directory comes first: the first frame of the canvas starts from the space and the project it names. A tab that is hidden
  // reads nothing, so a tab restored behind another asks for it when it is first shown; it is then kept.
  const [directory, setDirectory] = useState<{ value: StatisticsDirectory | null } | null>(null);
  const known = directory !== null;
  useEffect(() => {
    if (!visible || known) return;
    const controller = new AbortController();
    api.directory(controller.signal).then(
      value => { if (!controller.signal.aborted) setDirectory({ value }); },
      () => { if (!controller.signal.aborted) setDirectory({ value: null }); });
    return () => controller.abort();
  }, [api, visible, known]);
  const context = useMemo(() => directory ? statisticsContext(alta, visible, directory.value) : null, [alta, visible, directory]);
  if (!context) return <div className="stats-loading" role="status" aria-busy="true" />;
  return <StatisticsCanvas api={api} context={context} />;
}
