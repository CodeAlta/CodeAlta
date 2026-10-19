import { modelCatalog, sessionOperations, sessionPermissions, type ModelCatalogProvidersRequest, type ModelCatalogProvidersResponse,
  type SessionPermissionResolution, type SessionPermissionResolveRequest, type SessionPermissionsPage, type SessionPermissionsRequest,
  type SessionReceiptPage, type SessionReceiptRequest } from "#neoastra";
import type { HostReads } from "./hostReadsContext";
import { createSharedRead } from "./sharedRead";

type CallOptions = Readonly<{ signal?: AbortSignal }>;

export type HostReadApi = Readonly<{
  providers: (request: ModelCatalogProvidersRequest, options: CallOptions) => Promise<ModelCatalogProvidersResponse>;
  receipts: (request: SessionReceiptRequest, options: CallOptions) => Promise<SessionReceiptPage>;
  permissions: (request: SessionPermissionsRequest, options: CallOptions) => Promise<SessionPermissionsPage>;
  resolvePermission: (request: SessionPermissionResolveRequest, options: CallOptions) => Promise<SessionPermissionResolution>;
}>;

const hostApi: HostReadApi = {
  providers: (request, options) => modelCatalog.providers(request, options),
  receipts: (request, options) => sessionOperations.receipts(request, options),
  permissions: (request, options) => sessionPermissions.list(request, options),
  resolvePermission: (request, options) => sessionPermissions.resolve(request, options),
};

/**
 * The shared reads of a window. The providers are host-wide and change seldom: an answer stays good for five seconds.
 * The receipts of the window and the permissions of a session are only joined while a read waits, so what a pane
 * reads is never older than what it would have read alone, and a decision made here makes the next read of its
 * session a new one.
 */
export function createHostReads(api: HostReadApi = hostApi, now?: () => number): HostReads {
  const permissions = createSharedRead(api.permissions, request => `${request.expectedHostEpoch}\n${request.sessionId}`, 0, now);
  return {
    providers: createSharedRead(api.providers, request => request.expectedEpoch, 5_000, now),
    receipts: createSharedRead(api.receipts, request => `${request.expectedEpoch}\n${request.offset}`, 0, now),
    permissions,
    resolvePermission: (request, options) => {
      const session = { expectedHostEpoch: request.expectedHostEpoch, sessionId: request.handle.sessionId };
      // A read that started before the decision may still list the request: the readers after it read again.
      permissions.forget(session);
      return api.resolvePermission(request, options).finally(() => permissions.forget(session));
    },
  };
}
