import { AppIcon } from "../AppIcon";
import { BrandIcon, isBrandIcon } from "../BrandIcon";

/** The icon of a vault: the logo of its brand when the app has it, else a terminal for a command, else a key. */
export function CredentialIcon({ icon, size = 16 }: { icon: string | null | undefined; size?: number }) {
  if (isBrandIcon(icon)) return <BrandIcon name={icon} size={size} />;
  return <AppIcon name={icon === "terminal" ? "terminal" : icon === "computer" ? "computer" : "secret"} size={size} />;
}
