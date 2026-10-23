// The host side of the protocol of MCP Apps (io.modelcontextprotocol/ui, 2026-01-26): JSON-RPC over postMessage between
// the window and the frame of a view. The view asks; the window answers what it lets a view do, and tells the view its
// input, its result and the changes of its context.

/** The version of the protocol the window speaks. */
export const mcpAppProtocolVersion = "2026-01-26";

export type McpAppDisplayMode = "inline" | "fullscreen";

/** What the window tells a view of where it is shown. */
export type McpAppHostContext = {
  theme?: "light" | "dark";
  styles?: { variables?: Record<string, string> };
  displayMode?: McpAppDisplayMode;
  availableDisplayModes?: McpAppDisplayMode[];
  containerDimensions?: { width?: number; maxHeight?: number; height?: number };
  locale?: string;
  timeZone?: string;
  platform?: "desktop";
  userAgent?: string;
  toolInfo?: { tool: unknown };
};

/** What a view may ask of the window, each answered by the host of the view (its server, its session, the window). */
export type McpAppHost = {
  /** Calls a tool of the server of the view; resolves to its CallToolResult, rejects with an McpAppError. */
  callTool(name: string, args: Record<string, unknown>): Promise<unknown>;
  /** Reads a resource of the server of the view; resolves to its ReadResourceResult, rejects with an McpAppError. */
  readResource(uri: string): Promise<unknown>;
  /** Sends a text to the session as a user message; resolves to whether the session took it. */
  sendMessage(text: string): Promise<boolean>;
  /** Opens an address outside the window; returns whether it was opened. */
  openLink(url: string): boolean;
  /** Asks for a display mode; returns the mode the view is now shown in. */
  requestDisplayMode(mode: string): McpAppDisplayMode;
  /** The view says how large its content is. */
  sizeChanged(size: { width?: number; height?: number }): void;
  /** A message of the view for the log. */
  log(params: unknown): void;
};

/** A refusal or a failure of a request of a view, answered as a JSON-RPC error. */
export class McpAppError extends Error {
  constructor(readonly code: number, message: string) { super(message); }
}

type Message = { jsonrpc?: unknown; id?: unknown; method?: unknown; params?: unknown; result?: unknown; error?: unknown };

const methodNotFound = -32601, invalidParams = -32602, invalidRequest = -32600, refused = -32000;
// Requests of a view that may wait at once: a view that asks more is told to wait.
const maximumPending = 8;
// Longest text of a message a view sends to the session.
const maximumMessageText = 16 * 1024;

/**
 * The conversation of the window with one view. Messages of the frame are given to `receive` (the frame checks that they
 * come from its own window); what the window says is given to `post`.
 */
export class McpAppBridge {
  // `ui/initialize` was answered; `ui/notifications/initialized` then makes the view ready.
  private handshake = false;
  private initialized = false;
  private stopped = false;
  private pending = 0;
  private messageInFlight = false;
  private nextId = 1;
  private readonly waiting = new Map<number, (ok: boolean) => void>();

  constructor(
    private readonly post: (message: unknown) => void,
    private readonly host: McpAppHost,
    private readonly view: { toolInput: Record<string, unknown>; toolResult: unknown; context: () => McpAppHostContext; hostVersion: string }) {}

  /** Whether the view finished its handshake: the window then tells it what changes. */
  get ready(): boolean { return this.initialized && !this.stopped; }

  /** Handles a message of the view. */
  receive(data: unknown): void {
    if (this.stopped || !data || typeof data !== "object") return;
    const message = data as Message;
    if (message.jsonrpc !== "2.0") return;
    const id = message.id;
    const hasId = typeof id === "string" || typeof id === "number";
    if (typeof message.method !== "string") {
      // An answer of the view to a request of the window (the teardown).
      if (typeof id === "number" && this.waiting.has(id)) { this.waiting.get(id)!(message.error === undefined); this.waiting.delete(id); }
      return;
    }

    const method = message.method;
    if (!hasId) { this.notification(method, message.params); return; }
    void this.request(id as string | number, method, message.params);
  }

  /** Tells the view a change of its context, once it is ready. */
  contextChanged(update: Partial<McpAppHostContext>): void {
    if (this.ready) this.post({ jsonrpc: "2.0", method: "ui/notifications/host-context-changed", params: update });
  }

  /** Tells the view it is about to go, and waits for its answer, at most `timeoutMs`; nothing is said afterwards. */
  async teardown(reason: string, timeoutMs = 500): Promise<void> {
    if (this.stopped) return;
    if (this.initialized) {
      const id = this.nextId++;
      const answered = new Promise<boolean>(resolve => this.waiting.set(id, resolve));
      this.post({ jsonrpc: "2.0", id, method: "ui/resource-teardown", params: { reason } });
      await Promise.race([answered, new Promise(resolve => setTimeout(resolve, timeoutMs))]);
      this.waiting.delete(id);
    }
    this.stop();
  }

  /** Stops the conversation: what the view sends afterwards is ignored. */
  stop(): void {
    this.stopped = true;
    for (const resolve of this.waiting.values()) resolve(false);
    this.waiting.clear();
  }

  private notification(method: string, params: unknown): void {
    switch (method) {
      case "ui/notifications/initialized":
        if (this.initialized || !this.handshake) return;
        this.initialized = true;
        this.post({ jsonrpc: "2.0", method: "ui/notifications/tool-input", params: { arguments: this.view.toolInput } });
        this.post({ jsonrpc: "2.0", method: "ui/notifications/tool-result", params: this.view.toolResult });
        return;
      case "ui/notifications/size-changed": {
        const size = params as { width?: unknown; height?: unknown } | null;
        const width = typeof size?.width === "number" && Number.isFinite(size.width) ? size.width : undefined;
        const height = typeof size?.height === "number" && Number.isFinite(size.height) ? size.height : undefined;
        if (width !== undefined || height !== undefined) this.host.sizeChanged({ width, height });
        return;
      }
      case "notifications/message":
        this.host.log(params);
        return;
      default:
        return;
    }
  }

  private async request(id: string | number, method: string, params: unknown): Promise<void> {
    if (method === "ui/initialize") {
      if (this.handshake) { this.error(id, invalidRequest, "The view is already initialized."); return; }
      this.handshake = true;
      this.answer(id, {
        protocolVersion: mcpAppProtocolVersion,
        hostInfo: { name: "CodeAlta", version: this.view.hostVersion },
        hostCapabilities: { openLinks: {}, serverTools: {}, serverResources: {}, logging: {}, message: { text: {} } },
        hostContext: this.view.context(),
      });
      return;
    }

    if (method === "ping") { this.answer(id, {}); return; }
    // Nothing but the handshake before the handshake.
    if (!this.initialized) { this.error(id, invalidRequest, "The view is not initialized."); return; }
    if (this.pending >= maximumPending) { this.error(id, refused, "Too many requests of the view wait."); return; }
    this.pending++;
    try {
      this.answer(id, await this.run(method, params));
    } catch (error) {
      if (error instanceof McpAppError) this.error(id, error.code, error.message);
      else this.error(id, refused, "The request of the view failed.");
    } finally {
      this.pending--;
    }
  }

  private async run(method: string, params: unknown): Promise<unknown> {
    const values = params && typeof params === "object" ? params as Record<string, unknown> : {};
    switch (method) {
      case "tools/call": {
        if (typeof values.name !== "string" || !values.name) throw new McpAppError(invalidParams, "A tool call names its tool.");
        const args = values.arguments === undefined || values.arguments === null ? {} : values.arguments;
        if (typeof args !== "object" || Array.isArray(args)) throw new McpAppError(invalidParams, "The arguments of a tool are an object.");
        return await this.host.callTool(values.name, args as Record<string, unknown>);
      }
      case "resources/read":
        if (typeof values.uri !== "string" || !values.uri) throw new McpAppError(invalidParams, "A read names its resource.");
        return await this.host.readResource(values.uri);
      case "ui/message": {
        const text = messageText(values);
        if (text === null) throw new McpAppError(invalidParams, "A message is the text of the user.");
        // One message at a time: a view does not flood the session.
        if (this.messageInFlight) throw new McpAppError(refused, "A message of the view is being sent.");
        this.messageInFlight = true;
        try {
          if (!await this.host.sendMessage(text)) throw new McpAppError(refused, "The session did not take the message.");
        } finally {
          this.messageInFlight = false;
        }
        return {};
      }
      case "ui/open-link":
        if (typeof values.url !== "string" || !this.host.openLink(values.url)) throw new McpAppError(refused, "The link was not opened.");
        return {};
      case "ui/request-display-mode":
        return { mode: this.host.requestDisplayMode(typeof values.mode === "string" ? values.mode : "") };
      default:
        throw new McpAppError(methodNotFound, `The window does not answer ${method.slice(0, 64)}.`);
    }
  }

  private answer(id: string | number, result: unknown): void {
    if (!this.stopped) this.post({ jsonrpc: "2.0", id, result });
  }

  private error(id: string | number, code: number, message: string): void {
    if (!this.stopped) this.post({ jsonrpc: "2.0", id, error: { code, message } });
  }
}

// The text of a message of a view: the user role, and text blocks (an array, or one block as the first spec wrote it).
function messageText(params: Record<string, unknown>): string | null {
  if (params.role !== undefined && params.role !== "user") return null;
  const blocks = Array.isArray(params.content) ? params.content : params.content ? [params.content] : [];
  const texts = blocks.map(block => block && typeof block === "object" && (block as { type?: unknown }).type === "text"
    && typeof (block as { text?: unknown }).text === "string" ? (block as { text: string }).text : null);
  if (texts.length === 0 || texts.some(text => text === null)) return null;
  const text = texts.join("\n\n").trim();
  return text.length > 0 && text.length <= maximumMessageText ? text : null;
}
