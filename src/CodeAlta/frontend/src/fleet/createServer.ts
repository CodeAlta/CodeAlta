import type { MessageKey } from "../localization";
import { asksSudoPassword, setupProblem } from "../machines/addMachine";

/** The steps of a server set up on this computer, in their order. */
export const localServerSteps: readonly string[] = ["find", "install", "start", "claim"];

/** The steps of a server set up on a machine over SSH, in their order. */
export const sshServerSteps: readonly string[] = ["connect", "check", "install", "service", "reach", "claim"];

/** The title of a step of either setup. */
export function serverStepTitle(step: string): MessageKey {
  switch (step) {
    case "find": return "Look for CodeAlta Server";
    case "connect": return "Connect over SSH";
    case "check": return "Check the system and .NET";
    case "install": return "Install CodeAlta Server";
    case "service": return "Start it as a service";
    case "start": return "Start it";
    case "reach": return "Check that it answers from here";
    default: return "Make you its owner";
  }
}

/** What a failure of a server setup asks the person to do; `{host}` names the machine and `{port}` the port of the server. */
export function serverSetupProblem(code: string): MessageKey {
  switch (code) {
    case "dotnet_missing": return "The machine has no .NET 10 SDK to install CodeAlta Server: install it there (dotnet.microsoft.com/download), then try again.";
    case "tool_install_failed": return "dotnet could not install CodeAlta Server.";
    case "packages_missing": return "The folder has no CodeAlta Server package.";
    case "server_start_failed": return "CodeAlta Server did not start: another program may use its port; choose another one under Advanced.";
    case "service_failed": return "The service of CodeAlta Server could not be started on the machine.";
    case "server_blocked_ufw": return "The server runs but does not answer from here: the firewall of {host} blocks port {port}. On {host}, run sudo ufw allow {port}/tcp, then try again.";
    case "server_blocked": return "The server runs but does not answer from here at {host}: check that port {port} is open in its firewall and that this name reaches it, then try again.";
    case "server_proxy_unreachable": return "The server runs, but its address does not answer from here: check that what you put in front of it forwards to port {port} of {host}, then try again.";
    case "devtunnel_host_missing": return "The server runs, but the Dev Tunnel does not answer: devtunnel host must forward to its loopback port. Devices need the tunnel account or a connect token; see the instructions below.";
    case "private_network_publish_missing": return "The server runs, but its private network does not publish it yet: let CodeAlta publish it, or run this command on the machine.";
    case "private_network_publish_missing_sudo": return "The private network needs sudo to publish the server: give your password on the machine once. It is not kept.";
    case "tailscale_serve_missing": return "The server runs, but Tailscale Serve does not publish its address on {host} yet: let CodeAlta publish it, or run this command there, then start over.";
    case "tailscale_serve_missing_sudo": return "The server runs, but Tailscale Serve does not publish its address on {host} yet: give your password on the machine for CodeAlta to publish it with sudo (it is not kept), or run this command there, then start over.";
    case "tailscale_sudo_password_needed": return "Tailscale Serve is changed as an administrator of {host}: give your password on the machine once, for sudo. It is not kept.";
    case "tailscale_serve_disabled": return "Tailscale Serve or HTTPS certificates are not turned on for your tailnet: an admin of the tailnet turns them on at this address, then try again.";
    case "tailscale_serve_failed": return "Tailscale Serve could not publish the server on {host}: run its command there yourself, then start over.";
    case "server_owned": return "A server with an owner runs there already: join it with an enrollment code instead.";
    case "setup_code_failed": return "The server gave no setup code.";
    case "forward_failed": return "This computer could not reach the server through SSH to set it up.";
    case "claim_refused": return "The server refused the setup.";
    case "invalid_request": return "Check the computer, the address and the folders: a folder is a full path, and a name is needed.";
    default: return setupProblem(code);
  }
}

/** Whether a failure of a server setup is an address that Tailscale Serve does not publish yet, which CodeAlta offers to publish on the machine. */
export function offersTailscaleServe(code: string): boolean {
  return code === "tailscale_serve_missing" || code === "tailscale_serve_missing_sudo";
}

/** Whether the matching private-network provider offers to publish on the server's machine. */
export function offersNetworkPublish(code: string): boolean {
  return offersTailscaleServe(code) || code === "private_network_publish_missing" || code === "private_network_publish_missing_sudo";
}

/**
 * Whether a failure of a server setup asks the person for their sudo password on the machine: for Always on, or for
 * Tailscale Serve, where the password comes with the offer to publish and is not needed to start over without it.
 */
export function serverAsksSudoPassword(code: string): boolean {
  return code === "private_network_publish_missing_sudo" || code === "tailscale_serve_missing_sudo" || code === "tailscale_sudo_password_needed" || asksSudoPassword(code);
}

/** The address of a server as typed, with its scheme (`https://` when none is given); empty when it cannot be one. */
export { organizationAddressOf as organizationUrlOf } from "./joinOrganization";
