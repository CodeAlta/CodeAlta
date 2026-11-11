import { PaintedProgressBar } from "../PaintedProgressBar";

/**
 * The progress of a program a machine installs or updates for a provider, as the download of Claude Code on this computer shows
 * its own (`CliDownloadProgress`): what the machine says it does, and a bar, striped while the size is not known.
 */
export function ProgramProgress({ message, fraction }: { message: string | null | undefined; fraction: number | null | undefined }) {
  const known = typeof fraction === "number";
  return <div className="provider-claude-cli-progress machine-program-progress" role="status">
    {message && <span>{message}</span>}
    <PaintedProgressBar intent="primary" stripes={!known} animate={!known} value={known ? Math.min(1, Math.max(0, fraction)) : undefined} />
  </div>;
}
