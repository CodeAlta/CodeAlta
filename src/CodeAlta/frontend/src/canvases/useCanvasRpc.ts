import { useEffect, useMemo } from "react";
import type { AltaRpc } from "../pluginScript/alta";
import type { CanvasHub } from "./canvasHub";
import { createCanvasRpc } from "./canvasRpc";

/**
 * The `alta.rpc` of the tab of an instance, or undefined while the tab has none. The controller is made without opening anything (the first
 * call of a script connects), so React's development double run of the render and of the effects costs nothing and never opens two sessions:
 * the effect only says that the controller serves calls while the tab is drawn, and ends its connection when the tab goes away.
 */
export function useCanvasRpc(hub: CanvasHub, instanceId: string | null, visible: boolean): AltaRpc | undefined {
  const controller = useMemo(() => instanceId ? createCanvasRpc({ carrier: hub.rpc, instanceId }) : null, [hub, instanceId]);
  useEffect(() => {
    controller?.attach();
    return () => controller?.detach();
  }, [controller]);
  useEffect(() => { controller?.setVisible(visible); }, [controller, visible]);
  return controller?.rpc;
}
