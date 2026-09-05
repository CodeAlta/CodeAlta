import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { boot, type BootStatus } from "#neoastra";
import "./style.css";

function App() {
  const [status, setStatus] = useState<BootStatus>();
  const [error, setError] = useState<string>();
  useEffect(() => {
    const abort = new AbortController();
    void boot.status({}, { signal: abort.signal, timeoutMilliseconds: 8_000 })
      .then(value => { if (!abort.signal.aborted) setStatus(value); })
      .catch(() => { if (!abort.signal.aborted) setError("The desktop bridge could not be initialized. Close the window and try again."); });
    return () => abort.abort();
  }, []);

  return <main>
    <h1>CodeAlta</h1>
    <p className="badge">Desktop — in development</p>
    <p>This milestone provides a native boot surface only. Agent sessions, providers, settings and shared profile ownership are not connected yet.</p>
    <p>Use <code>altatui</code> for current agent functionality.</p>
    <p role="status">{error ?? (status ? `Desktop bridge ready · ${status.version}` : "Initializing desktop bridge…")}</p>
    <p className="detail">Only the explicitly supplied temporary data root is used for the native browser cache. No production profile is opened.</p>
  </main>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><App /></StrictMode>);
