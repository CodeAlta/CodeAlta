import type { MessageKey } from "../localization";

/** The steps of a setup over SSH, in their order. */
export type SetupStep = "connect" | "check" | "install" | "service" | "pair";

/** The steps, in their order. */
export const setupSteps: readonly SetupStep[] = ["connect", "check", "install", "service", "pair"];

/** The title of a step, as the checklist says it. */
export function stepTitle(step: SetupStep): MessageKey {
  switch (step) {
    case "connect": return "Connect over SSH";
    case "check": return "Check the system and .NET";
    case "install": return "Install CodeAlta Node";
    case "service": return "Start it as a service";
    case "pair": return "Pair it with this computer";
  }
}

/** What a person types for a computer, as ssh takes it (`user@host`, `user@host:port`, a host of the SSH configuration); empty when it cannot be one. */
export function sshTargetOf(text: string): string {
  let value = text.trim();
  if (value.toLowerCase().startsWith("ssh ")) value = value.slice(4).trim();
  if (value.toLowerCase().startsWith("ssh://")) value = value.slice(6).replace(/\/+$/u, "");
  return /^([A-Za-z0-9._-]+@)?(\[[0-9A-Fa-f:.]+\]|[A-Za-z0-9._-]+)(:\d{1,5})?$/u.test(value) && !value.startsWith("-") && value.length <= 255 ? value : "";
}

/**
 * The fingerprint of the SSH key of a computer that ssh does not know yet, as the failure of its first connection gives it
 * (`SHA256:… (ED25519)`): shown before the person trusts it. Null when the failure does not give one.
 */
export function hostKeyOf(code: string, detail: string | null | undefined): string | null {
  if (code !== "host_key_unknown" || !detail) return null;
  const match = /SHA256:[A-Za-z0-9+/=]{8,}(?: \([A-Z0-9-]{2,16}\))?/u.exec(detail);
  return match ? match[0] : null;
}

/** What a failure of a setup asks the person to do; `{host}` names the computer. */
export function setupProblem(code: string): MessageKey {
  switch (code) {
    case "host_key_unknown": return "This computer does not know the SSH key of {host} yet. If it is your machine, trust it and try again.";
    case "host_key_changed": return "The SSH key of {host} changed since this computer last connected: find out why before going on.";
    case "auth_failed": return "SSH refused the sign-in: add your key to the SSH agent (ssh-add), or copy it to the machine (ssh-copy-id), then try again.";
    case "unknown_host": return "No computer answers to this name: check it, or use its address.";
    case "unreachable": return "The computer does not answer on SSH: check that it is on and that SSH runs there.";
    case "ssh_missing": return "The OpenSSH client is missing on this computer: add it (on Windows, Settings > System > Optional features).";
    case "unsupported_system": return "CodeAlta installs over SSH on Linux and macOS: on this machine, install CodeAlta Node and pair it with its link.";
    case "dotnet_missing": return "The machine has no .NET 10: install it there (dotnet.microsoft.com/download), then try again.";
    case "packages_missing": return "The folder has no CodeAlta Node package for this machine.";
    case "copy_failed": return "The packages could not be copied to the machine.";
    case "tool_install_failed": return "dotnet could not install CodeAlta Node on the machine.";
    case "runtime_install_failed": return "CodeAlta Node could not be unpacked on the machine.";
    case "service_failed": return "The service of CodeAlta Node could not be installed on the machine.";
    case "pair_failed": return "The node did not start, or made no pairing link.";
    case "pair_unreachable": return "This computer could not reach the node: check that the machine accepts connections on its port (a firewall), then try again.";
    case "local_network_blocked": return "macOS keeps CodeAlta from the local network, so it cannot reach {host}: in System Settings > Privacy & Security > Local Network, turn on CodeAlta, then try again.";
    case "invalid_target": return "Give the computer as user@host, or a host of your SSH configuration.";
    case "invalid_path": return "A folder of the machine is a full path, or starts with ~/.";
    default: return "The setup did not go through.";
  }
}
