import { useEffect } from "react";
import { createRoot } from "react-dom/client";
import * as Dialog from "@radix-ui/react-dialog";
import { probe } from "#neoastra";
import { check, eventually } from "./checks";
import "./style.css";

function App() {
  useEffect(() => {
    let disposeVisuals: (() => void) | undefined;
    const output = document.querySelector<HTMLOutputElement>("#results")!;
    const report = (text: string) => { output.textContent += `${text}\n`; };
    const violations: string[] = [];
    const onViolation = (event: SecurityPolicyViolationEvent) => violations.push(`${event.violatedDirective}: ${event.blockedURI}`);
    document.addEventListener("securitypolicyviolation", onViolation);

    async function run() {
      report(`Browser: ${navigator.userAgent}`);
      const browserData = navigator as Navigator & { userAgentData?: { getHighEntropyValues(hints: string[]): Promise<unknown> } };
      if (browserData.userAgentData) report(`Browser full version: ${JSON.stringify(await browserData.userAgentData.getHighEntropyValues(["fullVersionList"]))}`);
      check(location.href === "app://codealta/index.html", `Unexpected document: ${location.href}`);
      const response = await probe.hello({ name: "desktop" }, { timeoutMilliseconds: 8_000 });
      check(response.message === "Hello, desktop (C#)", "Generated RPC round-trip mismatch");
      report("PASS generated typed RPC round-trip");

      const abort = new AbortController();
      const stream = await probe.observe({}, { signal: abort.signal, timeoutMilliseconds: 8_000 });
      const iterator = stream[Symbol.asyncIterator]();
      check((await iterator.next()).value?.index === 0, "First channel item out of order");
      abort.abort();
      let canceled = false;
      try { await iterator.next(); } catch (error) { canceled = (error as { code?: string }).code === "operation_canceled"; }
      check(canceled, "Channel abort did not reject further reads");
      await eventually(async () => (await probe.state({})).disposed === 1, "abort enumerator disposal");
      report("PASS AbortSignal after channel opening and backend disposal");
      const second = await probe.observe({});
      for await (const item of second) {
        check(item.index === 0, "Second channel item out of order");
        break;
      }
      await eventually(async () => (await probe.state({})).disposed === 2, "iterator return disposal");
      report("PASS iterator break and backend disposal");

      const visuals = await import("./visuals");
      disposeVisuals = await visuals.mount(document.querySelector<HTMLElement>("#visuals")!, report);
      const trigger = document.querySelector<HTMLButtonElement>("#dialog-trigger")!;
      trigger.focus();
      trigger.click();
      await eventually(() => document.activeElement?.id === "dialog-input", "Radix portal input autofocus");
      check(document.querySelector('[role="dialog"]') !== null, "Radix dialog did not open");
      document.querySelector<HTMLButtonElement>("#dialog-close")!.click();
      await eventually(() => document.activeElement === trigger && !document.querySelector('[role="dialog"]'), "Radix close focus restoration");
      report("PASS Radix portal, autofocus, close and focus restoration");

      check(violations.length === 0, `CSP violations: ${violations.join(", ")}`);
      const resources = performance.getEntriesByType("resource") as PerformanceResourceTiming[];
      check(resources.every(resource => resource.name.startsWith("app://codealta/") || resource.name.startsWith("data:")), "Unexpected remote resource request");
      // Chromium may omit custom-scheme loads from Resource Timing. The imported module verifies
      // its actual import.meta.url and the loaded stylesheet rather than fabricating timing entries.
      report(`PASS local resource graph, no CSP violations (Resource Timing exposed ${resources.length} entries)`);
      report("PASS frontend smoke complete");
      document.documentElement.dataset.probePassed = "true";
    }

    void run().catch(error => report(`FAIL ${String(error)}\n${(error as Error).stack ?? ""}`)).finally(() => {
      document.documentElement.dataset.probeDone = "true";
    });
    return () => {
      disposeVisuals?.();
      document.removeEventListener("securitypolicyviolation", onViolation);
    };
  }, []);

  return <main>
    <h1>CodeAlta Desktop Probe</h1>
    <p>Fake data only · packaged local assets · no providers, plugins, or production profile</p>
    <Dialog.Root>
      <Dialog.Trigger id="dialog-trigger">Open Radix probe</Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="overlay" />
        <Dialog.Content className="dialog">
          <Dialog.Title>Probe dialog</Dialog.Title>
          <Dialog.Description>Checks portal rendering and focus restoration.</Dialog.Description>
          <label>Fake name <input id="dialog-input" defaultValue="desktop" /></label>
          <Dialog.Close id="dialog-close">Close</Dialog.Close>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
    <div id="visuals" />
    <output id="results" aria-live="polite" />
  </main>;
}

createRoot(document.querySelector("#app")!).render(<App />);
