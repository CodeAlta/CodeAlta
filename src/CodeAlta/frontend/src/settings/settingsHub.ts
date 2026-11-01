import type { SettingsCatalogResponse, SettingsDescriptor, SettingsEvent, SettingsReportResponse, SettingsSetResponse, SettingsValueResponse, SettingsWindowValue } from "#neoastra";

/** What the hub asks of the host: the `settings` service, or a fixture of it. */
export type SettingsApi = Readonly<{
  catalog(request: { expectedEpoch: string }, options?: { timeoutMilliseconds?: number }): Promise<SettingsCatalogResponse>;
  get(request: { expectedEpoch: string; id: string | null; projectId: string | null }, options?: { timeoutMilliseconds?: number }): Promise<SettingsValueResponse>;
  set(request: { expectedEpoch: string; id: string | null; value: string | null; projectId: string | null }, options?: { timeoutMilliseconds?: number }): Promise<SettingsSetResponse>;
  reportWindow(request: { expectedEpoch: string; values: SettingsWindowValue[]; requestId: string | null; error: string | null }, options?: { timeoutMilliseconds?: number }): Promise<SettingsReportResponse>;
  watch(request: { expectedEpoch: string }, options?: { signal?: AbortSignal }): Promise<AsyncIterable<SettingsEvent>>;
}>;

/** A value as the host read it: the JSON value, where it comes from, and the choices of a choice whose choices change. */
export type SettingRead = Readonly<{ status: string; value: unknown; source: string | null; message: string | null; choices: readonly { value: string; label: string | null }[] | null }>;

/** How a change ended: the value now and the one before. */
export type SettingWrite = Readonly<{ status: string; value: unknown; previous: unknown; message: string | null }>;

/** What the hub knows: the settings the host lists (null until it answered, or without a host). */
export type SettingsHubSnapshot = Readonly<{ descriptors: readonly SettingsDescriptor[] | null }>;

type Timers = Readonly<{ set(callback: () => void, milliseconds: number): unknown; clear(handle: unknown): void }>;

const parse = (text: string | null | undefined): unknown => {
  if (text === null || text === undefined) return null;
  try { return JSON.parse(text) as unknown; } catch { return null; }
};

/**
 * The link of the window with the settings of the host: the catalog (read again when the plugins change it), a value,
 * a change, and the events of the registry, which the window handles (`show` opens a setting, `changed` tells of a
 * change an agent made, `write` changes a setting the page keeps). One hub for the window, connected to one host.
 */
export function createSettingsHub(api: SettingsApi, options: { reconnectMilliseconds?: number; timers?: Timers } = {}) {
  const reconnectMilliseconds = options.reconnectMilliseconds ?? 2000;
  const timers: Timers = options.timers ?? { set: (callback, milliseconds) => setTimeout(callback, milliseconds), clear: handle => clearTimeout(handle as ReturnType<typeof setTimeout>) };
  const listeners = new Set<() => void>();
  const eventListeners = new Set<(event: SettingsEvent) => void>();
  let snapshot: SettingsHubSnapshot = Object.freeze({ descriptors: null });
  let epoch: string | null = null;
  let catalogRequest = 0;

  function publish(next: SettingsHubSnapshot) {
    snapshot = next;
    for (const listener of [...listeners]) listener();
  }

  async function readCatalog() {
    const expected = epoch;
    if (!expected) return;
    const request = ++catalogRequest;
    try {
      const reply = await api.catalog({ expectedEpoch: expected }, { timeoutMilliseconds: 15_000 });
      if (request !== catalogRequest || epoch !== expected || reply.status !== "ok") return;
      publish(Object.freeze({ descriptors: reply.settings }));
    } catch { /* The built-in settings stay what the window shows. */ }
  }

  return Object.freeze({
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => snapshot,
    /** Listens to the events of the registry: `show`, `changed`, `write`, `catalog`. */
    onEvent(listener: (event: SettingsEvent) => void) { eventListeners.add(listener); return () => { eventListeners.delete(listener); }; },
    /** Listens to one host until the returned function is called. */
    connect(hostEpoch: string) {
      const abort = new AbortController();
      let timer: unknown = null;
      epoch = hostEpoch;
      publish(Object.freeze({ descriptors: null }));
      async function watch() {
        try {
          for await (const event of await api.watch({ expectedEpoch: hostEpoch }, { signal: abort.signal })) {
            if (abort.signal.aborted) return;
            if (event.kind === "catalog") void readCatalog();
            for (const listener of [...eventListeners]) listener(event);
          }
        } catch { /* Watched again below. */ }
        if (!abort.signal.aborted) timer = timers.set(() => void watch(), reconnectMilliseconds);
      }
      void readCatalog();
      void watch();
      return () => {
        abort.abort();
        if (timer !== null) timers.clear(timer);
        if (epoch === hostEpoch) { epoch = null; publish(Object.freeze({ descriptors: null })); }
      };
    },
    /** Reads a value of the host; `unavailable` without a host. */
    async get(id: string, projectId: string | null = null): Promise<SettingRead> {
      if (!epoch) return Object.freeze({ status: "unavailable", value: null, source: null, message: null, choices: null });
      try {
        const reply = await api.get({ expectedEpoch: epoch, id, projectId }, { timeoutMilliseconds: 15_000 });
        return Object.freeze({ status: reply.status, value: parse(reply.value), source: reply.source ?? null, message: reply.message ?? null, choices: reply.choices ?? null });
      } catch { return Object.freeze({ status: "failed", value: null, source: null, message: null, choices: null }); }
    },
    /** Changes a value as the user does in the window; undefined gives the setting back its default. */
    async set(id: string, value: unknown, projectId: string | null = null): Promise<SettingWrite> {
      if (!epoch) return Object.freeze({ status: "unavailable", value: null, previous: null, message: null });
      try {
        const reply = await api.set({ expectedEpoch: epoch, id, value: value === undefined ? null : JSON.stringify(value), projectId }, { timeoutMilliseconds: 30_000 });
        return Object.freeze({ status: reply.status, value: parse(reply.value), previous: parse(reply.previous), message: reply.message ?? null });
      } catch { return Object.freeze({ status: "failed", value: null, previous: null, message: null }); }
    },
    /** Reports values of the settings the page keeps, with the answer to a change it was asked for. */
    async report(values: readonly { id: string; value: unknown; choices?: readonly { value: string; label: string | null }[] | null }[], requestId: string | null = null, error: string | null = null) {
      if (!epoch) return;
      try {
        await api.reportWindow({ expectedEpoch: epoch, values: values.map(item => ({ id: item.id, value: JSON.stringify(item.value ?? null), choices: item.choices ? item.choices.map(choice => ({ ...choice })) : null })),
          requestId, error }, { timeoutMilliseconds: 15_000 });
      } catch { /* The next change reports them again. */ }
    },
  });
}

export type SettingsHub = ReturnType<typeof createSettingsHub>;
