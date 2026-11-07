import type { Intent } from "@blueprintjs/core";
import type { MessageKey } from "../localization";
import { managementProblem } from "./machineManagement";

/** The words of what a machine signs in: a provider, an MCP server or a tool. */
export function signInKindLabel(kind: string): MessageKey {
  switch (kind) {
    case "mcp": return "MCP server";
    case "cli": return "Tool";
    default: return "Provider";
  }
}

/** The words of how a sign-in of a machine stands. */
export function signInStateLabel(state: string): MessageKey {
  switch (state) {
    case "signed_in": return "Signed in";
    case "signed_out": return "Not signed in";
    default: return "Unknown";
  }
}

/** The color of how a sign-in of a machine stands. */
export function signInStateIntent(state: string): Intent {
  switch (state) {
    case "signed_in": return "success";
    case "signed_out": return "warning";
    default: return "none";
  }
}

/** Why a sign-in of a machine did not start or end, in words. */
export function signInProblem(status: string): MessageKey {
  switch (status) {
    case "failed": case "signin.failed": case "none": return "The sign-in did not complete on the machine.";
    case "signin.unsupported": case "signin.not_found": case "signin.unavailable": case "port_busy": case "signin.browser": return "The sign-in could not start on the machine.";
    default: return managementProblem(status) ?? "The sign-in could not start on the machine.";
  }
}
