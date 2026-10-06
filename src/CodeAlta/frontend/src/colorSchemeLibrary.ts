import { useCallback, useEffect, useRef, useState } from "react";
import { colorSchemes as service, type ColorSchemeProblem } from "#neoastra";
import { parseCustomScheme, type CustomColorScheme } from "./colorSchemes";
import { schemeSaveRequest } from "./customColorSchemes";

type Api = Pick<typeof service, "list" | "save" | "delete" | "reveal">;
type Reading = Readonly<{ status: string; directory: string | null; problems: readonly ColorSchemeProblem[] }>;
const call = { timeoutMilliseconds: 10_000 };

/** The schemes of a listing; an entry that is not a scheme is left out. */
export function listedSchemes(schemes: readonly unknown[]): CustomColorScheme[] {
  return schemes.map(parseCustomScheme).filter((scheme): scheme is CustomColorScheme => scheme !== null);
}

/**
 * The user's color schemes as the host keeps them. It reads them once, and again on `reload` and after a
 * change; `accept` receives each listing, with the scheme to select after a save. `status` is `loading`
 * until the host has answered, then the status of its last answer.
 */
export function useColorSchemeLibrary(accept: (schemes: readonly CustomColorScheme[], selected?: string) => void, api: Api = service) {
  const [reading, setReading] = useState<Reading>({ status: "loading", directory: null, problems: [] });
  const latest = useRef(accept); latest.current = accept;
  const generation = useRef(0);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  const reload = useCallback(async (selected?: string) => {
    const current = ++generation.current;
    let listing: Awaited<ReturnType<Api["list"]>> | null = null;
    try { listing = await api.list({}, call); } catch { /* Reported as a failed read below. */ }
    // An answer that a later read has overtaken says nothing new.
    if (!alive.current || current !== generation.current) return;
    if (listing?.status === "ok") latest.current(listedSchemes(listing.schemes), selected);
    setReading({ status: listing?.status ?? "read_failed", directory: listing?.directory ?? null, problems: listing?.status === "ok" ? listing.problems : [] });
  }, [api]);
  useEffect(() => { void reload(); }, [reload]);

  /** Saves a scheme and selects it. */
  const save = useCallback(async (scheme: CustomColorScheme) => {
    const result = await api.save(schemeSaveRequest(scheme), call);
    if (result.status === "ok" && result.id) await reload(result.id);
    return result;
  }, [api, reload]);
  const remove = useCallback(async (id: string) => {
    const result = await api.delete({ id }, call);
    // Gone either way: the list is read again.
    if (result.status === "ok" || result.status === "not_found") await reload();
    return result;
  }, [api, reload]);
  /** Shows a scheme's file in the file manager, or the folder of the schemes. */
  const reveal = useCallback((id: string | null) => api.reveal({ id }, call), [api]);
  return { ...reading, reload, save, remove, reveal };
}

export type ColorSchemeLibrary = ReturnType<typeof useColorSchemeLibrary>;
