import { AppIcon } from "../AppIcon";
import { BrandIcon } from "../BrandIcon";
import { triggerIcon } from "./automations";

/** The icon of what starts an automation: the logo of the product for a trigger that belongs to one, else the icon of its kind. */
export function TriggerIcon({ type, size }: { type: string | undefined; size: number }) {
  return type === "jira" ? <BrandIcon name="jira" size={size} /> : <AppIcon name={triggerIcon(type)} size={size} />;
}
