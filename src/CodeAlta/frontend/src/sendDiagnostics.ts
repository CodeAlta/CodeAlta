/**
 * Console diagnostics of the Send path (composer action and guards, dispatch, settlement), kept for
 * investigating a Send that locks up. They are off; turn them on from DevTools with
 * `localStorage.setItem("codealta.debug.send", "1")` and reload the window.
 */
const enabled = (() => {
  try { return typeof localStorage !== "undefined" && localStorage.getItem("codealta.debug.send") === "1"; }
  catch { return false; } // Storage can be unavailable; diagnostics then stay off.
})();

export const sendDiagnostics = {
  info(message: string, details?: Record<string, unknown>): void {
    if (enabled) console.info(`[CodeAlta Send] ${message}`, ...(details ? [details] : []));
  },
  warn(message: string, details?: Record<string, unknown>): void {
    if (enabled) console.warn(`[CodeAlta Send] ${message}`, ...(details ? [details] : []));
  },
};
