import { useLayoutEffect, useRef, useState, type ContextType, type RefObject } from "react";
import { sessionOperations, type SessionReferenceObservationResponse } from "#neoastra";
import type { ProjectReferenceContext } from "./ProjectReferencePicker";
import { validReferenceSpans } from "./projectReferences";
import { useShellLanguage } from "./shellLanguage";
import type { PromptInput } from "./PromptEditor";

// A raw-text preview, not an editor overlay: native caret, wrapping, selection and
// IME remain entirely owned by the textarea. Only the host supplies span semantics.
export function ProjectReferencePresentation({ text, input, scope }: {
  text: string; input: RefObject<PromptInput | null>; scope: ContextType<typeof ProjectReferenceContext>;
}) {
  const { t } = useShellLanguage();
  const identity = JSON.stringify(scope);
  const [page, setPage] = useState<SessionReferenceObservationResponse | null>(null);
  const [pending, setPending] = useState(false);
  const generation = useRef(0);
  const composing = useRef(false);
  const controller = useRef<AbortController | null>(null);
  function invalidate() {
    generation.current++; controller.current?.abort(); controller.current = null;
  }
  useLayoutEffect(() => {
    invalidate(); setPage(null); setPending(false);
    return invalidate;
  }, [identity, text, input]);
  useLayoutEffect(() => {
    const element = input.current;
    if (!element) return;
    const edit = () => { invalidate(); setPage(null); setPending(false); };
    const begin = () => { composing.current = true; edit(); };
    const end = () => { composing.current = false; };
    element.addEventListener("input", edit);
    element.addEventListener("compositionstart", begin);
    element.addEventListener("compositionend", end);
    return () => { element.removeEventListener("input", edit); element.removeEventListener("compositionstart", begin); element.removeEventListener("compositionend", end); };
  }, [input]);
  async function check() {
    const element = input.current;
    if (!scope || !element || element.value !== text || composing.current) return;
    invalidate();
    const version = generation.current;
    const abort = new AbortController(); controller.current = abort;
    setPage(null); setPending(true);
    const valid = () => !abort.signal.aborted && generation.current === version && input.current === element && element.isConnected && element.value === text;
    try {
      const { observe, lifetime: _lifetime, capturePopup: _capturePopup, ...request } = scope;
      const value = await sessionOperations.observeReferences({ ...request, text }, { signal: abort.signal, timeoutMilliseconds: 3000 });
      if (!valid()) return;
      observe?.(value);
      if (value.epoch !== scope.expectedEpoch || !validReferenceSpans(text, value.items)) {
        setPage({ status: "read_error", epoch: scope.expectedEpoch, items: [], omitted: true }); return;
      }
      setPage(value);
    } catch { if (valid()) setPage({ status: "read_error", epoch: scope.expectedEpoch, items: [], omitted: true }); }
    finally { if (valid()) setPending(false); }
  }
  if (!scope) return null;
  let cursor = 0;
  const runs = page?.status === "ok" ? page.items.map((span, index) => {
    const before = text.slice(cursor, span.start); cursor = span.start + span.length;
    const label = t(span.status === "resolved" ? "Path metadata observed; Send revalidates" : span.status === "escaped" ? "Escaped at sign; literal text" : "Unresolved or over budget; literal at this observation");
    return <span key={index}>{before}<mark className={`reference-${span.status}`} title={label} aria-label={`${text.slice(span.start, cursor)} — ${label}`}>{text.slice(span.start, cursor)}</mark></span>;
  }) : null;
  return <section className="reference-presentation" aria-label={t("Project reference presentation")}>
    <button type="button" disabled={pending || !text} onClick={() => void check()}>{t("Check reference metadata")}</button>
    <p role="status">{pending ? t("Checking bounded metadata…") : page ? <>{page.status}{page.omitted ? ` — ${t("reference spans omitted")}` : ""}</> : t("Not checked (or edited since observation).")} {t("Resolved metadata / unresolved literal / escaped @. Normal Send revalidates paths and ranges, not file bytes. Queue/Steer stay literal.")}</p>
    {runs && <pre aria-label={t("Raw prompt reference preview")}>{runs}{text.slice(cursor)}</pre>}
  </section>;
}

