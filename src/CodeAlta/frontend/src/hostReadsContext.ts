import { createContext } from "react";
import type { ModelCatalogProvidersRequest, ModelCatalogProvidersResponse, SessionPermissionResolution, SessionPermissionResolveRequest,
  SessionPermissionsPage, SessionPermissionsRequest, SessionReceiptPage, SessionReceiptRequest } from "#neoastra";
import type { SharedRead } from "./sharedRead";

/**
 * The reads of the host that the panes of the window share, owned by the window (`createHostReads`): with many tabs
 * open, the panes that poll the same thing at the same time cost the host one read.
 */
export type HostReads = Readonly<{
  providers: SharedRead<ModelCatalogProvidersRequest, ModelCatalogProvidersResponse>;
  receipts: SharedRead<SessionReceiptRequest, SessionReceiptPage>;
  permissions: SharedRead<SessionPermissionsRequest, SessionPermissionsPage>;
  resolvePermission: (request: SessionPermissionResolveRequest, options: Readonly<{ signal?: AbortSignal }>) => Promise<SessionPermissionResolution>;
}>;

/** The shared reads of the window; null where none is set up (a component then reads the host itself). */
export const HostReadsContext = createContext<HostReads | null>(null);

/**
 * Whether the pane around a component is on screen: a session tab that is hidden stays mounted, and what it polls
 * waits until it is shown again. True where no pane says otherwise.
 */
export const PaneObservingContext = createContext(true);
