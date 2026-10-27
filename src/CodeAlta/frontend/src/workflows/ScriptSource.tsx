import { useMemo } from "react";
import { CodeLines } from "../ToolCallViews";

/**
 * The source of a script of Code Mode, in the colors of JavaScript and with the numbers of its lines: the errors of a run
 * name the line they happened on.
 */
export function ScriptSource({ source, className }: { source: string; className?: string }) {
  const lines = useMemo(() => source.replace(/\r\n/gu, "\n").replace(/\n$/u, "").split("\n"), [source]);
  return <div className={className ? `workflow-script-source ${className}` : "workflow-script-source"}><CodeLines lines={lines} start={1} language="javascript" /></div>;
}
