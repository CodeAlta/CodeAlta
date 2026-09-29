// Never log error messages/objects, arguments, session paths, prompts or provider data.
// Unknown codes are deliberately collapsed rather than trusting arbitrary wire strings.
export function rpcFailureCode(error: unknown): string {
  const code = error && typeof error === "object" && "code" in error ? error.code : undefined;
  switch (code) {
    case "duplicate_request": case "too_many_requests": case "connection_closed":
    case "operation_canceled": case "timeout": case "invalid_request":
    case "contract_mismatch": case "command_not_found": case "internal_error":
      return code;
    default: return "transport_or_client_failure";
  }
}

export function diagnosticRequestId(value: string): string | undefined {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) ? value : undefined;
}
